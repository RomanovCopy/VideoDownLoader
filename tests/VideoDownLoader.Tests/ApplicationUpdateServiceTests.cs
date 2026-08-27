using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using VideoDownLoader.Services;

namespace VideoDownLoader.Tests;

public sealed class ApplicationUpdateServiceTests
{
    private const string CurrentCommit = "1111111111111111111111111111111111111111";
    private const string NewCommit = "2222222222222222222222222222222222222222";

    [Fact]
    public async Task CheckForUpdateAsync_ReturnsDifferentPublishedCommit()
    {
        var package = Encoding.UTF8.GetBytes("package");
        var manifest = CreateManifest(package, NewCommit);
        using var client = new HttpClient(new StaticResponseHandler(manifest));
        var service = CreateService(client, CurrentCommit);

        var update = await service.CheckForUpdateAsync();

        Assert.NotNull(update);
        Assert.Equal(NewCommit, update.Commit);
    }

    [Fact]
    public async Task CheckForUpdateAsync_IgnoresCurrentCommit()
    {
        var package = Encoding.UTF8.GetBytes("package");
        var manifest = CreateManifest(package, CurrentCommit);
        using var client = new HttpClient(new StaticResponseHandler(manifest));
        var service = CreateService(client, CurrentCommit);

        var update = await service.CheckForUpdateAsync();

        Assert.Null(update);
    }

    [Fact]
    public async Task CheckForUpdateAsync_SkipsLocalDevelopmentBuild()
    {
        using var client = new HttpClient(new ThrowingHandler());
        var service = CreateService(client, "development");

        var update = await service.CheckForUpdateAsync();

        Assert.Null(update);
    }

    [Fact]
    public async Task DownloadUpdateAsync_VerifiesHashAndWritesPackage()
    {
        var root = CreateTemporaryDirectory();
        var package = Encoding.UTF8.GetBytes("verified package contents");
        using var client = new HttpClient(new StaticResponseHandler(package));
        var service = new ApplicationUpdateService(
            client,
            root,
            CurrentCommit,
            new Uri(ApplicationUpdateService.DefaultManifestUrl));
        var update = new ApplicationUpdate(
            NewCommit,
            DateTimeOffset.UtcNow,
            new Uri("https://raw.githubusercontent.com/RomanovCopy/VideoDownLoader/updates/VideoDownLoader-Setup.exe"),
            Convert.ToHexString(SHA256.HashData(package)),
            package.Length);

        try
        {
            var path = await service.DownloadUpdateAsync(update);

            Assert.Equal(package, await File.ReadAllBytesAsync(path));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DownloadUpdateAsync_RejectsWrongHash()
    {
        var root = CreateTemporaryDirectory();
        var package = Encoding.UTF8.GetBytes("corrupted package");
        using var client = new HttpClient(new StaticResponseHandler(package));
        var service = new ApplicationUpdateService(
            client,
            root,
            CurrentCommit,
            new Uri(ApplicationUpdateService.DefaultManifestUrl));
        var update = new ApplicationUpdate(
            NewCommit,
            DateTimeOffset.UtcNow,
            new Uri("https://raw.githubusercontent.com/RomanovCopy/VideoDownLoader/updates/VideoDownLoader-Setup.exe"),
            new string('0', 64),
            package.Length);

        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(
                () => service.DownloadUpdateAsync(update));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CheckForUpdateAsync_RejectsPackageOutsideUpdatesBranch()
    {
        var package = Encoding.UTF8.GetBytes("package");
        var hash = Convert.ToHexString(SHA256.HashData(package));
        var manifest = Encoding.UTF8.GetBytes($$"""
            {
              "commit": "{{NewCommit}}",
              "publishedAtUtc": "2026-08-27T10:00:00Z",
              "packageUrl": "https://raw.githubusercontent.com/SomeoneElse/repository/main/setup.exe",
              "sha256": "{{hash}}",
              "size": {{package.Length}}
            }
            """);
        using var client = new HttpClient(new StaticResponseHandler(manifest));
        var service = CreateService(client, CurrentCommit);

        await Assert.ThrowsAsync<InvalidDataException>(() => service.CheckForUpdateAsync());
    }

    private static ApplicationUpdateService CreateService(HttpClient client, string currentCommit)
    {
        return new ApplicationUpdateService(
            client,
            Path.GetTempPath(),
            currentCommit,
            new Uri(ApplicationUpdateService.DefaultManifestUrl));
    }

    private static byte[] CreateManifest(byte[] package, string commit)
    {
        var hash = Convert.ToHexString(SHA256.HashData(package));
        return Encoding.UTF8.GetBytes($$"""
            {
              "commit": "{{commit}}",
              "publishedAtUtc": "2026-08-27T10:00:00Z",
              "packageUrl": "https://raw.githubusercontent.com/RomanovCopy/VideoDownLoader/updates/VideoDownLoader-Setup.exe",
              "sha256": "{{hash}}",
              "size": {{package.Length}}
            }
            """);
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"VideoDownLoader.Tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class StaticResponseHandler(byte[] content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(content)
            });
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("HTTP-запрос не должен выполняться.");
        }
    }
}
