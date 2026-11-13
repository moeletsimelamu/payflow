using System.Net;
using System.Net.Sockets;
using PayFlow.Modules.Webhooks.Contracts;
using PayFlow.SharedKernel.Results;

namespace PayFlow.Modules.Webhooks.Infrastructure;

/// <summary>Raised when a webhook destination resolves only to addresses PayFlow refuses to call.</summary>
public sealed class SsrfBlockedException : Exception
{
    public SsrfBlockedException()
    {
    }

    public SsrfBlockedException(string message)
        : base(message)
    {
    }

    public SsrfBlockedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Server-side request forgery protection for webhook delivery (WH-7). Merchants choose the URL, so
/// PayFlow must never be tricked into calling its own network, cloud metadata services or other
/// internal hosts. Checked twice: when the URL is saved, and against the resolved IP address at
/// connect time, which defeats DNS rebinding (a name that resolves publicly at save time and
/// privately at send time).
/// </summary>
internal static class SsrfGuard
{
    private static readonly IPNetwork[] Blocked =
    [
        IPNetwork.Parse("0.0.0.0/8"), // "this" network
        IPNetwork.Parse("10.0.0.0/8"), // private
        IPNetwork.Parse("100.64.0.0/10"), // carrier-grade NAT
        IPNetwork.Parse("127.0.0.0/8"), // loopback
        IPNetwork.Parse("169.254.0.0/16"), // link-local, including cloud metadata 169.254.169.254
        IPNetwork.Parse("172.16.0.0/12"), // private
        IPNetwork.Parse("192.0.0.0/24"), // IETF protocol assignments
        IPNetwork.Parse("192.0.2.0/24"), // documentation
        IPNetwork.Parse("192.88.99.0/24"), // 6to4 relay
        IPNetwork.Parse("192.168.0.0/16"), // private
        IPNetwork.Parse("198.18.0.0/15"), // benchmarking
        IPNetwork.Parse("198.51.100.0/24"), // documentation
        IPNetwork.Parse("203.0.113.0/24"), // documentation
        IPNetwork.Parse("224.0.0.0/4"), // multicast
        IPNetwork.Parse("240.0.0.0/4"), // reserved, including broadcast
        IPNetwork.Parse("::/128"), // unspecified
        IPNetwork.Parse("::1/128"), // loopback
        IPNetwork.Parse("64:ff9b::/96"), // NAT64 (can map to any IPv4, including private)
        IPNetwork.Parse("100::/64"), // discard
        IPNetwork.Parse("2001:db8::/32"), // documentation
        IPNetwork.Parse("fc00::/7"), // unique local
        IPNetwork.Parse("fe80::/10"), // link-local
        IPNetwork.Parse("ff00::/8"), // multicast
    ];

    private static readonly string[] InternalNameSuffixes = [".localhost", ".local", ".internal", ".lan", ".home.arpa"];

    public static bool IsPublic(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        return !Blocked.Any(n => n.BaseAddress.AddressFamily == address.AddressFamily && n.Contains(address));
    }

    /// <summary>Save-time URL policy. Hostnames are re-checked against their resolved addresses at connect time.</summary>
    public static Result<Uri> ValidateUrl(string? url, WebhookOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(url) || url.Length > 2048 || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return Error.Validation("invalid_webhook_url", "url must be an absolute URL of at most 2048 characters.");
        }

        if (uri.Scheme != Uri.UriSchemeHttps && !(options.AllowHttp && uri.Scheme == Uri.UriSchemeHttp))
        {
            return Error.Validation("webhook_url_not_allowed", "Webhook URLs must use https.");
        }

        if (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment))
        {
            return Error.Validation("webhook_url_not_allowed", "Webhook URLs must not contain credentials or a fragment.");
        }

        if (options.AllowPrivateNetworks)
        {
            return uri;
        }

        var host = uri.IdnHost.TrimEnd('.').ToLowerInvariant();
        if (host == "localhost" || InternalNameSuffixes.Any(s => host.EndsWith(s, StringComparison.Ordinal)) || !host.Contains('.', StringComparison.Ordinal) && uri.HostNameType == UriHostNameType.Dns)
        {
            return Error.Validation("webhook_url_not_allowed", "Webhook URLs must point to a public internet host.");
        }

        if (IPAddress.TryParse(uri.IdnHost.Trim('[', ']'), out var literal) && !IsPublic(literal))
        {
            return Error.Validation("webhook_url_not_allowed", "Webhook URLs must not point to private, loopback or link-local addresses.");
        }

        return uri;
    }

    /// <summary>
    /// The HTTP handler PayFlow uses for webhooks: no redirects (a redirect could point inside),
    /// no cookies, and a connect callback that resolves the host itself and connects only to vetted addresses.
    /// </summary>
    public static SocketsHttpHandler CreateHandler(WebhookOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectCallback = async (context, ct) =>
            {
                var host = context.DnsEndPoint.Host;
                var addresses = IPAddress.TryParse(host, out var literal) ? [literal] : await Dns.GetHostAddressesAsync(host, ct);
                var permitted = options.AllowPrivateNetworks ? addresses : addresses.Where(IsPublic).ToArray();
                if (permitted.Length == 0)
                {
                    throw new SsrfBlockedException($"Destination '{host}' resolves only to non-public addresses.");
                }

                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(permitted, context.DnsEndPoint.Port, ct);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        };
    }
}
