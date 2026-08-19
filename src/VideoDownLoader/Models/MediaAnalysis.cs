namespace VideoDownLoader.Models;

public sealed record MediaFormat(
    string Id,
    string DisplayName,
    int? Width,
    int? Height,
    double? FramesPerSecond,
    string? VideoCodec,
    string? AudioCodec,
    long? FileSize,
    bool HasVideo,
    bool HasAudio,
    bool HasDrm);

public sealed record MediaAnalysis(
    string Url,
    string Title,
    string? Channel,
    TimeSpan? Duration,
    string? ThumbnailUrl,
    bool IsLive,
    bool IsPlaylist,
    int? PlaylistCount,
    bool HasDrm,
    bool IsDownloadable,
    long? EstimatedFileSize,
    IReadOnlyList<MediaFormat> Formats);
