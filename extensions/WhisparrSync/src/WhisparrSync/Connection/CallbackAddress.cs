using System.Globalization;

namespace WhisparrSync.Connection;

/// <summary>
/// Builds the address Whisparr calls back on, and reads an edited one back into the part of it a user
/// is allowed to change.
/// </summary>
/// <remarks>
/// Pure: no store, no clock, no request. Everything it needs arrives as an argument, so the merge rule
/// is decidable without a host.
/// <para>
/// Only the part of an edit up to where this extension's own route begins is honoured — scheme, host,
/// port and path prefix. A Cove behind a reverse proxy on a subpath cannot produce a working callback
/// under a scheme-host-port-only reading, and that failure presents as Whisparr's fault.
/// </para>
/// </remarks>
public static class CallbackAddress
{
    /// <summary>The query parameter a hand-pasted address carries its secret in.</summary>
    public const string SecretQueryParameter = "s";

    /// <summary>The route this extension mounts the inbound callback on, relative to the host root.</summary>
    /// <exception cref="ArgumentException"><paramref name="extensionId"/> is empty or whitespace.</exception>
    public static string RouteFor(string extensionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extensionId);
        return "/api/extensions/" + extensionId + "/callback";
    }

    /// <summary>
    /// The part of <paramref name="editedAddress"/> a user is allowed to change: scheme, host, port
    /// and path prefix, with this extension's own route and any secret removed.
    /// </summary>
    /// <remarks>
    /// Applying this to its own output returns the same value, so an address that is saved and
    /// reloaded does not drift.
    /// <para>
    /// An address that is not an absolute http or https URL yields the empty host, which falls back to
    /// the request host rather than registering something no instance can reach.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="extensionId"/> is empty or whitespace.</exception>
    public static string HostPartOf(string? editedAddress, string extensionId)
    {
        var route = RouteFor(extensionId);
        if (string.IsNullOrWhiteSpace(editedAddress)
            || !Uri.TryCreate(editedAddress.Trim(), UriKind.Absolute, out var parsed)
            || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
        {
            return "";
        }

        // GetLeftPart omits a port that is the scheme's default, so http://host:80 and http://host
        // reduce to one spelling and a saved value stops depending on how it was typed.
        var authority = parsed.GetLeftPart(UriPartial.Authority);
        var path = parsed.AbsolutePath;
        if (path.EndsWith(route, StringComparison.OrdinalIgnoreCase))
        {
            path = path[..^route.Length];
        }

        return authority + path.TrimEnd('/');
    }

    /// <summary>
    /// The callback host to build on: the stored one when one is stored, otherwise the host this
    /// request arrived on.
    /// </summary>
    /// <remarks>
    /// A stored host is used even when it equals the request host. What storing it buys is that a
    /// later request arriving on a different host does not silently move the address.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="requestHost"/> is empty or whitespace.</exception>
    public static string ResolveHost(string? storedCallbackHost, string requestHost)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestHost);
        return string.IsNullOrWhiteSpace(storedCallbackHost)
            ? requestHost.Trim().TrimEnd('/')
            : storedCallbackHost.Trim().TrimEnd('/');
    }

    /// <summary>The address to register, which carries no secret.</summary>
    /// <remarks>
    /// The secret travels out of band wherever the connected generation can carry it. A query string
    /// is written to the access log of every proxy and load balancer on the delivery path.
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// <paramref name="callbackHost"/> or <paramref name="extensionId"/> is empty or whitespace.
    /// </exception>
    public static string WithoutSecret(string callbackHost, string extensionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(callbackHost);
        return callbackHost.Trim().TrimEnd('/') + RouteFor(extensionId);
    }

    /// <summary>
    /// The address a user copies, which carries the secret because a pasted address has nowhere else
    /// to put one.
    /// </summary>
    /// <exception cref="ArgumentException">Any argument is empty or whitespace.</exception>
    public static string WithSecret(string callbackHost, string extensionId, string secret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{WithoutSecret(callbackHost, extensionId)}?{SecretQueryParameter}={Uri.EscapeDataString(secret)}");
    }
}
