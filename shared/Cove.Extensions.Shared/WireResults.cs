using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;

namespace Cove.Extensions.Shared;

/// <summary>
/// A <c>200 OK</c> JSON result that serializes with the extension's own options AND describes its own
/// payload type to OpenAPI.
/// </summary>
/// <remarks>
/// <para>
/// <c>Results.Json</c> and <c>TypedResults.Json</c> are the only framework calls that can carry
/// per-endpoint <see cref="JsonSerializerOptions"/>, and the results they return describe no response
/// schema at all — the type does not implement <see cref="IEndpointMetadataProvider"/>, so an endpoint
/// returning one is documented with an empty response body. That is <c>dotnet/aspnetcore#47630</c>,
/// open since 2023 and closed without a fix. An extension cannot reach host startup to register a
/// converter globally, so it must serialize per endpoint and therefore cannot use the typed results
/// that would describe themselves.
/// </para>
/// <para>
/// Declaring this type as a handler's return type is what publishes the schema, so a wrong
/// <typeparamref name="T"/> is a compile error rather than a document that quietly describes the
/// wrong shape.
/// </para>
/// </remarks>
/// <typeparam name="T">The response body type, published as the <c>200</c> schema.</typeparam>
/// <param name="value">The body to write.</param>
public sealed class WireJson<T>(T value)
    : IResult,
        IEndpointMetadataProvider,
        IStatusCodeHttpResult,
        IValueHttpResult,
        IValueHttpResult<T>
{
    /// <summary>The body this result writes.</summary>
    public T? Value => value;

    object? IValueHttpResult.Value => value;

    /// <summary>Always <c>200</c>; a different status needs its own type (see the remarks).</summary>
    public int? StatusCode => StatusCodes.Status200OK;

    /// <inheritdoc />
    public Task ExecuteAsync(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        return httpContext.Response.WriteAsJsonAsync(value, WireJsonOptions.Instance);
    }

    // Static, so the status code cannot come from the instance — which is why each non-200 arm of a
    // Results<> union is its own type rather than a status passed to this one.
    static void IEndpointMetadataProvider.PopulateMetadata(MethodInfo method, EndpointBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Metadata.Add(
            new ProducesResponseTypeMetadata(StatusCodes.Status200OK, typeof(T), [WireJsonOptions.ContentType]));
    }
}

/// <summary>A <c>403 FORBIDDEN</c> result carrying an <see cref="ErrorCode"/> body and its own schema.</summary>
public sealed class ForbiddenCode
    : IResult,
        IEndpointMetadataProvider,
        IStatusCodeHttpResult,
        IValueHttpResult,
        IValueHttpResult<ErrorCode>
{
    private static readonly ErrorCode Body = new("FORBIDDEN");

    /// <summary>The error body this result writes.</summary>
    public ErrorCode? Value => Body;

    object? IValueHttpResult.Value => Body;

    /// <summary>Always <c>403</c>.</summary>
    public int? StatusCode => StatusCodes.Status403Forbidden;

    /// <inheritdoc />
    public Task ExecuteAsync(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        httpContext.Response.StatusCode = StatusCodes.Status403Forbidden;
        return httpContext.Response.WriteAsJsonAsync(Body, WireJsonOptions.Instance);
    }

    static void IEndpointMetadataProvider.PopulateMetadata(MethodInfo method, EndpointBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Metadata.Add(
            new ProducesResponseTypeMetadata(
                StatusCodes.Status403Forbidden,
                typeof(ErrorCode),
                [WireJsonOptions.ContentType]));
    }
}

/// <summary>A <c>400 BAD REQUEST</c> result carrying a caller-supplied <see cref="ErrorCode"/> body.</summary>
/// <param name="code">The stable machine-readable code the UI branches on.</param>
public sealed class BadRequestCode(string code)
    : IResult,
        IEndpointMetadataProvider,
        IStatusCodeHttpResult,
        IValueHttpResult,
        IValueHttpResult<ErrorCode>
{
    private readonly ErrorCode _body = new(code);

    /// <summary>The error body this result writes.</summary>
    public ErrorCode? Value => _body;

    object? IValueHttpResult.Value => _body;

    /// <summary>Always <c>400</c>.</summary>
    public int? StatusCode => StatusCodes.Status400BadRequest;

    /// <inheritdoc />
    public Task ExecuteAsync(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
        return httpContext.Response.WriteAsJsonAsync(_body, WireJsonOptions.Instance);
    }

    static void IEndpointMetadataProvider.PopulateMetadata(MethodInfo method, EndpointBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Metadata.Add(
            new ProducesResponseTypeMetadata(
                StatusCodes.Status400BadRequest,
                typeof(ErrorCode),
                [WireJsonOptions.ContentType]));
    }
}

/// <summary>The error body every non-2xx wire result carries: one stable machine-readable code.</summary>
/// <param name="Code">
/// A stable SCREAMING_SNAKE token the UI branches on (<c>FORBIDDEN</c>, <c>INVALID_BODY</c>, …).
/// Not localized and not for display; it is part of the wire contract, so changing one is a breaking
/// change even though nothing in the type system says so.
/// </param>
public sealed record ErrorCode(string Code);

internal static class WireJsonOptions
{
    internal const string ContentType = "application/json";

    // One frozen instance for every wire result. The same configuration the endpoints' own response
    // options carry, declared here because these types ship below the extensions that use them and
    // cannot reach up to an extension's contracts.
    internal static readonly JsonSerializerOptions Instance = CoveJsonOptions.WebWithEnumStrings();
}
