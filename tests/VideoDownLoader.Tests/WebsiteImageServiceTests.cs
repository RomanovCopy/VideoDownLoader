using System.Net;
using System.Net.Http.Headers;
using System.Text;
using VideoDownLoader.Models;
using VideoDownLoader.Services;

namespace VideoDownLoader.Tests;

public sealed class WebsiteImageServiceTests
{
    [Fact]
    public void ParseImages_ResolvesModernImageSourcesAndDropsIconMetadata()
    {
        var images = WebsiteImageService.ParseImages(new Uri("https://example.test/articles/page"), """
            <html>
              <head>
                <base href="/assets/">
                <meta property="og:image" content="cover.jpg">
                <link rel="icon" href="favicon.ico">
              </head>
              <body style="background-image: url('background.webp')">
                <img src="thumb.jpg" data-full="photos/original.jpg" alt="Фото">
                <picture><source srcset="wide-800.jpg 800w, wide-1600.jpg 1600w"></picture>
              </body>
            </html>
            """);

        Assert.Contains(images, image => image.Address == "https://example.test/assets/cover.jpg");
        Assert.Contains(images, image => image.Address == "https://example.test/assets/photos/original.jpg");
        Assert.Contains(images, image => image.Address == "https://example.test/assets/wide-1600.jpg");
        Assert.Contains(images, image => image.Address == "https://example.test/assets/background.webp");
        Assert.DoesNotContain(images, image => image.Address.EndsWith("favicon.ico", StringComparison.Ordinal));
    }

    [Fact]
    public void ParsePreviewLinks_FollowsOnlyLinksContainingPreviews()
    {
        var links = WebsiteImageService.ParsePreviewLinks(new Uri("https://example.test/gallery/"), """
            <a href="photo/42"><img src="thumb/42.jpg"></a>
            <a href="/other">Обычная ссылка</a>
            <a href="https://cdn.example.test/full/43.jpg"><picture><source srcset="43.webp"></picture></a>
            """);

        Assert.Equal(2, links.Count);
        Assert.Contains(new Uri("https://example.test/gallery/photo/42"), links);
        Assert.Contains(new Uri("https://cdn.example.test/full/43.jpg"), links);
    }

    [Theory]
    [InlineData(192, 192, ImageQualityPreset.Relaxed, false)]
    [InlineData(400, 200, ImageQualityPreset.Relaxed, true)]
    [InlineData(600, 300, ImageQualityPreset.Standard, true)]
    [InlineData(300, 599, ImageQualityPreset.Standard, false)]
    [InlineData(800, 1200, ImageQualityPreset.High, true)]
    public void PassesQualityFilter_AppliesPresetAndAlwaysRejectsIcons(
        int width,
        int height,
        ImageQualityPreset preset,
        bool expected)
    {
        Assert.Equal(expected, WebsiteImageService.PassesQualityFilter(width, height, preset));
    }

