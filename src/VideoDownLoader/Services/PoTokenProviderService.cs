using System.Net.Http;
using System.Text.Json;
using System.IO;

namespace VideoDownLoader.Services;

public sealed class PoTokenProviderService
{
    private readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(6)
    };

    public async Task<string> CheckAsync(string baseUrl, CancellationToken cancellationToken = default)
    {
        var pingUrl = baseUrl.TrimEnd('/') + "/ping";
        try
        {
            using var response = await _httpClient.GetAsync(pingUrl, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (document.RootElement.TryGetProperty("version", out var version) &&
                version.GetString() is { Length: > 0 } value)
            {
                return value;
            }

            throw new InvalidDataException("Provider ответил, но не сообщил версию.");
        }
        catch (Exception exception) when (
            !cancellationToken.IsCancellationRequested &&
            exception is HttpRequestException or TaskCanceledException or JsonException or InvalidDataException)
        {
            throw new InvalidOperationException(
                $"PO Token provider недоступен по адресу {baseUrl}. Проверьте, что сервер запущен и endpoint /ping отвечает.",
                exception);
        }
    }
}
