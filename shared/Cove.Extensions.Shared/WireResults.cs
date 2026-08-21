using System.Reflection;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;

namespace Cove.Extensions.Shared;

/// <summary>A <c>403 FORBIDDEN</c> result carrying an <see cref="ErrorCode"/> body and its own schema.</summary>
/// <remarks>
/// The framework's typed results cover every other arm these endpoints return, but none of them is a 403
/// WITH a body: <c>ForbidHttpResult</c> writes none, and the results that do carry one describe no
/// response schema (<c>dotnet/aspnetcore#47630</c>). Declaring this type as a handler's return type is
/// what publishes the 403 shape.
/// </remarks>
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
        return httpContext.Response.WriteAsJsonAsync(Body, httpContext.RequestAborted);
    }

    static void IEndpointMetadataProvider.PopulateMetadata(MethodInfo method, EndpointBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Metadata.Add(
            new ProducesResponseTypeMetadata(
                StatusCodes.Status403Forbidden,
                typeof(ErrorCode),
                ["application/json"]));
    }
}

/// <summary>The error body every non-2xx wire result carries: one stable machine-readable code.</summary>
/// <param name="Code">
/// A stable SCREAMING_SNAKE token the UI branches on (<c>FORBIDDEN</c>, <c>INVALID_BODY</c>, …). Not
/// localized and not for display; it is part of the wire contract, so changing one is a breaking change
/// even though nothing in the type system says so.
/// </param>
/// <param name="Max">
/// The bound a cap rejection exceeded, so the caller can batch to fit rather than guess. Written only
/// when a code carries one: a status code admits exactly one response schema, so a second error record
/// for the cap arm could not be described alongside this one.
/// </param>
public sealed record ErrorCode(
    string Code,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Max = null);
