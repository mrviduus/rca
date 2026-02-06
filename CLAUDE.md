# CLAUDE.md

## Build & Test

```bash
dotnet build
dotnet run --project src/Rca -- analyze <path> [options]
```

## Pack

```bash
dotnet pack src/Rca/Rca.csproj -c Release -o ./artifacts
```

## Architecture

- `src/Rca/` - CLI tool
  - `Commands/AnalyzeCommand.cs` - main command
  - `Providers/ChatClientFactory.cs` - creates IChatClient
  - `Providers/OllamaProvider.cs` - Ollama IChatClient impl
  - `Prompts/AnalyzePrompt.txt` - system prompt

## Providers

Uses `Microsoft.Extensions.AI` unified interface:
- OpenAI via `Microsoft.Extensions.AI.OpenAI`
- Claude via `Anthropic.SDK` (implements IChatClient)
- Gemini via `GeminiDotnet.Extensions.AI`
- Ollama via custom `OllamaProvider`
