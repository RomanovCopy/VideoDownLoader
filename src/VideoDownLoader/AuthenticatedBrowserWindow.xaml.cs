using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using VideoDownLoader.Models;
using VideoDownLoader.Services;

namespace VideoDownLoader;

public partial class AuthenticatedBrowserWindow : Window
{
    private readonly Uri _initialUri;
    private readonly DispatcherTimer _navigationTimeout;
    private CoreWebView2Environment? _environment;
    private bool _initialized;

    public AuthenticatedBrowserWindow(Uri initialUri)
    {
        _initialUri = initialUri;
        InitializeComponent();
        DarkWindowChromeService.Enable(this);
        AddressTextBox.Text = initialUri.AbsoluteUri;
        _navigationTimeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _navigationTimeout.Tick += NavigationTimeout_Tick;
    }

    public WebsiteBrowserSession? Session { get; private set; }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        try
        {
            var profileDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VideoDownLoader",
                "WebView2");
            _environment = await CoreWebView2Environment.CreateAsync(userDataFolder: profileDirectory);
            await Browser.EnsureCoreWebView2Async(_environment);
            Browser.CoreWebView2.Settings.AreDevToolsEnabled = false;
            Browser.CoreWebView2.Settings.IsPasswordAutosaveEnabled = false;
            Browser.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
            Browser.CoreWebView2.NavigationStarting += Browser_NavigationStarting;
            Browser.CoreWebView2.NavigationCompleted += Browser_NavigationCompleted;
            Browser.CoreWebView2.DOMContentLoaded += Browser_DOMContentLoaded;
            Browser.CoreWebView2.ProcessFailed += Browser_ProcessFailed;
            Browser.CoreWebView2.SourceChanged += (_, _) =>
            {
                if (Browser.Source is not null)
                {
                    AddressTextBox.Text = Browser.Source.AbsoluteUri;
                }
            };
            Browser.CoreWebView2.NewWindowRequested += Browser_NewWindowRequested;
            NavigateButton.IsEnabled = true;
            RefreshButton.IsEnabled = true;
            Browser.CoreWebView2.Navigate(_initialUri.AbsoluteUri);
        }
        catch (Exception exception) when (exception is WebView2RuntimeNotFoundException or InvalidOperationException)
        {
            BrowserStatusTextBlock.Text = "Не удалось запустить встроенный браузер.";
            MessageBox.Show(this,
                $"Для авторизации требуется Microsoft Edge WebView2 Runtime. {exception.Message}",
                "WebView2 недоступен",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void NavigateButton_Click(object sender, RoutedEventArgs e)
    {
        NavigateFromAddressBar();
    }

    private void AddressTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            NavigateFromAddressBar();
        }
    }

    private void NavigateFromAddressBar()
    {
        if (Browser.CoreWebView2 is null ||
            !Uri.TryCreate(AddressTextBox.Text.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            BrowserStatusTextBlock.Text = "Введите корректный адрес HTTP/HTTPS.";
            return;
        }

        Browser.CoreWebView2.Stop();
        Browser.CoreWebView2.Navigate(uri.AbsoluteUri);
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        if (Browser.CanGoBack)
        {
            Browser.GoBack();
        }
    }

    private void ForwardButton_Click(object sender, RoutedEventArgs e)
    {
        if (Browser.CanGoForward)
        {
            Browser.GoForward();
        }
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        if (Browser.CoreWebView2 is null)
        {
            return;
        }

        Browser.CoreWebView2.Stop();
        Browser.Reload();
    }

    private void Browser_NavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        _navigationTimeout.Stop();
        _navigationTimeout.Start();
        SetNavigationBusy(true);
    }

    private void Browser_DOMContentLoaded(object? sender, CoreWebView2DOMContentLoadedEventArgs e)
    {
        _navigationTimeout.Stop();
        SetNavigationBusy(false);
        UsePageButton.IsEnabled = Browser.Source is { Scheme: "http" or "https" };
        BrowserStatusTextBlock.Text = "Страница открыта. Можно продолжать вход или использовать её.";
    }

    private void Browser_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        _navigationTimeout.Stop();
        SetNavigationBusy(false);
        BrowserStatusTextBlock.Text = e.IsSuccess
            ? "Страница загружена. После входа откройте страницу с изображениями."
            : $"Ошибка загрузки: {e.WebErrorStatus}";
        UsePageButton.IsEnabled = e.IsSuccess && Browser.Source is { Scheme: "http" or "https" };
        BackButton.IsEnabled = Browser.CanGoBack;
        ForwardButton.IsEnabled = Browser.CanGoForward;
    }

    private async void Browser_NewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        var deferral = e.GetDeferral();
        AuthenticatedBrowserPopupWindow? popup = null;
        try
        {
            if (_environment is null)
            {
                e.Handled = true;
                return;
            }

            popup = new AuthenticatedBrowserPopupWindow { Owner = this };
            popup.Show();
            var popupWebView = await popup.InitializeAsync(_environment);
            e.NewWindow = popupWebView;
            e.Handled = true;
        }
        catch (Exception exception)
        {
            popup?.Close();
            e.Handled = true;
            BrowserStatusTextBlock.Text = "Не удалось открыть окно авторизации.";
            MessageBox.Show(this, exception.Message, "Ошибка окна авторизации",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            deferral.Complete();
        }
    }

    private void Browser_ProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        _navigationTimeout.Stop();
        SetNavigationBusy(false);
        BrowserStatusTextBlock.Text = $"Сбой процесса браузера: {e.ProcessFailedKind}. Нажмите «Обновить».";
    }

    private void NavigationTimeout_Tick(object? sender, EventArgs e)
    {
        _navigationTimeout.Stop();
        Browser.CoreWebView2?.Stop();
        SetNavigationBusy(false);
        BrowserStatusTextBlock.Text = "Сайт не завершил загрузку. Управление разблокировано; можно обновить страницу или перейти по адресу.";
    }

    private async void UsePageButton_Click(object sender, RoutedEventArgs e)
    {
        if (Browser.CoreWebView2 is null || Browser.Source is null)
        {
            return;
        }

        UsePageButton.IsEnabled = false;
        BrowserStatusTextBlock.Text = "Получение содержимого и сессии текущего сайта…";
        try
        {
            var htmlJson = await Browser.CoreWebView2.ExecuteScriptAsync("document.documentElement.outerHTML");
            var userAgentJson = await Browser.CoreWebView2.ExecuteScriptAsync("navigator.userAgent");
            var html = JsonSerializer.Deserialize<string>(htmlJson) ?? string.Empty;
            var userAgent = JsonSerializer.Deserialize<string>(userAgentJson) ?? "VideoDownLoader/1.0";
            var webViewCookies = await Browser.CoreWebView2.CookieManager.GetCookiesAsync(Browser.Source.AbsoluteUri);
            var cookies = webViewCookies.Select(cookie =>
            {
                var systemCookie = cookie.ToSystemNetCookie();
                return new WebsiteSessionCookie(
                    systemCookie.Name,
                    systemCookie.Value,
                    systemCookie.Domain,
                    systemCookie.Path,
                    systemCookie.Secure,
                    systemCookie.Expires == DateTime.MinValue ? null : systemCookie.Expires);
            }).ToArray();

            Session = new WebsiteBrowserSession(Browser.Source, html, userAgent, cookies);
            DialogResult = true;
        }
        catch (Exception exception)
        {
            BrowserStatusTextBlock.Text = "Не удалось получить данные текущей страницы.";
            MessageBox.Show(this, exception.Message, "Ошибка браузерной сессии",
                MessageBoxButton.OK, MessageBoxImage.Error);
            UsePageButton.IsEnabled = true;
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void SetNavigationBusy(bool isBusy)
    {
        NavigateButton.IsEnabled = Browser.CoreWebView2 is not null;
        RefreshButton.IsEnabled = Browser.CoreWebView2 is not null;
        AddressTextBox.IsEnabled = Browser.CoreWebView2 is not null;
        UsePageButton.IsEnabled = !isBusy && Browser.Source is { Scheme: "http" or "https" };
        BrowserStatusTextBlock.Text = isBusy ? "Загрузка страницы…" : BrowserStatusTextBlock.Text;
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        _navigationTimeout.Stop();
        Browser.Dispose();
    }
}
