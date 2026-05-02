using System.Diagnostics;
using System.IO;

namespace VexPN.Desktop;

/// <summary>
/// Mihomo (Clash Meta) с TUN и правилами PROCESS-NAME — VPN только для выбранных .exe (Windows).
/// </summary>
public sealed class MihomoVpnService : IAsyncDisposable
{
    private readonly string _storageDir;
    private readonly object _gate = new();
    private Process? _process;
    private string? _configPath;
    private string? _logPath;

    public MihomoVpnService(string storageDir) =>
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

    public static string GetBundledMihomoPath() =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "mihomo.exe");

    /// <param name="vpnAppExePaths">Полные пути к .exe (до 5), должны быть непустые.</param>
    public async Task<string?> ConnectAsync(string vlessUri, IReadOnlyList<string> vpnAppExePaths, CancellationToken ct)
    {
        if (vpnAppExePaths.Count == 0)
            return "Выберите хотя бы одно приложение для VPN.";

        if (!VlessUriParser.TryParse(vlessUri, out var parsed, out var parseErr))
            return parseErr;

        if (parsed is null)
            return "Не удалось разобрать VLESS ссылку.";

        if (string.Equals(parsed.Security, "reality", StringComparison.OrdinalIgnoreCase) &&
            string.IsNullOrWhiteSpace(parsed.PublicKey))
            return "В VLESS ссылке нет public key (pbk) для REALITY.";

        var mihomoExe = GetBundledMihomoPath();
        if (!File.Exists(mihomoExe))
            return $"Не найден mihomo.exe. Ожидался путь: {mihomoExe}";

        var wintun = Path.Combine(AppContext.BaseDirectory, "Assets", "wintun.dll");
        if (!File.Exists(wintun))
            return $"Не найден wintun.dll по пути: {wintun}";

        lock (_gate)
        {
            if (_process is { HasExited: false })
                return "VPN уже подключён.";
        }

        var basenames = vpnAppExePaths
            .Select(p => Path.GetFileName(p.Trim()))
            .Where(f => !string.IsNullOrWhiteSpace(f) && f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (basenames.Count == 0)
            return "Некорректные пути к приложениям (.exe).";

        string yaml;
        try
        {
            yaml = MihomoConfigBuilder.BuildYaml(parsed, basenames);
        }
        catch (Exception ex)
        {
            return ex.Message;
        }

        Directory.CreateDirectory(_storageDir);
        _configPath = Path.Combine(_storageDir, "mihomo-run.yaml");
        await File.WriteAllTextAsync(_configPath, yaml, ct).ConfigureAwait(false);

        _logPath = Path.Combine(_storageDir, "mihomo-run.log");
        try { File.WriteAllText(_logPath, string.Empty); } catch { /* ignore */ }

        Process? proc = null;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = mihomoExe,
                Arguments = $"run -f \"{_configPath}\"",
                WorkingDirectory = Path.GetDirectoryName(mihomoExe)!,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };

            proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            if (!proc.Start())
                return "Не удалось запустить mihomo.exe.";

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

            await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);

            if (proc.HasExited)
            {
                TryKill(proc);
                var hint = _logPath is not null ? $" Лог: {_logPath}" : string.Empty;
                return "Mihomo завершился сразу после запуска." + hint;
            }

            lock (_gate)
            {
                _process = proc;
                proc = null;
            }

            return null;
        }
        catch (OperationCanceledException)
        {
            TryKill(proc);
            throw;
        }
        catch (Exception ex)
        {
            TryKill(proc);
            return ex.Message;
        }
    }

    public async Task DisconnectAsync(CancellationToken ct)
    {
        Process? proc;
        lock (_gate)
        {
            proc = _process;
            _process = null;
        }

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

    public async ValueTask DisposeAsync() =>
        await DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
}
