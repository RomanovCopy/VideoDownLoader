using System.IO;
using System.Net.Http;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Diagnostics;
using System.Text.Json;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using VideoDownLoader.Models;

namespace VideoDownLoader.Services;

public sealed record ToolPaths(string? YtDlp, string? Ffmpeg, string? Deno)
{
    public bool IsReady => YtDlp is not null && Ffmpeg is not null && Deno is not null;
}

public sealed record ToolUpdateInfo(
    string CurrentYtDlpVersion,
    string LatestYtDlpVersion,
    DateTimeOffset LocalFfmpegTimestamp,
    DateTimeOffset? LatestFfmpegTimestamp,
    string CurrentDenoVersion,
    string LatestDenoVersion,
    bool YtDlpUpdateAvailable,
    bool FfmpegUpdateAvailable,
    bool DenoUpdateAvailable)
{
    public bool IsUpdateAvailable => YtDlpUpdateAvailable || FfmpegUpdateAvailable || DenoUpdateAvailable;
}

public sealed class DependencyManager
{
    private const string StableYtDlpReleaseBase =
        "https://github.com/yt-dlp/yt-dlp/releases/latest/download";

    private const string NightlyYtDlpReleaseBase =
        "https://github.com/yt-dlp/yt-dlp-nightly-builds/releases/latest/download";

    private const string LatestNightlyYtDlpReleaseApi =
        "https://api.github.com/repos/yt-dlp/yt-dlp-nightly-builds/releases/latest";

    private const string LatestStableYtDlpReleaseApi =
        "https://api.github.com/repos/yt-dlp/yt-dlp/releases/latest";

    private const string LatestFfmpegReleaseApi =
        "https://api.github.com/repos/yt-dlp/FFmpeg-Builds/releases/tags/latest";

    private const string LatestDenoReleaseApi =
        "https://api.github.com/repos/denoland/deno/releases/latest";

