using System.CommandLine;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Rca.Models;
using Rca.Providers;

namespace Rca.Commands;

public class AnalyzeCommand : Command
{
    private const int DefaultParallel = 3;
    private const int DefaultTimeoutSeconds = 60;
    private const int MaxRetries = 3;

    public AnalyzeCommand() : base("analyze", "Analyze failed test logs using LLM")
    {
        var pathArg = new Argument<string>("path", "Path to logs directory or file");
        var providerOpt = new Option<string>("--provider", () => "openai", "LLM provider (openai, claude, gemini, ollama)");
        var apiKeyOpt = new Option<string?>("--api-key", "API key (or use env var)");
        var modelOpt = new Option<string?>("--model", "Model override");
        var outputDirOpt = new Option<string>("--output-dir", () => ".", "Output directory for reports");
        var parallelOpt = new Option<int>("--parallel", () => DefaultParallel, "Max parallel API calls");
        var timeoutOpt = new Option<int>("--timeout", () => DefaultTimeoutSeconds, "Timeout per API call (seconds)");

        AddArgument(pathArg);
        AddOption(providerOpt);
        AddOption(apiKeyOpt);
        AddOption(modelOpt);
        AddOption(outputDirOpt);
        AddOption(parallelOpt);
        AddOption(timeoutOpt);

        this.SetHandler(ExecuteAsync, pathArg, providerOpt, apiKeyOpt, modelOpt, outputDirOpt, parallelOpt, timeoutOpt);
    }

    private async Task ExecuteAsync(string path, string provider, string? apiKey, string? model, string outputDir, int parallel, int timeout)
    {
        var logs = await LoadLogsAsync(path);
        if (logs.Count == 0)
        {
            Console.WriteLine("No failed test logs found.");
            return;
        }

        Console.WriteLine($"Found {logs.Count} failed test(s). Analyzing with {provider} (parallel={parallel})...\n");

        Directory.CreateDirectory(outputDir);

        using var client = ChatClientFactory.Create(provider, apiKey, model, timeout);
        var systemPrompt = await LoadPromptAsync();
        var options = new ChatOptions { ModelId = model, MaxOutputTokens = 2048 };
        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");

        var results = new List<AnalysisResult>();
        var semaphore = new SemaphoreSlim(parallel);
        var cts = new CancellationTokenSource();
        var completed = 0;

        var tasks = logs.Select(async (log, i) =>
        {
            await semaphore.WaitAsync(cts.Token);
            try
            {
                var result = await AnalyzeWithRetryAsync(client, systemPrompt, options, log, timeout, cts.Token);
                var current = Interlocked.Increment(ref completed);
                Console.WriteLine($"[{current}/{logs.Count}] {log.TestClass}.{log.TestName} - {(result.Success ? "✅" : "❌")}");

                if (result.Success)
                {
                    var report = BuildReport(log, result.Response!);
                    var safeName = SanitizeFileName($"{log.TestClass}.{log.TestName}");
                    var fileName = $"rca-{safeName}-{timestamp}.md";
                    var filePath = Path.Combine(outputDir, fileName);
                    await File.WriteAllTextAsync(filePath, report, cts.Token);
                    result = result with { ReportFile = fileName };
                }

                return result;
            }
            finally
            {
                semaphore.Release();
            }
        }).ToList();

        results.AddRange(await Task.WhenAll(tasks));

        // Generate index.md
        await GenerateIndexAsync(outputDir, results, timestamp);

        var successCount = results.Count(r => r.Success);
        var failCount = results.Count - successCount;

        Console.WriteLine($"\nDone: {successCount} success, {failCount} failed");
        Console.WriteLine($"Reports: {outputDir}/");

        // Exit codes
        if (failCount == logs.Count)
            Environment.ExitCode = 2; // complete failure
        else if (failCount > 0)
            Environment.ExitCode = 1; // partial failure
        // else 0 (success)
    }

