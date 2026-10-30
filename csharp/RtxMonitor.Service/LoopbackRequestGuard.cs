using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace RtxMonitor.Service;

internal static class LoopbackRequestGuard
{
    internal static async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        if (!IsAllowed(context))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        await next(context).ConfigureAwait(false);
    }

    private static bool IsAllowed(HttpContext context)
    {
        if (!IsLoopback(context.Connection.RemoteIpAddress) ||
            !IsLoopback(context.Connection.LocalIpAddress) ||
            context.Connection.LocalPort is < 1 or > 65535 ||
            context.Request.Scheme != "http" ||
            HasForwardingHeaders(context.Request.Headers))
        {
            return false;
        }

        IHeaderDictionary headers = context.Request.Headers;
        int port = context.Connection.LocalPort;
        if (headers.Host.Count != 1 ||
            !TryGetLocalAuthority(headers.Host[0], port, out string? authority))
        {
            return false;
        }

        if (headers.TryGetValue("Origin", out var origins) &&
            (origins.Count != 1 || origins[0] is not string origin ||
                !origin.StartsWith("http://", StringComparison.Ordinal) ||
                !TryGetLocalAuthority(origin["http://".Length..], port, out string? originAuthority) ||
                !string.Equals(authority, originAuthority, StringComparison.Ordinal)))
        {
            return false;
        }

        return !headers.TryGetValue("Sec-Fetch-Site", out var sites) ||
            (sites.Count == 1 && sites[0] is "same-origin" or "none");
    }

    private static bool HasForwardingHeaders(IHeaderDictionary headers) =>
        headers.Keys.Any(name =>
            name.Equals("Forwarded", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("X-Forwarded-", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("X-Original-", StringComparison.OrdinalIgnoreCase));

    private static bool IsLoopback(IPAddress? address) =>
        address is not null && IPAddress.IsLoopback(
            address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address);

    private static bool TryGetLocalAuthority(string? value, int port, out string? authority)
    {
        authority = null;
        if (string.IsNullOrEmpty(value) || value.Contains('%'))
        {
            return false;
        }

        int hostEnd = value[0] == '[' ? value.IndexOf(']') + 1 : value.IndexOf(':');
        if (hostEnd == 0)
        {
            return false;
        }
        if (hostEnd < 0)
        {
            hostEnd = value.Length;
        }

        string host = value[..hostEnd];
        string suffix = value[hostEnd..];
        string portText = port.ToString(CultureInfo.InvariantCulture);
        if ((suffix.Length == 0 && port != 80) ||
            (suffix.Length != 0 && suffix != ":" + portText) ||
            !TryGetCanonicalLoopbackHost(host, out string? canonicalHost))
        {
            return false;
        }

        authority = canonicalHost + ":" + portText;
        return true;
    }

    private static bool TryGetCanonicalLoopbackHost(string host, out string? canonicalHost)
    {
        canonicalHost = null;
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            canonicalHost = "localhost";
            return true;
        }

        bool bracketed = host.StartsWith('[') && host.EndsWith(']');
        string literal = bracketed ? host[1..^1] : host;
        if (!IPAddress.TryParse(literal, out IPAddress? address) || !IsLoopback(address) ||
            bracketed != (address.AddressFamily == AddressFamily.InterNetworkV6))
        {
            return false;
        }

        string canonical = bracketed ? "[" + address + "]" : address.ToString();
        if (!host.Equals(canonical, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        canonicalHost = canonical;
        return true;
    }
}