    private readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromMinutes(15)
    };

    public DependencyManager()
    {
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("VideoDownLoader/0.1");
        _httpClient.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    public string ToolsDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VideoDownLoader",
        "tools");

    public ToolPaths FindTools()
    {
        var ytDlp = FindExecutable("yt-dlp.exe", Path.Combine(ToolsDirectory, "yt-dlp.exe"));
        var ffmpeg = FindExecutable("ffmpeg.exe", Path.Combine(ToolsDirectory, "ffmpeg.exe"));
        var deno = FindExecutable("deno.exe", Path.Combine(ToolsDirectory, "deno.exe"));
        return new ToolPaths(ytDlp, ffmpeg, deno);
    }

    public async Task<ToolUpdateInfo> CheckForUpdatesAsync(
        ToolPaths tools,
        YtDlpChannel channel = YtDlpChannel.Nightly,
        CancellationToken cancellationToken = default)
    {
        if (!tools.IsReady)
        {
            throw new InvalidOperationException("Инструменты ещё не установлены.");
        }

        var currentVersionTask = GetExecutableVersionAsync(tools.YtDlp!, cancellationToken);
        var latestYtDlpTask = GetLatestYtDlpVersionAsync(channel, cancellationToken);
        var latestFfmpegTask = GetLatestFfmpegTimestampAsync(cancellationToken);
        var currentDenoVersionTask = GetDenoVersionAsync(tools.Deno!, cancellationToken);
        var latestDenoVersionTask = GetLatestDenoVersionAsync(cancellationToken);
        await Task.WhenAll(
            currentVersionTask,
            latestYtDlpTask,
            latestFfmpegTask,
            currentDenoVersionTask,
            latestDenoVersionTask);

        var currentVersion = await currentVersionTask;
        var latestVersion = await latestYtDlpTask;
        var latestFfmpegTimestamp = await latestFfmpegTask;
        var currentDenoVersion = await currentDenoVersionTask;
        var latestDenoVersion = await latestDenoVersionTask;
        var localFfmpegTimestamp = new DateTimeOffset(File.GetLastWriteTimeUtc(tools.Ffmpeg!), TimeSpan.Zero);

        return new ToolUpdateInfo(
            currentVersion,
            latestVersion,
            localFfmpegTimestamp,
            latestFfmpegTimestamp,
            currentDenoVersion,
            latestDenoVersion,
            IsYtDlpUpdateAvailable(currentVersion, latestVersion),
            latestFfmpegTimestamp is { } remote && remote > localFfmpegTimestamp.AddMinutes(1),
            IsSemanticVersionUpdateAvailable(currentDenoVersion, latestDenoVersion));
    }

    public async Task<ToolPaths> InstallAsync(
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default,
        YtDlpChannel channel = YtDlpChannel.Nightly)
    {
        Directory.CreateDirectory(ToolsDirectory);
        var stagingDirectory = Path.Combine(Path.GetTempPath(), $"vdl-tools-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingDirectory);
        var ytDlpPath = Path.Combine(stagingDirectory, "yt-dlp.exe");
        var ffmpegPath = Path.Combine(stagingDirectory, "ffmpeg.exe");
        var ffprobePath = Path.Combine(stagingDirectory, "ffprobe.exe");
        var archivePath = Path.Combine(stagingDirectory, "ffmpeg.zip");
        var denoArchivePath = Path.Combine(stagingDirectory, "deno.zip");
        var denoChecksumPath = Path.Combine(stagingDirectory, "deno.zip.sha256sum");
        var denoPath = Path.Combine(stagingDirectory, "deno.exe");

        try
        {
            progress?.Report("Загрузка yt-dlp…");
            await DownloadFileAsync(GetYtDlpReleaseBase(channel) + "/yt-dlp.exe", ytDlpPath, cancellationToken);
            progress?.Report("Проверка SHA-256 yt-dlp…");
            await VerifyYtDlpChecksumAsync(ytDlpPath, channel, cancellationToken);

            progress?.Report("Загрузка FFmpeg…");
            var ffmpegAsset = await GetLatestFfmpegAssetAsync(cancellationToken);
            await DownloadFileAsync(ffmpegAsset.DownloadUrl, archivePath, cancellationToken);
            progress?.Report("Проверка SHA-256 FFmpeg…");
            await VerifySha256Async(archivePath, ffmpegAsset.Sha256, "FFmpeg", cancellationToken);
            progress?.Report("Распаковка FFmpeg…");
            ExtractExecutable(archivePath, "ffmpeg.exe", ffmpegPath);
            ExtractExecutable(archivePath, "ffprobe.exe", ffprobePath);

            var denoAssetName = GetDenoAssetName();
            var denoReleaseBaseUrl = $"https://github.com/denoland/deno/releases/latest/download/{denoAssetName}";
            progress?.Report("Загрузка Deno…");
            await DownloadFileAsync(denoReleaseBaseUrl, denoArchivePath, cancellationToken);
            await DownloadFileAsync(denoReleaseBaseUrl + ".sha256sum", denoChecksumPath, cancellationToken);
            progress?.Report("Проверка SHA-256 Deno…");
            await VerifyChecksumFileAsync(denoArchivePath, denoChecksumPath, denoAssetName, cancellationToken);
            progress?.Report("Распаковка Deno…");
            ExtractExecutable(denoArchivePath, "deno.exe", denoPath);

            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report("Активация инструментов…");
            CommitExecutable(ytDlpPath, Path.Combine(ToolsDirectory, "yt-dlp.exe"));
            CommitExecutable(ffmpegPath, Path.Combine(ToolsDirectory, "ffmpeg.exe"));
            CommitExecutable(ffprobePath, Path.Combine(ToolsDirectory, "ffprobe.exe"));
            CommitExecutable(denoPath, Path.Combine(ToolsDirectory, "deno.exe"));
            File.SetLastWriteTimeUtc(Path.Combine(ToolsDirectory, "ffmpeg.exe"), ffmpegAsset.UpdatedAt.UtcDateTime);
            File.SetLastWriteTimeUtc(Path.Combine(ToolsDirectory, "ffprobe.exe"), ffmpegAsset.UpdatedAt.UtcDateTime);
        }
        finally
        {
            try
            {
                DeleteStagingDirectory(stagingDirectory);
            }
            catch (IOException)
            {
                // Временные файлы удалит системная очистка.
            }
            catch (UnauthorizedAccessException)
            {
                // Временные файлы удалит системная очистка.
            }
        }

        progress?.Report("Инструменты установлены.");
        return FindTools();
    }

    public async Task<ToolPaths> UpdateYouTubeComponentsAsync(
        YtDlpChannel channel,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(ToolsDirectory);
        var stagingDirectory = Path.Combine(Path.GetTempPath(), $"vdl-tools-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingDirectory);
        var ytDlpPath = Path.Combine(stagingDirectory, "yt-dlp.exe");
        var denoArchivePath = Path.Combine(stagingDirectory, "deno.zip");
        var denoChecksumPath = Path.Combine(stagingDirectory, "deno.zip.sha256sum");
        var denoPath = Path.Combine(stagingDirectory, "deno.exe");

        try
        {
            progress?.Report($"Загрузка yt-dlp ({ChannelDisplayName(channel)})…");
            await DownloadFileAsync(GetYtDlpReleaseBase(channel) + "/yt-dlp.exe", ytDlpPath, cancellationToken);
            await VerifyYtDlpChecksumAsync(ytDlpPath, channel, cancellationToken);

            var denoAssetName = GetDenoAssetName();
            var denoReleaseBaseUrl = $"https://github.com/denoland/deno/releases/latest/download/{denoAssetName}";
            progress?.Report("Загрузка Deno…");
            await DownloadFileAsync(denoReleaseBaseUrl, denoArchivePath, cancellationToken);
            await DownloadFileAsync(denoReleaseBaseUrl + ".sha256sum", denoChecksumPath, cancellationToken);
            await VerifyChecksumFileAsync(denoArchivePath, denoChecksumPath, denoAssetName, cancellationToken);
            ExtractExecutable(denoArchivePath, "deno.exe", denoPath);

            cancellationToken.ThrowIfCancellationRequested();
            CommitExecutable(ytDlpPath, Path.Combine(ToolsDirectory, "yt-dlp.exe"));
            CommitExecutable(denoPath, Path.Combine(ToolsDirectory, "deno.exe"));
        }
        finally
        {
            try
            {
                DeleteStagingDirectory(stagingDirectory);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Временные файлы удалит системная очистка.
            }
        }

        progress?.Report("YouTube-компоненты обновлены.");
        return FindTools();
    }

    private async Task VerifyYtDlpChecksumAsync(
        string executablePath,
        YtDlpChannel channel,
        CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(
            GetYtDlpReleaseBase(channel) + "/SHA2-256SUMS",
            cancellationToken);
        response.EnsureSuccessStatusCode();
        var checksumFile = await response.Content.ReadAsStringAsync(cancellationToken);
        var expected = checksumFile.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .Where(parts => parts.Length >= 2)
            .FirstOrDefault(parts => parts[^1].TrimStart('*').Equals("yt-dlp.exe", StringComparison.OrdinalIgnoreCase))?
            .FirstOrDefault();

        if (string.IsNullOrWhiteSpace(expected))
        {
            throw new InvalidDataException("В официальном файле не найдена контрольная сумма yt-dlp.exe.");
        }

        await using var stream = File.OpenRead(executablePath);
        var actualBytes = await SHA256.HashDataAsync(stream, cancellationToken);
        var actual = Convert.ToHexString(actualBytes);
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Контрольная сумма загруженного yt-dlp.exe не совпадает с официальной.");
        }
    }

    private static async Task VerifyChecksumFileAsync(
        string filePath,
        string checksumPath,
        string expectedFileName,
        CancellationToken cancellationToken)
    {
        var checksumFile = await File.ReadAllTextAsync(checksumPath, cancellationToken);
        var expected = ParsePublishedChecksum(checksumFile, expectedFileName);
        if (expected is null)
        {
            throw new InvalidDataException("Официальный файл контрольной суммы Deno имеет некорректный формат.");
        }

        await using var stream = File.OpenRead(filePath);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Контрольная сумма загруженного Deno не совпадает с официальной.");
        }
    }

    private static async Task VerifySha256Async(
        string filePath,
        string expected,
        string componentName,
        CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(filePath);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Контрольная сумма загруженного {componentName} не совпадает с опубликованной GitHub.");
        }
    }

    internal static string? ParsePublishedChecksum(string content, string expectedFileName)
    {
        var standardParts = content.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (standardParts.Length >= 2 &&
            standardParts[0].Length == 64 &&
            standardParts[^1].TrimStart('*').Equals(expectedFileName, StringComparison.OrdinalIgnoreCase))
        {
            return standardParts[0];
        }

        var hashMatch = Regex.Match(
            content,
            @"(?im)^Hash\s*:\s*(?<hash>[a-f0-9]{64})\s*$",
            RegexOptions.CultureInvariant);
        var pathMatch = Regex.Match(
            content,
            @"(?im)^Path\s*:\s*(?<path>.+?)\s*$",
            RegexOptions.CultureInvariant);
        return hashMatch.Success &&
               pathMatch.Success &&
               Path.GetFileName(pathMatch.Groups["path"].Value).Equals(
                   expectedFileName,
                   StringComparison.OrdinalIgnoreCase)
            ? hashMatch.Groups["hash"].Value
            : null;
    }

    private static void CommitExecutable(string stagedPath, string destination)
    {
        File.Move(stagedPath, destination, overwrite: true);
    }

    private static void DeleteStagingDirectory(string stagingDirectory)
    {
        var fullStagingPath = Path.GetFullPath(stagingDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var tempRoot = Path.GetFullPath(Path.GetTempPath())
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var parent = Directory.GetParent(fullStagingPath)?.FullName
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(fullStagingPath);

        if (string.Equals(parent, tempRoot, StringComparison.OrdinalIgnoreCase) &&
            name.StartsWith("vdl-tools-", StringComparison.Ordinal) &&
            Directory.Exists(fullStagingPath))
        {
            Directory.Delete(fullStagingPath, recursive: true);
        }
    }

    private async Task<DateTimeOffset?> DownloadFileAsync(
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
            var lastModified = response.Content.Headers.LastModified ?? response.Headers.Date;

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
            return lastModified;
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private async Task<string> GetLatestYtDlpVersionAsync(
        YtDlpChannel channel,
        CancellationToken cancellationToken)
    {
        var api = channel == YtDlpChannel.Stable
            ? LatestStableYtDlpReleaseApi
            : LatestNightlyYtDlpReleaseApi;
        using var document = await GetGitHubJsonAsync(api, cancellationToken);
        if (document.RootElement.TryGetProperty("tag_name", out var tag) &&
            tag.GetString() is { Length: > 0 } value)
        {
            return value;
        }

        throw new InvalidDataException("GitHub не вернул версию последнего выпуска yt-dlp.");
    }

    private async Task<DateTimeOffset?> GetLatestFfmpegTimestampAsync(CancellationToken cancellationToken)
    {
        return (await GetLatestFfmpegAssetAsync(cancellationToken)).UpdatedAt;
    }

    private async Task<FfmpegReleaseAsset> GetLatestFfmpegAssetAsync(CancellationToken cancellationToken)
    {
        using var document = await GetGitHubJsonAsync(LatestFfmpegReleaseApi, cancellationToken);
        if (!document.RootElement.TryGetProperty("assets", out var assets) ||
            assets.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("GitHub не вернул список файлов FFmpeg.");
        }

        foreach (var asset in assets.EnumerateArray())
        {
            if (asset.TryGetProperty("name", out var name) &&
                name.GetString()?.Equals("ffmpeg-master-latest-win64-gpl.zip", StringComparison.OrdinalIgnoreCase) == true &&
                asset.TryGetProperty("updated_at", out var updatedAt) &&
                updatedAt.ValueKind == JsonValueKind.String &&
                updatedAt.TryGetDateTimeOffset(out var timestamp) &&
                asset.TryGetProperty("browser_download_url", out var downloadUrl) &&
                downloadUrl.GetString() is { Length: > 0 } url &&
                asset.TryGetProperty("digest", out var digest) &&
                ParseSha256Digest(digest.GetString()) is { } sha256)
            {
                return new FfmpegReleaseAsset(url, timestamp, sha256);
            }
        }

        throw new InvalidDataException("GitHub не вернул URL или SHA-256 официальной сборки FFmpeg.");
    }

    internal static string? ParseSha256Digest(string? digest) =>
        digest?.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) == true &&
        digest.Length == 71 &&
        Regex.IsMatch(digest[7..], "^[a-f0-9]{64}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            ? digest[7..]
            : null;

    private async Task<string> GetLatestDenoVersionAsync(CancellationToken cancellationToken)
    {
        using var document = await GetGitHubJsonAsync(LatestDenoReleaseApi, cancellationToken);
        if (document.RootElement.TryGetProperty("tag_name", out var tag) &&
            tag.GetString() is { Length: > 0 } value)
        {
            return NormalizeVersion(value);
        }

        throw new InvalidDataException("GitHub не вернул версию последнего выпуска Deno.");
    }

    private async Task<JsonDocument> GetGitHubJsonAsync(string url, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(
            url,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    private static async Task<string> GetExecutableVersionAsync(
        string executablePath,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("--version");

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException("Не удалось проверить версию yt-dlp.");
        }

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var output = (await outputTask).Trim();
        var error = (await errorTask).Trim();
        if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error)
                ? "yt-dlp не сообщил свою версию."
                : error);
        }

        return output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)[0].Trim();
    }

    private static string NormalizeVersion(string version) => version.Trim().TrimStart('v', 'V');

    internal static bool IsYtDlpUpdateAvailable(string currentVersion, string latestVersion)
    {
        var current = NormalizeVersion(currentVersion);
        var latest = NormalizeVersion(latestVersion);
        if (current.Equals(latest, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (Version.TryParse(current, out var currentParsed) && Version.TryParse(latest, out var latestParsed))
        {
            return currentParsed < latestParsed;
        }

        if (TryGetReleaseDate(current, out var currentDate) && TryGetReleaseDate(latest, out var latestDate))
        {
            return currentDate < latestDate;
        }

        return true;
    }

    internal static bool IsSemanticVersionUpdateAvailable(string currentVersion, string latestVersion) =>
        Version.TryParse(NormalizeVersion(currentVersion), out var current) &&
        Version.TryParse(NormalizeVersion(latestVersion), out var latest)
            ? current < latest
            : !NormalizeVersion(currentVersion).Equals(NormalizeVersion(latestVersion), StringComparison.OrdinalIgnoreCase);

    private static async Task<string> GetDenoVersionAsync(
        string executablePath,
        CancellationToken cancellationToken)
    {
        var output = await GetExecutableOutputAsync(executablePath, "--version", cancellationToken);
        var firstLine = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        var parts = firstLine?.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts is not { Length: >= 2 } || !parts[0].Equals("deno", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Deno не сообщил свою версию.");
        }

        return parts[1];
    }

    private static async Task<string> GetExecutableOutputAsync(
        string executablePath,
        string argument,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException($"Не удалось запустить {Path.GetFileName(executablePath)}.");
        }

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var output = (await outputTask).Trim();
        var error = (await errorTask).Trim();
        if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error)
                ? $"{Path.GetFileName(executablePath)} не сообщил версию."
                : error);
        }

        return output;
    }

    private static string GetDenoAssetName() => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64 => "deno-x86_64-pc-windows-msvc.zip",
        Architecture.Arm64 => "deno-aarch64-pc-windows-msvc.zip",
        _ => throw new PlatformNotSupportedException(
            $"Deno не поддерживается для архитектуры {RuntimeInformation.ProcessArchitecture}.")
    };

    private static string GetYtDlpReleaseBase(YtDlpChannel channel) =>
        channel == YtDlpChannel.Stable ? StableYtDlpReleaseBase : NightlyYtDlpReleaseBase;

    public static string ChannelDisplayName(YtDlpChannel channel) =>
        channel == YtDlpChannel.Stable ? "stable" : "nightly";

    private static bool TryGetReleaseDate(string version, out DateOnly date)
    {
        var datePart = version.Length >= 10 ? version[..10] : version;
        return DateOnly.TryParseExact(
            datePart,
            "yyyy.MM.dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out date);
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

    private sealed record FfmpegReleaseAsset(
        string DownloadUrl,
        DateTimeOffset UpdatedAt,
        string Sha256);
}
