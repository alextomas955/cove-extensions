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
    /// Holds no state between calls, so two tests running at once each describe their own address.
    /// </remarks>
    Task<ConnectionTestView> TestAsync(string? address, string? apiKey, CancellationToken ct);
}

internal sealed class ConnectionTester(WhisparrTransport transport, ILogger<ConnectionTester> logger)
    : IWhisparrConnectionTester
{
    // How much of a name the answering instance chose is echoed back. The version is bounded by the
    // stored reading's ceiling instead, so the echoed and the recorded version cannot differ.
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
            var response = await transport.ReadStatusAsync(baseAddress, apiKey, ct).ConfigureAwait(false);
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
        catch (Exception failure)
            when (failure is HttpRequestException or IOException or TaskCanceledException)
        {
            // The caller turns this into an answer for the user, so nothing here rethrows and the
            // swallowed failure emits exactly one line.
            var category = CategoryOf(failure);
            WhisparrSyncLog.ConnectionTransportFailure(logger, category, baseAddress.Host);
            observation = ConnectionObservation.TransportFailed(category);
        }

        return Describe(observation, baseAddress);
    }

    // Names the setting that is empty when the pair cannot be read. The address is examined first, so
    // a call supplying neither names the address. baseAddress is still set on a refusal the key
    // caused, so that refusal can echo the address it would have used.
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

    // Rebuilt from scheme, authority and path rather than used as typed. The authority carries no
    // user-info, so credentials a user embedded cannot reach a log line, a response body or the
    // outbound request.
    internal static bool TryReadAddress(string? address, [NotNullWhen(true)] out Uri? baseAddress)
    {
        baseAddress = null;
        if (string.IsNullOrWhiteSpace(address)
            || !Uri.TryCreate(address.Trim(), UriKind.Absolute, out var parsed)
            || !WhisparrTransport.IsAddressable(parsed))
        {
            return false;
        }

        baseAddress = new Uri(
            string.Create(CultureInfo.InvariantCulture, $"{parsed.Scheme}://{parsed.Authority}{parsed.AbsolutePath}"));
        return true;
    }

    // Strips surrounding space and trailing separators only. Nothing is added: an address with no
    // scheme is left without one, so it is refused rather than guessed at.
    internal static string NormaliseAddress(string? address)
        => (address ?? "").Trim().TrimEnd('/');

    // A trailing separator and letter case do not count as an edit, so neither discards a reading
    // taken against the address before it.
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

        // The capability set describes the generation that answered, not the one the settings select.
        var connected = kind == ConnectionFailureKind.Connected ? reading.Generation : null;

        // Nothing between the response buffer's ceiling and this projection bounds what the answering
        // instance sent, so every echoed string is shortened here.
        return new ConnectionTestView(
            kind,
            connected,
            connected is { } generation ? GenerationCapabilities.CapabilitiesOf(generation) : null,
            BoundedText.Shorten(
                reading.Version, WhisparrSyncGenerationConnection.RecordedVersionMaxLength),
            BoundedText.Shorten(reading.Branch, ReportedNameMaxLength),
            reading.Corroborated,
            BoundedText.Shorten(otherApplication, ReportedNameMaxLength),
            baseAddress.ToString(),
            null);
    }
}
