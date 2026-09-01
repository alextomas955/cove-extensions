using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.Extensions.Logging;
using WhisparrSync.Contracts;
using WhisparrSync.Options;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Connection;

/// <summary>Tests one Whisparr address and key and reports what answered.</summary>
public interface IWhisparrConnectionTester
{
    /// <summary>
    /// Tests <paramref name="address"/> with <paramref name="apiKey"/> and classifies the answer.
    /// </summary>
    /// <remarks>
    /// Takes both explicitly, because the transient test describes the address that was in the field
    /// rather than the one that was last saved. Holds no state between calls, so two tests running at
    /// once each describe their own address.
    /// </remarks>
    Task<ConnectionTestView> TestAsync(string? address, string? apiKey, CancellationToken ct);
}

/// <inheritdoc cref="IWhisparrConnectionTester"/>
internal sealed class ConnectionTester(IWhisparrClient client, ILogger<ConnectionTester> logger)
    : IWhisparrConnectionTester
{
    /// <summary>How much of a name the answering instance chose is echoed back.</summary>
    /// <remarks>
    /// Long enough for every branch and application name either generation declares. The version
    /// answers to the stored reading's ceiling instead, so the echoed version and the recorded one
    /// cannot differ in length.
    /// </remarks>
    internal const int ReportedNameMaxLength = 64;

    public async Task<ConnectionTestView> TestAsync(string? address, string? apiKey, CancellationToken ct)
    {
        if (!TryReadConnection(address, apiKey, out var baseAddress, out var missing))
        {
            return ConnectionTestView.NotConfigured(missing, baseAddress?.ToString());
        }

        ConnectionObservation observation;
        try
        {
            var response = await client.ReadStatusAsync(baseAddress, apiKey, ct).ConfigureAwait(false);
            observation = ConnectionObservation.Answered(
                response.StatusCode,
                response.ContentType,
                WhisparrStatusDocument.Parse(response.Body));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // A shutdown is not a verdict about the address, so it must not be reported as one.
            throw;
        }
        catch (Exception failure) when (failure is HttpRequestException or TaskCanceledException)
        {
            // Best-effort, and therefore exactly one line. The caller turns this into an answer for the
            // user, so nothing here rethrows and nothing here is swallowed in silence.
            var category = CategoryOf(failure);
            WhisparrSyncLog.ConnectionTransportFailure(logger, category, baseAddress.Host);
            observation = ConnectionObservation.TransportFailed(category);
        }

        return Describe(observation, baseAddress);
    }

    /// <summary>
    /// Reads <paramref name="address"/> and <paramref name="apiKey"/> as a connection a request may be
    /// made with, and names the setting that is empty when it cannot.
    /// </summary>
    /// <remarks>
    /// The address is examined first, so a call supplying neither setting names the address rather than
    /// varying between runs. <paramref name="baseAddress"/> is still set on a refusal the key caused,
    /// so that refusal can echo the address it would have been made against.
    /// </remarks>
    /// <param name="address">The base address, as it was typed or stored.</param>
    /// <param name="apiKey">The key, as it was typed or stored.</param>
    /// <param name="baseAddress">The address a request may be made to, or null when it is not one.</param>
    /// <param name="missing">The empty setting, meaningful only when this returns false.</param>
    internal static bool TryReadConnection(
        string? address,
        [NotNullWhen(true)] string? apiKey,
        [NotNullWhen(true)] out Uri? baseAddress,
        out ConnectionSetting missing)
    {
        if (!TryReadAddress(address, out baseAddress))
        {
            missing = ConnectionSetting.Address;
            return false;
        }

        missing = ConnectionSetting.ApiKey;
        return !string.IsNullOrWhiteSpace(apiKey);
    }

    /// <summary>
    /// Reads <paramref name="address"/> as an address a request may be made to.
    /// </summary>
    /// <remarks>
    /// Rebuilt from its scheme, authority and path rather than used as typed: the authority carries no
    /// user-info, so anything a user embedded as credentials is dropped here and cannot reach a log
    /// line, a response body or the outbound request.
    /// </remarks>
    internal static bool TryReadAddress(string? address, [NotNullWhen(true)] out Uri? baseAddress)
    {
        baseAddress = null;
        if (string.IsNullOrWhiteSpace(address)
            || !Uri.TryCreate(address.Trim(), UriKind.Absolute, out var parsed)
            || !WhisparrClient.IsAddressable(parsed))
        {
            return false;
        }

        baseAddress = new Uri(
            string.Create(CultureInfo.InvariantCulture, $"{parsed.Scheme}://{parsed.Authority}{parsed.AbsolutePath}"));
        return true;
    }

    /// <summary>
    /// <paramref name="address"/> with the parts that do not change where it points removed.
    /// </summary>
    /// <remarks>
    /// Surrounding space and trailing separators only. Nothing is added: an address with no scheme is
    /// left without one, so it is refused and named rather than turned into a guess at what was meant.
    /// </remarks>
    internal static string NormaliseAddress(string? address)
        => (address ?? "").Trim().TrimEnd('/');

    /// <summary>Whether the two addresses point at the same instance.</summary>
    /// <remarks>
    /// A trailing separator and letter case do not count as an edit, so neither discards a reading
    /// taken against the address before it.
    /// </remarks>
    internal static bool IsSameAddress(string? left, string? right)
        => string.Equals(
            NormaliseAddress(left), NormaliseAddress(right), StringComparison.OrdinalIgnoreCase);

    private static ConnectionTransportFailure CategoryOf(Exception failure) => failure switch
    {
        HttpRequestException { HttpRequestError: HttpRequestError.SecureConnectionError }
            => ConnectionTransportFailure.Tls,
        TaskCanceledException => ConnectionTransportFailure.Timeout,
        _ => ConnectionTransportFailure.NoResponse,
    };

    // The classifier and the detector are both pure and both read the same document, so the kind and
    // the reading on one view cannot disagree.
    private static ConnectionTestView Describe(ConnectionObservation observation, Uri baseAddress)
    {
        var kind = ConnectionFailureClassifier.Classify(observation);
        var reading = GenerationDetector.Detect(observation.Document);
        var appName = observation.Document?.AppName;
        var otherApplication =
            appName is not null
            && !appName.Equals(ConnectionFailureClassifier.WhisparrAppName, StringComparison.OrdinalIgnoreCase)
                ? appName
                : null;

        // The capability set is built here because this is where a connection is established: it
        // describes the generation that answered, not the one the settings currently select.
        var connected = kind == ConnectionFailureKind.Connected ? reading.Generation : null;

        // Nothing between the response buffer's ceiling and this projection bounds what the answering
        // instance chose to send. The version is held to the stored reading's ceiling because the
        // stored reading is taken from it.
        return new ConnectionTestView(
            kind,
            connected,
            connected is { } generation ? GenerationCapabilities.For(generation).Held : null,
            BoundedText.Shorten(
                reading.Version, WhisparrSyncGenerationConnection.RecordedVersionMaxLength),
            BoundedText.Shorten(reading.Branch, ReportedNameMaxLength),
            reading.Corroborated,
            BoundedText.Shorten(otherApplication, ReportedNameMaxLength),
            baseAddress.ToString(),
            null);
    }
}
