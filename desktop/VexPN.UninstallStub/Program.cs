using System.Diagnostics;
using System.IO;
using System.Windows;

namespace VexPN.UninstallStub;

public static class Program
{
    [STAThread]
    public static int Main()
    {
        try
        {
            var baseDir = AppContext.BaseDirectory;
            var unins = Path.Combine(baseDir, "unins000.exe");
            if (!File.Exists(unins))
            {
                MessageBox.Show(
                    $"Не найден файл деинсталляции: {unins}",
                    "VexPN — удаление",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return 2;
            }

            // Запускаем деинсталлятор с GUI (как у обычных установщиков).
            Process.Start(new ProcessStartInfo
            {
                FileName = unins,
                UseShellExecute = true
            });
            return 0;
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "VexPN — удаление", MessageBoxButton.OK, MessageBoxImage.Error);
            return 1;
        }
    }
}

