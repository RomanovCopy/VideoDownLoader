using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace VideoDownLoader.Models;

public enum DownloadQueueStatus
{
    Pending,
    Downloading,
    Processing,
    Completed,
    Failed,
    Canceled
}

public sealed class DownloadQueueItem : INotifyPropertyChanged
{
    private DownloadQueueStatus _status = DownloadQueueStatus.Pending;
    private double _progress;
    private string _statusText = "Ожидает";

    public DownloadQueueItem(MediaAnalysis analysis, DownloadOptions options)
    {
        Id = Guid.NewGuid();
        Analysis = analysis;
        Options = options;
    }

    public Guid Id { get; }
    public MediaAnalysis Analysis { get; }
    public DownloadOptions Options { get; }
    public string Title => Analysis.Title;

    public DownloadQueueStatus Status
    {
        get => _status;
        set => SetField(ref _status, value);
    }

    public double Progress
    {
        get => _progress;
        set => SetField(ref _progress, value);
    }

    public string StatusText
    {
        get => _statusText;
        set => SetField(ref _statusText, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Reset()
    {
        Progress = 0;
        Status = DownloadQueueStatus.Pending;
        StatusText = "Ожидает";
    }

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
