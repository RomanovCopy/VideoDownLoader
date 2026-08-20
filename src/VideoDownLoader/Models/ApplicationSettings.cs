namespace VideoDownLoader.Models;

public sealed record ApplicationSettings
{
    public string OutputDirectory { get; init; } = string.Empty;
    public QualityPreset Quality { get; init; } = QualityPreset.Best;
    public OutputContainer Container { get; init; } = OutputContainer.Mkv;
    public AudioFormat AudioFormat { get; init; } = AudioFormat.Mp3;
    public bool DownloadPlaylist { get; init; }
    public bool LiveFromStart { get; init; }
    public bool DownloadSubtitles { get; init; }
    public string SubtitleLanguages { get; init; } = "ru,en";
    public bool EmbedMetadata { get; init; } = true;
    public bool EmbedThumbnail { get; init; } = true;
    public CookieBrowser CookieBrowser { get; init; } = CookieBrowser.None;
    public YtDlpChannel YtDlpChannel { get; init; } = YtDlpChannel.Nightly;
    public bool AutoRepairYouTube { get; init; } = true;
    public bool UsePoTokenProvider { get; init; }
    public string PoTokenProviderUrl { get; init; } = "http://127.0.0.1:4416";
    public string ImageOutputDirectory { get; init; } = string.Empty;
    public string LastWebsiteUrl { get; init; } = string.Empty;
    public int ImageQualityPresetIndex { get; init; }
    public int ImageScanDepth { get; init; } = 1;
    public int ImageAccessMode { get; init; }
    public IReadOnlyList<string> FavoriteWebsiteUrls { get; init; } = [];
}

public sealed record DownloadHistoryEntry(
    Guid Id,
    DateTimeOffset Timestamp,
    string Title,
    string Url,
    string OutputDirectory,
    bool Succeeded,
    string Result);
