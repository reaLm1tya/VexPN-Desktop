using System.Text.Json;
using System.Text.Json.Nodes;

namespace VexPN.Desktop;

/// <summary>
/// Builds Xray-core JSON config: TUN inbound + VLESS outbound + basic DNS/routing.
/// </summary>
public static class XrayConfigBuilder
{
    private const string TunName = "xray0";

    public static string BuildJson(VlessUriParser.ParsedVless v)
    {
        var root = new JsonObject
        {
            ["log"] = new JsonObject { ["loglevel"] = "warning" },
            // UseIPv4: избегаем AAAA/IPv6, который на TUN часто «ломает» отдельные клиенты (в т.ч. Telegram).
            ["dns"] = new JsonObject
            {
                ["queryStrategy"] = "UseIPv4",
                ["servers"] = new JsonArray(
                    JsonValue.Create("1.1.1.1"),
                    JsonValue.Create("1.0.0.1"),
                    JsonValue.Create("8.8.8.8"),
                    JsonValue.Create("8.8.4.4"))
            },
            ["inbounds"] = new JsonArray(BuildTunInbound()),
            ["outbounds"] = new JsonArray(
                BuildVlessOutbound(v),
                new JsonObject
                {
                    ["tag"] = "direct",
                    ["protocol"] = "freedom",
                    ["settings"] = new JsonObject()
                }),
            // IPIfNonMatch: лучше согласуется с sniffing (TLS/HTTP) и обходом «зависаний» на DNS/доменах.
            ["routing"] = new JsonObject
            {
                ["domainStrategy"] = "IPIfNonMatch",
                ["rules"] = new JsonArray(
                    new JsonObject
                    {
                        ["type"] = "field",
                        ["network"] = "tcp,udp",
                        ["outboundTag"] = "proxy"
                    })
            }
        };

        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static JsonObject BuildTunInbound() =>
        new()
        {
            ["tag"] = "tun-in",
            ["port"] = 0,
            ["protocol"] = "tun",
            ["settings"] = new JsonObject
            {
                ["name"] = TunName,
                // Ниже MTU — меньше обрывов у отдельных UDP/крупных сессий (в т.ч. мессенджеров).
                ["MTU"] = 1360
            },
            ["sniffing"] = new JsonObject
            {
                ["enabled"] = true,
                ["routeOnly"] = false,
                ["destOverride"] = new JsonArray(
                    JsonValue.Create("http"),
                    JsonValue.Create("tls"),
                    JsonValue.Create("quic"))
            }
        };

    private static JsonObject BuildVlessOutbound(VlessUriParser.ParsedVless v)
    {
        var user = new JsonObject
        {
            ["id"] = v.Uuid,
            ["encryption"] = string.IsNullOrEmpty(v.Encryption) ? "none" : v.Encryption
        };
        if (!string.IsNullOrWhiteSpace(v.Flow))
            user["flow"] = v.Flow!;

        var outbound = new JsonObject
        {
            ["tag"] = "proxy",
            ["protocol"] = "vless",
            ["settings"] = new JsonObject
            {
                ["vnext"] = new JsonArray(new JsonObject
                {
                    ["address"] = v.Address,
                    ["port"] = v.Port,
                    ["users"] = new JsonArray(user)
                })
            },
            ["streamSettings"] = BuildStreamSettings(v)
        };

        return outbound;
    }

    private static JsonObject BuildStreamSettings(VlessUriParser.ParsedVless v)
    {
        var stream = new JsonObject
        {
            ["network"] = v.Type
        };

        switch (v.Security.ToLowerInvariant())
        {
            case "reality":
                stream["security"] = "reality";
                stream["realitySettings"] = new JsonObject
                {
                    ["show"] = false,
                    ["fingerprint"] = string.IsNullOrWhiteSpace(v.Fingerprint) ? "chrome" : v.Fingerprint!,
                    ["serverName"] = string.IsNullOrWhiteSpace(v.Sni) ? v.Address : v.Sni!,
                    ["publicKey"] = v.PublicKey ?? string.Empty,
                    ["shortId"] = v.ShortId ?? string.Empty,
                    ["spiderX"] = string.IsNullOrWhiteSpace(v.SpiderX) ? "/" : v.SpiderX!
                };
                break;
            case "tls":
                stream["security"] = "tls";
                var tls = new JsonObject
                {
                    ["serverName"] = string.IsNullOrWhiteSpace(v.Sni) ? v.Address : v.Sni!,
                    ["allowInsecure"] = v.AllowInsecure
                };
                if (!string.IsNullOrWhiteSpace(v.Alpn))
                {
                    tls["alpn"] = new JsonArray(
                        v.Alpn.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                            .Select(s => JsonValue.Create(s)!)
                            .ToArray());
                }

                stream["tlsSettings"] = tls;
                break;
            default:
                stream["security"] = "none";
                break;
        }

        switch (v.Type.ToLowerInvariant())
        {
            case "ws":
                stream["wsSettings"] = new JsonObject
                {
                    ["path"] = string.IsNullOrWhiteSpace(v.Path) ? "/" : v.Path!,
                    ["headers"] = new JsonObject
                    {
                        ["Host"] = string.IsNullOrWhiteSpace(v.HostHeader)
                            ? (string.IsNullOrWhiteSpace(v.Sni) ? v.Address : v.Sni!)
                            : v.HostHeader!
                    }
                };
                break;
            case "grpc":
                stream["grpcSettings"] = new JsonObject
                {
                    ["serviceName"] = string.IsNullOrWhiteSpace(v.ServiceName) ? string.Empty : v.ServiceName!
                };
                break;
            case "http":
            case "h2":
                stream["httpSettings"] = new JsonObject
                {
                    ["path"] = string.IsNullOrWhiteSpace(v.Path) ? "/" : v.Path!,
                    ["host"] = new JsonArray(string.IsNullOrWhiteSpace(v.HostHeader)
                        ? (string.IsNullOrWhiteSpace(v.Sni) ? v.Address : v.Sni!)
                        : v.HostHeader!)
                };
                break;
        }

        return stream;
    }
}
