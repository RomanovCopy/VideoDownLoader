using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using VideoDownLoader.Models;
using VideoDownLoader.Services;

namespace VideoDownLoader;

public partial class MainWindow : Window
{
    private readonly DependencyManager _dependencyManager = new();
    private readonly DownloaderService _downloaderService = new();
    private CancellationTokenSource? _operationCancellation;
    private ToolPaths _tools = new(null, null);

    public MainWindow()
    {
        InitializeComponent();
        OutputDirectoryTextBox.Text = GetDefaultOutputDirectory();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        RefreshToolsStatus();
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        _operationCancellation?.Cancel();
        _downloaderService.Cancel();
    }

    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Выберите папку для сохранения",
            InitialDirectory = Directory.Exists(OutputDirectoryTextBox.Text)
                ? OutputDirectoryTextBox.Text
                : GetDefaultOutputDirectory()
        };

        if (dialog.ShowDialog(this) == true)
        {
            OutputDirectoryTextBox.Text = dialog.FolderName;
        }
    }

    private async void InstallToolsButton_Click(object sender, RoutedEventArgs e)
    {
        _operationCancellation = new CancellationTokenSource();
        SetBusy(true, "Установка инструментов…");
        LogTextBox.Clear();

        try
        {
            var progress = new Progress<string>(message =>
            {
                StatusTextBlock.Text = message;
                AppendLog(message);
            });

            _tools = await _dependencyManager.InstallAsync(
                progress,
                _operationCancellation.Token);

            RefreshToolsStatus();
            StatusTextBlock.Text = "yt-dlp и FFmpeg готовы к работе.";
        }
        catch (OperationCanceledException)
        {
            StatusTextBlock.Text = "Установка отменена.";
        }
        catch (Exception exception)
        {
            StatusTextBlock.Text = "Не удалось установить инструменты.";
            AppendLog($"ОШИБКА: {exception.Message}");
            MessageBox.Show(
                this,
                exception.Message,
                "Ошибка установки",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            _operationCancellation.Dispose();
            _operationCancellation = null;
            SetBusy(false);
        }
    }

    private async void DownloadButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryCreateOptions(out var options))
        {
            return;
        }

        _tools = _dependencyManager.FindTools();
        if (!_tools.IsReady)
        {
            MessageBox.Show(
                this,
                "Сначала нажмите «Установить инструменты».",
                "Не найдены зависимости",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        _operationCancellation = new CancellationTokenSource();
        SetBusy(true, "Подготовка загрузки…");
        LogTextBox.Clear();
        DownloadProgressBar.Value = 0;

        try
        {
            var progress = new Progress<DownloadProgress>(update =>
            {
                if (update.Percentage is { } percentage)
                {
                    DownloadProgressBar.IsIndeterminate = false;
                    DownloadProgressBar.Value = percentage;
                }

                StatusTextBlock.Text = GetFriendlyStatus(update.Message);
                AppendLog(update.Message);
            });

            await _downloaderService.DownloadAsync(
                _tools,
                options!,
                progress,
                _operationCancellation.Token);

            DownloadProgressBar.IsIndeterminate = false;
            DownloadProgressBar.Value = 100;
            StatusTextBlock.Text = "Загрузка завершена.";
            AppendLog("Готово.");
        }
        catch (OperationCanceledException)
        {
            DownloadProgressBar.IsIndeterminate = false;
            StatusTextBlock.Text = "Загрузка отменена.";
            AppendLog("Операция отменена пользователем.");
        }
        catch (Exception exception)
        {
            DownloadProgressBar.IsIndeterminate = false;
            StatusTextBlock.Text = "Загрузка завершилась с ошибкой.";
            AppendLog($"ОШИБКА: {exception.Message}");
            MessageBox.Show(
                this,
                exception.Message,
                "Ошибка загрузки",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            _operationCancellation.Dispose();
            _operationCancellation = null;
            SetBusy(false);
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        StatusTextBlock.Text = "Остановка…";
        _operationCancellation?.Cancel();
        _downloaderService.Cancel();
    }

    private void OpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var directory = OutputDirectoryTextBox.Text.Trim();
        if (!Directory.Exists(directory))
        {
            MessageBox.Show(
                this,
                "Указанная папка пока не существует.",
                "Папка не найдена",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = "explorer.exe",
            UseShellExecute = true
        };
        startInfo.ArgumentList.Add(directory);
        Process.Start(startInfo);
    }

    private bool TryCreateOptions(out DownloadOptions? options)
    {
        options = null;
        var url = UrlTextBox.Text.Trim();
        var outputDirectory = OutputDirectoryTextBox.Text.Trim();

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            MessageBox.Show(
                this,
                "Введите корректную ссылку HTTP или HTTPS.",
                "Некорректная ссылка",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            UrlTextBox.Focus();
            return false;
        }

        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            MessageBox.Show(
                this,
                "Выберите папку для сохранения.",
                "Не указана папка",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }

        options = new DownloadOptions(
            url,
            outputDirectory,
            GetComboBoxText(QualityComboBox),
            GetComboBoxText(ContainerComboBox),
            PlaylistCheckBox.IsChecked == true,
            LiveFromStartCheckBox.IsChecked == true);
        return true;
    }

    private void RefreshToolsStatus()
    {
        _tools = _dependencyManager.FindTools();
        ToolsStatusTextBlock.Text = _tools.IsReady
            ? "Инструменты: yt-dlp ✓   FFmpeg ✓"
            : $"Инструменты: yt-dlp {Mark(_tools.YtDlp)}   FFmpeg {Mark(_tools.Ffmpeg)}";
        InstallToolsButton.Content = _tools.IsReady ? "Обновить инструменты" : "Установить инструменты";
        StatusTextBlock.Text = _tools.IsReady
            ? "Готово к загрузке."
            : "Для первого запуска установите инструменты.";
    }

    private void SetBusy(bool isBusy, string? status = null)
    {
        UrlTextBox.IsEnabled = !isBusy;
        OutputDirectoryTextBox.IsEnabled = !isBusy;
        BrowseButton.IsEnabled = !isBusy;
        QualityComboBox.IsEnabled = !isBusy;
        ContainerComboBox.IsEnabled = !isBusy;
        PlaylistCheckBox.IsEnabled = !isBusy;
        LiveFromStartCheckBox.IsEnabled = !isBusy;
        InstallToolsButton.IsEnabled = !isBusy;
        DownloadButton.IsEnabled = !isBusy;
        CancelButton.IsEnabled = isBusy;
        DownloadProgressBar.IsIndeterminate = isBusy;

        if (status is not null)
        {
            StatusTextBlock.Text = status;
        }
    }

    private void AppendLog(string message)
    {
        if (LogTextBox.Text.Length > 100_000)
        {
            LogTextBox.Clear();
        }

        LogTextBox.AppendText(message + Environment.NewLine);
        LogTextBox.ScrollToEnd();
    }

    private static string GetFriendlyStatus(string line)
    {
        if (line.Contains("[download]", StringComparison.OrdinalIgnoreCase))
        {
            return "Загрузка потока…";
        }

        if (line.Contains("[Merger]", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("[VideoRemuxer]", StringComparison.OrdinalIgnoreCase))
        {
            return "Объединение видео и аудио…";
        }

        if (line.Contains("Extracting URL", StringComparison.OrdinalIgnoreCase))
        {
            return "Анализ ссылки…";
        }

        return "Выполнение…";
    }

    private static string GetComboBoxText(ComboBox comboBox) =>
        (comboBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? string.Empty;

    private static string Mark(string? path) => path is null ? "✗" : "✓";

    private static string GetDefaultOutputDirectory()
    {
        var downloads = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads");
        return Directory.Exists(downloads)
            ? downloads
            : Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
    }
}
