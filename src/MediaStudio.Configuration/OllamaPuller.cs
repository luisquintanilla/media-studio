using System.Net.Http.Json;
using System.Text.Json;

namespace MediaStudio.Configuration;

public sealed record OllamaPullProgress(string Model, string Status, long Completed, long Total)
{
    public double Fraction => Total <= 0 ? 0 : Math.Clamp((double)Completed / Total, 0, 1);
}

/// <summary>Pulls Ollama models through the local REST API (<c>POST /api/pull</c>), streaming progress.</summary>
public sealed class OllamaPuller(HttpClient? httpClient = null)
{
    public static readonly Uri DefaultApi = new("http://localhost:11434/");
    private readonly HttpClient _http = httpClient ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };

    public async Task PullAsync(string model, IProgress<OllamaPullProgress>? progress = null, Uri? api = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        var baseUri = api ?? DefaultApi;
        if (!baseUri.IsLoopback) throw new ArgumentException("Ollama pulls are only issued to a loopback endpoint.", nameof(api));
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, "api/pull")) {
            Content = JsonContent.Create(new { model, stream = true })
        };
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"ollama pull {model} failed: HTTP {(int)response.StatusCode}. Is Ollama running? (winget install Ollama.Ollama)");
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false));
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (line.Length == 0) continue;
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.TryGetProperty("error", out var error))
                throw new InvalidOperationException($"ollama pull {model} failed: {error.GetString()}");
            var status = root.TryGetProperty("status", out var s) ? s.GetString() ?? "" : "";
            var completed = root.TryGetProperty("completed", out var c) && c.TryGetInt64(out var cv) ? cv : 0;
            var total = root.TryGetProperty("total", out var t) && t.TryGetInt64(out var tv) ? tv : 0;
            progress?.Report(new(model, status, completed, total));
        }
    }
}
