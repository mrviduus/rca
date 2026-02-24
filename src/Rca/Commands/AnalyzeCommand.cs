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

    private const int ChunkCharThreshold = 40_000;
    private const int ChunkCharSize      = 30_000;
    private const int ChunkOverlapCount  = 10;

    private readonly Argument<string> _pathArg = new("path") { Description = "Path to logs directory or file" };
    private readonly Option<string> _providerOpt = new("--provider") { Description = "LLM provider (openai, claude, gemini, ollama)", DefaultValueFactory = _ => "openai" };
    private readonly Option<string?> _apiKeyOpt = new("--api-key") { Description = "API key (or use env var)" };
    private readonly Option<string?> _modelOpt = new("--model") { Description = "Model override" };
    private readonly Option<string> _outputDirOpt = new("--output-dir") { Description = "Output directory for reports", DefaultValueFactory = _ => "." };
    private readonly Option<int> _parallelOpt = new("--parallel") { Description = "Max parallel API calls", DefaultValueFactory = _ => DefaultParallel };
    private readonly Option<int> _timeoutOpt = new("--timeout") { Description = "Timeout per API call (seconds)", DefaultValueFactory = _ => DefaultTimeoutSeconds };

    public AnalyzeCommand() : base("analyze", "Analyze failed test logs using LLM")
    {
        Add(_pathArg);
        Add(_providerOpt);
        Add(_apiKeyOpt);
        Add(_modelOpt);
        Add(_outputDirOpt);
        Add(_parallelOpt);
        Add(_timeoutOpt);

        SetAction(ExecuteAsync);
    }

    private async Task ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        var path = parseResult.GetValue(_pathArg)!;
        var provider = parseResult.GetValue(_providerOpt)!;
        var apiKey = parseResult.GetValue(_apiKeyOpt);
        var model = parseResult.GetValue(_modelOpt);
        var outputDir = parseResult.GetValue(_outputDirOpt)!;
        var parallel = parseResult.GetValue(_parallelOpt);
        var timeout = parseResult.GetValue(_timeoutOpt);

        var logs = await LoadLogsAsync(path);
        if (logs.Count == 0)
        {
            Console.WriteLine("No failed test logs found.");
            return;
        }

        Console.WriteLine($"Found {logs.Count} failed test(s). Analyzing with {provider} (parallel={parallel})...\n");

        Directory.CreateDirectory(outputDir);

        using var client = ChatClientFactory.Create(provider, apiKey, model, timeout);
        var (systemPrompt, chunkPrompt, synthesizePrompt) = await LoadPromptsAsync();
        var options = new ChatOptions { ModelId = model, MaxOutputTokens = 4096 };
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
                var result = await AnalyzeTestAsync(client, systemPrompt, chunkPrompt, synthesizePrompt, options, log, timeout, cts.Token);
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

    private static async Task<AnalysisResult> AnalyzeTestAsync(
        IChatClient client, string systemPrompt, string chunkPrompt, string synthesizePrompt,
        ChatOptions options, FailedTestLog log, int timeoutSec, CancellationToken ct)
    {
        var significantLogs = GetSignificantLogs(log);
        if (EstimateLogPayloadSize(significantLogs) > ChunkCharThreshold)
            return await AnalyzeInChunksAsync(client, chunkPrompt, synthesizePrompt,
                options, log, significantLogs, timeoutSec, ct);
        return await AnalyzeWithRetryAsync(client, systemPrompt, options, log, timeoutSec, ct);
    }

    private static async Task<AnalysisResult> AnalyzeInChunksAsync(
        IChatClient client, string chunkPrompt, string synthesizePrompt,
        ChatOptions options, FailedTestLog log, List<LogEntry> significantLogs,
        int timeoutSec, CancellationToken ct)
    {
        var chunks = BuildChunks(significantLogs, ChunkCharSize, ChunkOverlapCount);
        var totalChars = EstimateLogPayloadSize(significantLogs);
        Console.WriteLine($"  → {log.TestClass}.{log.TestName} — {totalChars:N0} chars, chunking ({chunks.Count} segments)...");

        var partialAnalyses = new List<string>(chunks.Count);
        for (var i = 0; i < chunks.Count; i++)
        {
            var segNum = i + 1;
            var sysPrompt = chunkPrompt
                .Replace("{segmentNumber}", segNum.ToString())
                .Replace("{totalSegments}", chunks.Count.ToString());

            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, sysPrompt),
                new(ChatRole.User, BuildChunkUserPrompt(log, chunks[i], segNum, chunks.Count))
            };

            var r = await CallWithRetryAsync(client, messages, options, timeoutSec, ct);
            if (!r.Success)
                return new AnalysisResult(log, false, null, $"Chunk {segNum} failed: {r.Error}", null);

            partialAnalyses.Add(r.Text!);
            Console.WriteLine($"    Segment {segNum}/{chunks.Count} analyzed.");
        }

        Console.WriteLine($"    Running synthesis...");
        var synthMessages = new List<ChatMessage>
        {
            new(ChatRole.System, synthesizePrompt),
            new(ChatRole.User, BuildSynthesisUserPrompt(log, partialAnalyses))
        };
        var synthesis = await CallWithRetryAsync(client, synthMessages, options, timeoutSec, ct);
        return synthesis.Success
            ? new AnalysisResult(log, true, synthesis.Text!, null, null)
            : new AnalysisResult(log, false, null, $"Synthesis failed: {synthesis.Error}", null);
    }

    private static async Task<AnalysisResult> AnalyzeWithRetryAsync(
        IChatClient client,
        string systemPrompt,
        ChatOptions options,
        FailedTestLog log,
        int timeoutSec,
        CancellationToken ct)
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, systemPrompt),
            new(ChatRole.User, BuildTestPrompt(log))
        };

        var r = await CallWithRetryAsync(client, messages, options, timeoutSec, ct);
        return r.Success
            ? new AnalysisResult(log, true, r.Text!, null, null)
            : new AnalysisResult(log, false, null, r.Error!, null);
    }

    private static async Task<CallResult> CallWithRetryAsync(
        IChatClient client, List<ChatMessage> messages,
        ChatOptions options, int timeoutSec, CancellationToken ct)
    {
        for (var attempt = 1; attempt <= MaxRetries; attempt++)
        {
            try
            {
                using var tcts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                tcts.CancelAfter(TimeSpan.FromSeconds(timeoutSec));
                var response = await client.GetResponseAsync(messages, options, tcts.Token);
                return new CallResult(true, response.Text, null);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                if (attempt == MaxRetries) return new CallResult(false, null, ex.Message);
                var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt - 1));
                Console.WriteLine($"    ⚠ Attempt {attempt}/{MaxRetries} failed: {ex.Message}. Retrying in {delay.TotalSeconds:0}s...");
                await Task.Delay(delay, ct);
            }
        }
        return new CallResult(false, null, "Max retries exceeded");
    }

    private static List<LogEntry> GetSignificantLogs(FailedTestLog log) =>
        log.Logs?
           .Where(e => e.Level is "Information" or "Warning" or "Error" or "Critical")
           .ToList()
        ?? [];

    private static int EstimateLogPayloadSize(List<LogEntry> entries) =>
        entries.Sum(e =>
            e.Timestamp.Length + e.Level.Length + e.Category.Length +
            e.Message.Length + (e.Exception?.Length ?? 0) + 10);

    private static List<List<LogEntry>> BuildChunks(List<LogEntry> entries, int chunkCharSize, int overlapCount)
    {
        var chunks = new List<List<LogEntry>>();
        var i = 0;
        while (i < entries.Count)
        {
            var chunk = new List<LogEntry>();
            var chars = 0;
            for (var j = i; j < entries.Count; j++)
            {
                var sz = entries[j].Timestamp.Length + entries[j].Level.Length +
                         entries[j].Category.Length + entries[j].Message.Length +
                         (entries[j].Exception?.Length ?? 0) + 10;
                if (chunk.Count > 0 && chars + sz > chunkCharSize) break;
                chunk.Add(entries[j]);
                chars += sz;
            }
            chunks.Add(chunk);
            i += Math.Max(1, chunk.Count - overlapCount);
        }
        return chunks;
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
            var significantLogs = GetSignificantLogs(log);
            sb.AppendLine($"\nLogs ({significantLogs.Count} of {log.Logs.Count} entries, filtered to Information+):");
            foreach (var entry in significantLogs)
            {
                sb.AppendLine($"[{entry.Timestamp}] [{entry.Level}] {entry.Category}: {entry.Message}");
                if (!string.IsNullOrEmpty(entry.Exception))
                    sb.AppendLine($"  Exception: {entry.Exception}");
            }
        }

        return sb.ToString();
    }

    private static string BuildChunkUserPrompt(FailedTestLog log, List<LogEntry> entries, int segNum, int totalSegs)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Test: {log.TestClass}.{log.TestName}");
        sb.AppendLine($"TraceId: {log.TraceId}");
        sb.AppendLine($"Error: {log.ErrorMessage}");

        if (!string.IsNullOrEmpty(log.StackTrace))
            sb.AppendLine($"Stack trace:\n{log.StackTrace}");

        sb.AppendLine($"\nLog segment {segNum} of {totalSegs} ({entries.Count} entries):");
        foreach (var entry in entries)
        {
            sb.AppendLine($"[{entry.Timestamp}] [{entry.Level}] {entry.Category}: {entry.Message}");
            if (!string.IsNullOrEmpty(entry.Exception))
                sb.AppendLine($"  Exception: {entry.Exception}");
        }

        return sb.ToString();
    }

    private static string BuildSynthesisUserPrompt(FailedTestLog log, List<string> partialAnalyses)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Test: {log.TestClass}.{log.TestName}");
        sb.AppendLine($"Error: {log.ErrorMessage}");
        sb.AppendLine();
        sb.AppendLine($"The following are partial analyses from {partialAnalyses.Count} consecutive log segments:");

        for (var i = 0; i < partialAnalyses.Count; i++)
        {
            sb.AppendLine();
            sb.AppendLine($"--- Segment {i + 1} Analysis ---");
            sb.AppendLine(partialAnalyses[i]);
        }

        return sb.ToString();
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
            foreach (var entry in log.Logs)
            {
                sb.AppendLine($"[{entry.Timestamp}] [{entry.Level}] {entry.Category}: {entry.Message}");
                if (!string.IsNullOrEmpty(entry.Exception))
                    sb.AppendLine($"  Exception: {entry.Exception}");
            }
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

    private static async Task<(string analyze, string analyzeChunk, string synthesize)> LoadPromptsAsync()
    {
        var dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
        var p = Path.Combine(dir, "Prompts");
        return (
            await File.ReadAllTextAsync(Path.Combine(p, "AnalyzePrompt.txt")),
            await File.ReadAllTextAsync(Path.Combine(p, "AnalyzeChunkPrompt.txt")),
            await File.ReadAllTextAsync(Path.Combine(p, "SynthesizePrompt.txt"))
        );
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

    private record AnalysisResult(
        FailedTestLog Log,
        bool Success,
        string? Response,
        string? Error,
        string? ReportFile
    );

    private record CallResult(bool Success, string? Text, string? Error);
}
