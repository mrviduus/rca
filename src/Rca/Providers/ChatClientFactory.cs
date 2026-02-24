using Microsoft.Extensions.AI;
using OpenAI.Chat;
using Anthropic.SDK;
using GeminiDotnet;
using GeminiDotnet.Extensions.AI;

namespace Rca.Providers;

public static class ChatClientFactory
{
    public static IChatClient Create(string provider, string? apiKey, string? model, int timeoutSeconds = 600)
    {
        return provider.ToLower() switch
        {
            "openai" => CreateOpenAi(apiKey, model),
            "claude" => CreateClaude(apiKey, model),
            "gemini" => CreateGemini(apiKey, model),
            "ollama" => CreateOllama(model, timeoutSeconds),
            _ => throw new ArgumentException($"Unknown provider: {provider}")
        };
    }

    private static IChatClient CreateOpenAi(string? apiKey, string? model)
    {
        var key = apiKey ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY")
            ?? throw new InvalidOperationException("OPENAI_API_KEY required");
        return new ChatClient(model ?? "gpt-5-nano", key).AsIChatClient();
    }

    private static IChatClient CreateClaude(string? apiKey, string? model)
    {
        var key = apiKey ?? Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")
            ?? throw new InvalidOperationException("ANTHROPIC_API_KEY required");
        return new AnthropicClient(new APIAuthentication(key)).Messages;
    }

    private static IChatClient CreateGemini(string? apiKey, string? model)
    {
        var key = apiKey ?? Environment.GetEnvironmentVariable("GEMINI_API_KEY")
            ?? throw new InvalidOperationException("GEMINI_API_KEY required");
        return new GeminiChatClient(new GeminiClientOptions
        {
            ApiKey = key,
            ModelId = model ?? "gemini-1.5-flash"
        });
    }

    private static IChatClient CreateOllama(string? model, int timeoutSeconds)
    {
        return new OllamaProvider(model ?? "llama3", timeoutSeconds: timeoutSeconds);
    }
}
