using System.Windows;
using Microsoft.Web.WebView2.Core;
using VideoDownLoader.Services;

namespace VideoDownLoader;

public partial class AuthenticatedBrowserPopupWindow : Window
{
    public AuthenticatedBrowserPopupWindow()
    {
        InitializeComponent();
        DarkWindowChromeService.Enable(this);
    }

    public async Task<CoreWebView2> InitializeAsync(CoreWebView2Environment environment)
    {
        await PopupBrowser.EnsureCoreWebView2Async(environment);
        PopupBrowser.CoreWebView2.Settings.AreDevToolsEnabled = false;
        PopupBrowser.CoreWebView2.Settings.IsPasswordAutosaveEnabled = false;
        PopupBrowser.CoreWebView2.NavigationStarting += (_, _) =>
            PopupStatusTextBlock.Text = "Загрузка страницы входа…";
        PopupBrowser.CoreWebView2.NavigationCompleted += (_, args) =>
        {
            PopupStatusTextBlock.Text = args.IsSuccess
                ? "Продолжите вход на сайте. Это окно закроется по команде сайта."
                : $"Ошибка загрузки: {args.WebErrorStatus}";
            UpdateTitle();
        };
        PopupBrowser.CoreWebView2.DocumentTitleChanged += (_, _) => UpdateTitle();
        PopupBrowser.CoreWebView2.WindowCloseRequested += (_, _) => Close();
        return PopupBrowser.CoreWebView2;
    }

    private void UpdateTitle()
    {
        var documentTitle = PopupBrowser.CoreWebView2?.DocumentTitle;
        Title = string.IsNullOrWhiteSpace(documentTitle) ? "Вход на сайт" : documentTitle;
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        PopupBrowser.Dispose();
    }
}
