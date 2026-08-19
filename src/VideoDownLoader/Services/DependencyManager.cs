using System.IO;
using System.Net.Http;
using System.IO.Compression;

namespace VideoDownLoader.Services;

public sealed record ToolPaths(string? YtDlp, string? Ffmpeg)
{
    public bool IsReady => YtDlp is not null && Ffmpeg is not null;
}

public sealed class DependencyManager
{
    private const string YtDlpUrl =
        "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";

    private const string FfmpegUrl =
        "https://github.com/yt-dlp/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip";

    private readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromMinutes(15)
    };

    public DependencyManager()
    {
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("VideoDownLoader/0.1");
    }

    public string ToolsDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VideoDownLoader",
        "tools");

    public ToolPaths FindTools()
    {
        var ytDlp = FindExecutable("yt-dlp.exe", Path.Combine(ToolsDirectory, "yt-dlp.exe"));
        var ffmpeg = FindExecutable("ffmpeg.exe", Path.Combine(ToolsDirectory, "ffmpeg.exe"));
        return new ToolPaths(ytDlp, ffmpeg);
    }

    public async Task<ToolPaths> InstallAsync(
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(ToolsDirectory);

        progress?.Report("Загрузка yt-dlp…");
        await DownloadFileAsync(
            YtDlpUrl,
            Path.Combine(ToolsDirectory, "yt-dlp.exe"),
            cancellationToken);

        progress?.Report("Загрузка FFmpeg…");
        var archivePath = Path.Combine(Path.GetTempPath(), $"vdl-ffmpeg-{Guid.NewGuid():N}.zip");

        try
        {
            await DownloadFileAsync(FfmpegUrl, archivePath, cancellationToken);
            progress?.Report("Распаковка FFmpeg…");
            ExtractExecutable(archivePath, "ffmpeg.exe", Path.Combine(ToolsDirectory, "ffmpeg.exe"));
            ExtractExecutable(archivePath, "ffprobe.exe", Path.Combine(ToolsDirectory, "ffprobe.exe"));
        }
        finally
        {
            if (File.Exists(archivePath))
            {
                File.Delete(archivePath);
            }
        }

        progress?.Report("Инструменты установлены.");
        return FindTools();
    }

    private async Task DownloadFileAsync(
        string url,
        string destination,
        CancellationToken cancellationToken)
    {
        var temporaryPath = destination + ".download";

        try
        {
            using var response = await _httpClient.GetAsync(
                url,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            response.EnsureSuccessStatusCode();

            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var target = new FileStream(
                temporaryPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                81920,
                useAsync: true))
            {
                await source.CopyToAsync(target, cancellationToken);
                await target.FlushAsync(cancellationToken);
            }

            // File.Move требует, чтобы временный файл уже был закрыт.
            File.Move(temporaryPath, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static void ExtractExecutable(string archivePath, string fileName, string destination)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        var entry = archive.Entries.FirstOrDefault(candidate =>
            string.Equals(Path.GetFileName(candidate.FullName), fileName, StringComparison.OrdinalIgnoreCase));

        if (entry is null)
        {
            throw new InvalidDataException($"В архиве FFmpeg не найден {fileName}.");
        }

        var temporaryPath = destination + ".download";
        entry.ExtractToFile(temporaryPath, overwrite: true);
        File.Move(temporaryPath, destination, overwrite: true);
    }

    private static string? FindExecutable(string executableName, string bundledPath)
    {
        if (File.Exists(bundledPath))
        {
            return bundledPath;
        }

        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(directory.Trim(), executableName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
            {
                // Пропускаем некорректный элемент PATH.
            }
        }

        return null;
    }
}
