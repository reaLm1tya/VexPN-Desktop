using System.IO;

namespace VexPN.Desktop;

/// <summary>Выбранное для VPN приложение (по пути к .exe).</summary>
public sealed class SelectedVpnAppVm
{
    public SelectedVpnAppVm(string exePath)
    {
        ExePath = exePath;
        DisplayName = Path.GetFileNameWithoutExtension(exePath);
    }

    public string ExePath { get; }

    public string DisplayName { get; }
}
