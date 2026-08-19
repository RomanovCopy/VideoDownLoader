using System.Text.Json;
using VideoDownLoader.Services;

namespace VideoDownLoader.Tests;

public sealed class MediaAnalysisServiceTests
{
    [Theory]
    [InlineData("ERROR: This video is DRM protected")]
    [InlineData("The requested stream is protected by DRM")]
    public void IsDrmError_RecognizesTerminalErrors(string message)
    {
        Assert.True(MediaAnalysisService.IsDrmError(message));
    }

    [Fact]
    public void Parse_UnprotectedFormats_AllowsDownload()
    {
        var analysis = Parse("""
            {
              "title": "Доступное видео",
              "channel": "Автор",
              "duration": 90,
              "formats": [
                { "format_id": "137", "height": 1080, "vcodec": "avc1.640028", "acodec": "none" },
                { "format_id": "140", "vcodec": "none", "acodec": "mp4a.40.2" }
              ]
            }
            """);

        Assert.False(analysis.HasDrm);
        Assert.True(analysis.IsDownloadable);
        Assert.Equal(2, analysis.Formats.Count);
        Assert.Equal(TimeSpan.FromSeconds(90), analysis.Duration);
    }

    [Fact]
    public void Parse_NullNumericFields_TreatsThemAsUnknown()
    {
        var analysis = Parse("""
            {
              "title": "Прямой эфир с неизвестными параметрами",
              "duration": null,
              "filesize": null,
              "filesize_approx": null,
              "playlist_count": null,
              "formats": [
                {
                  "format_id": "live",
                  "width": null,
                  "height": null,
                  "fps": null,
                  "filesize": null,
                  "filesize_approx": null,
                  "vcodec": "avc1",
                  "acodec": "mp4a"
                }
              ]
            }
            """);

        Assert.Null(analysis.Duration);
        Assert.Null(analysis.EstimatedFileSize);
        var format = Assert.Single(analysis.Formats);
        Assert.Null(format.Width);
        Assert.Null(format.Height);
        Assert.Null(format.FramesPerSecond);
        Assert.Null(format.FileSize);
        Assert.True(analysis.IsDownloadable);
    }

    [Fact]
    public void Parse_RootDrmFlag_BlocksDownload()
    {
        var analysis = Parse("""
            {
              "title": "Защищённое видео",
              "has_drm": true,
              "formats": [
                { "format_id": "drm-1", "height": 1080, "vcodec": "avc1", "acodec": "mp4a", "has_drm": true }
              ]
            }
            """);

        Assert.True(analysis.HasDrm);
        Assert.False(analysis.IsDownloadable);
    }

    [Fact]
    public void Parse_MixedFormats_AllowsOnlyUnprotectedChoice()
    {
        var analysis = Parse("""
            {
              "title": "Смешанные форматы",
              "formats": [
                { "format_id": "clear", "height": 720, "vcodec": "avc1", "acodec": "mp4a" },
                { "format_id": "protected", "height": 1080, "vcodec": "avc1", "acodec": "mp4a", "has_drm": true }
              ]
            }
            """);

        Assert.True(analysis.HasDrm);
        Assert.True(analysis.IsDownloadable);
        Assert.Single(analysis.Formats, format => !format.HasDrm);
    }

    [Fact]
    public void Parse_AllFormatsProtected_BlocksDownload()
    {
        var analysis = Parse("""
            {
              "title": "Только DRM",
              "formats": [
                { "format_id": "protected", "height": 1080, "vcodec": "avc1", "acodec": "mp4a", "has_drm": true }
              ]
            }
            """);

        Assert.True(analysis.HasDrm);
        Assert.False(analysis.IsDownloadable);
    }

    private static VideoDownLoader.Models.MediaAnalysis Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return MediaAnalysisService.Parse("https://example.test/video", document.RootElement);
    }
}
