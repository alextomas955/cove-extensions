using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;

namespace Cove.Extensions.Shared;

/// <summary>
/// The RFC 9457 problem body every non-2xx wire result carries, and the per-status results that write
/// it.
/// </summary>
/// <remarks>
/// A status is one type, because a result declares exactly one response schema and the document is
/// emitted from the handler's declared return type. The framework's own
/// <c>TypedResults.Problem</c> writes the right body but declares <c>500</c> whatever status it was
/// given, so it cannot describe these arms.
/// </remarks>
public static class Problems
{
    /// <summary>The media type RFC 9457 defines for a problem body.</summary>
    public const string MediaType = "application/problem+json";

    /// <summary>
    /// The extension member carrying the stable token a caller branches on.
    /// </summary>
    /// <remarks>
    /// A problem's <c>type</c> is a URI, which is awkward to compare against. RFC 9457 allows
    /// extension members for exactly this, so the short token stays available and the envelope stays
    /// the standard one the host's own errors use.
    /// </remarks>
    public const string CodeMember = "code";

    /// <summary>The bound a cap rejection exceeded, so a caller can batch to fit rather than guess.</summary>
    public const string MaxMember = "max";

    // Stable and non-dereferenceable on purpose: a problem type is an identifier, and an http URL here
    // would promise a page this repository does not publish.
    private const string TypePrefix = "urn:cove:extension:error:";

    /// <summary>Builds the problem body for one code.</summary>
    /// <param name="status">The status the result writes; echoed into the body as RFC 9457 requires.</param>
    /// <param name="code">A stable SCREAMING_SNAKE token. Part of the wire contract.</param>
    /// <param name="title">A short human-readable summary, which never varies by occurrence.</param>
    /// <param name="max">Written only for a cap rejection.</param>
    public static ProblemDetails Create(int status, string code, string title, int? max = null)
    {
        var problem = new ProblemDetails
        {
            Type = TypePrefix + code,
            Title = title,
            Status = status,
        };
        problem.Extensions[CodeMember] = code;
        if (max is { } bound)
        {
            problem.Extensions[MaxMember] = bound;
        }

        return problem;
    }
}

/// <summary>A non-2xx result writing an RFC 9457 problem body and declaring its own status.</summary>
public abstract class ProblemResultBase
    : IResult,
        IStatusCodeHttpResult,
        IValueHttpResult,
        IValueHttpResult<ProblemDetails>
{
    private protected ProblemResultBase(ProblemDetails problem) => Problem = problem;

    /// <summary>The problem body this result writes.</summary>
    public ProblemDetails Problem { get; }

    /// <inheritdoc />
    public ProblemDetails? Value => Problem;

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
            new ProducesResponseTypeMetadata(status, typeof(ProblemDetails), [Problems.MediaType]));
    }
}

/// <summary>A <c>400</c> carrying the code that names what the request got wrong.</summary>
public sealed class BadRequestProblem : ProblemResultBase, IEndpointMetadataProvider
{
    /// <summary>Builds the result for one code.</summary>
    public BadRequestProblem(string code, string title, int? max = null)
        : base(Problems.Create(StatusCodes.Status400BadRequest, code, title, max)) { }

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
                "The signed-in user may not perform this action."))
    { }

    static void IEndpointMetadataProvider.PopulateMetadata(MethodInfo method, EndpointBuilder builder) =>
        Describe(builder, StatusCodes.Status403Forbidden);
}

/// <summary>A <c>409</c> for a request the current stored state refuses.</summary>
public sealed class ConflictProblem : ProblemResultBase, IEndpointMetadataProvider
{
    /// <summary>Builds the result for one code.</summary>
    public ConflictProblem(string code, string title)
        : base(Problems.Create(StatusCodes.Status409Conflict, code, title)) { }

    static void IEndpointMetadataProvider.PopulateMetadata(MethodInfo method, EndpointBuilder builder) =>
        Describe(builder, StatusCodes.Status409Conflict);
}
