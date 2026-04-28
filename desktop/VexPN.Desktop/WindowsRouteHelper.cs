using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace VexPN.Desktop;

/// <summary>
/// Windows route.exe helpers: default gateway capture, TUN split routing, bypass private ranges,
/// and explicit /32 to VLESS server via physical gateway (avoids Xray routing loop — see Xray TUN README).
/// </summary>
public sealed class WindowsRouteHelper
{
    public sealed record DefaultRoute(string Destination, string Mask, string Gateway, int IfIndex, int Metric);

    public sealed record AddedRoute(string Destination, string Mask, string Gateway, int IfIndex);

    private readonly List<AddedRoute> _added = [];
    private int? _ipv6DisabledIfIndex;

    public IReadOnlyList<AddedRoute> AddedRoutes => _added;

    public static async Task<DefaultRoute?> GetPrimaryIpv4DefaultRouteAsync(CancellationToken ct)
    {
        try
        {
            // В системе может быть несколько default route (например, Radmin VPN + обычный LAN).
            // Берём маршрут 0.0.0.0/0 с минимальной метрикой из route.exe print -4.
            var output = await RunRoutePrint4Async(ct).ConfigureAwait(false);
            var best = ParseBestDefaultRoute(output);
            if (best is not null)
                return best;

            // Fallback: старый метод (на случай нестандартного вывода route.exe).
            return await Task.Run(() =>
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up)
                        continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                        continue;
                    if (ni.Name.StartsWith("xray", StringComparison.OrdinalIgnoreCase) ||
                        ni.Description.Contains("Wintun", StringComparison.OrdinalIgnoreCase) ||
                        ni.Description.Contains("Xray", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var props = ni.GetIPProperties();
                    var gw = props.GatewayAddresses
                        .FirstOrDefault(g => g.Address.AddressFamily == AddressFamily.InterNetwork);
                    if (gw is null || IPAddress.Any.Equals(gw.Address))
                        continue;

                    var v4 = props.GetIPv4Properties();
                    if (v4 is null || v4.Index <= 0)
                        continue;

                    return new DefaultRoute("0.0.0.0", "0.0.0.0", gw.Address.ToString(), v4.Index, int.MaxValue);
                }

                return (DefaultRoute?)null;
            }, ct).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string> RunRoutePrint4Async(CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "route.exe",
            Arguments = "print -4",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var p = new Process { StartInfo = psi };
        p.Start();
        var stdout = await p.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
        var stderr = await p.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
        await p.WaitForExitAsync(ct).ConfigureAwait(false);
        return stdout + "\n" + stderr;
    }

    private static DefaultRoute? ParseBestDefaultRoute(string text)
    {
        // Ищем строки вида:
        // 0.0.0.0  0.0.0.0  <gateway>  <interface ip>  <metric>
        // or: 0.0.0.0  0.0.0.0  <gateway>  <ifIndex>  <metric> (зависит от локали)
        // Мы аккуратно парсим по токенам.
        DefaultRoute? best = null;
        var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (!line.StartsWith("0.0.0.0", StringComparison.Ordinal))
                continue;

            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length < 5)
                continue;
            if (parts[0] != "0.0.0.0" || parts[1] != "0.0.0.0")
                continue;

            var gateway = parts[2];
            if (!IPAddress.TryParse(gateway, out var gwIp) || IPAddress.Any.Equals(gwIp))
                continue;

            // metric is usually last token
            if (!int.TryParse(parts[^1], out var metric))
                continue;

            // ifIndex is often not printed directly; but Windows prints interface IP in column 4.
            // We'll map interface IP back to interface index.
            var ifaceToken = parts[3];
            var ifIndex = TryMapInterfaceIndexFromIp(ifaceToken);
            if (ifIndex is null)
                continue;

            var candidate = new DefaultRoute("0.0.0.0", "0.0.0.0", gateway, ifIndex.Value, metric);
            if (best is null || candidate.Metric < best.Metric)
                best = candidate;
        }

