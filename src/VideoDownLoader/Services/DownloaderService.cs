using System.IO;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Collections.Concurrent;
using VideoDownLoader.Models;

namespace VideoDownLoader.Services;

public sealed record DownloadProgress(double? Percentage, string Message);

public sealed class DownloaderService
{
    private static readonly Regex PercentageRegex = new(
        @"(?<value>\d{1,3}(?:[\.,]\d+)?)%",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly object _processLock = new();
    private Process? _activeProcess;

    public async Task DownloadAsync(
        ToolPaths tools,
        DownloadOptions options,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!tools.IsReady)
        {
            throw new InvalidOperationException("Сначала установите yt-dlp, FFmpeg и Deno.");
        }

        Directory.CreateDirectory(options.OutputDirectory);

        var startInfo = new ProcessStartInfo
        {
            FileName = tools.YtDlp!,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        AddArguments(startInfo, tools, options);
        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var diagnosticLines = new ConcurrentQueue<string>();

        lock (_processLock)
        {
            _activeProcess = process;
        }

        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("Не удалось запустить yt-dlp.");
            }

            using var cancellationRegistration = cancellationToken.Register(() =>
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // Процесс уже завершён.
                }
            });

            var outputTask = ReadOutputAsync(process.StandardOutput, progress, CaptureDiagnosticLine);
            var errorTask = ReadOutputAsync(process.StandardError, progress, CaptureDiagnosticLine);

            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(outputTask, errorTask);

            cancellationToken.ThrowIfCancellationRequested();

            if (process.ExitCode != 0)
            {
                throw new YtDlpException(process.ExitCode, YtDlpDiagnostics.Classify(diagnosticLines));
            }

            return;

            void CaptureDiagnosticLine(string line)
            {
                diagnosticLines.Enqueue(line);
                while (diagnosticLines.Count > 200 && diagnosticLines.TryDequeue(out _))
                {
                }
            }
        }
        finally
        {
            lock (_processLock)
            {
                if (ReferenceEquals(_activeProcess, process))
                {
                    _activeProcess = null;
                }
            }
        }
    }

    public void Cancel()
    {
        lock (_processLock)
        {
            try
            {
                if (_activeProcess is { HasExited: false })
                {
                    _activeProcess.Kill(entireProcessTree: true);
                }
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Процесс успел завершиться.
            }
        }
    }

    internal static void AddArguments(
        ProcessStartInfo startInfo,
        ToolPaths tools,
        DownloadOptions options)
    {
        Add(startInfo,
            "--ignore-config",
            "--newline",
            "--no-color",
            "--windows-filenames",
            "--continue",
            "--part",
            "--retries", "20",
            "--fragment-retries", "20",
            "--retry-sleep", "http:exp=1:20",
            "--retry-sleep", "fragment:exp=1:20",
            "--socket-timeout", "30",
            "--sleep-requests", "1",
            "--js-runtimes", $"deno:{tools.Deno}",
            "--paths", options.OutputDirectory,
            "--output", "%(title).180B [%(id)s].%(ext)s",
            "--ffmpeg-location", Path.GetDirectoryName(tools.Ffmpeg!)!);

        if (options.CookieBrowser != CookieBrowser.None)
        {
            Add(startInfo, "--cookies-from-browser", options.CookieBrowser.ToString().ToLowerInvariant());
        }

        if (Uri.TryCreate(options.PoTokenProviderUrl, UriKind.Absolute, out var providerUri) &&
            (providerUri.Scheme == Uri.UriSchemeHttp || providerUri.Scheme == Uri.UriSchemeHttps))
        {
            Add(startInfo,
                "--plugin-dirs", Path.Combine(Path.GetDirectoryName(tools.YtDlp!)!, "plugins"),
                "--extractor-args", $"youtubepot-bgutilhttp:base_url={providerUri.GetLeftPart(UriPartial.Authority)}",
                "--extractor-args", "youtube:player_client=mweb");
        }

        if (!options.DownloadPlaylist)
        {
            Add(startInfo, "--no-playlist");
        }
        else
        {
            Add(startInfo, "--yes-playlist");
        }

        if (options.LiveFromStart)
        {
            Add(startInfo, "--live-from-start");
        }

        if (!string.IsNullOrWhiteSpace(options.SelectedFormatId))
        {
            var expression = options.SelectedFormatHasVideo && !options.SelectedFormatHasAudio
                ? $"{options.SelectedFormatId}+ba/{options.SelectedFormatId}"
                : options.SelectedFormatId;
            Add(startInfo, "--format", expression);
        }
        else
        {
            switch (options.Quality)
            {
                case QualityPreset.UpTo1080:
                    Add(startInfo, "--format", "bv*[height<=1080]+ba/b[height<=1080]");
                    break;
                case QualityPreset.UpTo720:
                    Add(startInfo, "--format", "bv*[height<=720]+ba/b[height<=720]");
                    break;
                case QualityPreset.AudioOnly:
                    Add(startInfo, "--format", "ba/b");
                    break;
                default:
                    Add(startInfo, "--format", "bv*+ba/b");
                    break;
            }
        }

        if (options.Quality == QualityPreset.AudioOnly ||
            (!options.SelectedFormatHasVideo && options.SelectedFormatHasAudio))
        {
            Add(startInfo,
                "--extract-audio",
                "--audio-format", options.AudioFormat.ToString().ToLowerInvariant());
        }
        else
        {
            var container = options.Container.ToString().ToLowerInvariant();
            Add(startInfo, "--merge-output-format", container, "--remux-video", container);
        }

        if (options.DownloadSubtitles)
        {
            var languages = string.IsNullOrWhiteSpace(options.SubtitleLanguages)
                ? "all"
                : options.SubtitleLanguages.Trim();
            Add(startInfo,
                "--write-subs",
                "--write-auto-subs",
                "--sub-langs", languages,
                "--embed-subs");
        }

        if (options.EmbedMetadata)
        {
            Add(startInfo, "--embed-metadata", "--embed-chapters");
        }

        if (options.EmbedThumbnail)
        {
            Add(startInfo, "--embed-thumbnail");
        }

        Add(startInfo, options.Url);
    }

    private static void Add(ProcessStartInfo startInfo, params string[] arguments)
    {
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
    }

    private static async Task ReadOutputAsync(
        StreamReader reader,
        IProgress<DownloadProgress>? progress,
        Action<string>? capture = null)
    {
        while (await reader.ReadLineAsync() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            capture?.Invoke(line);

            double? percentage = null;
            var match = PercentageRegex.Match(line);
            if (match.Success)
            {
                var normalized = match.Groups["value"].Value.Replace(',', '.');
                if (double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                {
                    percentage = Math.Clamp(value, 0, 100);
                }
            }

            progress?.Report(new DownloadProgress(percentage, line));
        }
    }
}
