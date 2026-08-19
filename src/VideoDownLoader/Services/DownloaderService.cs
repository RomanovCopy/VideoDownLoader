using System.IO;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
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
            throw new InvalidOperationException("Сначала установите yt-dlp и FFmpeg.");
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
                catch (InvalidOperationException)
                {
                    // Процесс уже завершён.
                }
            });

            var outputTask = ReadOutputAsync(process.StandardOutput, progress);
            var errorTask = ReadOutputAsync(process.StandardError, progress);

            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(outputTask, errorTask);

            cancellationToken.ThrowIfCancellationRequested();

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"yt-dlp завершился с кодом {process.ExitCode}. Подробности находятся в журнале.");
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
            catch (InvalidOperationException)
            {
                // Процесс успел завершиться.
            }
        }
    }

    private static void AddArguments(
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
            "--retries", "10",
            "--fragment-retries", "10",
            "--paths", options.OutputDirectory,
            "--output", "%(title).180B [%(id)s].%(ext)s",
            "--ffmpeg-location", Path.GetDirectoryName(tools.Ffmpeg!)!);

        if (!options.DownloadPlaylist)
        {
            Add(startInfo, "--no-playlist");
        }

        if (options.LiveFromStart)
        {
            Add(startInfo, "--live-from-start");
        }

        switch (options.Quality)
        {
            case "До 1080p":
                Add(startInfo, "--format", "bv*[height<=1080]+ba/b[height<=1080]");
                break;
            case "До 720p":
                Add(startInfo, "--format", "bv*[height<=720]+ba/b[height<=720]");
                break;
            case "Только аудио (MP3)":
                Add(startInfo, "--format", "ba/b", "--extract-audio", "--audio-format", "mp3");
                break;
            default:
                Add(startInfo, "--format", "bv*+ba/b");
                break;
        }

        if (!string.Equals(options.Quality, "Только аудио (MP3)", StringComparison.Ordinal))
        {
            Add(startInfo, "--merge-output-format", options.Container.ToLowerInvariant());
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
        IProgress<DownloadProgress>? progress)
    {
        while (await reader.ReadLineAsync() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

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
