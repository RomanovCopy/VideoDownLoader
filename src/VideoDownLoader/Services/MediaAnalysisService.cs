using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using VideoDownLoader.Models;

namespace VideoDownLoader.Services;

public sealed class MediaAnalysisService
{
    public async Task<MediaAnalysis> AnalyzeAsync(
        ToolPaths tools,
        string url,
        CookieBrowser cookieBrowser,
        string? poTokenProviderUrl = null,
        CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = tools.YtDlp!,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        Add(startInfo,
            "--ignore-config",
            "--dump-single-json",
            "--js-runtimes", $"deno:{tools.Deno}",
            "--skip-download",
            "--playlist-items", "1");
        if (cookieBrowser != CookieBrowser.None)
        {
            Add(startInfo, "--cookies-from-browser", cookieBrowser.ToString().ToLowerInvariant());
        }

        if (Uri.TryCreate(poTokenProviderUrl, UriKind.Absolute, out var providerUri) &&
            (providerUri.Scheme == Uri.UriSchemeHttp || providerUri.Scheme == Uri.UriSchemeHttps))
        {
            Add(startInfo,
                "--plugin-dirs", Path.Combine(Path.GetDirectoryName(tools.YtDlp!)!, "plugins"),
                "--extractor-args", $"youtubepot-bgutilhttp:base_url={providerUri.GetLeftPart(UriPartial.Authority)}",
                "--extractor-args", "youtube:player_client=mweb");
        }

        Add(startInfo, url);

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException("Не удалось запустить анализ ссылки.");
        }

