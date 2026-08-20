using System.Diagnostics;
using VideoDownLoader.Models;
using VideoDownLoader.Services;

namespace VideoDownLoader.Tests;

public sealed class DownloaderServiceTests
{
    [Fact]
    public void AddArguments_VideoOnlyFormat_AddsAudioAndRequestedOptions()
    {
        var startInfo = new ProcessStartInfo();
        var options = CreateOptions() with
        {
            SelectedFormatId = "137",
            SelectedFormatHasVideo = true,
            SelectedFormatHasAudio = false,
            DownloadSubtitles = true,
            SubtitleLanguages = "ru,en",
            CookieBrowser = CookieBrowser.Firefox
        };

        DownloaderService.AddArguments(startInfo, CreateTools(), options);
        var arguments = startInfo.ArgumentList.ToList();

        AssertArgumentPair(arguments, "--format", "137+ba/137");
        AssertArgumentPair(arguments, "--sub-langs", "ru,en");
        AssertArgumentPair(arguments, "--cookies-from-browser", "firefox");
        AssertArgumentPair(arguments, "--js-runtimes", @"deno:C:\tools\deno.exe");
        AssertArgumentPair(arguments, "--sleep-requests", "1");
        AssertArgumentPair(arguments, "--retries", "20");
        Assert.Contains("http:exp=1:20", arguments);
        Assert.Contains("fragment:exp=1:20", arguments);
        Assert.Contains("--embed-subs", arguments);
        Assert.Contains("--embed-metadata", arguments);
        Assert.Equal(options.Url, arguments[^1]);
    }

    [Fact]
    public void AddArguments_PoTokenProvider_AddsPluginAndMwebClient()
    {
        var startInfo = new ProcessStartInfo();
        var options = CreateOptions() with { PoTokenProviderUrl = "http://127.0.0.1:4416" };

        DownloaderService.AddArguments(startInfo, CreateTools(), options);
        var arguments = startInfo.ArgumentList.ToList();

        AssertArgumentPair(arguments, "--plugin-dirs", @"plugins");
        Assert.Contains("youtubepot-bgutilhttp:base_url=http://127.0.0.1:4416", arguments);
        Assert.Contains("youtube:player_client=mweb", arguments);
    }

    [Fact]
    public void AddArguments_AudioOnly_UsesRequestedAudioFormat()
    {
        var startInfo = new ProcessStartInfo();
        var options = CreateOptions() with
        {
            Quality = QualityPreset.AudioOnly,
            AudioFormat = AudioFormat.Opus
        };

        DownloaderService.AddArguments(startInfo, CreateTools(), options);
        var arguments = startInfo.ArgumentList.ToList();

        AssertArgumentPair(arguments, "--audio-format", "opus");
        Assert.Contains("--extract-audio", arguments);
        Assert.DoesNotContain("--merge-output-format", arguments);
    }

    [Fact]
    public void AddArguments_Playlist_ExplicitlyEnablesPlaylistDownload()
    {
        var startInfo = new ProcessStartInfo();
        var options = CreateOptions() with { DownloadPlaylist = true };

        DownloaderService.AddArguments(startInfo, CreateTools(), options);
        var arguments = startInfo.ArgumentList.ToList();

        Assert.Contains("--yes-playlist", arguments);
        Assert.DoesNotContain("--no-playlist", arguments);
    }

    private static DownloadOptions CreateOptions() => new(
        "https://example.test/video",
        @"C:\downloads",
        QualityPreset.Best,
        OutputContainer.Mkv,
        AudioFormat.Mp3,
        null,
        false,
        false,
        false,
        false,
        false,
        "ru,en",
        true,
        true,
        CookieBrowser.None);

    private static ToolPaths CreateTools() =>
        new("yt-dlp.exe", @"C:\tools\ffmpeg.exe", @"C:\tools\deno.exe");

    private static void AssertArgumentPair(IReadOnlyList<string> arguments, string name, string expectedValue)
    {
        var index = arguments.IndexOf(name);
        Assert.True(index >= 0, $"Аргумент {name} не найден.");
        Assert.Equal(expectedValue, arguments[index + 1]);
    }
}

internal static class ListExtensions
{
    public static int IndexOf<T>(this IReadOnlyList<T> source, T value)
    {
        for (var index = 0; index < source.Count; index++)
        {
            if (EqualityComparer<T>.Default.Equals(source[index], value))
            {
                return index;
            }
        }

        return -1;
    }
}
