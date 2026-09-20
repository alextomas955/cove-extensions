using System.Net;
using System.Net.Http.Headers;
using WhisparrSync.Contracts;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Connection;

/// <summary>Why a connection attempt produced no response at all.</summary>
public enum ConnectionTransportFailure
{
    /// <summary>The connection never established: an unresolvable host, a refusal, a reset.</summary>
    NoResponse,

    /// <summary>The request outlived the client's timeout.</summary>
    Timeout,

    /// <summary>TLS negotiation failed, which includes a certificate the host does not trust.</summary>
    Tls,
}

/// <summary>Everything one connection attempt produced, in the form the classifier reads.</summary>
/// <remarks>
/// Build one through the factories rather than the constructor, so a caller cannot assemble a
/// combination the decision table has no row for.
/// <para>
/// <c>ContentType</c> is the header as received, unparsed. The measured values include a form with
/// no space after the semicolon, so matching the header string would fail on a body that is JSON.
/// </para>
/// </remarks>
public sealed record ConnectionObservation(
    bool Configured,
    ConnectionTransportFailure? Transport,
    int? StatusCode,
    string? ContentType,
    WhisparrStatusDocument? Document)
{
    /// <summary>No request was made, because the address or the key was not supplied.</summary>
    public static ConnectionObservation NotConfigured() => new(false, null, null, null, null);

    /// <summary>A request was made and produced no response.</summary>
    public static ConnectionObservation TransportFailed(ConnectionTransportFailure failure)
        => new(true, failure, null, null, null);

    /// <summary>A request was made and something answered it.</summary>
    public static ConnectionObservation Answered(
        int statusCode,
        string? contentType,
        WhisparrStatusDocument? document)
        => new(true, null, statusCode, contentType, document);
}

/// <summary>
/// Turns one connection observation into the kind that describes it.
/// </summary>
/// <remarks>
/// The step order below is load-bearing. A rejected key answers with an empty content-type on both
/// generations, so the status test runs before the content-type test. A wrong address answers 200 as
/// a web page, so no step branches on a success status alone.
/// </remarks>
public static class ConnectionFailureClassifier
{
    // The value appName carries on both generations.
    internal const string WhisparrAppName = "Whisparr";

    public static ConnectionFailureKind Classify(ConnectionObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);

        if (!observation.Configured)
        {
            return ConnectionFailureKind.NotConfigured;
        }

        if (observation.Transport is not null)
        {
            return ConnectionFailureKind.Unreachable;
        }

        // Runs before the content-type test. A 403 folds in here: an instance behind an auth proxy is
        // unobserved, and the key field is the right answer when the status came from Whisparr.
        if (observation.StatusCode is (int)HttpStatusCode.Unauthorized or (int)HttpStatusCode.Forbidden)
        {
            return ConnectionFailureKind.KeyRejected;
        }

        if (!IsJsonMediaType(observation.ContentType))
        {
            return ConnectionFailureKind.NotTheWhisparrApi;
        }

        // A problem+json body reaches here, parses, and fails on the absent version.
        if (observation.Document is not { } document || string.IsNullOrWhiteSpace(document.Version))
        {
            return ConnectionFailureKind.NotTheWhisparrApi;
        }

        // A negative test, so it cannot mis-refuse a real Whisparr, whose appName is measured.
        if (!string.IsNullOrEmpty(document.AppName)
            && !document.AppName.Equals(WhisparrAppName, StringComparison.OrdinalIgnoreCase))
        {
            return ConnectionFailureKind.VersionNotManaged;
        }

        // The detector owns the version-major reading, so the refusal and the success are decided
        // from one place.
        return GenerationDetector.Detect(document).Generation is null
            ? ConnectionFailureKind.VersionNotManaged
            : ConnectionFailureKind.Connected;
    }

    // Parsed and compared on the media type alone: the measured values carry a charset parameter,
    // with and without a space after the semicolon. A +json suffix counts.
    internal static bool IsJsonMediaType(string? contentType)
    {
        if (!MediaTypeHeaderValue.TryParse(contentType, out var parsed) || parsed.MediaType is not { } mediaType)
        {
            return false;
        }

        return mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase)
            || mediaType.Equals("text/json", StringComparison.OrdinalIgnoreCase)
            || mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase);
    }
}