        using var cancellationRegistration = cancellationToken.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Процесс уже завершён.
            }
        });

        var standardOutputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardErrorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var output = await standardOutputTask;
        var error = await standardErrorTask;

        if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
        {
            if (IsDrmError(error))
            {
                return new MediaAnalysis(
                    url,
                    "Защищённый источник",
                    null,
                    null,
                    null,
                    false,
                    false,
                    null,
                    true,
                    false,
                    null,
                    []);
            }

            var failure = YtDlpDiagnostics.Classify(error.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
            throw new YtDlpException(process.ExitCode, failure);
        }

        try
        {
            using var document = JsonDocument.Parse(output);
            return Parse(url, document.RootElement);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("yt-dlp вернул некорректные сведения о ссылке.", exception);
        }
    }

    internal static MediaAnalysis Parse(string url, JsonElement root)
    {
        var type = GetString(root, "_type");
        var entries = root.TryGetProperty("entries", out var entriesElement) &&
                      entriesElement.ValueKind == JsonValueKind.Array
            ? entriesElement.GetArrayLength()
            : (int?)null;
        var formats = ParseFormats(root);
        var rootHasDrm = GetBoolean(root, "has_drm");
        var hasDrm = rootHasDrm || formats.Any(format => format.HasDrm);
        var hasUnprotectedFormat = formats.Any(format => !format.HasDrm);
        var isDownloadable = !rootHasDrm && (formats.Count == 0 || hasUnprotectedFormat);

        return new MediaAnalysis(
            url,
            GetString(root, "title") ?? "Без названия",
            GetString(root, "channel") ?? GetString(root, "uploader"),
            GetDouble(root, "duration") is { } duration ? TimeSpan.FromSeconds(duration) : null,
            GetString(root, "thumbnail"),
            GetBoolean(root, "is_live") || string.Equals(GetString(root, "live_status"), "is_live", StringComparison.Ordinal),
            string.Equals(type, "playlist", StringComparison.OrdinalIgnoreCase) || entries is not null,
            GetInt32(root, "playlist_count") ?? entries,
            hasDrm,
            isDownloadable,
            GetInt64(root, "filesize_approx") ?? GetInt64(root, "filesize"),
            formats);
    }

    internal static bool IsDrmError(string error) =>
        error.Contains("DRM protected", StringComparison.OrdinalIgnoreCase) ||
        error.Contains("DRM-protected", StringComparison.OrdinalIgnoreCase) ||
        error.Contains("protected by DRM", StringComparison.OrdinalIgnoreCase) ||
        error.Contains("This video is DRM", StringComparison.OrdinalIgnoreCase) ||
        error.Contains("DRM protection", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<MediaFormat> ParseFormats(JsonElement root)
    {
        if (!root.TryGetProperty("formats", out var formatsElement) || formatsElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var formats = new List<MediaFormat>();
        foreach (var element in formatsElement.EnumerateArray())
        {
            var id = GetString(element, "format_id");
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            var videoCodec = GetString(element, "vcodec");
            var audioCodec = GetString(element, "acodec");
            var hasVideo = !string.IsNullOrWhiteSpace(videoCodec) && videoCodec != "none";
            var hasAudio = !string.IsNullOrWhiteSpace(audioCodec) && audioCodec != "none";
            if (!hasVideo && !hasAudio)
            {
                continue;
            }

            var width = GetInt32(element, "width");
            var height = GetInt32(element, "height");
            var fps = GetDouble(element, "fps");
            var size = GetInt64(element, "filesize") ?? GetInt64(element, "filesize_approx");
            var hasDrm = GetBoolean(element, "has_drm");
            var resolution = height is not null ? $"{height}p" : hasAudio ? "аудио" : "видео";
            var fpsText = fps is > 30 ? $"{fps:0.#} fps" : null;
            var codecs = string.Join(" + ", new[]
            {
                hasVideo ? ShortCodec(videoCodec) : null,
                hasAudio ? ShortCodec(audioCodec) : null
            }.Where(value => !string.IsNullOrWhiteSpace(value)));
            var sizeText = size is not null ? FormatBytes(size.Value) : "размер неизвестен";
            var details = string.Join(" · ", new[] { resolution, fpsText, codecs, sizeText }
                .Where(value => !string.IsNullOrWhiteSpace(value)));
            var display = $"[{id}] {details}";

            formats.Add(new MediaFormat(
                id,
                display,
                width,
                height,
                fps,
                videoCodec,
                audioCodec,
                size,
                hasVideo,
                hasAudio,
                hasDrm));
        }

        return formats
            .OrderByDescending(format => format.HasVideo)
            .ThenByDescending(format => format.Height ?? 0)
            .ThenByDescending(format => format.FramesPerSecond ?? 0)
            .ThenByDescending(format => format.FileSize ?? 0)
            .ToList();
    }

    private static string GetUsefulError(string error)
    {
        var usefulLines = error.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.Contains("ERROR", StringComparison.OrdinalIgnoreCase))
            .TakeLast(3)
            .ToArray();
        return usefulLines.Length > 0
            ? string.Join(Environment.NewLine, usefulLines)
            : "Не удалось получить сведения по этой ссылке.";
    }

    private static string? ShortCodec(string? codec)
    {
        if (string.IsNullOrWhiteSpace(codec) || codec == "none")
        {
            return null;
        }

        var separator = codec.IndexOf('.');
        return separator > 0 ? codec[..separator] : codec;
    }

    private static string FormatBytes(long bytes)
    {
        var value = (double)bytes;
        string[] units = ["Б", "КБ", "МБ", "ГБ", "ТБ"];
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return string.Create(CultureInfo.CurrentCulture, $"{value:0.#} {units[unit]}");
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static int? GetInt32(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property) &&
        property.ValueKind == JsonValueKind.Number &&
        property.TryGetInt32(out var value)
            ? value
            : null;

    private static long? GetInt64(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property) &&
        property.ValueKind == JsonValueKind.Number &&
        property.TryGetInt64(out var value)
            ? value
            : null;

    private static double? GetDouble(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property) &&
        property.ValueKind == JsonValueKind.Number &&
        property.TryGetDouble(out var value)
            ? value
            : null;

    private static bool GetBoolean(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.True;

    private static void Add(ProcessStartInfo startInfo, params string[] arguments)
    {
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
    }
}
