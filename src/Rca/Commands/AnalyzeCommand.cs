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
    public AnalyzeCommand() : base("analyze", "Analyze failed test logs using LLM")
    {
        var pathArg = new Argument<string>("path", "Path to logs directory or file");
        var providerOpt = new Option<string>("--provider", () => "openai", "LLM provider (openai, claude, gemini, ollama)");
        var apiKeyOpt = new Option<string?>("--api-key", "API key (or use env var)");
        var modelOpt = new Option<string?>("--model", "Model override");
        var outputOpt = new Option<string?>("--output", "Output file path");

        AddArgument(pathArg);
        AddOption(providerOpt);
        AddOption(apiKeyOpt);
        AddOption(modelOpt);
        AddOption(outputOpt);

        this.SetHandler(ExecuteAsync, pathArg, providerOpt, apiKeyOpt, modelOpt, outputOpt);
    }

    private async Task ExecuteAsync(string path, string provider, string? apiKey, string? model, string? output)
    {
        var logs = await LoadLogsAsync(path);
        if (logs.Count == 0)
        {
            Console.WriteLine("No failed test logs found.");
            return;
        }

        Console.WriteLine($"Found {logs.Count} failed test(s). Analyzing with {provider}...\n");

        using var client = ChatClientFactory.Create(provider, apiKey, model);
        var systemPrompt = await LoadPromptAsync();
        var options = new ChatOptions { ModelId = model, MaxOutputTokens = 2048 };

        var report = new StringBuilder();
        report.AppendLine("# RCA Report\n");

        for (var i = 0; i < logs.Count; i++)
        {
            var log = logs[i];
            Console.WriteLine($"[{i + 1}/{logs.Count}] {log.TestName}");

            var userPrompt = BuildTestPrompt(log);
            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, systemPrompt),
                new(ChatRole.User, userPrompt)
            };

            var response = await client.GetResponseAsync(messages, options);

            report.AppendLine($"## {log.TestName}");
            report.AppendLine();
            report.AppendLine(response.Text);
            report.AppendLine();
            report.AppendLine("---");
            report.AppendLine();
        }

        var result = report.ToString();

        if (!string.IsNullOrEmpty(output))
        {
            await File.WriteAllTextAsync(output, result);
            Console.WriteLine($"\nReport saved to {output}");
        }
        else
        {
            Console.WriteLine("\n--- Analysis ---\n");
            Console.WriteLine(result);
        }
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

        foreach (var file in files)
        {
            var content = await File.ReadAllTextAsync(file);
            var items = JsonSerializer.Deserialize<List<FailedTestLog>>(content, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            if (items != null) logs.AddRange(items);
        }

        return logs;
    }

    private static string BuildTestPrompt(FailedTestLog log)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Test: {log.TestName}");
        sb.AppendLine($"Class: {log.ClassName}");
        sb.AppendLine($"Error: {log.ErrorMessage}");
        if (!string.IsNullOrEmpty(log.StackTrace))
            sb.AppendLine($"Stack trace:\n{log.StackTrace}");
        return sb.ToString();
    }
}
