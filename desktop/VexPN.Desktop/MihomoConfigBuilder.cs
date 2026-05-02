using System.Globalization;
using System.Text;

namespace VexPN.Desktop;

/// <summary>
/// Генерирует YAML для Mihomo (Clash Meta): TUN + VLESS outbound + правила PROCESS-NAME (Windows).
/// </summary>
public static class MihomoConfigBuilder
{
    public const string TunInterfaceName = "VexPNTun";
    private const string ProxyEntryName = "vex-vless-node";
    private const string ProxyGroupName = "vex";

    public static string BuildYaml(VlessUriParser.ParsedVless v, IReadOnlyList<string> processExeBasenames)
    {
        if (processExeBasenames.Count == 0)
            throw new ArgumentException("Нужен хотя бы один процесс.", nameof(processExeBasenames));

        var sb = new StringBuilder();
        sb.AppendLine("mode: rule");
        sb.AppendLine("log-level: warning");
        sb.AppendLine("ipv6: false");
        sb.AppendLine();

        sb.AppendLine("dns:");
        sb.AppendLine("  enable: true");
        sb.AppendLine("  ipv6: false");
        sb.AppendLine("  enhanced-mode: redir-host");
        sb.AppendLine("  use-hosts: true");
        sb.AppendLine("  nameserver:");
        sb.AppendLine("    - 1.1.1.1");
        sb.AppendLine("    - 1.0.0.1");
        sb.AppendLine("    - 8.8.8.8");
        sb.AppendLine();

        sb.AppendLine("tun:");
        sb.AppendLine("  enable: true");
        sb.AppendLine("  stack: system");
        sb.AppendLine($"  interface-name: {TunInterfaceName}");
        sb.AppendLine("  mtu: 1360");
        sb.AppendLine("  auto-route: true");
        sb.AppendLine("  auto-detect-interface: true");
        sb.AppendLine("  strict-route: false");
        sb.AppendLine("  dns-hijack:");
        sb.AppendLine("    - any:53");
        sb.AppendLine();

        sb.AppendLine("proxies:");
        AppendVlessProxy(sb, v);
        sb.AppendLine();

        sb.AppendLine("proxy-groups:");
        sb.AppendLine($"  - name: {ProxyGroupName}");
        sb.AppendLine("    type: select");
        sb.AppendLine("    proxies:");
        sb.AppendLine($"      - {ProxyEntryName}");
        sb.AppendLine();

        sb.AppendLine("rules:");
        foreach (var baseName in processExeBasenames.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var exe = baseName.Trim();
            if (!exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                exe += ".exe";
            sb.AppendLine($"  - PROCESS-NAME,{exe},{ProxyGroupName}");
        }

        sb.AppendLine("  - MATCH,DIRECT");

        return sb.ToString();
    }

    private static void AppendVlessProxy(StringBuilder sb, VlessUriParser.ParsedVless v)
    {
        var net = v.Type.ToLowerInvariant();

        sb.AppendLine($"  - name: {ProxyEntryName}");
        sb.AppendLine("    type: vless");
        sb.AppendLine($"    server: {YamlEsc(v.Address)}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"    port: {v.Port}");
        sb.AppendLine($"    uuid: {YamlEsc(v.Uuid)}");
        sb.AppendLine($"    network: {net}");
        sb.AppendLine("    udp: true");

        if (!string.IsNullOrWhiteSpace(v.Flow))
            sb.AppendLine($"    flow: {YamlEsc(v.Flow)}");

        switch (v.Security.ToLowerInvariant())
        {
            case "reality":
                sb.AppendLine("    tls: true");
                sb.AppendLine($"    servername: {YamlEsc(string.IsNullOrWhiteSpace(v.Sni) ? v.Address : v.Sni!)}");
                sb.AppendLine(
                    $"    client-fingerprint: {YamlEsc(string.IsNullOrWhiteSpace(v.Fingerprint) ? "chrome" : v.Fingerprint!)}");
                sb.AppendLine("    reality-opts:");
                sb.AppendLine($"      public-key: {YamlEsc(v.PublicKey ?? string.Empty)}");
                sb.AppendLine($"      short-id: {YamlEsc(v.ShortId ?? string.Empty)}");
                if (!string.IsNullOrWhiteSpace(v.SpiderX))
                    sb.AppendLine($"      spider-x: {YamlEsc(v.SpiderX)}");
                break;
            case "tls":
                sb.AppendLine("    tls: true");
                sb.AppendLine($"    servername: {YamlEsc(string.IsNullOrWhiteSpace(v.Sni) ? v.Address : v.Sni!)}");
                sb.AppendLine($"    skip-cert-verify: {(v.AllowInsecure ? "true" : "false")}");
                if (!string.IsNullOrWhiteSpace(v.Alpn))
                {
                    sb.AppendLine("    alpn:");
                    foreach (var p in v.Alpn.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        sb.AppendLine($"      - {YamlEsc(p)}");
                }

                break;
            default:
                sb.AppendLine("    tls: false");
                break;
        }

        switch (v.Type.ToLowerInvariant())
        {
            case "ws":
                sb.AppendLine("    ws-opts:");
                sb.AppendLine($"      path: {YamlEsc(string.IsNullOrWhiteSpace(v.Path) ? "/" : v.Path!)}");
                sb.AppendLine("      headers:");
                sb.AppendLine(
                    $"        Host: {YamlEsc(string.IsNullOrWhiteSpace(v.HostHeader) ? (string.IsNullOrWhiteSpace(v.Sni) ? v.Address : v.Sni!) : v.HostHeader!)}");
                break;
            case "grpc":
                sb.AppendLine("    grpc-opts:");
                sb.AppendLine(
                    $"      grpc-service-name: {YamlEsc(string.IsNullOrWhiteSpace(v.ServiceName) ? string.Empty : v.ServiceName!)}");
                break;
            case "http":
            case "h2":
                sb.AppendLine("    http-opts:");
                sb.AppendLine($"      path: {YamlEsc(string.IsNullOrWhiteSpace(v.Path) ? "/" : v.Path!)}");
                sb.AppendLine("      host:");
                sb.AppendLine(
                    $"        - {YamlEsc(string.IsNullOrWhiteSpace(v.HostHeader) ? (string.IsNullOrWhiteSpace(v.Sni) ? v.Address : v.Sni!) : v.HostHeader!)}");
                break;
        }
    }

    private static string YamlEsc(string s)
    {
        if (string.IsNullOrEmpty(s))
            return "\"\"";
        return "\"" + s.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) +
               "\"";
    }
}
