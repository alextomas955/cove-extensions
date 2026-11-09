using System.Reflection;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;

namespace Cove.Extensions.Shared;

/// <summary>
/// An RFC 9457 problem body with this repository's two extension members declared on it.
/// </summary>
/// <remarks>
/// Declared properties rather than entries in <see cref="ProblemDetails.Extensions"/>, which carries
/// <c>JsonExtensionData</c> and is therefore invisible to schema generation: members put there are
/// written on the wire and absent from the document, so no generated client can read them. RFC 9457
/// places no constraint on how an extension member is implemented, only on the names being distinct
/// from the standard ones.
/// </remarks>
public sealed class ExtensionProblemDetails : ProblemDetails
{
    /// <summary>
    /// A stable SCREAMING_SNAKE token a caller branches on (<c>FORBIDDEN</c>, <c>INVALID_BODY</c>, …).
    /// </summary>
    /// <remarks>
    /// Part of the wire contract: changing one is a breaking change even though nothing in the type
    /// system says so. It exists beside <see cref="ProblemDetails.Type"/> because that member is a
    /// URI, which is awkward to compare against.
    /// </remarks>
    [JsonPropertyName(Problems.CodeMember)]
    public required string Code { get; init; }

    /// <summary>The bound a cap rejection exceeded, so a caller can batch to fit rather than guess.</summary>
    [JsonPropertyName(Problems.MaxMember)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Max { get; init; }
}

/// <summary>Builds the problem bodies the results below write.</summary>
public static class Problems
{
    /// <summary>The media type RFC 9457 defines for a problem body.</summary>
    public const string MediaType = "application/problem+json";

    /// <summary>The extension member carrying the stable token a caller branches on.</summary>
    public const string CodeMember = "code";

    /// <summary>The extension member carrying the bound a cap rejection exceeded.</summary>
    public const string MaxMember = "max";

    // Stable and non-dereferenceable on purpose: a problem type is an identifier, and an http URL here
    // would promise a page this repository does not publish.
    private const string TypePrefix = "urn:cove:extension:error:";

    /// <summary>Builds the problem body for one code.</summary>
    /// <param name="status">The status the result writes; echoed into the body as RFC 9457 requires.</param>
    /// <param name="code">A stable SCREAMING_SNAKE token. Part of the wire contract.</param>
    /// <param name="title">A short human-readable summary, which never varies by occurrence.</param>
    /// <param name="detail">What happened on this occurrence, written to be read by a person.</param>
    /// <param name="max">Written only for a cap rejection.</param>
    public static ExtensionProblemDetails Create(
        int status, string code, string title, string detail, int? max = null) =>
        new()
        {
            Type = TypePrefix + code,
            Title = title,
            Status = status,
            Detail = detail,
            Code = code,
            Max = max,
        };
}

/// <summary>A result writing an RFC 9457 problem body and declaring its own status.</summary>
/// <remarks>
/// One type per status, because a result declares exactly one response schema and the document is
/// emitted from the handler's declared return type. The framework's own <c>TypedResults.Problem</c>
/// returns a single type carrying a <see cref="ProblemDetails"/>, so a handler cannot declare two
/// statuses through it, and the body it describes has neither extension member. <c>ProducesProblem</c>
/// does not help: it is generic over the builder, not over the body, so it also describes the base
/// type.
/// <para>
/// Not every refusal uses one. A route answering <c>404</c> says all it has to say in the status, so
/// it returns no body at all.
/// </para>
/// </remarks>
public abstract class ProblemResultBase
    : IResult,
        IStatusCodeHttpResult,
        IValueHttpResult,
        IValueHttpResult<ExtensionProblemDetails>
{
    private protected ProblemResultBase(ExtensionProblemDetails problem) => Problem = problem;

    /// <summary>The problem body this result writes.</summary>
    public ExtensionProblemDetails Problem { get; }

    /// <inheritdoc />
    public ExtensionProblemDetails? Value => Problem;

    object? IValueHttpResult.Value => Problem;

    /// <inheritdoc />
    public int? StatusCode => Problem.Status;

    /// <inheritdoc />
    public Task ExecuteAsync(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        httpContext.Response.StatusCode = Problem.Status ?? StatusCodes.Status500InternalServerError;
        return httpContext.Response.WriteAsJsonAsync(
            Problem, options: null, contentType: Problems.MediaType, httpContext.RequestAborted);
    }

    private protected static void Describe(EndpointBuilder builder, int status)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Metadata.Add(
            new ProducesResponseTypeMetadata(status, typeof(ExtensionProblemDetails), [Problems.MediaType]));
    }
}

/// <summary>A <c>400</c> carrying the code that names what the request got wrong.</summary>
public sealed class BadRequestProblem : ProblemResultBase, IEndpointMetadataProvider
{
    /// <summary>Builds the result for one code, with the sentence a reader is shown.</summary>
    public BadRequestProblem(string code, string detail, int? max = null)
        : base(Problems.Create(
            StatusCodes.Status400BadRequest, code, "Bad Request", detail, max))
    { }

    static void IEndpointMetadataProvider.PopulateMetadata(MethodInfo method, EndpointBuilder builder) =>
        Describe(builder, StatusCodes.Status400BadRequest);
}

/// <summary>A <c>403</c> for a caller the permission gate refused.</summary>
public sealed class ForbiddenProblem : ProblemResultBase, IEndpointMetadataProvider
{
    /// <summary>The one occurrence there is: the gate states no more than that it refused.</summary>
    public ForbiddenProblem()
        : base(
            Problems.Create(
                StatusCodes.Status403Forbidden,
                "FORBIDDEN",
                "Forbidden",
                "The signed-in user may not perform this action."))
    { }

    static void IEndpointMetadataProvider.PopulateMetadata(MethodInfo method, EndpointBuilder builder) =>
        Describe(builder, StatusCodes.Status403Forbidden);
}

/// <summary>A <c>409</c> for a request the current stored state refuses.</summary>
public sealed class ConflictProblem : ProblemResultBase, IEndpointMetadataProvider
{
    /// <summary>Builds the result for one code, with the sentence a reader is shown.</summary>
    public ConflictProblem(string code, string detail)
        : base(Problems.Create(StatusCodes.Status409Conflict, code, "Conflict", detail)) { }

    static void IEndpointMetadataProvider.PopulateMetadata(MethodInfo method, EndpointBuilder builder) =>
        Describe(builder, StatusCodes.Status409Conflict);
}