    [Fact]
    public async Task AnalyzeAsync_DepthOneFindsLargeImageBehindPreviewPage()
    {
        var handler = new StubHttpMessageHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/" => Html("<a href='/details'><img src='/thumb.png'></a>"),
            "/details" => Html("<img src='/full.png'>"),
            "/thumb.png" => Png(120, 90),
            "/full.png" => Png(1600, 900),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        });
        var service = new WebsiteImageService(new HttpClient(handler));

        var withoutNesting = await service.AnalyzeAsync(
            new Uri("https://example.test/"),
            0,
            ImageQualityPreset.Standard);
        var withNesting = await service.AnalyzeAsync(
            new Uri("https://example.test/"),
            1,
            ImageQualityPreset.Standard);

        Assert.Empty(withoutNesting);
        var image = Assert.Single(withNesting);
        Assert.Equal("https://example.test/full.png", image.Address);
        Assert.Equal(1600, image.Width);
        Assert.Equal(900, image.Height);
    }

    [Fact]
    public async Task AnalyzeAsync_UsesBrowserSessionWithoutLeakingCookieAcrossRedirect()
    {
        var observedCookies = new List<(string Host, string? Cookie)>();
        string? observedUserAgent = null;
        var handler = new StubHttpMessageHandler(request =>
        {
            observedCookies.Add((request.RequestUri!.Host,
                request.Headers.TryGetValues("Cookie", out var values) ? values.Single() : null));
            observedUserAgent = request.Headers.UserAgent.ToString();
            if (request.RequestUri.Host == "members.example.test")
            {
                return new HttpResponseMessage(HttpStatusCode.Redirect)
                {
                    Headers = { Location = new Uri("https://cdn.example.test/full.png") }
                };
            }

            return Png(1600, 900);
        });
        var service = new WebsiteImageService(new HttpClient(handler));
        var session = new VideoDownLoader.Models.WebsiteBrowserSession(
            new Uri("https://members.example.test/gallery"),
            "<img src='/redirect.png'>",
            "Authenticated Test Browser/1.0",
            [new VideoDownLoader.Models.WebsiteSessionCookie(
                "session", "secret", "members.example.test", "/", true, null)]);

        var images = await service.AnalyzeAsync(
            session.PageUri,
            0,
            ImageQualityPreset.Standard,
            session: session);

        Assert.Single(images);
        Assert.Equal("Authenticated Test Browser/1.0", observedUserAgent);
        Assert.Equal("session=secret", observedCookies[0].Cookie);
        Assert.Null(observedCookies[1].Cookie);
    }

    [Fact]
    public async Task AnalyzeAsync_ReportsPageAndImageProgress()
    {
        var handler = new StubHttpMessageHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/" => Html("<a href='/details'><img src='/first.png'></a>"),
            "/details" => Html("<img src='/second.png'>"),
            "/first.png" => Png(1600, 900),
            "/second.png" => Png(1400, 800),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        });
        var service = new WebsiteImageService(new HttpClient(handler));
        var updates = new List<ImageAnalysisProgress>();

        await service.AnalyzeAsync(
            new Uri("https://example.test/"),
            1,
            ImageQualityPreset.Standard,
            new InlineProgress<ImageAnalysisProgress>(updates.Add));

        var pageUpdates = updates.Where(update => update.Stage == ImageAnalysisStage.Pages).ToArray();
        var imageUpdates = updates.Where(update => update.Stage == ImageAnalysisStage.Images).ToArray();
        Assert.Equal(new ImageAnalysisProgress(ImageAnalysisStage.Pages, 2, 2), pageUpdates[^1]);
        Assert.Equal(2, imageUpdates[^1].Processed);
        Assert.Equal(2, imageUpdates[^1].Total);
    }

    [Fact]
    public async Task AnalyzeAsync_DropsIdenticalContentFromDifferentAddresses()
    {
        var handler = new StubHttpMessageHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/" => Html("<img src='/first.png'><img src='/copy.png'>"),
            "/first.png" or "/copy.png" => Png(1600, 900),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        });
        var service = new WebsiteImageService(new HttpClient(handler));

        var images = await service.AnalyzeAsync(
            new Uri("https://example.test/"),
            0,
            ImageQualityPreset.Standard);

        Assert.Single(images);
        Assert.NotNull(images[0].ContentFingerprint);
    }

    [Fact]
    public async Task DownloadUniqueAsync_DoesNotCreateSecondFileForIdenticalContent()
    {
        var handler = new StubHttpMessageHandler(_ => Png(1600, 900));
        var service = new WebsiteImageService(new HttpClient(handler));
        var fingerprints = new HashSet<string>(StringComparer.Ordinal);
        var outputDirectory = Path.Combine(Path.GetTempPath(), $"VideoDownLoader.Tests-{Guid.NewGuid():N}");

        try
        {
            var first = await service.DownloadUniqueAsync(
                new WebsiteImageItem(new Uri("https://example.test/first.png"), "test"),
                outputDirectory,
                fingerprints);
            var duplicate = await service.DownloadUniqueAsync(
                new WebsiteImageItem(new Uri("https://example.test/copy.png"), "test"),
                outputDirectory,
                fingerprints);

            Assert.NotNull(first);
            Assert.Null(duplicate);
            Assert.Single(Directory.GetFiles(outputDirectory));
        }
        finally
        {
            if (Directory.Exists(outputDirectory))
            {
                Directory.Delete(outputDirectory, recursive: true);
            }
        }
    }

    private static HttpResponseMessage Html(string html)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(html, Encoding.UTF8, "text/html")
        };
    }

    private static HttpResponseMessage Png(int width, int height)
    {
        var data = new byte[24];
        data[0] = 0x89;
        data[1] = 0x50;
        data[2] = 0x4E;
        data[3] = 0x47;
        WriteBigEndian(data, 16, width);
        WriteBigEndian(data, 20, height);
        var content = new ByteArrayContent(data);
        content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private static void WriteBigEndian(byte[] data, int offset, int value)
    {
        data[offset] = (byte)(value >> 24);
        data[offset + 1] = (byte)(value >> 16);
        data[offset + 2] = (byte)(value >> 8);
        data[offset + 3] = (byte)value;
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = responseFactory(request);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
