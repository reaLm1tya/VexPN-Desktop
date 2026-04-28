using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace VexPN.Desktop;

public partial class App : System.Windows.Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;

        // Иначе приложение может завершиться при закрытии splash (OnLastWindowClose по умолчанию).
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        base.OnStartup(e);

        var splash = new SplashWindow();
        splash.Show();

        await Task.Delay(TimeSpan.FromSeconds(5));

        splash.Close();
        var mainWindow = new MainWindow();
        MainWindow = mainWindow;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        mainWindow.Show();
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        TryLogAndShow(e.Exception);
        e.Handled = true;
        Current?.Shutdown(-1);
    }

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
            TryLogAndShow(ex);
    }

    private static void TryLogAndShow(Exception ex)
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VexPN");
        Directory.CreateDirectory(dir);
        var logPath = Path.Combine(dir, "error.log");
        try
        {
            File.AppendAllText(
                logPath,
                $"{DateTime.UtcNow:O}{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // ignored
        }

        try
        {
            MessageBox.Show(
                $"{ex.Message}{Environment.NewLine}{Environment.NewLine}Подробности записаны в:{Environment.NewLine}{logPath}",
                "VexPN — ошибка",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        catch
        {
            // ignored
        }
    }
}
