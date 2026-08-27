using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VideoDownLoader.Services;

internal sealed record ApplicationUpdate(
    string Commit,
    DateTimeOffset PublishedAtUtc,
    Uri PackageUrl,
    string Sha256,
    long Size)
{
    public string ShortCommit => Commit[..7];
}

internal sealed partial class ApplicationUpdateService
{
    internal const string DefaultManifestUrl =
        "https://raw.githubusercontent.com/RomanovCopy/VideoDownLoader/updates/manifest.json";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly HttpClient SharedHttpClient = CreateHttpClient();

    private readonly HttpClient _httpClient;
    private readonly string _downloadRoot;
    private readonly string _currentCommit;
    private readonly Uri _manifestUri;

    public ApplicationUpdateService()
        : this(
            SharedHttpClient,
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VideoDownLoader",
                "updates"),
            BuildMetadata.CommitSha,
            new Uri(DefaultManifestUrl))
    {
    }

    internal ApplicationUpdateService(
        HttpClient httpClient,
        string downloadRoot,
        string currentCommit,
        Uri manifestUri)
    {
        _httpClient = httpClient;
        _downloadRoot = downloadRoot;
        _currentCommit = currentCommit;
        _manifestUri = manifestUri;
    }

    public async Task<ApplicationUpdate?> CheckForUpdateAsync(CancellationToken cancellationToken = default)
    {
        if (!CommitRegex().IsMatch(_currentCommit))
        {
            return null;
        }

        var manifestUri = new UriBuilder(_manifestUri)
        {
            Query = $"cache={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}"
        }.Uri;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));

        using var request = new HttpRequestMessage(HttpMethod.Get, manifestUri);
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };

        using var response = await _httpClient.SendAsync(request, timeout.Token);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        var manifest = await JsonSerializer.DeserializeAsync<UpdateManifestDto>(
            stream,
            SerializerOptions,
            timeout.Token);
        var update = ValidateManifest(manifest);

        return string.Equals(update.Commit, _currentCommit, StringComparison.OrdinalIgnoreCase)
            ? null
            : update;
    }

    public async Task<string> DownloadUpdateAsync(
        ApplicationUpdate update,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        CleanOldDownloads(update.Commit);
        var updateDirectory = Path.Combine(_downloadRoot, update.Commit);
        Directory.CreateDirectory(updateDirectory);

        var destination = Path.Combine(updateDirectory, "VideoDownLoader-Setup.exe");
        var temporary = destination + ".download";

        try
        {
            using var response = await _httpClient.GetAsync(
                update.PackageUrl,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            response.EnsureSuccessStatusCode();

            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var target = new FileStream(
                temporary,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                useAsync: true);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            var buffer = new byte[81920];
            long downloaded = 0;
            progress?.Report(0);
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                hash.AppendData(buffer, 0, read);
                downloaded += read;
                progress?.Report(Math.Clamp((double)downloaded / update.Size, 0, 1));
            }

            await target.FlushAsync(cancellationToken);

            if (update.Size > 0 && downloaded != update.Size)
            {
                throw new InvalidDataException(
                    $"Размер пакета не совпадает: ожидалось {update.Size}, получено {downloaded} байт.");
            }

            var actualHash = Convert.ToHexString(hash.GetHashAndReset());
            if (!string.Equals(actualHash, update.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Контрольная сумма пакета обновления не совпадает.");
            }

            await target.DisposeAsync();
            File.Move(temporary, destination, overwrite: true);
            progress?.Report(1);
            return destination;
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }
    }

    public void LaunchInstaller(string packagePath)
    {
        if (!File.Exists(packagePath))
        {
            throw new FileNotFoundException(
                "Установщик обновления отсутствует.",
                packagePath);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = Path.GetFullPath(packagePath),
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(packagePath))!
        };
        startInfo.ArgumentList.Add("/VERYSILENT");
        startInfo.ArgumentList.Add("/SP-");
        startInfo.ArgumentList.Add("/SUPPRESSMSGBOXES");
        startInfo.ArgumentList.Add("/NORESTART");
        startInfo.ArgumentList.Add("/CLOSEAPPLICATIONS");

        _ = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Не удалось запустить обновление.");
    }

    private static ApplicationUpdate ValidateManifest(UpdateManifestDto? manifest)
    {
        if (manifest is null || !CommitRegex().IsMatch(manifest.Commit ?? string.Empty))
        {
            throw new InvalidDataException("Манифест обновления содержит неверный SHA коммита.");
        }

        if (!Sha256Regex().IsMatch(manifest.Sha256 ?? string.Empty))
        {
            throw new InvalidDataException("Манифест обновления содержит неверную контрольную сумму.");
        }

        if (!Uri.TryCreate(manifest.PackageUrl, UriKind.Absolute, out var packageUri) ||
            packageUri.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(packageUri.Host, "raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                packageUri.AbsolutePath,
                "/RomanovCopy/VideoDownLoader/updates/VideoDownLoader-Setup.exe",
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("Манифест обновления содержит недоверенный адрес пакета.");
        }

        if (manifest.Size <= 0)
        {
            throw new InvalidDataException("Манифест обновления содержит неверный размер пакета.");
        }

        return new ApplicationUpdate(
            manifest.Commit!,
            manifest.PublishedAtUtc,
            packageUri,
            manifest.Sha256!,
            manifest.Size);
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(10)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            $"VideoDownLoader/{Assembly.GetExecutingAssembly().GetName().Version}");
        return client;
    }

    private void CleanOldDownloads(string currentCommit)
    {
        try
        {
            if (!Directory.Exists(_downloadRoot))
            {
                return;
            }

            foreach (var directory in Directory.EnumerateDirectories(_downloadRoot))
            {
                if (!string.Equals(
                        Path.GetFileName(directory),
                        currentCommit,
                        StringComparison.OrdinalIgnoreCase))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed record UpdateManifestDto(
        string? Commit,
        DateTimeOffset PublishedAtUtc,
        string? PackageUrl,
        string? Sha256,
        long Size);

    [GeneratedRegex("^[0-9a-f]{40}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CommitRegex();

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Regex();
}