        return best;
    }

    private static int? TryMapInterfaceIndexFromIp(string interfaceIp)
    {
        if (!IPAddress.TryParse(interfaceIp, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork)
            return null;

        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                var props = ni.GetIPProperties();
                var v4 = props.GetIPv4Properties();
                if (v4 is null || v4.Index <= 0)
                    continue;
                if (ni.Name.StartsWith("xray", StringComparison.OrdinalIgnoreCase) ||
                    ni.Description.Contains("Wintun", StringComparison.OrdinalIgnoreCase) ||
                    ni.Description.Contains("Xray", StringComparison.OrdinalIgnoreCase))
                    continue;

                foreach (var ua in props.UnicastAddresses)
                {
                    if (ua.Address.AddressFamily == AddressFamily.InterNetwork && ua.Address.Equals(ip))
                        return v4.Index;
                }
            }
        }
        catch
        {
            // ignore
        }

        return null;
    }

    /// <summary>
    /// Finds TUN adapter index (name xray0 or description contains Xray).
    /// </summary>
    public static async Task<int?> WaitForTunInterfaceIndexAsync(string tunName, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var idx = await Task.Run(() => FindTunIndex(tunName), ct).ConfigureAwait(false);
            if (idx is not null)
                return idx;
            await Task.Delay(350, ct).ConfigureAwait(false);
        }

        return null;
    }

    private static int? FindTunIndex(string tunName)
    {
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                var name = ni.Name;
                var desc = ni.Description;
                if (string.Equals(name, tunName, StringComparison.OrdinalIgnoreCase))
                    return ni.GetIPProperties().GetIPv4Properties()?.Index;
                if (desc.Contains("Xray", StringComparison.OrdinalIgnoreCase) &&
                    desc.Contains("Tunnel", StringComparison.OrdinalIgnoreCase))
                    return ni.GetIPProperties().GetIPv4Properties()?.Index;
                if (desc.Contains("Wintun", StringComparison.OrdinalIgnoreCase) &&
                    (name.Contains("xray", StringComparison.OrdinalIgnoreCase) ||
                     desc.Contains("Tunnel", StringComparison.OrdinalIgnoreCase)))
                    return ni.GetIPProperties().GetIPv4Properties()?.Index;
            }
        }
        catch
        {
            // ignore
        }

        return null;
    }

    public static async Task<IPAddress?> ResolveServerIpv4Async(string hostOrIp, CancellationToken ct)
    {
        if (IPAddress.TryParse(hostOrIp, out var ip))
        {
            if (ip.AddressFamily == AddressFamily.InterNetwork)
                return ip;
            return null;
        }

        try
        {
            var entries = await Dns.GetHostAddressesAsync(hostOrIp).WaitAsync(ct).ConfigureAwait(false);
            return entries.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
        }
        catch
        {
            return null;
        }
    }

    public async Task<bool> ApplyVpnRoutesAsync(
        IPAddress serverIp,
        int tunIfIndex,
        DefaultRoute physical,
        CancellationToken ct)
    {
        _added.Clear();
        var server = serverIp.ToString();

        // IMPORTANT: prevent IPv6 leakage. Our MVP routing is IPv4-only; if IPv6 stays enabled,
        // some apps/sites may keep using IPv6 and show the old location.
        // Disable IPv6 on the physical interface while VPN is connected, then re-enable on disconnect.
        // (Requires admin; app already runs elevated.)
        if (!await TrySetIpv6EnabledAsync(physical.IfIndex, enabled: false, ct).ConfigureAwait(false))
        {
            await RollbackAsync(ct).ConfigureAwait(false);
            return false;
        }
        _ipv6DisabledIfIndex = physical.IfIndex;

        // 1) Pin upstream IPs to physical gateway (prevents loops; Xray TUN README).
        // Besides the VLESS server, also pin public DNS IPs so that DNS continues working even after split-default routing.
        var pinned = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            server,
            "1.1.1.1",
            "1.0.0.1",
            "8.8.8.8",
            "8.8.4.4"
        };

        foreach (var ip in pinned)
        {
            if (!await TryRouteAddAsync($"{ip} mask 255.255.255.255 {physical.Gateway} metric 1 if {physical.IfIndex}", ct)
                    .ConfigureAwait(false))
            {
                await RollbackAsync(ct).ConfigureAwait(false);
                return false;
            }
            _added.Add(new AddedRoute(ip, "255.255.255.255", physical.Gateway, physical.IfIndex));
        }

        // 2) Bypass local/private traffic through physical NIC.
        // 127.0.0.0/8 обычно обрабатывается ОС; явный route может сломать loopback.
        var bypassViaGateway = new (string Dest, string Mask)[]
        {
            ("10.0.0.0", "255.0.0.0"),
            ("172.16.0.0", "255.240.0.0"),
            ("192.168.0.0", "255.255.0.0")
        };

        foreach (var (d, m) in bypassViaGateway)
        {
            if (await TryRouteAddAsync($"{d} mask {m} {physical.Gateway} metric 1 if {physical.IfIndex}", ct)
                    .ConfigureAwait(false))
                _added.Add(new AddedRoute(d, m, physical.Gateway, physical.IfIndex));
            // ignore failures (may already exist)
        }

        // Link-local и multicast должны оставаться on-link на физическом интерфейсе.
        var bypassOnLink = new (string Dest, string Mask)[]
        {
            ("169.254.0.0", "255.255.0.0"),
            ("224.0.0.0", "240.0.0.0"),
            ("255.255.255.255", "255.255.255.255")
        };

        foreach (var (d, m) in bypassOnLink)
        {
            if (await TryRouteAddAsync($"{d} mask {m} 0.0.0.0 metric 1 if {physical.IfIndex}", ct)
                    .ConfigureAwait(false))
                _added.Add(new AddedRoute(d, m, "0.0.0.0", physical.IfIndex));
        }

        // 3) Split IPv4 default into two /1 via TUN (on-link style per Xray Windows README).
        var tunAdds = new[]
        {
            ("0.0.0.0", "128.0.0.0", "0.0.0.0"),
            ("128.0.0.0", "128.0.0.0", "0.0.0.0")
        };

        foreach (var (dest, mask, gw) in tunAdds)
        {
            if (!await TryRouteAddAsync($"{dest} mask {mask} {gw} metric 6 if {tunIfIndex}", ct).ConfigureAwait(false))
            {
                await RollbackAsync(ct).ConfigureAwait(false);
                return false;
            }

            _added.Add(new AddedRoute(dest, mask, gw, tunIfIndex));
        }

        return true;
    }

    private async Task RollbackAsync(CancellationToken ct)
    {
        for (var i = _added.Count - 1; i >= 0; i--)
        {
            var a = _added[i];
            _ = await TryRouteDeleteAsync(a, ct).ConfigureAwait(false);
        }

        _added.Clear();

        if (_ipv6DisabledIfIndex is { } idx)
        {
            _ = await TrySetIpv6EnabledAsync(idx, enabled: true, ct).ConfigureAwait(false);
            _ipv6DisabledIfIndex = null;
        }
    }

    public async Task RemoveAddedRoutesAsync(CancellationToken ct)
    {
        for (var i = _added.Count - 1; i >= 0; i--)
        {
            var a = _added[i];
            _ = await TryRouteDeleteAsync(a, ct).ConfigureAwait(false);
        }

        _added.Clear();

        if (_ipv6DisabledIfIndex is { } idx)
        {
            _ = await TrySetIpv6EnabledAsync(idx, enabled: true, ct).ConfigureAwait(false);
            _ipv6DisabledIfIndex = null;
        }
    }

    private static async Task<bool> TrySetIpv6EnabledAsync(int ifIndex, bool enabled, CancellationToken ct)
    {
        // netsh interface ipv6 set interface interfaceindex=13 disabled|enabled
        var state = enabled ? "enabled" : "disabled";
        var psi = new ProcessStartInfo
        {
            FileName = "netsh.exe",
            Arguments = $"interface ipv6 set interface interfaceindex={ifIndex} {state}",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        try
        {
            using var p = new Process { StartInfo = psi };
            p.Start();
            _ = await p.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            _ = await p.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
            // netsh may return non-zero if IPv6 already in desired state; treat as best-effort.
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> TryRouteAddAsync(string args, CancellationToken ct)
    {
        var (code, _) = await RunRouteAsync("add " + args, ct).ConfigureAwait(false);
        return code == 0;
    }

    private static async Task<bool> TryRouteDeleteAsync(AddedRoute a, CancellationToken ct)
    {
        // Windows accepts: route delete <dest> MASK <mask> <gateway> IF <if>
        var gwPart = string.IsNullOrEmpty(a.Gateway) ? string.Empty : $" {a.Gateway}";
        var cmd = $"delete {a.Destination} MASK {a.Mask}{gwPart} IF {a.IfIndex}";
        var (code, _) = await RunRouteAsync(cmd, ct).ConfigureAwait(false);
        if (code == 0)
            return true;

        // Fallback without gateway
        cmd = $"delete {a.Destination} MASK {a.Mask} IF {a.IfIndex}";
        (code, _) = await RunRouteAsync(cmd, ct).ConfigureAwait(false);
        return code == 0;
    }

    private static async Task<(int ExitCode, string Output)> RunRouteAsync(string arguments, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "route.exe",
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var p = new Process { StartInfo = psi };
        p.Start();
        var stdout = await p.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
        var stderr = await p.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
        await p.WaitForExitAsync(ct).ConfigureAwait(false);
        return (p.ExitCode, stdout + stderr);
    }
}
