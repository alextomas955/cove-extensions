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
/// Build one through the factories rather than the constructor: each names the situation it stands
/// for, so a caller cannot assemble a combination the decision table has no row for.
/// </remarks>
/// <param name="Configured">Whether an address and a key were both supplied at all.</param>
/// <param name="Transport">The failure category when no response arrived, otherwise null.</param>
/// <param name="StatusCode">The HTTP status when one arrived, otherwise null.</param>
/// <param name="ContentType">
/// The <c>Content-Type</c> header AS RECEIVED, unparsed. Kept raw because the comparison rule is part
/// of the classification: the measured values include a form with no space after the semicolon, so
/// matching the header string would fail on a document that is JSON.
/// </param>
/// <param name="Document">The parsed status document, or null when the body was not one.</param>
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
/// Pure: no I/O, no HTTP client, no clock. The steps below are evaluated in a fixed order and the
/// order is load-bearing, because two of the four refusals are indistinguishable under the other
/// one's test:
/// <para>
/// A rejected key answers with an EMPTY content-type on both generations, so the status test has to
/// run BEFORE the content-type test or a rejected key reads as an answer from something that is not
/// the API.
/// </para>
/// <para>
/// A wrong address answers 200 as a web page, so no step may branch on a success status alone.
/// </para>
/// </remarks>
public static class ConnectionFailureClassifier
{
    /// <summary>The value <c>appName</c> carries on an instance of this product, on both generations.</summary>
    internal const string WhisparrAppName = "Whisparr";

    /// <summary>The kind that describes <paramref name="observation"/>.</summary>
    public static ConnectionFailureKind Classify(ConnectionObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);

        // Step 0.
        if (!observation.Configured)
        {
            return ConnectionFailureKind.NotConfigured;
        }

        // Step 1.
        if (observation.Transport is not null)
        {
            return ConnectionFailureKind.Unreachable;
        }

        // Step 2, BEFORE the content-type test. A 403 folds in here: an instance behind an auth proxy
        // is unobserved, and sending the user to the key field is the answer that is right when the
        // status came from Whisparr itself.
        if (observation.StatusCode is (int)HttpStatusCode.Unauthorized or (int)HttpStatusCode.Forbidden)
        {
            return ConnectionFailureKind.KeyRejected;
        }

        // Step 3.
        if (!IsJsonMediaType(observation.ContentType))
        {
            return ConnectionFailureKind.NotTheWhisparrApi;
        }

        // Step 4. A problem+json body reaches here, parses, and fails on the absent version, which is
        // the right answer for it.
        if (observation.Document is not { } document || string.IsNullOrWhiteSpace(document.Version))
        {
            return ConnectionFailureKind.NotTheWhisparrApi;
        }

        // Step 5. A negative test, so it cannot mis-refuse a real Whisparr, whose appName is measured.
        if (!string.IsNullOrEmpty(document.AppName)
            && !document.AppName.Equals(WhisparrAppName, StringComparison.OrdinalIgnoreCase))
        {
            return ConnectionFailureKind.VersionNotManaged;
        }

        // Steps 6 and 7. The detector owns the version-major reading, so the refusal and the success
        // are decided from one place rather than from two that could drift.
        return GenerationDetector.Detect(document).Generation is null
            ? ConnectionFailureKind.VersionNotManaged
            : ConnectionFailureKind.Connected;
    }

    /// <summary>Whether <paramref name="contentType"/> declares a JSON media type.</summary>
    /// <remarks>
    /// Parsed rather than string-matched, and compared on the media type alone: the measured values
    /// carry a charset parameter, with and without a space after the semicolon. A <c>+json</c> suffix
    /// counts, so a <c>problem+json</c> body is parsed and then refused for what it lacks.
    /// </remarks>
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
