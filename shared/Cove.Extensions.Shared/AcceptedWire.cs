using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace Cove.Extensions.Shared;

/// <summary>
/// A <c>202 ACCEPTED</c> JSON result that writes its body with the extension's own serializer options.
/// </summary>
/// <remarks>
/// <para>
/// <c>TypedResults.Accepted</c> and <c>Results.Accepted</c> serialize through the HOST's
/// <c>JsonOptions</c>, which an extension neither owns nor can see. Every other response body here is
/// written with options this repository controls, so the enqueue routes were the one shape a host-side
/// naming change could re-case underneath a shipped UI.
/// </para>
/// <para>
/// That failure is silent, which is why it is worth removing rather than testing for: the panel reads
/// <c>res.jobId</c>, a re-cased <c>JobId</c> leaves that <c>undefined</c>, and the UI then polls a job
/// id of "undefined" indefinitely without raising an error anywhere.
/// </para>
/// </remarks>
/// <typeparam name="T">The response body type.</typeparam>
/// <param name="value">The body to write.</param>
public sealed class AcceptedWire<T>(T value)
    : IResult,
        IStatusCodeHttpResult,
        IValueHttpResult,
        IValueHttpResult<T>
{
    private static readonly JsonSerializerOptions Options = CoveJsonOptions.WebWithEnumStrings();

    /// <summary>The body this result writes.</summary>
    public T? Value => value;

    // The non-generic interface is implemented too: it is what a caller inspecting a result without
    // knowing T reads, and IValueHttpResult<T> does not supply it.
    object? IValueHttpResult.Value => value;

    /// <summary>Always <c>202</c>.</summary>
    public int? StatusCode => StatusCodes.Status202Accepted;

    /// <inheritdoc />
    public Task ExecuteAsync(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        httpContext.Response.StatusCode = StatusCodes.Status202Accepted;
        return httpContext.Response.WriteAsJsonAsync(value, Options, httpContext.RequestAborted);
    }
}
