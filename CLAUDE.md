# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build & Run

```bash
dotnet build
dotnet run --project src/Rca -- analyze <path> [options]
dotnet run --project src/Rca -- analyze ./samples/failed-tests.json --provider openai
```

## Test

No test project exists yet. The CI pipeline runs `dotnet test` but there are no test files.

## Pack & Install

```bash
dotnet pack src/Rca/Rca.csproj -c Release -o ./artifacts
dotnet tool install --global --add-source ./artifacts rca-cli
```

## Architecture

This is a .NET 10 CLI tool (global dotnet tool) that analyzes failed test logs using LLM providers. It uses `System.CommandLine` for CLI parsing.

**Entry point:** `src/Rca/Program.cs` — registers the `AnalyzeCommand` on a `RootCommand`.

**Core flow** (`Commands/AnalyzeCommand.cs`):
1. Loads JSON test failure logs (xUnitOTel format — single object per file, not arrays)
2. Creates an `IChatClient` via `ChatClientFactory`
3. Analyzes each failure in parallel using a `SemaphoreSlim` (default 3 concurrent)
4. Retries with exponential backoff (1s, 2s, 4s — max 3 attempts)
5. Writes per-test Markdown reports + a summary `index-{timestamp}.md`
6. Exit codes: 0 = all success, 1 = partial failure, 2 = complete failure

**Provider abstraction** (`Providers/ChatClientFactory.cs`):
All providers implement `Microsoft.Extensions.AI.IChatClient`. Factory selects provider by name:
- `openai` → `OpenAI.Chat.ChatClient` (default model: gpt-4o, env: `OPENAI_API_KEY`)
- `claude` → `AnthropicClient.Messages` from Anthropic.SDK (env: `ANTHROPIC_API_KEY`)
- `gemini` → `GeminiChatClient` (default model: gemini-1.5-flash, env: `GEMINI_API_KEY`)
- `ollama` → Custom `OllamaProvider` (default model: llama3, endpoint: localhost:11434)

**Data model** (`Models/FailedTestLog.cs`):
Records using xUnitOTel JSON format — `FailedTestLog` contains `FailureInfo` (messages + stack traces) and optional `LogEntry` list.

**System prompt** (`Prompts/AnalyzePrompt.txt`):
Embedded as content file (CopyToOutputDirectory). Loaded at runtime from assembly location.

## Key Dependencies

Package versions are centrally managed in `Directory.Packages.props`:
- `System.CommandLine` 2.0.0-beta4 — CLI framework (still prerelease)
- `Microsoft.Extensions.AI` 9.7.1 — unified LLM interface
- `Anthropic.SDK` 5.4.3 — Claude provider (implements IChatClient directly)

## CI/CD

GitHub Actions workflow (`.github/workflows/ci-cd.yml`): build → test → pack → publish to NuGet → create GitHub release on tags (`v*`). Targets .NET 10.
