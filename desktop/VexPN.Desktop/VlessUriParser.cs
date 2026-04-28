using System.Globalization;
using System.Web;

namespace VexPN.Desktop;

/// <summary>
/// Parses VLESS sharing links (vless://uuid@host:port?...#name).
/// </summary>
public static class VlessUriParser
{
    public sealed record ParsedVless(
        string Uuid,
        string Address,
        int Port,
        string Encryption,
        string Security,
        string Type,
        string? Sni,
        string? HostHeader,
        string? Path,
        string? Alpn,
        string? Fingerprint,
        string? PublicKey,
        string? ShortId,
        string? SpiderX,
        string? ServiceName,
        string? Flow,
        bool AllowInsecure);

    public static bool TryParse(string? vlessUri, out ParsedVless? parsed, out string? error)
    {
        parsed = null;
        error = null;

        if (string.IsNullOrWhiteSpace(vlessUri))
        {
            error = "Пустая VLESS ссылка.";
            return false;
        }

        var raw = vlessUri.Trim();
        if (!raw.StartsWith("vless://", StringComparison.OrdinalIgnoreCase))
        {
            error = "Ссылка должна начинаться с vless://";
            return false;
        }

        var rest = raw["vless://".Length..];
        var hashIdx = rest.IndexOf('#', StringComparison.Ordinal);
        if (hashIdx >= 0)
            rest = rest[..hashIdx];

        var qIdx = rest.IndexOf('?', StringComparison.Ordinal);
        var main = qIdx >= 0 ? rest[..qIdx] : rest;
        var query = qIdx >= 0 ? rest[(qIdx + 1)..] : string.Empty;

        var at = main.LastIndexOf('@');
        if (at <= 0 || at >= main.Length - 1)
        {
            error = "Неверный формат: ожидается uuid@host:port";
            return false;
        }

        var uuid = Uri.UnescapeDataString(main[..at]);
        if (string.IsNullOrWhiteSpace(uuid))
        {
            error = "Отсутствует UUID.";
            return false;
        }

        var hostPort = main[(at + 1)..];
        string address;
        int port = 443;
        if (hostPort.StartsWith('['))
        {
            var endBracket = hostPort.IndexOf(']', StringComparison.Ordinal);
            if (endBracket < 0)
            {
                error = "Неверный IPv6 в ссылке.";
                return false;
            }

            address = hostPort[1..endBracket];
            var after = hostPort[(endBracket + 1)..];
            if (after.StartsWith(':'))
            {
                var p = after[1..];
                if (!int.TryParse(p, NumberStyles.Integer, CultureInfo.InvariantCulture, out port))
                {
                    error = "Неверный порт.";
                    return false;
                }
            }
        }
        else
        {
            var colon = hostPort.LastIndexOf(':');
            if (colon > 0 && colon < hostPort.Length - 1 &&
                int.TryParse(hostPort[(colon + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var p))
            {
                address = hostPort[..colon];
                port = p;
            }
            else
            {
                address = hostPort;
            }
        }

        if (string.IsNullOrWhiteSpace(address))
        {
            error = "Отсутствует адрес сервера.";
            return false;
        }

        var q = ParseQuery(query);

        var encryption = Get(q, "encryption") ?? "none";
        var security = (Get(q, "security") ?? "none").ToLowerInvariant();
        var type = (Get(q, "type") ?? "tcp").ToLowerInvariant();
        var sni = Get(q, "sni");
        var hostHeader = Get(q, "host");
        var path = Get(q, "path");
        var alpn = Get(q, "alpn");
        var fp = Get(q, "fp");
        var pbk = Get(q, "pbk");
        var sid = Get(q, "sid");
        var spx = Get(q, "spx");
        var serviceName = Get(q, "serviceName");
        var flow = Get(q, "flow");
        var insecure = string.Equals(Get(q, "allowInsecure"), "1", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(Get(q, "allowInsecure"), "true", StringComparison.OrdinalIgnoreCase);

        parsed = new ParsedVless(
            uuid,
            address,
            port,
            encryption,
            security,
            type,
            sni,
            hostHeader,
            path is not null ? Uri.UnescapeDataString(path) : null,
            alpn,
            fp,
            pbk,
            sid,
            spx,
            serviceName,
            flow,
            insecure);

        return true;
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(query))
            return dict;

        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq <= 0)
                continue;
            var k = Uri.UnescapeDataString(pair[..eq]);
            var v = eq < pair.Length - 1 ? Uri.UnescapeDataString(pair[(eq + 1)..]) : string.Empty;
            if (!string.IsNullOrEmpty(k))
                dict[k] = v;
        }

        return dict;
    }

    private static string? Get(Dictionary<string, string> q, string key) =>
        q.TryGetValue(key, out var v) ? v : null;
}
