using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using VideoDownLoader.Models;
using VideoDownLoader.Services;

namespace VideoDownLoader;

public partial class MainWindow : Window
{
    private readonly DependencyManager _dependencyManager = new();
    private readonly DownloaderService _downloaderService = new();
    private readonly MediaAnalysisService _analysisService = new();
    private readonly PoTokenProviderService _poTokenProviderService = new();
    private readonly JsonStorageService _storageService = new();
    private readonly ObservableCollection<DownloadQueueItem> _queue = [];
    private readonly ObservableCollection<DownloadHistoryEntry> _history = [];
    private CancellationTokenSource? _operationCancellation;
    private ToolPaths _tools = new(null, null, null);
    private MediaAnalysis? _currentAnalysis;
    private DownloadQueueItem? _activeQueueItem;
    private bool _isQueueRunning;
    private bool _isInstalling;
    private bool _isCheckingToolUpdates;

    public MainWindow()
    {
        InitializeComponent();
        DarkWindowChromeService.Enable(this);
        QueueListView.ItemsSource = _queue;
        HistoryListView.ItemsSource = _history;
        FormatComboBox.ItemsSource = new[] { FormatChoice.Automatic };
        FormatComboBox.SelectedIndex = 0;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        ApplySettings(_storageService.LoadSettings());
        foreach (var entry in _storageService.LoadHistory().OrderByDescending(item => item.Timestamp))
        {
            _history.Add(entry);
        }

        RefreshToolsStatus();
        await CheckToolUpdatesAtStartupAsync();
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if ((_isQueueRunning || _isInstalling) &&
            MessageBox.Show(
                this,
                "Сейчас выполняется операция. Остановить её и закрыть приложение?",
                "Подтверждение закрытия",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            e.Cancel = true;
            return;
        }

        SaveApplicationState();
        _operationCancellation?.Cancel();
        _downloaderService.Cancel();
    }

    private void PasteButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (Clipboard.ContainsText())
            {
                UrlTextBox.Text = Clipboard.GetText().Trim();
                UrlTextBox.CaretIndex = UrlTextBox.Text.Length;
            }
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            StatusTextBlock.Text = "Буфер обмена временно недоступен.";
        }
    }

    private async void AnalyzeButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetUrl(out var url) || !EnsureToolsReady() || !TryGetPoTokenProviderUrl(out var providerUrl))
        {
            return;
        }

        _operationCancellation?.Dispose();
        _operationCancellation = new CancellationTokenSource();
        SetAnalysisBusy(true);
        ResetAnalysis();
        StatusTextBlock.Text = "Анализ ссылки и проверка защиты…";
        GlobalProgressBar.IsIndeterminate = true;

        try
        {
            if (providerUrl is not null)
            {
                var providerVersion = await _poTokenProviderService.CheckAsync(providerUrl, _operationCancellation.Token);
                AppendLog($"PO Token provider {providerVersion}: доступен.");
            }

            _currentAnalysis = await AnalyzeWithRecoveryAsync(
                url,
                GetCookieBrowser(),
                providerUrl,
                _operationCancellation.Token);
            ShowAnalysis(_currentAnalysis);
            StatusTextBlock.Text = _currentAnalysis.IsDownloadable
                ? "Ссылка проверена. Можно добавить загрузку в очередь."
                : "Загрузка заблокирована: источник сообщает DRM-защиту.";
        }
        catch (OperationCanceledException)
        {
            StatusTextBlock.Text = "Анализ отменён.";
        }
        catch (Exception exception)
        {
            StatusTextBlock.Text = "Не удалось проанализировать ссылку.";
            AppendLog($"ОШИБКА АНАЛИЗА: {exception.Message}");
            if (exception is YtDlpException ytDlpException)
            {
                AppendLog($"КЛАССИФИКАЦИЯ: {ytDlpException.Failure.Kind}");
                AppendLog(ytDlpException.Failure.DiagnosticTail);
            }
            MessageBox.Show(this, exception.Message, "Ошибка анализа", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            GlobalProgressBar.IsIndeterminate = false;
            _operationCancellation?.Dispose();
            _operationCancellation = null;
            SetAnalysisBusy(false);
        }
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
        if (_isQueueRunning)
        {
            return;
        }

        _isInstalling = true;
        _operationCancellation = new CancellationTokenSource();
        SetAnalysisBusy(true);
        InstallToolsButton.IsEnabled = false;
        CancelButton.IsEnabled = true;
        GlobalProgressBar.IsIndeterminate = true;
        StatusTextBlock.Text = "Установка инструментов…";
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
                _operationCancellation.Token,
                GetYtDlpChannel());
            RefreshToolsStatus();
            StatusTextBlock.Text = "yt-dlp, FFmpeg и Deno готовы к работе.";
        }
        catch (OperationCanceledException)
        {
            StatusTextBlock.Text = "Установка отменена.";
        }
        catch (Exception exception)
        {
            StatusTextBlock.Text = "Не удалось установить инструменты.";
            AppendLog($"ОШИБКА: {exception.Message}");
            MessageBox.Show(this, exception.Message, "Ошибка установки", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _operationCancellation.Dispose();
            _operationCancellation = null;
            _isInstalling = false;
            GlobalProgressBar.IsIndeterminate = false;
            CancelButton.IsEnabled = false;
            SetAnalysisBusy(false);
            RefreshToolsStatus();
        }
    }

    private void AddToQueueButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentAnalysis is null || !_currentAnalysis.IsDownloadable)
        {
            MessageBox.Show(
                this,
                "Сначала проверьте ссылку. DRM-защищённые источники без доступного обычного формата не добавляются.",
                "Загрузка недоступна",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (!string.Equals(_currentAnalysis.Url, UrlTextBox.Text.Trim(), StringComparison.Ordinal))
        {
            MessageBox.Show(this, "Ссылка изменилась. Выполните проверку ещё раз.", "Нужен повторный анализ",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!TryCreateOptions(out var options))
        {
            return;
        }

        _queue.Add(new DownloadQueueItem(_currentAnalysis, options!));
        StatusTextBlock.Text = "Задание добавлено в очередь.";
        UrlTextBox.SelectAll();
        UrlTextBox.Focus();
    }

    private async void StartQueueButton_Click(object sender, RoutedEventArgs e)
    {
        await RunQueueAsync();
    }

    private async Task RunQueueAsync()
    {
        if (_isQueueRunning || !EnsureToolsReady())
        {
            return;
        }

        var pending = _queue.Where(item => item.Status == DownloadQueueStatus.Pending).ToList();
        if (pending.Count == 0)
        {
            StatusTextBlock.Text = "В очереди нет ожидающих заданий.";
            return;
        }

        _isQueueRunning = true;
        _operationCancellation = new CancellationTokenSource();
        SetAnalysisBusy(true);
        StartQueueButton.IsEnabled = false;
        CancelButton.IsEnabled = true;
        InstallToolsButton.IsEnabled = false;
        var completed = 0;
        var failed = 0;
        var canceled = 0;
        using var sleepBlocker = new SystemSleepBlocker();

        try
        {
            foreach (var item in pending)
            {
                if (_operationCancellation.IsCancellationRequested)
                {
                    break;
                }

                _activeQueueItem = item;
                item.Status = DownloadQueueStatus.Downloading;
                item.StatusText = "Подготовка…";
                item.Progress = 0;
                StatusTextBlock.Text = $"Загрузка: {item.Title}";
                AppendLog($"--- {item.Title} ---");

                try
                {
                    EnsureEnoughFreeSpace(item);
                    var progress = new Progress<DownloadProgress>(update => ApplyDownloadProgress(item, update));
                    await DownloadWithRecoveryAsync(item, progress, _operationCancellation.Token);
                    item.Progress = 100;
                    item.Status = DownloadQueueStatus.Completed;
                    item.StatusText = "Завершено";
                    completed++;
                    AddHistory(item, true, "Загрузка завершена");
                }
                catch (OperationCanceledException)
                {
                    item.Status = DownloadQueueStatus.Canceled;
                    item.StatusText = "Отменено пользователем";
                    canceled++;
                    AddHistory(item, false, "Отменено пользователем");
                    break;
                }
                catch (Exception exception)
                {
                    item.Status = DownloadQueueStatus.Failed;
                    item.StatusText = exception.Message;
                    failed++;
                    AppendLog($"ОШИБКА: {exception.Message}");
                    if (exception is YtDlpException ytDlpException)
                    {
                        AppendLog($"КЛАССИФИКАЦИЯ: {ytDlpException.Failure.Kind}");
                        AppendLog("ДИАГНОСТИЧЕСКИЙ ХВОСТ (секреты URL скрыты):");
                        AppendLog(ytDlpException.Failure.DiagnosticTail);
                    }
                    AddHistory(item, false, exception.Message);
                    // Ошибка одного задания не останавливает оставшуюся очередь.
                }
                finally
                {
                    _activeQueueItem = null;
                }
            }
        }
        finally
        {
            _storageService.SaveHistory(_history);
            _operationCancellation.Dispose();
            _operationCancellation = null;
            _isQueueRunning = false;
            SetAnalysisBusy(false);
            StartQueueButton.IsEnabled = true;
            CancelButton.IsEnabled = false;
            InstallToolsButton.IsEnabled = true;
            GlobalProgressBar.Value = 0;
            StatusTextBlock.Text = $"Очередь завершена: успешно — {completed}, ошибок — {failed}, отменено — {canceled}.";
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        StatusTextBlock.Text = "Остановка…";
        _operationCancellation?.Cancel();
        _downloaderService.Cancel();
    }

    private void RetryQueueItem_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not DownloadQueueItem item ||
            item == _activeQueueItem)
        {
            return;
        }

        item.Reset();
        StatusTextBlock.Text = "Задание возвращено в очередь.";
    }

    private void RemoveQueueItem_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is DownloadQueueItem item && item != _activeQueueItem)
        {
            _queue.Remove(item);
        }
    }

    private void OpenHistoryFolder_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is DownloadHistoryEntry entry)
        {
            OpenDirectory(entry.OutputDirectory);
        }
    }

    private void ReuseHistoryUrl_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is DownloadHistoryEntry entry)
        {
            UrlTextBox.Text = entry.Url;
            UrlTextBox.Focus();
        }
    }

    private void ClearHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_history.Count == 0 || MessageBox.Show(
                this,
                "Очистить историю загрузок? Файлы на диске удалены не будут.",
                "Очистка истории",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        _history.Clear();
        _storageService.SaveHistory(_history);
    }

    private void OpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        OpenDirectory(OutputDirectoryTextBox.Text.Trim());
    }

    private void QualityComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateFormatControls();
    }

    private void YtDlpChannelComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (StatusTextBlock is not null && IsLoaded && !_isInstalling && !_isQueueRunning)
        {
            StatusTextBlock.Text = $"Выбран канал yt-dlp: {DependencyManager.ChannelDisplayName(GetYtDlpChannel())}. " +
                                   "Нажмите «Обновить инструменты», чтобы применить его сразу.";
        }
    }

    private void FormatComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateFormatControls();
    }

    private void UpdateFormatControls()
    {
        if (AudioFormatComboBox is null || ContainerComboBox is null ||
            QualityComboBox is null || FormatComboBox is null)
        {
            return;
        }

        var selectedFormat = (FormatComboBox.SelectedItem as FormatChoice)?.Format;
        var exactFormat = selectedFormat is not null;
        var audioOnly = exactFormat
            ? selectedFormat!.HasAudio && !selectedFormat.HasVideo
            : QualityComboBox.SelectedIndex == (int)QualityPreset.AudioOnly;
        QualityComboBox.IsEnabled = !exactFormat;
        AudioFormatComboBox.IsEnabled = audioOnly;
        ContainerComboBox.IsEnabled = !audioOnly;
    }

    private void SubtitlesCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (SubtitleOptionsGrid is not null)
        {
            SubtitleOptionsGrid.Visibility = SubtitlesCheckBox.IsChecked == true
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
    }

    private void PoTokenProviderCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (PoTokenProviderUrlTextBox is not null)
        {
            PoTokenProviderUrlTextBox.IsEnabled = PoTokenProviderCheckBox.IsChecked == true;
            CheckPoTokenProviderButton.IsEnabled = PoTokenProviderCheckBox.IsChecked == true;
            PoTokenProviderStatusTextBlock.Text = PoTokenProviderCheckBox.IsChecked == true
                ? "Provider не проверен"
                : "Provider отключён";
        }
    }

    private async void CheckPoTokenProviderButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetPoTokenProviderUrl(out var providerUrl) || providerUrl is null)
        {
            return;
        }

        CheckPoTokenProviderButton.IsEnabled = false;
        PoTokenProviderStatusTextBlock.Text = "Проверка…";
        try
        {
            var version = await _poTokenProviderService.CheckAsync(providerUrl);
            PoTokenProviderStatusTextBlock.Text = $"Доступен: версия {version} ✓";
            AppendLog($"PO Token provider {version} доступен: {providerUrl}");
        }
        catch (Exception exception)
        {
            PoTokenProviderStatusTextBlock.Text = "Недоступен ✗";
            AppendLog($"PO TOKEN PROVIDER: {exception.Message}");
            MessageBox.Show(this, exception.Message, "Проверка PO Token provider",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            CheckPoTokenProviderButton.IsEnabled = PoTokenProviderCheckBox.IsChecked == true;
        }
    }

    private void CopyDiagnosticsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(YtDlpDiagnostics.Sanitize(LogTextBox.Text));
            StatusTextBlock.Text = "Диагностика скопирована; параметры доступа в URL скрыты.";
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            StatusTextBlock.Text = "Буфер обмена временно недоступен.";
        }
    }

    private void ShowAnalysis(MediaAnalysis analysis)
    {
        AnalysisPanel.Visibility = Visibility.Visible;
        AnalysisTitleTextBlock.Text = analysis.Title;
        var details = new List<string>();
        if (!string.IsNullOrWhiteSpace(analysis.Channel))
        {
            details.Add(analysis.Channel);
        }

        if (analysis.Duration is { } duration)
        {
            details.Add(duration.TotalHours >= 1 ? duration.ToString(@"h\:mm\:ss") : duration.ToString(@"m\:ss"));
        }

        if (analysis.IsLive)
        {
            details.Add("прямой эфир");
        }

        if (analysis.IsPlaylist)
        {
            details.Add(analysis.PlaylistCount is { } count ? $"плейлист: {count}" : "плейлист");
        }

        if (analysis.EstimatedFileSize is { } size)
        {
            details.Add(FormatBytes(size));
        }

        AnalysisDetailsTextBlock.Text = string.Join(" · ", details);
        SetThumbnail(analysis.ThumbnailUrl);

        if (analysis.HasDrm)
        {
            DrmWarningBorder.Visibility = Visibility.Visible;
            DrmWarningTextBlock.Text = analysis.IsDownloadable
                ? "Источник содержит DRM-защищённые форматы. Они исключены; доступны только обычные форматы."
                : "Обнаружена DRM-защита, а обычного доступного формата или ключа источник не предоставляет. Загрузка отключена.";
        }
        else
        {
            DrmWarningBorder.Visibility = Visibility.Collapsed;
        }

        var choices = new List<FormatChoice> { FormatChoice.Automatic };
        choices.AddRange(analysis.Formats
            .Where(format => !format.HasDrm)
            .Take(100)
            .Select(format => new FormatChoice(format.DisplayName, format)));
        FormatComboBox.ItemsSource = choices;
        FormatComboBox.SelectedIndex = 0;
        AddToQueueButton.IsEnabled = analysis.IsDownloadable;
    }

    private void ResetAnalysis()
    {
        _currentAnalysis = null;
        AnalysisPanel.Visibility = Visibility.Collapsed;
        ThumbnailImage.Source = null;
        FormatComboBox.ItemsSource = new[] { FormatChoice.Automatic };
        FormatComboBox.SelectedIndex = 0;
        AddToQueueButton.IsEnabled = false;
    }

    private bool TryCreateOptions(out DownloadOptions? options)
    {
        options = null;
        var outputDirectory = OutputDirectoryTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            MessageBox.Show(this, "Выберите папку для сохранения.", "Не указана папка",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        try
        {
            Directory.CreateDirectory(outputDirectory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            MessageBox.Show(this, $"Папка недоступна: {exception.Message}", "Ошибка папки",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        if (!TryGetPoTokenProviderUrl(out var providerUrl))
        {
            return false;
        }

        var choice = FormatComboBox.SelectedItem as FormatChoice ?? FormatChoice.Automatic;
        var selectedFormat = choice.Format;
        options = new DownloadOptions(
            _currentAnalysis!.Url,
            outputDirectory,
            (QualityPreset)Math.Max(0, QualityComboBox.SelectedIndex),
            (OutputContainer)Math.Max(0, ContainerComboBox.SelectedIndex),
            (AudioFormat)Math.Max(0, AudioFormatComboBox.SelectedIndex),
            selectedFormat?.Id,
            selectedFormat?.HasVideo == true,
            selectedFormat?.HasAudio == true,
            PlaylistCheckBox.IsChecked == true,
            LiveFromStartCheckBox.IsChecked == true,
            SubtitlesCheckBox.IsChecked == true,
            SubtitleLanguagesTextBox.Text.Trim(),
            MetadataCheckBox.IsChecked == true,
            ThumbnailCheckBox.IsChecked == true,
            GetCookieBrowser(),
            providerUrl);
        return true;
    }

    private void ApplyDownloadProgress(DownloadQueueItem item, DownloadProgress update)
    {
        if (update.Percentage is { } percentage)
        {
            item.Progress = percentage;
            GlobalProgressBar.Value = percentage;
        }

        if (update.Message.Contains("[Merger]", StringComparison.OrdinalIgnoreCase) ||
            update.Message.Contains("[VideoRemuxer]", StringComparison.OrdinalIgnoreCase) ||
            update.Message.Contains("[ExtractAudio]", StringComparison.OrdinalIgnoreCase) ||
            update.Message.Contains("[Embed", StringComparison.OrdinalIgnoreCase))
        {
            item.Status = DownloadQueueStatus.Processing;
            item.StatusText = "Обработка и объединение…";
        }
        else if (update.Message.Contains("[download]", StringComparison.OrdinalIgnoreCase))
        {
            item.StatusText = ExtractDownloadSummary(update.Message);
        }
        else
        {
            item.StatusText = "Выполнение…";
        }

        AppendLog(update.Message);
    }

    private void AddHistory(DownloadQueueItem item, bool succeeded, string result)
    {
        _history.Insert(0, new DownloadHistoryEntry(
            Guid.NewGuid(),
            DateTimeOffset.Now,
            item.Title,
            item.Options.Url,
            item.Options.OutputDirectory,
            succeeded,
            result));
        while (_history.Count > 200)
        {
            _history.RemoveAt(_history.Count - 1);
        }
    }

    private bool TryGetUrl(out string url)
    {
        url = UrlTextBox.Text.Trim();
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            return true;
        }

        MessageBox.Show(this, "Введите корректную ссылку HTTP или HTTPS.", "Некорректная ссылка",
            MessageBoxButton.OK, MessageBoxImage.Warning);
        UrlTextBox.Focus();
        return false;
    }

    private bool EnsureToolsReady()
    {
        _tools = _dependencyManager.FindTools();
        if (_tools.IsReady)
        {
            return true;
        }

        MessageBox.Show(this, "Сначала установите yt-dlp, FFmpeg и Deno.", "Не найдены зависимости",
            MessageBoxButton.OK, MessageBoxImage.Information);
        return false;
    }

    private async Task CheckToolUpdatesAtStartupAsync()
    {
        if (!_tools.IsReady || _isInstalling)
        {
            return;
        }

        _isCheckingToolUpdates = true;
        InstallToolsButton.IsEnabled = false;
        ToolsStatusTextBlock.Text = "Инструменты: проверка обновлений…";
        StatusTextBlock.Text = "Проверка обновлений yt-dlp, FFmpeg и Deno…";

        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        try
        {
            var channel = GetYtDlpChannel();
            var update = await _dependencyManager.CheckForUpdatesAsync(_tools, channel, timeout.Token);
            if (!IsLoaded)
            {
                return;
            }

            if ((update.YtDlpUpdateAvailable || update.DenoUpdateAvailable) &&
                AutoRepairYouTubeCheckBox.IsChecked == true)
            {
                AppendLog("Автообновление YouTube-компонентов…");
                var progress = new Progress<string>(AppendLog);
                _tools = await _dependencyManager.UpdateYouTubeComponentsAsync(channel, progress, timeout.Token);
                ToolsStatusTextBlock.Text = $"YouTube-компоненты обновлены: {DependencyManager.ChannelDisplayName(channel)} ✓";
                StatusTextBlock.Text = "Готово к загрузке. YouTube-компоненты обновлены автоматически.";
                update = await _dependencyManager.CheckForUpdatesAsync(_tools, channel, timeout.Token);
            }

            if (update.IsUpdateAvailable)
            {
                var components = new List<string>();
                if (update.YtDlpUpdateAvailable)
                {
                    components.Add($"yt-dlp {update.CurrentYtDlpVersion} → {update.LatestYtDlpVersion}");
                }

                if (update.FfmpegUpdateAvailable)
                {
                    components.Add("FFmpeg");
                }

                if (update.DenoUpdateAvailable)
                {
                    components.Add($"Deno {update.CurrentDenoVersion} → {update.LatestDenoVersion}");
                }

                var description = string.Join(", ", components);
                ToolsStatusTextBlock.Text = $"Доступно обновление: {description}";
                StatusTextBlock.Text = "Доступны новые инструменты. Нажмите «Обновить инструменты».";
                InstallToolsButton.Content = "Обновить инструменты";
                InstallToolsButton.SetResourceReference(BackgroundProperty, "AccentBrush");
                InstallToolsButton.SetResourceReference(ForegroundProperty, "AccentTextBrush");
                AppendLog($"Обнаружено обновление: {description}.");
            }
            else
            {
                ToolsStatusTextBlock.Text = $"Инструменты актуальны: yt-dlp {update.CurrentYtDlpVersion} ✓   FFmpeg ✓   Deno {update.CurrentDenoVersion} ✓";
                StatusTextBlock.Text = "Готово к загрузке. Инструменты актуальны.";
                InstallToolsButton.ClearValue(BackgroundProperty);
                InstallToolsButton.ClearValue(ForegroundProperty);
            }
        }
        catch (OperationCanceledException)
        {
            if (IsLoaded)
            {
                ToolsStatusTextBlock.Text = "Инструменты установлены; проверка обновлений отложена.";
                StatusTextBlock.Text = "Готово к загрузке.";
                AppendLog("Автоматическая проверка или установка обновлений превысила время ожидания.");
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or InvalidDataException or InvalidOperationException)
        {
            if (IsLoaded)
            {
                ToolsStatusTextBlock.Text = "Инструменты: yt-dlp ✓   FFmpeg ✓   Deno ✓";
                StatusTextBlock.Text = "Готово к загрузке. Проверить обновления не удалось.";
                AppendLog($"Проверка обновлений: {exception.Message}");
            }
        }
        finally
        {
            _isCheckingToolUpdates = false;
            if (IsLoaded)
            {
                InstallToolsButton.IsEnabled = !_isQueueRunning && !_isInstalling;
            }
        }
    }

    private void RefreshToolsStatus()
    {
        _tools = _dependencyManager.FindTools();
        ToolsStatusTextBlock.Text = _tools.IsReady
            ? "Инструменты: yt-dlp ✓   FFmpeg ✓   Deno ✓"
            : $"Инструменты: yt-dlp {Mark(_tools.YtDlp)}   FFmpeg {Mark(_tools.Ffmpeg)}   Deno {Mark(_tools.Deno)}";
        InstallToolsButton.Content = _tools.IsReady ? "Обновить инструменты" : "Установить инструменты";
        InstallToolsButton.ClearValue(BackgroundProperty);
        InstallToolsButton.ClearValue(ForegroundProperty);
        InstallToolsButton.IsEnabled = !_isQueueRunning && !_isInstalling && !_isCheckingToolUpdates;
        if (!_tools.IsReady)
        {
            StatusTextBlock.Text = "Для первого запуска установите инструменты.";
        }
    }

    private void SetAnalysisBusy(bool isBusy)
    {
        UrlTextBox.IsEnabled = !isBusy;
        PasteButton.IsEnabled = !isBusy;
        AnalyzeButton.IsEnabled = !isBusy;
        CookieBrowserComboBox.IsEnabled = !isBusy;
        YtDlpChannelComboBox.IsEnabled = !isBusy;
        AutoRepairYouTubeCheckBox.IsEnabled = !isBusy;
        PoTokenProviderCheckBox.IsEnabled = !isBusy;
        PoTokenProviderUrlTextBox.IsEnabled = !isBusy && PoTokenProviderCheckBox.IsChecked == true;
        CheckPoTokenProviderButton.IsEnabled = !isBusy && PoTokenProviderCheckBox.IsChecked == true;
        InstallToolsButton.IsEnabled = !isBusy && !_isQueueRunning && !_isInstalling && !_isCheckingToolUpdates;
        StartQueueButton.IsEnabled = !isBusy && !_isQueueRunning;
        AddToQueueButton.IsEnabled = !isBusy && _currentAnalysis?.IsDownloadable == true;
    }

    private void ApplySettings(ApplicationSettings settings)
    {
        OutputDirectoryTextBox.Text = !string.IsNullOrWhiteSpace(settings.OutputDirectory)
            ? settings.OutputDirectory
            : GetDefaultOutputDirectory();
        QualityComboBox.SelectedIndex = (int)settings.Quality;
        ContainerComboBox.SelectedIndex = (int)settings.Container;
        AudioFormatComboBox.SelectedIndex = (int)settings.AudioFormat;
        PlaylistCheckBox.IsChecked = settings.DownloadPlaylist;
        LiveFromStartCheckBox.IsChecked = settings.LiveFromStart;
        SubtitlesCheckBox.IsChecked = settings.DownloadSubtitles;
        SubtitleLanguagesTextBox.Text = settings.SubtitleLanguages;
        MetadataCheckBox.IsChecked = settings.EmbedMetadata;
        ThumbnailCheckBox.IsChecked = settings.EmbedThumbnail;
        CookieBrowserComboBox.SelectedIndex = (int)settings.CookieBrowser;
        YtDlpChannelComboBox.SelectedIndex = (int)settings.YtDlpChannel;
        AutoRepairYouTubeCheckBox.IsChecked = settings.AutoRepairYouTube;
        PoTokenProviderCheckBox.IsChecked = settings.UsePoTokenProvider;
        PoTokenProviderUrlTextBox.Text = settings.PoTokenProviderUrl;
        PoTokenProviderUrlTextBox.IsEnabled = settings.UsePoTokenProvider;
        CheckPoTokenProviderButton.IsEnabled = settings.UsePoTokenProvider;
    }

    private void SaveApplicationState()
    {
        _storageService.SaveSettings(new ApplicationSettings
        {
            OutputDirectory = OutputDirectoryTextBox.Text.Trim(),
            Quality = (QualityPreset)Math.Max(0, QualityComboBox.SelectedIndex),
            Container = (OutputContainer)Math.Max(0, ContainerComboBox.SelectedIndex),
            AudioFormat = (AudioFormat)Math.Max(0, AudioFormatComboBox.SelectedIndex),
            DownloadPlaylist = PlaylistCheckBox.IsChecked == true,
            LiveFromStart = LiveFromStartCheckBox.IsChecked == true,
            DownloadSubtitles = SubtitlesCheckBox.IsChecked == true,
            SubtitleLanguages = SubtitleLanguagesTextBox.Text.Trim(),
            EmbedMetadata = MetadataCheckBox.IsChecked == true,
            EmbedThumbnail = ThumbnailCheckBox.IsChecked == true,
            CookieBrowser = GetCookieBrowser(),
            YtDlpChannel = GetYtDlpChannel(),
            AutoRepairYouTube = AutoRepairYouTubeCheckBox.IsChecked == true,
            UsePoTokenProvider = PoTokenProviderCheckBox.IsChecked == true,
            PoTokenProviderUrl = PoTokenProviderUrlTextBox.Text.Trim()
        });
        _storageService.SaveHistory(_history);
    }

    private CookieBrowser GetCookieBrowser() =>
        (CookieBrowser)Math.Max(0, CookieBrowserComboBox.SelectedIndex);

    private YtDlpChannel GetYtDlpChannel() =>
        (YtDlpChannel)Math.Max(0, YtDlpChannelComboBox.SelectedIndex);

    private bool TryGetPoTokenProviderUrl(out string? providerUrl)
    {
        providerUrl = null;
        if (PoTokenProviderCheckBox.IsChecked != true)
        {
            return true;
        }

        var value = PoTokenProviderUrlTextBox.Text.Trim();
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            providerUrl = uri.GetLeftPart(UriPartial.Authority);
            return true;
        }

        MessageBox.Show(this, "Введите корректный HTTP/HTTPS-адрес PO Token provider.",
            "Некорректный адрес provider", MessageBoxButton.OK, MessageBoxImage.Warning);
        PoTokenProviderUrlTextBox.Focus();
        return false;
    }

    private async Task DownloadWithRecoveryAsync(
        DownloadQueueItem item,
        IProgress<DownloadProgress> progress,
        CancellationToken cancellationToken)
    {
        AppendLog($"КОНФИГУРАЦИЯ: yt-dlp {DependencyManager.ChannelDisplayName(GetYtDlpChannel())}; " +
                  $"Deno — включён; PO provider — {(item.Options.PoTokenProviderUrl is null ? "нет" : item.Options.PoTokenProviderUrl)}");
        if (item.Options.PoTokenProviderUrl is { } providerUrl)
        {
            var providerVersion = await _poTokenProviderService.CheckAsync(providerUrl, cancellationToken);
            AppendLog($"PO Token provider {providerVersion}: endpoint /ping доступен.");
        }

        try
        {
            await _downloaderService.DownloadAsync(_tools, item.Options, progress, cancellationToken);
        }
        catch (YtDlpException exception) when (
            exception.Failure.Kind == YtDlpFailureKind.Network &&
            AutoRepairYouTubeCheckBox.IsChecked == true &&
            !cancellationToken.IsCancellationRequested)
        {
            item.Status = DownloadQueueStatus.Processing;
            item.StatusText = "Сеть нестабильна; повтор через 10 секунд…";
            AppendLog("СЕТЬ: внутренние повторы исчерпаны. Через 10 секунд процесс будет перезапущен один раз с продолжением .part-файла.");
            await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
            item.Status = DownloadQueueStatus.Downloading;
            item.StatusText = "Продолжение загрузки…";
            await _downloaderService.DownloadAsync(_tools, item.Options, progress, cancellationToken);
        }
        catch (YtDlpException exception) when (
            exception.Failure.CanRepairByUpdating &&
            AutoRepairYouTubeCheckBox.IsChecked == true &&
            !cancellationToken.IsCancellationRequested)
        {
            AppendLog($"АВТОВОССТАНОВЛЕНИЕ: {exception.Failure.Kind}. {exception.Message}");
            item.Status = DownloadQueueStatus.Processing;
            item.StatusText = "Обновление YouTube-компонентов…";
            var updateProgress = new Progress<string>(message =>
            {
                item.StatusText = message;
                AppendLog(message);
            });
            _tools = await _dependencyManager.UpdateYouTubeComponentsAsync(
                GetYtDlpChannel(),
                updateProgress,
                cancellationToken);
            item.Status = DownloadQueueStatus.Downloading;
            item.StatusText = "Повтор после обновления…";
            AppendLog("ПОВТОР: одна попытка после проверенного обновления yt-dlp и Deno.");
            await _downloaderService.DownloadAsync(_tools, item.Options, progress, cancellationToken);
        }
    }

    private async Task<MediaAnalysis> AnalyzeWithRecoveryAsync(
        string url,
        CookieBrowser cookieBrowser,
        string? providerUrl,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _analysisService.AnalyzeAsync(
                _tools, url, cookieBrowser, providerUrl, cancellationToken);
        }
        catch (YtDlpException exception) when (
            exception.Failure.Kind == YtDlpFailureKind.Network &&
            AutoRepairYouTubeCheckBox.IsChecked == true &&
            !cancellationToken.IsCancellationRequested)
        {
            AppendLog("СЕТЬ: повтор анализа через 3 секунды.");
            await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
            return await _analysisService.AnalyzeAsync(
                _tools, url, cookieBrowser, providerUrl, cancellationToken);
        }
        catch (YtDlpException exception) when (
            exception.Failure.CanRepairByUpdating &&
            AutoRepairYouTubeCheckBox.IsChecked == true &&
            !cancellationToken.IsCancellationRequested)
        {
            AppendLog($"АВТОВОССТАНОВЛЕНИЕ АНАЛИЗА: {exception.Failure.Kind}. {exception.Message}");
            var progress = new Progress<string>(message =>
            {
                StatusTextBlock.Text = message;
                AppendLog(message);
            });
            _tools = await _dependencyManager.UpdateYouTubeComponentsAsync(
                GetYtDlpChannel(), progress, cancellationToken);
            AppendLog("ПОВТОР АНАЛИЗА после обновления yt-dlp и Deno.");
            return await _analysisService.AnalyzeAsync(
                _tools, url, cookieBrowser, providerUrl, cancellationToken);
        }
    }

    private void SetThumbnail(string? url)
    {
        ThumbnailImage.Source = null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return;
        }

        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = uri;
            image.DecodePixelWidth = 240;
            image.CacheOption = BitmapCacheOption.OnDemand;
            image.EndInit();
            ThumbnailImage.Source = image;
        }
        catch (Exception exception) when (exception is NotSupportedException or IOException)
        {
            AppendLog($"Не удалось показать обложку: {exception.Message}");
        }
    }

    private void OpenDirectory(string directory)
    {
        if (!Directory.Exists(directory))
        {
            MessageBox.Show(this, "Указанная папка не существует.", "Папка не найдена",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var startInfo = new ProcessStartInfo { FileName = "explorer.exe", UseShellExecute = true };
        startInfo.ArgumentList.Add(directory);
        Process.Start(startInfo);
    }

    private void AppendLog(string message)
    {
        if (LogTextBox.Text.Length > 200_000)
        {
            LogTextBox.Clear();
        }

        LogTextBox.AppendText(message + Environment.NewLine);
        LogTextBox.ScrollToEnd();
    }

    private static string ExtractDownloadSummary(string line)
    {
        var marker = line.IndexOf("[download]", StringComparison.OrdinalIgnoreCase);
        return marker >= 0 ? line[(marker + "[download]".Length)..].Trim() : "Загрузка…";
    }

    private static void EnsureEnoughFreeSpace(DownloadQueueItem item)
    {
        if (item.Analysis.IsPlaylist)
        {
            return;
        }

        var estimatedSize = item.Analysis.Formats
            .FirstOrDefault(format => format.Id == item.Options.SelectedFormatId)?.FileSize ??
            item.Analysis.EstimatedFileSize;
        if (estimatedSize is null)
        {
            return;
        }

        var fullPath = Path.GetFullPath(item.Options.OutputDirectory);
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrWhiteSpace(root))
        {
            return;
        }

        var drive = new DriveInfo(root);
        var reserve = Math.Max(256L * 1024 * 1024, estimatedSize.Value / 10);
        if (drive.AvailableFreeSpace < estimatedSize.Value + reserve)
        {
            throw new IOException(
                $"Недостаточно места на диске {drive.Name}: требуется примерно {FormatBytes(estimatedSize.Value + reserve)}, " +
                $"доступно {FormatBytes(drive.AvailableFreeSpace)}.");
        }
    }

    private static string FormatBytes(long bytes)
    {
        var value = (double)bytes;
        string[] units = ["Б", "КБ", "МБ", "ГБ", "ТБ"];
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return string.Create(CultureInfo.CurrentCulture, $"{value:0.#} {units[unit]}");
    }

    private static string Mark(string? path) => path is null ? "✗" : "✓";

    private static string GetDefaultOutputDirectory()
    {
        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        return Directory.Exists(downloads)
            ? downloads
            : Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
    }

    private sealed record FormatChoice(string DisplayName, MediaFormat? Format)
    {
        public static FormatChoice Automatic { get; } = new("Автоматически по выбранному качеству", null);

        public override string ToString() => DisplayName;
    }
}
