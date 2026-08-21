using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VideoDownLoader.Models;

public sealed class WebsiteImageItem : INotifyPropertyChanged
{
    private bool _isSelected = true;
    private string _status = "Готово к сохранению";

    public WebsiteImageItem(
        Uri url,
        string source,
        string? description = null,
        int? width = null,
        int? height = null,
        long? fileSize = null,
        byte[]? previewData = null,
        string? contentFingerprint = null,
        Uri? referrer = null)
    {
        Url = url;
        Source = source;
        Description = description;
        Width = width;
        Height = height;
        FileSize = fileSize;
        ContentFingerprint = contentFingerprint;
        Referrer = referrer;
        Preview = CreatePreview(previewData);
    }

    public Uri Url { get; }

    public string Source { get; }

    public string? Description { get; }

    public int? Width { get; }

    public int? Height { get; }

    public long? FileSize { get; }

    public string? ContentFingerprint { get; }

    public Uri? Referrer { get; }

    public ImageSource? Preview { get; }

    public string Address => Url.AbsoluteUri;

    public string DisplayName
    {
        get
        {
            var name = Uri.UnescapeDataString(Path.GetFileName(Url.AbsolutePath));
            return string.IsNullOrWhiteSpace(name) ? Url.Host : name;
        }
    }

    public string QualityDescription
    {
        get
        {
            var dimensions = Width is { } width && Height is { } height
                ? $"{width} × {height} px"
                : "размер неизвестен";
            var size = FileSize is { } bytes
                ? bytes >= 1024 * 1024
                    ? $"{bytes / 1024d / 1024d:0.0} МБ"
                    : $"{Math.Max(1, bytes / 1024d):0} КБ"
                : null;
            return size is null ? dimensions : $"{dimensions} · {size}";
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
            {
                return;
            }

            _isSelected = value;
            OnPropertyChanged();
        }
    }

    public string Status
    {
        get => _status;
        set
        {
            if (_status == value)
            {
                return;
            }

            _status = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private static ImageSource? CreatePreview(byte[]? data)
    {
        if (data is null || data.Length == 0)
        {
            return null;
        }

        try
        {
            using var stream = new MemoryStream(data, writable: false);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.DecodePixelWidth = 208;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception exception) when (exception is NotSupportedException or IOException or
                                           COMException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }
}
