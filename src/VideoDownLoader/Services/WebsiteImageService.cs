using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using VideoDownLoader.Models;

namespace VideoDownLoader.Services;

public enum ImageQualityPreset
{
    Standard,
    High,
    Relaxed
}

public enum ImageAnalysisStage
{
    Pages,
    Images
}

public readonly record struct ImageAnalysisProgress(
    ImageAnalysisStage Stage,
    int Processed,
    int Total);

public sealed partial class WebsiteImageService
{
    private const int MaximumHtmlSize = 12 * 1024 * 1024;
    private const int MaximumProbeSize = 384 * 1024;
    private const int MaximumCandidates = 250;
    private const int MaximumPages = 40;
    private static readonly string[] ImageAttributes =
    [
        "src", "data-src", "data-original", "data-lazy-src", "data-url",
        "data-full", "data-large", "data-zoom-image"
    ];

    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _requestPacer = new(1, 1);
    private long _lastRequestTimestamp;

    public WebsiteImageService(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? CreateHttpClient();
    }

    public async Task<IReadOnlyList<WebsiteImageItem>> AnalyzeAsync(
        Uri pageUri,
        int scanDepth = 0,
        ImageQualityPreset quality = ImageQualityPreset.Standard,
        IProgress<ImageAnalysisProgress>? progress = null,
        CancellationToken cancellationToken = default,
        WebsiteBrowserSession? session = null)
    {
        ValidateHttpUri(pageUri, "Адрес страницы");
        scanDepth = Math.Clamp(scanDepth, 0, 3);

        var discovered = await CrawlAsync(pageUri, scanDepth, session, progress, cancellationToken);
        var candidates = discovered
            .Where(image => !LooksLikeIcon(image.Url))
            .Take(MaximumCandidates)
            .ToArray();
        progress?.Report(new ImageAnalysisProgress(ImageAnalysisStage.Images, 0, candidates.Length));
        var probed = new ProbedImage?[candidates.Length];
        using var gate = new SemaphoreSlim(2);
        var processed = 0;

        await Task.WhenAll(candidates.Select(async (candidate, index) =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                var probe = await ProbeAsync(candidate.Url, session, pageUri, cancellationToken);
                if (probe is not null && PassesQualityFilter(probe.Value.Width, probe.Value.Height, quality))
                {
                    probed[index] = new ProbedImage(candidate, probe.Value);
                }
            }
            catch (HttpRequestException)
            {
                // Недоступный ресурс не проходит фильтр качества.
            }
            catch (InvalidOperationException)
            {
                // Ресурс не является распознаваемым изображением.
            }
            finally
            {
                gate.Release();
                progress?.Report(new ImageAnalysisProgress(
                    ImageAnalysisStage.Images,
                    Interlocked.Increment(ref processed),
                    candidates.Length));
            }
        }));

        var effectiveAddresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var contentFingerprints = new HashSet<string>(StringComparer.Ordinal);
        var accepted = new List<WebsiteImageItem>();
        foreach (var result in probed)
        {
            if (result is null)
            {
                continue;
            }

            var candidate = result.Value.Candidate;
            var probe = result.Value.Probe;
            if (!effectiveAddresses.Add(GetAddressKey(probe.EffectiveUri)) ||
                (probe.ContentFingerprint is not null && !contentFingerprints.Add(probe.ContentFingerprint)))
            {
                continue;
            }

            accepted.Add(new WebsiteImageItem(
                candidate.Url,
                candidate.Source,
                candidate.Description,
                probe.Width,
                probe.Height,
                probe.FileSize,
                probe.PreviewData,
                probe.ContentFingerprint));
        }

        return accepted;
    }

    private async Task<IReadOnlyList<WebsiteImageItem>> CrawlAsync(
        Uri startUri,
        int maximumDepth,
        WebsiteBrowserSession? session,
        IProgress<ImageAnalysisProgress>? progress,
        CancellationToken cancellationToken)
    {
        var pending = new Queue<(Uri Uri, int Depth, Uri? Referrer)>();
        var scheduled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var found = new Dictionary<string, WebsiteImageItem>(StringComparer.OrdinalIgnoreCase);
        pending.Enqueue((startUri, 0, null));
        scheduled.Add(GetAddressKey(startUri));
        ReportPageProgress(progress, 0, pending.Count);

        while (pending.Count > 0 && visited.Count < MaximumPages && found.Count < MaximumCandidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (uri, depth, referrer) = pending.Dequeue();
            var key = GetAddressKey(uri);
            if (!visited.Add(key))
            {
                continue;
            }

            PageResource resource;
            try
            {
                if (depth == 0 && session is not null && AreSamePage(uri, session.PageUri))
                {
                    resource = new PageResource(session.PageUri, session.Html, false);
                }
                else
                {
                    resource = await FetchPageResourceAsync(uri, session, referrer, cancellationToken);
                }
            }
            catch (Exception exception) when (depth > 0 && exception is HttpRequestException or InvalidOperationException)
            {
                ReportPageProgress(progress, visited.Count, pending.Count);
                continue;
            }

            if (resource.IsImage)
            {
                AddCandidate(found, resource.Uri, resource.Uri.AbsoluteUri, "ссылка с превью", null);
                ReportPageProgress(progress, visited.Count, pending.Count);
                continue;
            }

            foreach (var image in ParseImages(resource.Uri, resource.Html!))
            {
                AddCandidate(found, image.Url, image.Url.AbsoluteUri, image.Source, image.Description);
                if (found.Count >= MaximumCandidates)
                {
                    break;
                }
            }

            if (depth >= maximumDepth)
            {
                ReportPageProgress(progress, visited.Count, pending.Count);
                continue;
            }

            foreach (var link in ParsePreviewLinks(resource.Uri, resource.Html!))
            {
                if (LooksLikeImageUrl(link))
                {
                    AddCandidate(found, link, link.AbsoluteUri, "оригинал по ссылке превью", null);
                }
                else if (scheduled.Count < MaximumPages && scheduled.Add(GetAddressKey(link)))
                {
                    pending.Enqueue((link, depth + 1, resource.Uri));
                }
            }

            ReportPageProgress(progress, visited.Count, pending.Count);
        }

        ReportPageProgress(progress, visited.Count, 0);
        return found.Values.ToArray();
    }

    private async Task<PageResource> FetchPageResourceAsync(
        Uri uri,
        WebsiteBrowserSession? session,
        Uri? referrer,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(
            HttpMethod.Get,
            uri,
            "text/html,application/xhtml+xml,image/*;q=0.8,*/*;q=0.5",
            session,
            referrer);
        using var response = await SendPolitelyAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken,
            session);
        response.EnsureSuccessStatusCode();

        var effectiveUri = response.RequestMessage?.RequestUri ?? uri;
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (mediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true)
        {
            return new PageResource(effectiveUri, null, true);
        }

        if (mediaType is not null &&
            !mediaType.Contains("html", StringComparison.OrdinalIgnoreCase) &&
            !mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Ссылка ведёт не на HTML-страницу ({mediaType}).");
        }

        if (response.Content.Headers.ContentLength > MaximumHtmlSize)
        {
            throw new InvalidOperationException("HTML-страница слишком большая для анализа (более 12 МБ).");
        }

        var bytes = await ReadLimitedAsync(response.Content, MaximumHtmlSize, cancellationToken);
        var encoding = GetEncoding(response.Content.Headers.ContentType?.CharSet);
        return new PageResource(effectiveUri, encoding.GetString(bytes), false);
    }

    public async Task<string> DownloadAsync(
        WebsiteImageItem image,
        string outputDirectory,
        CancellationToken cancellationToken = default,
        WebsiteBrowserSession? session = null)
    {
        return (await DownloadCoreAsync(
            image,
            outputDirectory,
            null,
            cancellationToken,
            session))!;
    }

    public Task<string?> DownloadUniqueAsync(
        WebsiteImageItem image,
        string outputDirectory,
        ISet<string> downloadedContentFingerprints,
        CancellationToken cancellationToken = default,
        WebsiteBrowserSession? session = null)
    {
        ArgumentNullException.ThrowIfNull(downloadedContentFingerprints);
        return DownloadCoreAsync(
            image,
            outputDirectory,
            downloadedContentFingerprints,
            cancellationToken,
            session);
    }

    private async Task<string?> DownloadCoreAsync(
        WebsiteImageItem image,
        string outputDirectory,
        ISet<string>? downloadedContentFingerprints,
        CancellationToken cancellationToken,
        WebsiteBrowserSession? session)
    {
        ValidateHttpUri(image.Url, "Адрес изображения");
        Directory.CreateDirectory(outputDirectory);

        using var request = CreateRequest(
            HttpMethod.Get,
            image.Url,
            "image/avif,image/webp,image/apng,image/svg+xml,image/*,*/*;q=0.8",
            session,
            session?.PageUri);
        using var response = await SendPolitelyAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken,
            session);
        response.EnsureSuccessStatusCode();

        var fileName = BuildFileName(image.Url, response.Content.Headers);
        var destination = GetAvailablePath(outputDirectory, fileName);
        var temporary = destination + ".part";
        string? addedFingerprint = null;

        try
        {
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var hash = downloadedContentFingerprints is null
                ? null
                : IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var target = new FileStream(
                             temporary,
                             FileMode.Create,
                             FileAccess.Write,
                             FileShare.None,
                             81920,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = new byte[81920];
                while (true)
                {
                    var read = await source.ReadAsync(buffer, cancellationToken);
                    if (read == 0)
                    {
                        break;
                    }

                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    hash?.AppendData(buffer, 0, read);
                }
            }

            if (hash is not null)
            {
                var fingerprint = Convert.ToHexString(hash.GetHashAndReset());
                if (!downloadedContentFingerprints!.Add(fingerprint))
                {
                    File.Delete(temporary);
                    return null;
                }

                addedFingerprint = fingerprint;
            }

            File.Move(temporary, destination);
            return destination;
        }
        catch
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }

            if (addedFingerprint is not null)
            {
                downloadedContentFingerprints?.Remove(addedFingerprint);
            }

            throw;
        }
    }

    public static IReadOnlyList<WebsiteImageItem> ParseImages(Uri pageUri, string html)
    {
        ArgumentNullException.ThrowIfNull(pageUri);
        ArgumentNullException.ThrowIfNull(html);

        var baseUri = FindBaseUri(pageUri, html);
        var found = new Dictionary<string, WebsiteImageItem>(StringComparer.OrdinalIgnoreCase);

        foreach (Match tagMatch in ImageTagRegex().Matches(html))
        {
            var tag = tagMatch.Value;
            var alt = GetAttribute(tag, "alt") ?? GetAttribute(tag, "title");

            foreach (var attributeName in ImageAttributes)
            {
                AddCandidate(found, baseUri, GetAttribute(tag, attributeName), attributeName, alt);
            }

            AddSrcSet(found, baseUri, GetAttribute(tag, "srcset"), "srcset", alt);
            AddSrcSet(found, baseUri, GetAttribute(tag, "data-srcset"), "data-srcset", alt);
        }

        foreach (Match tagMatch in SourceTagRegex().Matches(html))
        {
            var tag = tagMatch.Value;
            AddSrcSet(found, baseUri, GetAttribute(tag, "srcset"), "picture/srcset", null);
            AddSrcSet(found, baseUri, GetAttribute(tag, "data-srcset"), "picture/data-srcset", null);
        }

        foreach (Match tagMatch in MetaOrLinkTagRegex().Matches(html))
        {
            var tag = tagMatch.Value;
            var key = GetAttribute(tag, "property") ?? GetAttribute(tag, "name") ?? GetAttribute(tag, "rel");
            if (key is null || !IsImageMetadataKey(key))
            {
                continue;
            }

            AddCandidate(found, baseUri, GetAttribute(tag, "content") ?? GetAttribute(tag, "href"), key, null);
        }

        foreach (Match match in CssUrlRegex().Matches(html))
        {
            AddCandidate(found, baseUri, match.Groups["url"].Value, "CSS", null);
        }

        return found.Values.ToArray();
    }

    public static IReadOnlyList<Uri> ParsePreviewLinks(Uri pageUri, string html)
    {
        ArgumentNullException.ThrowIfNull(pageUri);
        ArgumentNullException.ThrowIfNull(html);

        var baseUri = FindBaseUri(pageUri, html);
        var found = new Dictionary<string, Uri>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in PreviewLinkRegex().Matches(html))
        {
            var href = GetAttribute(match.Groups["tag"].Value, "href");
            if (!TryResolveUri(baseUri, href, out var resolved))
            {
                continue;
            }

            var key = resolved.GetComponents(UriComponents.HttpRequestUrl, UriFormat.SafeUnescaped);
            found.TryAdd(key, resolved);
        }

        return found.Values.ToArray();
    }

    public static bool PassesQualityFilter(int width, int height, ImageQualityPreset quality)
    {
        var shortSide = Math.Min(width, height);
        var longSide = Math.Max(width, height);
        var (minimumShortSide, minimumLongSide) = quality switch
        {
            ImageQualityPreset.High => (600, 1200),
            ImageQualityPreset.Relaxed => (200, 400),
            _ => (300, 600)
        };

        // Любое изображение размером с типичную иконку исключается во всех режимах.
        return shortSide >= minimumShortSide && longSide >= minimumLongSide && longSide > 256;
    }

    private async Task<ImageProbe?> ProbeAsync(
        Uri uri,
        WebsiteBrowserSession? session,
        Uri referrer,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(
            HttpMethod.Get,
            uri,
            "image/avif,image/webp,image/apng,image/svg+xml,image/*,*/*;q=0.8",
            session,
            referrer);
        request.Headers.Range = new RangeHeaderValue(0, MaximumProbeSize - 1);
        using var response = await SendPolitelyAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken,
            session);
        if (response.StatusCode != HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            return await ReadProbeResponseAsync(response, uri, cancellationToken);
        }

        response.Dispose();
        using var fallbackRequest = CreateRequest(
            HttpMethod.Get,
            uri,
            "image/avif,image/webp,image/apng,image/svg+xml,image/*,*/*;q=0.8",
            session,
            referrer);
        using var fallbackResponse = await SendPolitelyAsync(
            fallbackRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken,
            session);
        return await ReadProbeResponseAsync(fallbackResponse, uri, cancellationToken);
    }

    private static async Task<ImageProbe?> ReadProbeResponseAsync(
        HttpResponseMessage response,
        Uri requestedUri,
        CancellationToken cancellationToken)
    {
        response.EnsureSuccessStatusCode();

        var mediaType = response.Content.Headers.ContentType?.MediaType;
        var readResult = await ReadAtMostAsync(response.Content, MaximumProbeSize, cancellationToken);
        var data = readResult.Data;
        var dimensions = ReadDimensions(data, mediaType);
        if (dimensions is null || dimensions.Value.Width <= 0 || dimensions.Value.Height <= 0)
        {
            return null;
        }

        var effectiveUri = response.RequestMessage?.RequestUri ?? requestedUri;
        var fileSize = response.Content.Headers.ContentRange?.Length ??
                       response.Content.Headers.ContentLength ??
                       (readResult.IsComplete ? data.LongLength : null);
        var previewData = readResult.IsComplete ? data : null;
        var contentFingerprint = previewData is null
            ? null
            : Convert.ToHexString(SHA256.HashData(previewData));
        return new ImageProbe(
            dimensions.Value.Width,
            dimensions.Value.Height,
            fileSize,
            previewData,
            effectiveUri,
            contentFingerprint);
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            UseCookies = false,
            AllowAutoRedirect = false
        };
        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(45)
        };
        return client;
    }

    private static HttpRequestMessage CreateRequest(
        HttpMethod method,
        Uri uri,
        string accept,
        WebsiteBrowserSession? session,
        Uri? referrer)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Accept.ParseAdd(accept);
        request.Headers.UserAgent.TryParseAdd(session?.UserAgent ?? "VideoDownLoader/1.0");

        var cookieHeader = session?.BuildCookieHeader(uri);
        if (!string.IsNullOrEmpty(cookieHeader))
        {
            request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
        }

        if (referrer is not null &&
            (referrer.Scheme == Uri.UriSchemeHttp || referrer.Scheme == Uri.UriSchemeHttps))
        {
            request.Headers.Referrer = referrer;
        }

        return request;
    }

    private async Task<HttpResponseMessage> SendPolitelyAsync(
        HttpRequestMessage request,
        HttpCompletionOption completionOption,
        CancellationToken cancellationToken,
        WebsiteBrowserSession? session)
    {
        var currentRequest = request;
        try
        {
            for (var redirectCount = 0; ; redirectCount++)
            {
                await WaitForRequestSlotAsync(cancellationToken);
                var response = await _httpClient.SendAsync(currentRequest, completionOption, cancellationToken);
                if (!IsRedirect(response.StatusCode) || response.Headers.Location is null || redirectCount >= 8)
                {
                    if (!ReferenceEquals(currentRequest, request))
                    {
                        currentRequest.Dispose();
                    }

                    return response;
                }

                var redirectUri = response.Headers.Location.IsAbsoluteUri
                    ? response.Headers.Location
                    : new Uri(currentRequest.RequestUri!, response.Headers.Location);
                response.Dispose();
                ValidateHttpUri(redirectUri, "Адрес перенаправления");

                var redirectedRequest = CloneForRedirect(currentRequest, redirectUri, session);
                if (!ReferenceEquals(currentRequest, request))
                {
                    currentRequest.Dispose();
                }

                currentRequest = redirectedRequest;
            }
        }
        catch
        {
            if (!ReferenceEquals(currentRequest, request))
            {
                currentRequest.Dispose();
            }

            throw;
        }
    }

    private async Task WaitForRequestSlotAsync(CancellationToken cancellationToken)
    {
        await _requestPacer.WaitAsync(cancellationToken);
        try
        {
            if (_lastRequestTimestamp != 0)
            {
                var elapsed = Stopwatch.GetElapsedTime(_lastRequestTimestamp);
                var remaining = TimeSpan.FromMilliseconds(450) - elapsed;
                if (remaining > TimeSpan.Zero)
                {
                    await Task.Delay(remaining, cancellationToken);
                }
            }

            _lastRequestTimestamp = Stopwatch.GetTimestamp();
        }
        finally
        {
            _requestPacer.Release();
        }
    }

    private static HttpRequestMessage CloneForRedirect(
        HttpRequestMessage source,
        Uri redirectUri,
        WebsiteBrowserSession? session)
    {
        var redirected = new HttpRequestMessage(HttpMethod.Get, redirectUri);
        foreach (var header in source.Headers)
        {
            if (header.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase) ||
                header.Key.Equals("Host", StringComparison.OrdinalIgnoreCase) ||
                header.Key.Equals("Referer", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            redirected.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        redirected.Headers.Referrer = source.RequestUri;
        var cookieHeader = session?.BuildCookieHeader(redirectUri);
        if (!string.IsNullOrEmpty(cookieHeader))
        {
            redirected.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
        }

        return redirected;
    }

    private static bool IsRedirect(HttpStatusCode statusCode)
    {
        return statusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or
            HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;
    }

    private static bool AreSamePage(Uri left, Uri right)
    {
        return Uri.Compare(
            left,
            right,
            UriComponents.SchemeAndServer | UriComponents.PathAndQuery,
            UriFormat.SafeUnescaped,
            StringComparison.OrdinalIgnoreCase) == 0;
    }

    private static string GetAddressKey(Uri uri)
    {
        return uri.GetComponents(UriComponents.HttpRequestUrl, UriFormat.SafeUnescaped);
    }

    private static void ReportPageProgress(
        IProgress<ImageAnalysisProgress>? progress,
        int processed,
        int pending)
    {
        progress?.Report(new ImageAnalysisProgress(
            ImageAnalysisStage.Pages,
            processed,
            processed + pending));
    }

    private static async Task<byte[]> ReadLimitedAsync(
        HttpContent content,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var result = new MemoryStream();
        var buffer = new byte[81920];

        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                return result.ToArray();
            }

            if (result.Length + read > maximumBytes)
            {
                throw new InvalidOperationException("HTML-страница слишком большая для анализа (более 12 МБ).");
            }

            result.Write(buffer, 0, read);
        }
    }

    private static async Task<BoundedReadResult> ReadAtMostAsync(
        HttpContent content,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var result = new MemoryStream();
        var buffer = new byte[81920];

        while (result.Length < maximumBytes)
        {
            var remaining = maximumBytes - (int)result.Length;
            var read = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining)), cancellationToken);
            if (read == 0)
            {
                return new BoundedReadResult(result.ToArray(), true);
            }

            result.Write(buffer, 0, read);
        }

        var trailingByte = new byte[1];
        var hasMoreData = await stream.ReadAsync(trailingByte, cancellationToken) != 0;
        return new BoundedReadResult(result.ToArray(), !hasMoreData);
    }

    private static (int Width, int Height)? ReadDimensions(byte[] data, string? mediaType)
    {
        if (data.Length >= 24 &&
            data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47)
        {
            return (ReadInt32BigEndian(data, 16), ReadInt32BigEndian(data, 20));
        }

        if (data.Length >= 10 && Encoding.ASCII.GetString(data, 0, 3) == "GIF")
        {
            return (BitConverter.ToUInt16(data, 6), BitConverter.ToUInt16(data, 8));
        }

        if (data.Length >= 26 && data[0] == (byte)'B' && data[1] == (byte)'M')
        {
            return (Math.Abs(BitConverter.ToInt32(data, 18)), Math.Abs(BitConverter.ToInt32(data, 22)));
        }

        if (data.Length >= 30 && Encoding.ASCII.GetString(data, 0, 4) == "RIFF" &&
            Encoding.ASCII.GetString(data, 8, 4) == "WEBP" && Encoding.ASCII.GetString(data, 12, 4) == "VP8X")
        {
            return (1 + ReadUInt24LittleEndian(data, 24), 1 + ReadUInt24LittleEndian(data, 27));
        }

        if (data.Length >= 30 && Encoding.ASCII.GetString(data, 0, 4) == "RIFF" &&
            Encoding.ASCII.GetString(data, 8, 4) == "WEBP" && Encoding.ASCII.GetString(data, 12, 4) == "VP8 " &&
            data[23] == 0x9D && data[24] == 0x01 && data[25] == 0x2A)
        {
            return (BitConverter.ToUInt16(data, 26) & 0x3FFF, BitConverter.ToUInt16(data, 28) & 0x3FFF);
        }

        if (data.Length >= 25 && Encoding.ASCII.GetString(data, 0, 4) == "RIFF" &&
            Encoding.ASCII.GetString(data, 8, 4) == "WEBP" && Encoding.ASCII.GetString(data, 12, 4) == "VP8L" &&
            data[20] == 0x2F)
        {
            var width = 1 + data[21] + ((data[22] & 0x3F) << 8);
            var height = 1 + (data[22] >> 6) + (data[23] << 2) + ((data[24] & 0x0F) << 10);
            return (width, height);
        }

        if (data.Length >= 4 && data[0] == 0xFF && data[1] == 0xD8)
        {
            var jpeg = ReadJpegDimensions(data);
            if (jpeg is not null)
            {
                return jpeg;
            }
        }

        if (mediaType?.Contains("svg", StringComparison.OrdinalIgnoreCase) == true ||
            data.AsSpan(0, Math.Min(data.Length, 1024)).IndexOf("<svg"u8) >= 0)
        {
            var svg = Encoding.UTF8.GetString(data);
            var svgTag = SvgTagRegex().Match(svg).Value;
            if (!string.IsNullOrEmpty(svgTag))
            {
                var width = ParseSvgLength(GetAttribute(svgTag, "width"));
                var height = ParseSvgLength(GetAttribute(svgTag, "height"));
                if (width is not null && height is not null)
                {
                    return (width.Value, height.Value);
                }

                var viewBox = GetAttribute(svgTag, "viewBox")?.Split(
                    new[] { ' ', ',' },
                    StringSplitOptions.RemoveEmptyEntries);
                if (viewBox is { Length: 4 } &&
                    double.TryParse(viewBox[2], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var viewBoxWidth) &&
                    double.TryParse(viewBox[3], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var viewBoxHeight))
                {
                    return ((int)Math.Round(viewBoxWidth), (int)Math.Round(viewBoxHeight));
                }
            }
        }

        var ispeOffset = data.AsSpan().IndexOf("ispe"u8);
        if (ispeOffset >= 4 && ispeOffset + 16 <= data.Length)
        {
            var width = ReadInt32BigEndian(data, ispeOffset + 8);
            var height = ReadInt32BigEndian(data, ispeOffset + 12);
            if (width > 0 && height > 0)
            {
                return (width, height);
            }
        }

        return null;
    }

    private static (int Width, int Height)? ReadJpegDimensions(byte[] data)
    {
        var offset = 2;
        while (offset + 8 < data.Length)
        {
            if (data[offset] != 0xFF)
            {
                offset++;
                continue;
            }

            var marker = data[offset + 1];
            if (marker is >= 0xC0 and <= 0xC3 or >= 0xC5 and <= 0xC7 or >= 0xC9 and <= 0xCB or >= 0xCD and <= 0xCF)
            {
                return ((data[offset + 7] << 8) | data[offset + 8],
                    (data[offset + 5] << 8) | data[offset + 6]);
            }

            if (marker is 0xD8 or 0xD9 || marker is >= 0xD0 and <= 0xD7)
            {
                offset += 2;
                continue;
            }

            var segmentLength = (data[offset + 2] << 8) | data[offset + 3];
            if (segmentLength < 2)
            {
                return null;
            }

            offset += segmentLength + 2;
        }

        return null;
    }

    private static int ReadInt32BigEndian(byte[] data, int offset)
    {
        return (data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3];
    }

    private static int ReadUInt24LittleEndian(byte[] data, int offset)
    {
        return data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16);
    }

    private static int? ParseSvgLength(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var match = SvgLengthRegex().Match(value);
        return match.Success && double.TryParse(
            match.Value,
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out var parsed)
            ? (int)Math.Round(parsed)
            : null;
    }

    private static bool LooksLikeIcon(Uri uri)
    {
        var path = uri.AbsolutePath.ToLowerInvariant();
        if (path.EndsWith(".ico", StringComparison.Ordinal))
        {
            return true;
        }

        var name = Path.GetFileNameWithoutExtension(path);
        return name.Equals("icon", StringComparison.Ordinal) ||
               name.StartsWith("favicon", StringComparison.Ordinal) ||
               name.Contains("sprite", StringComparison.Ordinal) ||
               name.Contains("tracking", StringComparison.Ordinal) ||
               name.Contains("spacer", StringComparison.Ordinal) ||
               name.Contains("pixel", StringComparison.Ordinal) ||
               name.Contains("emoji", StringComparison.Ordinal) ||
               name.Contains("badge", StringComparison.Ordinal) ||
               path.Contains("/icons/", StringComparison.Ordinal) ||
               path.Contains("/emoji/", StringComparison.Ordinal);
    }

    private static bool LooksLikeImageUrl(Uri uri)
    {
        var extension = Path.GetExtension(uri.AbsolutePath);
        return extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".gif", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".webp", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".avif", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".bmp", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".tif", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".tiff", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".svg", StringComparison.OrdinalIgnoreCase);
    }

    private static Encoding GetEncoding(string? charset)
    {
        if (!string.IsNullOrWhiteSpace(charset))
        {
            try
            {
                return Encoding.GetEncoding(charset.Trim('"', '\''));
            }
            catch (ArgumentException)
            {
                // Некорректная кодировка сервера: используем безопасный UTF-8 по умолчанию.
            }
        }

        return Encoding.UTF8;
    }

    private static Uri FindBaseUri(Uri pageUri, string html)
    {
        var match = BaseTagRegex().Match(html);
        var href = match.Success ? GetAttribute(match.Value, "href") : null;
        return TryResolveUri(pageUri, href, out var resolved) ? resolved : pageUri;
    }

    private static string? GetAttribute(string tag, string name)
    {
        foreach (Match match in AttributeRegex().Matches(tag))
        {
            if (!string.Equals(match.Groups["name"].Value, name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = match.Groups["dq"].Success
                ? match.Groups["dq"].Value
                : match.Groups["sq"].Success
                    ? match.Groups["sq"].Value
                    : match.Groups["bare"].Value;
            return WebUtility.HtmlDecode(value).Trim();
        }

        return null;
    }

    private static void AddSrcSet(
        IDictionary<string, WebsiteImageItem> found,
        Uri baseUri,
        string? srcSet,
        string source,
        string? description)
    {
        if (string.IsNullOrWhiteSpace(srcSet))
        {
            return;
        }

        string? bestAddress = null;
        double bestDescriptor = double.MinValue;
        foreach (var candidate in srcSet.Split(','))
        {
            var parts = candidate.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            var address = parts.FirstOrDefault();
            if (address is null)
            {
                continue;
            }

            var descriptor = parts.Length < 2
                ? 1d
                : double.TryParse(
                    parts[1].TrimEnd('w', 'W', 'x', 'X'),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var parsed)
                    ? parsed
                    : 1d;
            if (bestAddress is null || descriptor >= bestDescriptor)
            {
                bestAddress = address;
                bestDescriptor = descriptor;
            }
        }

        AddCandidate(found, baseUri, bestAddress, source, description);
    }

    private static void AddCandidate(
        IDictionary<string, WebsiteImageItem> found,
        Uri baseUri,
        string? address,
        string source,
        string? description)
    {
        if (!TryResolveUri(baseUri, address, out var resolved))
        {
            return;
        }

        if (LooksLikeIcon(resolved))
        {
            return;
        }

        var key = resolved.GetComponents(UriComponents.HttpRequestUrl, UriFormat.SafeUnescaped);
        found.TryAdd(key, new WebsiteImageItem(resolved, source, description));
    }

    private static bool TryResolveUri(Uri baseUri, string? address, out Uri resolved)
    {
        resolved = null!;
        if (string.IsNullOrWhiteSpace(address))
        {
            return false;
        }

        var decoded = WebUtility.HtmlDecode(address.Trim().Trim('"', '\''));
        if (!Uri.TryCreate(baseUri, decoded, out var candidate) ||
            (candidate.Scheme != Uri.UriSchemeHttp && candidate.Scheme != Uri.UriSchemeHttps))
        {
            return false;
        }

        resolved = candidate;
        return true;
    }

    private static bool IsImageMetadataKey(string key)
    {
        return key.Contains("image", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildFileName(Uri uri, HttpContentHeaders headers)
    {
        var headerName = headers.ContentDisposition?.FileNameStar ?? headers.ContentDisposition?.FileName;
        var rawName = string.IsNullOrWhiteSpace(headerName)
            ? Uri.UnescapeDataString(Path.GetFileName(uri.AbsolutePath))
            : headerName.Trim('"');
        rawName = Path.GetFileName(rawName);

        var invalid = Path.GetInvalidFileNameChars();
        var safeName = new string(rawName.Select(character => invalid.Contains(character) ? '_' : character).ToArray()).Trim();
        if (string.IsNullOrWhiteSpace(safeName))
        {
            safeName = "image";
        }

        if (string.IsNullOrWhiteSpace(Path.GetExtension(safeName)))
        {
            safeName += ExtensionFor(headers.ContentType?.MediaType);
        }

        return safeName;
    }

    private static string ExtensionFor(string? mediaType)
    {
        return mediaType?.ToLowerInvariant() switch
        {
            "image/jpeg" => ".jpg",
            "image/png" => ".png",
            "image/gif" => ".gif",
            "image/webp" => ".webp",
            "image/avif" => ".avif",
            "image/svg+xml" => ".svg",
            "image/bmp" => ".bmp",
            "image/tiff" => ".tiff",
            "image/x-icon" or "image/vnd.microsoft.icon" => ".ico",
            _ => ".img"
        };
    }

    private static string GetAvailablePath(string outputDirectory, string fileName)
    {
        var candidate = Path.Combine(outputDirectory, fileName);
        if (!File.Exists(candidate) && !File.Exists(candidate + ".part"))
        {
            return candidate;
        }

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var index = 2; ; index++)
        {
            candidate = Path.Combine(outputDirectory, $"{stem}_{index}{extension}");
            if (!File.Exists(candidate) && !File.Exists(candidate + ".part"))
            {
                return candidate;
            }
        }
    }

    private static void ValidateHttpUri(Uri uri, string label)
    {
        if (!uri.IsAbsoluteUri || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException($"{label} должен использовать HTTP или HTTPS.", nameof(uri));
        }
    }

    [GeneratedRegex(@"<img\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ImageTagRegex();

    [GeneratedRegex(@"<source\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SourceTagRegex();

    [GeneratedRegex(@"<(?:meta|link)\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MetaOrLinkTagRegex();

    [GeneratedRegex(@"<base\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BaseTagRegex();

    [GeneratedRegex(
        @"(?<name>[\w:-]+)\s*=\s*(?:""(?<dq>[^""]*)""|'(?<sq>[^']*)'|(?<bare>[^\s>]+))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AttributeRegex();

    [GeneratedRegex(
        @"url\(\s*(?:""(?<url>[^""]+)""|'(?<url>[^']+)'|(?<url>[^)'""\s]+))\s*\)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CssUrlRegex();

    [GeneratedRegex(@"<svg\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SvgTagRegex();

    [GeneratedRegex(
        @"(?<tag><a\b[^>]*>)(?:(?!</a>).)*(?:<img\b|<picture\b|<source\b)(?:(?!</a>).)*</a\s*>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex PreviewLinkRegex();

    [GeneratedRegex(@"[+-]?(?:\d+(?:\.\d*)?|\.\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex SvgLengthRegex();

    private readonly record struct ImageProbe(
        int Width,
        int Height,
        long? FileSize,
        byte[]? PreviewData,
        Uri EffectiveUri,
        string? ContentFingerprint);

    private readonly record struct ProbedImage(WebsiteImageItem Candidate, ImageProbe Probe);

    private readonly record struct BoundedReadResult(byte[] Data, bool IsComplete);

    private readonly record struct PageResource(Uri Uri, string? Html, bool IsImage);
}
