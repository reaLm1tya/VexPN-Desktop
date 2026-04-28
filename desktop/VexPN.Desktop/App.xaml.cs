using System.IO;
using System.Threading;
using System.IO.Pipes;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace VexPN.Desktop;

public partial class App : System.Windows.Application
{
    private static Mutex? _singleInstanceMutex;
    private const string PipeName = "VexPN.Desktop.SingleInstance.Pipe";

    protected override async void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;

        // Single instance: if already running, ask it to show the window, then exit.
        var createdNew = false;
        _singleInstanceMutex = new Mutex(initiallyOwned: true, name: "Global\\VexPN.Desktop.SingleInstance", createdNew: out createdNew);
        if (!createdNew)
        {
            TryRequestShowExistingInstance();
            Shutdown(-2);
            return;
        }

        _ = Task.Run(ListenForShowRequests);

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

    private static void TryRequestShowExistingInstance()
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(350);
            var bytes = Encoding.UTF8.GetBytes("show");
            client.Write(bytes, 0, bytes.Length);
            client.Flush();
        }
        catch
        {
            // ignore
        }
    }

    private static async Task ListenForShowRequests()
    {
        while (true)
        {
            try
            {
                using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync().ConfigureAwait(false);
                var buf = new byte[32];
                var n = await server.ReadAsync(buf, 0, buf.Length).ConfigureAwait(false);
                var msg = n > 0 ? Encoding.UTF8.GetString(buf, 0, n) : string.Empty;
                if (msg.Contains("show", StringComparison.OrdinalIgnoreCase))
                {
                    Current?.Dispatcher.Invoke(() =>
                    {
                        if (Current?.MainWindow is MainWindow mw)
                            mw.ShowFromTray();
                        else
                            Current?.MainWindow?.Activate();
                    });
                }
            }
            catch
            {
                await Task.Delay(500).ConfigureAwait(false);
            }
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _singleInstanceMutex?.ReleaseMutex();
        }
        catch
        {
            // ignore
        }
        finally
        {
            try { _singleInstanceMutex?.Dispose(); } catch { /* ignore */ }
            _singleInstanceMutex = null;
        }

        base.OnExit(e);
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
            System.Windows.MessageBox.Show(
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