    private async Task<AnalysisResult> AnalyzeWithRetryAsync(
        IChatClient client,
        string systemPrompt,
        ChatOptions options,
        FailedTestLog log,
        int timeoutSec,
        CancellationToken ct)
    {
        var userPrompt = BuildTestPrompt(log);
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, systemPrompt),
            new(ChatRole.User, userPrompt)
        };

        for (var attempt = 1; attempt <= MaxRetries; attempt++)
        {
            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSec));

                var response = await client.GetResponseAsync(messages, options, timeoutCts.Token);
                return new AnalysisResult(log, true, response.Text, null, null);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (attempt == MaxRetries)
                    return new AnalysisResult(log, false, null, ex.Message, null);

                // Exponential backoff: 1s, 2s, 4s
                var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt - 1));
                await Task.Delay(delay, ct);
            }
        }

        return new AnalysisResult(log, false, null, "Max retries exceeded", null);
    }

    private static string BuildReport(FailedTestLog log, string analysis)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {log.TestClass}.{log.TestName}");
        sb.AppendLine();
        sb.AppendLine($"**TraceId:** `{log.TraceId}`");
        sb.AppendLine($"**Time:** {log.Timestamp:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine();
        sb.AppendLine("## Error");
        sb.AppendLine($"```\n{log.ErrorMessage}\n```");

        if (!string.IsNullOrEmpty(log.StackTrace))
        {
            sb.AppendLine();
            sb.AppendLine("## Stack Trace");
            sb.AppendLine($"```\n{log.StackTrace}\n```");
        }

        if (log.Logs?.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Logs");
            sb.AppendLine("```");
            foreach (var entry in log.Logs.Take(20))
            {
                sb.AppendLine($"[{entry.Timestamp}] [{entry.Level}] {entry.Category}: {entry.Message}");
                if (!string.IsNullOrEmpty(entry.Exception))
                    sb.AppendLine($"  Exception: {entry.Exception}");
            }
            if (log.Logs.Count > 20)
                sb.AppendLine($"... and {log.Logs.Count - 20} more");
            sb.AppendLine("```");
        }

        sb.AppendLine();
        sb.AppendLine("## Analysis");
        sb.AppendLine();
        sb.AppendLine(analysis);

        return sb.ToString();
    }

    private static async Task GenerateIndexAsync(string outputDir, List<AnalysisResult> results, string timestamp)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# RCA Summary");
        sb.AppendLine();
        sb.AppendLine($"Generated: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine();
        sb.AppendLine("| Test | Status | Report |");
        sb.AppendLine("|------|--------|--------|");

        foreach (var r in results.OrderBy(x => x.Log.TestClass).ThenBy(x => x.Log.TestName))
        {
            var testName = $"{r.Log.TestClass}.{r.Log.TestName}";
            var status = r.Success ? "✅" : $"❌ {r.Error}";
            var report = r.ReportFile != null ? $"[report]({r.ReportFile})" : "-";
            sb.AppendLine($"| {testName} | {status} | {report} |");
        }

        var successCount = results.Count(x => x.Success);
        sb.AppendLine();
        sb.AppendLine($"**Total:** {results.Count} | **Success:** {successCount} | **Failed:** {results.Count - successCount}");

        var indexPath = Path.Combine(outputDir, $"index-{timestamp}.md");
        await File.WriteAllTextAsync(indexPath, sb.ToString());
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new StringBuilder();
        foreach (var c in name)
        {
            sanitized.Append(invalid.Contains(c) ? '_' : c);
        }
        return sanitized.ToString();
    }

    private static async Task<string> LoadPromptAsync()
    {
        var dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
        var path = Path.Combine(dir, "Prompts", "AnalyzePrompt.txt");
        return await File.ReadAllTextAsync(path);
    }

    private static async Task<List<FailedTestLog>> LoadLogsAsync(string path)
    {
        var logs = new List<FailedTestLog>();
        var files = Directory.Exists(path)
            ? Directory.GetFiles(path, "*.json")
            : [path];

        var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        foreach (var file in files)
        {
            try
            {
                var content = await File.ReadAllTextAsync(file);
                // xUnitOTel writes single object per file
                var log = JsonSerializer.Deserialize<FailedTestLog>(content, jsonOptions);
                if (log != null)
                    logs.Add(log);
            }
            catch (JsonException)
            {
                // Skip malformed files
                Console.WriteLine($"Warning: Could not parse {Path.GetFileName(file)}");
            }
        }

        return logs;
    }

    private static string BuildTestPrompt(FailedTestLog log)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Test: {log.TestClass}.{log.TestName}");
        sb.AppendLine($"TraceId: {log.TraceId}");
        sb.AppendLine($"Error: {log.ErrorMessage}");

        if (!string.IsNullOrEmpty(log.StackTrace))
            sb.AppendLine($"Stack trace:\n{log.StackTrace}");

        if (log.Logs?.Count > 0)
        {
            sb.AppendLine("\nLogs:");
            foreach (var entry in log.Logs.Take(30))
            {
                sb.AppendLine($"[{entry.Level}] {entry.Category}: {entry.Message}");
            }
        }

        return sb.ToString();
    }

    private record AnalysisResult(
        FailedTestLog Log,
        bool Success,
        string? Response,
        string? Error,
        string? ReportFile
    );
}
