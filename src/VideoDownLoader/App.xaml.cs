using System.Diagnostics;
using System.Windows;
using VideoDownLoader.Services;

namespace VideoDownLoader;

public partial class App : Application
{
    private bool _updateCheckStarted;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var mainWindow = new MainWindow();
        MainWindow = mainWindow;
        mainWindow.ContentRendered += MainWindow_ContentRendered;
        mainWindow.Show();
    }

    private async void MainWindow_ContentRendered(object? sender, EventArgs e)
    {
        if (_updateCheckStarted || sender is not MainWindow mainWindow)
        {
            return;
        }

        _updateCheckStarted = true;
        mainWindow.ContentRendered -= MainWindow_ContentRendered;

        ApplicationUpdate? update;
        try
        {
            var updateService = new ApplicationUpdateService();
            update = await updateService.CheckForUpdateAsync();
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Не удалось проверить обновление: {exception}");
            return;
        }

        if (update is null || !mainWindow.IsVisible)
        {
            return;
        }

        var answer = MessageBox.Show(
            mainWindow,
            $"Доступно обновление приложения.\n\n" +
            $"Текущая сборка: {BuildMetadata.ShortCommit}\n" +
            $"Новая сборка: {update.ShortCommit}\n\n" +
            "Скачать и установить его сейчас?",
            "Обновление VideoDownLoader",
            MessageBoxButton.YesNo,
            MessageBoxImage.Information);

        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            var updateService = new ApplicationUpdateService();
            var originalTitle = mainWindow.Title;
            var progress = new Progress<double>(value =>
                mainWindow.Title = $"VideoDownLoader — загрузка обновления {value:P0}");
            var packagePath = await updateService.DownloadUpdateAsync(update, progress);
            mainWindow.Title = originalTitle;
            updateService.LaunchInstaller(packagePath);
            Shutdown();
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Не удалось установить обновление: {exception}");
            mainWindow.Title = "VideoDownLoader";

            if (mainWindow.IsVisible)
            {
                MessageBox.Show(
                    mainWindow,
                    "Не удалось скачать или запустить обновление. Приложение продолжит работу.\n\n" +
                    exception.Message,
                    "Обновление VideoDownLoader",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
    }
}
