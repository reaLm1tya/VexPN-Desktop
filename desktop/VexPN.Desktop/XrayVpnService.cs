using System.Diagnostics;
using System.IO;

namespace VexPN.Desktop;

/// <summary>
/// Запускает Xray-core с TUN inbound, применяет системные маршруты Windows и корректно откатывает при ошибке.
/// </summary>
public sealed class XrayVpnService : IAsyncDisposable
{
    private const string TunName = "xray0";
    private readonly string _storageDir;
    private readonly object _gate = new();
    private Process? _process;
    private WindowsRouteHelper? _routes;
    private string? _configPath;
    private string? _logPath;

    public XrayVpnService(string storageDir) =>
        _storageDir = storageDir;

    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                try
                {
                    return _process is { HasExited: false };
                }
                catch
                {
                    return false;
                }
            }
        }
    }

    public static string GetBundledXrayPath() =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "xray.exe");

    public async Task<string?> ConnectAsync(string vlessUri, CancellationToken ct)
    {
        if (!VlessUriParser.TryParse(vlessUri, out var parsed, out var parseErr))
            return parseErr;

        if (parsed is null)
            return "Не удалось разобрать VLESS ссылку.";

        if (string.Equals(parsed.Security, "reality", StringComparison.OrdinalIgnoreCase) &&
            string.IsNullOrWhiteSpace(parsed.PublicKey))
            return "В VLESS ссылке нет public key (pbk) для REALITY.";

        var xrayExe = GetBundledXrayPath();
        if (!File.Exists(xrayExe))
            return $"Не найден xray.exe по пути: {xrayExe}";

        var wintun = Path.Combine(AppContext.BaseDirectory, "Assets", "wintun.dll");
        if (!File.Exists(wintun))
        {
            // Xray на Windows требует wintun.dll рядом с xray.exe (см. README TUN).
            return $"Не найден wintun.dll по пути: {wintun}";
        }

        lock (_gate)
        {
            if (_process is { HasExited: false })
                return "VPN уже подключён.";
        }

        Directory.CreateDirectory(_storageDir);
        var physical = await WindowsRouteHelper.GetPrimaryIpv4DefaultRouteAsync(ct).ConfigureAwait(false);
        if (physical is null)
            return "Не удалось определить основной шлюз IPv4. Проверьте подключение к сети.";

        var serverIp = await WindowsRouteHelper.ResolveServerIpv4Async(parsed.Address, ct).ConfigureAwait(false);
        if (serverIp is null)
            return "Не удалось определить IPv4 адрес сервера (DNS).";

        var json = XrayConfigBuilder.BuildJson(parsed);
        _configPath = Path.Combine(_storageDir, "xray-run.json");
        await File.WriteAllTextAsync(_configPath, json, ct).ConfigureAwait(false);
        _logPath = Path.Combine(_storageDir, "xray-run.log");
        try { File.WriteAllText(_logPath, string.Empty); } catch { /* ignore */ }

        var routeHelper = new WindowsRouteHelper();
        Process? proc = null;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = xrayExe,
                Arguments = $"run -c \"{_configPath}\"",
                WorkingDirectory = Path.GetDirectoryName(xrayExe)!,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };

            proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            if (!proc.Start())
                return "Не удалось запустить xray.exe.";

            void AppendLog(string? line)
            {
                if (string.IsNullOrWhiteSpace(line) || string.IsNullOrWhiteSpace(_logPath))
                    return;
                try
                {
                    File.AppendAllText(_logPath, $"[{DateTime.Now:O}] {line}\r\n");
                }
                catch
                {
                    // ignore
                }
            }

            proc.OutputDataReceived += (_, e) => AppendLog(e.Data);
            proc.ErrorDataReceived += (_, e) => AppendLog(e.Data);
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            // Дождаться TUN адаптера
            var tunIf = await WindowsRouteHelper.WaitForTunInterfaceIndexAsync(TunName, TimeSpan.FromSeconds(60), ct)
                .ConfigureAwait(false);
            if (tunIf is null)
            {
                TryKill(proc);
                var hint = _logPath is not null ? $" Лог: {_logPath}" : string.Empty;
                return "Не появился сетевой адаптер TUN (xray0). Проверьте wintun.dll и версию Xray." + hint;
            }

            if (!await routeHelper.ApplyVpnRoutesAsync(serverIp, tunIf.Value, physical, ct).ConfigureAwait(false))
            {
                TryKill(proc);
                return "Не удалось добавить маршруты. Подключение отменено.";
            }

            lock (_gate)
            {
                _process = proc;
                _routes = routeHelper;
                proc = null; // ownership transferred
                routeHelper = null!;
            }

            return null;
        }
        catch (OperationCanceledException)
        {
            TryKill(proc);
            await routeHelper.RemoveAddedRoutesAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            TryKill(proc);
            await routeHelper.RemoveAddedRoutesAsync(CancellationToken.None).ConfigureAwait(false);
            return ex.Message;
        }
    }

    public async Task DisconnectAsync(CancellationToken ct)
    {
        Process? proc;
        WindowsRouteHelper? routes;
        lock (_gate)
        {
            proc = _process;
            routes = _routes;
            _process = null;
            _routes = null;
        }

        if (routes is not null)
            await routes.RemoveAddedRoutesAsync(ct).ConfigureAwait(false);

        if (proc is not null)
        {
            try
            {
                if (!proc.HasExited)
                    proc.Kill(entireProcessTree: true);
            }
            catch
            {
                // ignore
            }

            try
            {
                await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                // ignore
            }
            finally
            {
                try { proc.Dispose(); } catch { /* ignore */ }
            }
        }
    }

    private static void TryKill(Process? p)
    {
        if (p is null)
            return;
        try
        {
            if (!p.HasExited)
            {
                p.Kill(entireProcessTree: true);
                p.WaitForExit(5000);
            }
        }
        catch
        {
            // ignore
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
    }
}
