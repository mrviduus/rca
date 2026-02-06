using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Rca.Providers;

public class OllamaProvider : IChatClient
{
    private readonly HttpClient _http;
    private readonly string _model;
    private readonly string _baseUrl;

    public ChatClientMetadata Metadata => new("ollama", new Uri(_baseUrl), _model);

    public OllamaProvider(string model = "llama3", string baseUrl = "http://localhost:11434")
    {
        _http = new HttpClient();
        _model = model;
        _baseUrl = baseUrl;
    }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken ct = default)
    {
        var prompt = string.Join("\n", messages.Select(m => $"{m.Role}: {m.Text}"));
        var request = new { model = options?.ModelId ?? _model, prompt, stream = false };

        var response = await _http.PostAsJsonAsync($"{_baseUrl}/api/generate", request, ct);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        var text = json.GetProperty("response").GetString() ?? string.Empty;

        return new ChatResponse([new ChatMessage(ChatRole.Assistant, text)]);
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var response = await GetResponseAsync(messages, options, ct);
        yield return new ChatResponseUpdate(ChatRole.Assistant, response.Text);
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() => _http.Dispose();
}
