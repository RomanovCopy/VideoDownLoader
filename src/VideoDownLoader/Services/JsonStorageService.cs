using System.IO;
using System.Text.Json;
using VideoDownLoader.Models;

namespace VideoDownLoader.Services;

public sealed class JsonStorageService
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _dataDirectory;

    public JsonStorageService(string? dataDirectory = null)
    {
        _dataDirectory = dataDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VideoDownLoader");
    }

    public ApplicationSettings LoadSettings()
    {
        return Load<ApplicationSettings>("settings.json") ?? new ApplicationSettings();
    }

    public void SaveSettings(ApplicationSettings settings)
    {
        Save("settings.json", settings);
    }

    public IReadOnlyList<DownloadHistoryEntry> LoadHistory()
    {
        return Load<List<DownloadHistoryEntry>>("history.json") ?? [];
    }

    public void SaveHistory(IEnumerable<DownloadHistoryEntry> history)
    {
        Save("history.json", history.Take(200).ToList());
    }

    private T? Load<T>(string fileName)
    {
        var path = Path.Combine(_dataDirectory, fileName);
        if (!File.Exists(path))
        {
            return default;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), SerializerOptions);
        }
        catch (JsonException)
        {
            return default;
        }
        catch (IOException)
        {
            return default;
        }
    }

    private void Save<T>(string fileName, T value)
    {
        var temporary = Path.Combine(_dataDirectory, fileName) + ".tmp";
        try
        {
            Directory.CreateDirectory(_dataDirectory);
            var destination = Path.Combine(_dataDirectory, fileName);
            File.WriteAllText(temporary, JsonSerializer.Serialize(value, SerializerOptions));
            File.Move(temporary, destination, overwrite: true);
        }
        catch (IOException)
        {
            TryDelete(temporary);
        }
        catch (UnauthorizedAccessException)
        {
            TryDelete(temporary);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Не мешаем закрытию приложения из-за сбоя необязательного сохранения.
        }
        catch (UnauthorizedAccessException)
        {
            // Не мешаем закрытию приложения из-за сбоя необязательного сохранения.
        }
    }
}
