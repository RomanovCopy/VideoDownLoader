namespace VideoDownLoader.Models;

public enum QualityPreset
{
    Best,
    UpTo1080,
    UpTo720,
    AudioOnly
}

public enum OutputContainer
{
    Mkv,
    Mp4,
    Webm
}

public enum AudioFormat
{
    Mp3,
    M4a,
    Opus
}

public enum CookieBrowser
{
    None,
    Chrome,
    Edge,
    Firefox,
    Brave
}

public enum YtDlpChannel
{
    Nightly,
    Stable
}

public sealed record DownloadOptions(
    string Url,
    string OutputDirectory,
    QualityPreset Quality,
    OutputContainer Container,
    AudioFormat AudioFormat,
    string? SelectedFormatId,
    bool SelectedFormatHasVideo,
    bool SelectedFormatHasAudio,
    bool DownloadPlaylist,
    bool LiveFromStart,
    bool DownloadSubtitles,
    string SubtitleLanguages,
    bool EmbedMetadata,
    bool EmbedThumbnail,
    CookieBrowser CookieBrowser,
    string? PoTokenProviderUrl = null);
