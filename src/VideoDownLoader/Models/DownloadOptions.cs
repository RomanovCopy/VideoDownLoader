namespace VideoDownLoader.Models;

public sealed record DownloadOptions(
    string Url,
    string OutputDirectory,
    string Quality,
    string Container,
    bool DownloadPlaylist,
    bool LiveFromStart);
