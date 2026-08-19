using VideoDownLoader.Services;

namespace VideoDownLoader.Tests;

public sealed class YtDlpDiagnosticsTests
{
    [Theory]
    [InlineData("ERROR: unable to download video data: HTTP Error 403: Forbidden", YtDlpFailureKind.HttpForbidden, true)]
    [InlineData("WARNING: format is MISSING POT", YtDlpFailureKind.PoTokenRequired, true)]
    [InlineData("Sign in to confirm you’re not a bot", YtDlpFailureKind.BotCheck, true)]
    [InlineData("ERROR: HTTP Error 429: Too Many Requests", YtDlpFailureKind.RateLimited, false)]
    [InlineData("ERROR: Requested format is not available", YtDlpFailureKind.FormatUnavailable, true)]
    [InlineData("ERROR: This video is DRM protected", YtDlpFailureKind.DrmProtected, false)]
    [InlineData("ConnectionResetError(10054, 'Удаленный хост принудительно разорвал существующее подключение')", YtDlpFailureKind.Network, false)]
    public void Classify_ReturnsActionableFailure(
        string line,
        YtDlpFailureKind expectedKind,
        bool expectedRepair)
    {
        var result = YtDlpDiagnostics.Classify([line]);

        Assert.Equal(expectedKind, result.Kind);
        Assert.Equal(expectedRepair, result.CanRepairByUpdating);
        Assert.False(string.IsNullOrWhiteSpace(result.UserMessage));
    }

    [Fact]
    public void Sanitize_HidesSignedUrlParameters()
    {
        var result = YtDlpDiagnostics.Sanitize(
            "https://googlevideo.test/x?expire=123&sig=secret&pot=token&id=public");

        Assert.DoesNotContain("secret", result);
        Assert.DoesNotContain("token", result);
        Assert.DoesNotContain("expire=123", result);
        Assert.Contains("id=public", result);
    }
}
