using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using VideoDownLoader.Models;
using VideoDownLoader.Services;

namespace VideoDownLoader.Behaviors;

public sealed partial class MainWindowBehavior
{
    private readonly WebsiteImageService _websiteImageService = new();
    private readonly ObservableCollection<WebsiteImageItem> _websiteImages = [];
    private readonly ObservableCollection<string> _favoriteWebsiteUrls = [];
    private CancellationTokenSource? _imageCancellation;
    private WebsiteBrowserSession? _websiteBrowserSession;
    private bool _isImageOperationRunning;
    private long _imageSearchId;

    private void PasteWebsiteUrlButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (Clipboard.ContainsText())
            {
                WebsiteUrlTextBox.Text = Clipboard.GetText().Trim();
                WebsiteUrlTextBox.CaretIndex = WebsiteUrlTextBox.Text.Length;
            }
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            StatusTextBlock.Text = "Буфер обмена временно недоступен.";
        }
    }

    private void FavoriteWebsiteUrlButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetWebsiteUri(out var uri))
        {
            return;
        }

        var address = uri.GetComponents(UriComponents.HttpRequestUrl, UriFormat.UriEscaped);
        var existing = _favoriteWebsiteUrls.FirstOrDefault(item =>
            item.Equals(address, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            _favoriteWebsiteUrls.Insert(0, address);
            while (_favoriteWebsiteUrls.Count > 100)
            {
                _favoriteWebsiteUrls.RemoveAt(_favoriteWebsiteUrls.Count - 1);
            }

            StatusTextBlock.Text = "URL добавлен в избранное.";
        }
        else
        {
            StatusTextBlock.Text = "Этот URL уже есть в избранном.";
        }

        WebsiteUrlTextBox.Text = address;
        SaveApplicationState();
    }

    private void FavoriteWebsiteUrlsComboBox_SelectionChanged(
        object sender,
        System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (FavoriteWebsiteUrlsComboBox.SelectedItem is string address)
        {
            WebsiteUrlTextBox.Text = address;
            WebsiteUrlTextBox.CaretIndex = address.Length;
        }
    }

    private void RemoveFavoriteWebsiteUrlButton_Click(object sender, RoutedEventArgs e)
    {
        var selected = FavoriteWebsiteUrlsComboBox.SelectedItem as string;
        if (selected is null)
        {
            StatusTextBlock.Text = "Выберите URL в списке избранного.";
            return;
        }

        _favoriteWebsiteUrls.Remove(selected);
        FavoriteWebsiteUrlsComboBox.SelectedIndex = -1;
        WebsiteUrlTextBox.Text = selected;
        StatusTextBlock.Text = "URL удалён из избранного.";
        SaveApplicationState();
    }

    private void ClearImageSearchButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isImageOperationRunning)
        {
            return;
        }

        _websiteImages.Clear();
        _websiteBrowserSession = null;
        _imageSearchId++;
        WebsiteUrlTextBox.Text = string.Empty;
        FavoriteWebsiteUrlsComboBox.SelectedIndex = -1;
        GlobalProgressBar.Value = 0;
        ImagesSummaryTextBlock.Text = "Результаты очищены";
        SaveSelectedImagesButton.IsEnabled = false;
        ImageSessionStatusTextBlock.Text = ImageAccessModeComboBox.SelectedIndex == 1
            ? "Сессия ещё не выбрана"
            : "Публичная страница";
        StatusTextBlock.Text = "Поиск изображений очищен.";
    }

    private async void AnalyzeImagesButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isImageOperationRunning)
        {
            return;
        }

        if (_isQueueRunning || _isInstalling)
        {
            StatusTextBlock.Text = "Дождитесь завершения текущей операции с видео или инструментами.";
            return;
        }

        if (!Uri.TryCreate(WebsiteUrlTextBox.Text.Trim(), UriKind.Absolute, out var pageUri) ||
            (pageUri.Scheme != Uri.UriSchemeHttp && pageUri.Scheme != Uri.UriSchemeHttps))
        {
            MessageBox.Show(_window, "Введите корректную HTTP/HTTPS-ссылку на страницу.",
                "Некорректная ссылка", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!EnsureWebsiteSession(pageUri))
        {
            return;
        }

        _websiteImages.Clear();
        UpdateImagesSummary();
        var searchId = ++_imageSearchId;
        var cancellation = new CancellationTokenSource();
        _imageCancellation = cancellation;
        SetImageOperationBusy(true);
        GlobalProgressBar.Value = 0;
        StatusTextBlock.Text = "Поиск изображений и проверка качества…";

        try
        {
            var progress = new Progress<ImageAnalysisProgress>(value =>
            {
                if (searchId != _imageSearchId ||
                    !ReferenceEquals(_imageCancellation, cancellation))
                {
                    return;
                }

                GlobalProgressBar.Value = value.Total == 0 ? 0 : value.Processed * 100d / value.Total;
                StatusTextBlock.Text = value.Stage == ImageAnalysisStage.Pages
                    ? $"Обработка страниц: {value.Processed} из {value.Total}…"
                    : $"Проверка качества изображений: {value.Processed} из {value.Total}…";
            });
            var images = await _websiteImageService.AnalyzeAsync(
                pageUri,
                Math.Max(0, ImageScanDepthComboBox.SelectedIndex),
                GetImageQualityPreset(),
                progress,
                cancellation.Token,
                GetActiveWebsiteSession());

            foreach (var image in images)
            {
                _websiteImages.Add(image);
            }

            UpdateImagesSummary();
            StatusTextBlock.Text = images.Count == 0
                ? "Подходящих изображений не найдено. Попробуйте снизить порог качества."
                : $"Найдено уникальных качественных изображений: {images.Count}. Иконки и дубли исключены.";
        }
        catch (OperationCanceledException)
        {
            StatusTextBlock.Text = "Поиск изображений отменён.";
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or
                                           InvalidOperationException or ArgumentException)
        {
            StatusTextBlock.Text = "Не удалось извлечь изображения со страницы.";
            AppendLog($"ИЗОБРАЖЕНИЯ: {exception.Message}");
            MessageBox.Show(_window, exception.Message, "Ошибка разбора страницы",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            cancellation.Dispose();
            if (ReferenceEquals(_imageCancellation, cancellation))
            {
                _imageCancellation = null;
                GlobalProgressBar.Value = 0;
                SetImageOperationBusy(false);
            }
        }
    }

    private async void SaveSelectedImagesButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isImageOperationRunning)
        {
            return;
        }

        if (_isQueueRunning || _isInstalling)
        {
            StatusTextBlock.Text = "Дождитесь завершения текущей операции с видео или инструментами.";
            return;
        }

        var selected = _websiteImages.Where(image => image.IsSelected).ToArray();
        if (selected.Length == 0)
        {
            MessageBox.Show(_window, "Выберите хотя бы одно изображение.", "Нет выбранных изображений",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var outputDirectory = ImageOutputDirectoryTextBox.Text.Trim();
        try
        {
            Directory.CreateDirectory(outputDirectory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            MessageBox.Show(_window, $"Папка недоступна: {exception.Message}", "Ошибка папки",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _imageCancellation = new CancellationTokenSource();
        SetImageOperationBusy(true);
        var completed = 0;
        var failed = 0;
        var duplicates = 0;
        var downloadedContentFingerprints = new HashSet<string>(StringComparer.Ordinal);

        try
        {
            for (var index = 0; index < selected.Length; index++)
            {
                _imageCancellation.Token.ThrowIfCancellationRequested();
                var image = selected[index];
                image.Status = "Сохранение…";
                GlobalProgressBar.Value = index * 100d / selected.Length;
                StatusTextBlock.Text = $"Сохранение изображений: {index + 1} из {selected.Length}…";

                try
                {
                    var path = await _websiteImageService.DownloadUniqueAsync(
                        image,
                        outputDirectory,
                        downloadedContentFingerprints,
                        _imageCancellation.Token,
                        GetActiveWebsiteSession());
                    if (path is null)
                    {
                        image.Status = "Пропущено: такое изображение уже сохранено";
                        duplicates++;
                    }
                    else
                    {
                        image.Status = $"Сохранено: {Path.GetFileName(path)}";
                        completed++;
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception) when (exception is HttpRequestException or IOException or
                                                   UnauthorizedAccessException)
                {
                    image.Status = $"Ошибка: {exception.Message}";
                    failed++;
                }
            }

            GlobalProgressBar.Value = 100;
            StatusTextBlock.Text =
                $"Изображения сохранены: {completed}; дублей пропущено: {duplicates}; ошибок: {failed}.";
        }
        catch (OperationCanceledException)
        {
            StatusTextBlock.Text = $"Сохранение отменено. Успешно сохранено: {completed}.";
        }
        finally
        {
            _imageCancellation.Dispose();
            _imageCancellation = null;
            GlobalProgressBar.Value = 0;
            SetImageOperationBusy(false);
        }
    }

    private void BrowseImageFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Выберите папку для изображений",
            InitialDirectory = Directory.Exists(ImageOutputDirectoryTextBox.Text)
                ? ImageOutputDirectoryTextBox.Text
                : GetDefaultOutputDirectory()
        };

        if (dialog.ShowDialog(_window) == true)
        {
            ImageOutputDirectoryTextBox.Text = dialog.FolderName;
        }
    }

    private void CancelImageOperationButton_Click(object sender, RoutedEventArgs e)
    {
        _imageCancellation?.Cancel();
    }

    private void ImageAccessModeComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (OpenAuthenticatedBrowserButton is null)
        {
            return;
        }

        var browserMode = ImageAccessModeComboBox.SelectedIndex == 1;
        OpenAuthenticatedBrowserButton.IsEnabled = browserMode && !_isImageOperationRunning;
        ImageSessionStatusTextBlock.Text = browserMode
            ? _websiteBrowserSession is null
                ? "Сессия ещё не выбрана"
                : $"Сессия: {_websiteBrowserSession.PageUri.Host}"
            : "Публичная страница";
    }

    private void OpenAuthenticatedBrowserButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetWebsiteUri(out var uri))
        {
            return;
        }

        OpenAuthenticatedBrowser(uri);
    }

    private bool EnsureWebsiteSession(Uri pageUri)
    {
        if (ImageAccessModeComboBox.SelectedIndex != 1)
        {
            return true;
        }

        if (_websiteBrowserSession?.IsForPage(pageUri) == true)
        {
            return true;
        }

        return OpenAuthenticatedBrowser(pageUri);
    }

    private bool OpenAuthenticatedBrowser(Uri uri)
    {
        var browserWindow = new AuthenticatedBrowserWindow(uri) { Owner = _window };
        if (browserWindow.ShowDialog() != true || browserWindow.Session is null)
        {
            return false;
        }

        _websiteBrowserSession = browserWindow.Session;
        WebsiteUrlTextBox.Text = _websiteBrowserSession.PageUri.AbsoluteUri;
        ImageSessionStatusTextBlock.Text =
            $"Сессия: {_websiteBrowserSession.PageUri.Host} · cookies: {_websiteBrowserSession.Cookies.Count}";
        StatusTextBlock.Text = "Авторизованная страница получена из встроенного браузера.";
        return true;
    }

    private bool TryGetWebsiteUri(out Uri uri)
    {
        if (Uri.TryCreate(WebsiteUrlTextBox.Text.Trim(), UriKind.Absolute, out var parsed) &&
            (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps))
        {
            uri = parsed;
            return true;
        }

        uri = null!;
        MessageBox.Show(_window, "Сначала введите HTTP/HTTPS-ссылку на нужный сайт.",
            "Не указана ссылка", MessageBoxButton.OK, MessageBoxImage.Information);
        return false;
    }

    private WebsiteBrowserSession? GetActiveWebsiteSession()
    {
        return ImageAccessModeComboBox.SelectedIndex == 1 ? _websiteBrowserSession : null;
    }

    private void SelectAllImagesButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (var image in _websiteImages)
        {
            image.IsSelected = true;
        }

        UpdateImagesSummary();
    }

    private void ClearImageSelectionButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (var image in _websiteImages)
        {
            image.IsSelected = false;
        }

        UpdateImagesSummary();
    }

    private void WebsiteImageSelection_Changed(object sender, RoutedEventArgs e)
    {
        UpdateImagesSummary();
    }

    private void OpenWebsiteImageButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not WebsiteImageItem image)
        {
            return;
        }

        Process.Start(new ProcessStartInfo(image.Address) { UseShellExecute = true });
    }

    private ImageQualityPreset GetImageQualityPreset()
    {
        return ImageQualityComboBox.SelectedIndex switch
        {
            1 => ImageQualityPreset.High,
            2 => ImageQualityPreset.Relaxed,
            _ => ImageQualityPreset.Standard
        };
    }

    private void UpdateImagesSummary()
    {
        if (ImagesSummaryTextBlock is null)
        {
            return;
        }

        var selected = _websiteImages.Count(image => image.IsSelected);
        ImagesSummaryTextBlock.Text = $"Найдено: {_websiteImages.Count} · выбрано: {selected}";
        SaveSelectedImagesButton.IsEnabled = !_isImageOperationRunning && selected > 0;
    }

    private void SetImageOperationBusy(bool isBusy)
    {
        _isImageOperationRunning = isBusy;
        WebsiteUrlTextBox.IsEnabled = !isBusy;
        FavoriteWebsiteUrlButton.IsEnabled = !isBusy;
        FavoriteWebsiteUrlsComboBox.IsEnabled = !isBusy;
        RemoveFavoriteWebsiteUrlButton.IsEnabled = !isBusy;
        ClearImageSearchButton.IsEnabled = !isBusy;
        AnalyzeImagesButton.IsEnabled = !isBusy;
        ImageQualityComboBox.IsEnabled = !isBusy;
        ImageScanDepthComboBox.IsEnabled = !isBusy;
        ImageAccessModeComboBox.IsEnabled = !isBusy;
        OpenAuthenticatedBrowserButton.IsEnabled = !isBusy && ImageAccessModeComboBox.SelectedIndex == 1;
        WebsiteImagesListView.IsEnabled = !isBusy;
        ImageOutputDirectoryTextBox.IsEnabled = !isBusy;
        CancelImageOperationButton.IsEnabled = isBusy;
        SaveSelectedImagesButton.IsEnabled = !isBusy && _websiteImages.Any(image => image.IsSelected);
        SetAnalysisBusy(isBusy || _isQueueRunning || _isInstalling);
    }
}
