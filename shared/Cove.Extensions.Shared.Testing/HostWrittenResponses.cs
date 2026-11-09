using System.Globalization;
using System.Text.Json.Serialization;
using Cove.Plugins;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Cove.Extensions.Shared.Testing;

/// <summary>
/// The body Cove's own extension-endpoint authorization middleware writes when it refuses a request
/// before the extension's handler runs.
/// </summary>
/// <remarks>
/// Plain <c>application/json</c>, and not the shape <c>ExtensionProblemDetails</c> describes: the
/// host wrote this and an extension cannot change it. <see cref="Code"/> is the one member both
/// shapes share, so it is what a caller branches on either way.
/// <para>
/// Only the two refusals a permission-gated route can receive: <c>UNAUTHORIZED</c> with a message,
/// and <c>FORBIDDEN</c> with the permissions the caller held none of. A route declaring the SDK's
/// route-bound entity access can also receive a body naming the entity; no route here declares one.
/// </para>
/// </remarks>
public sealed class CoveAuthorizationDenial
{
    /// <summary>A stable SCREAMING_SNAKE token: <c>UNAUTHORIZED</c> or <c>FORBIDDEN</c>.</summary>
    public required string Code { get; init; }

    /// <summary>Written with <c>UNAUTHORIZED</c>. Absent from a permission refusal.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Message { get; init; }

    /// <summary>The permissions the caller holds none of. Written with <c>FORBIDDEN</c>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Missing { get; init; }
}

/// <summary>
/// Adds the responses written in front of an extension's handler, which the handler's own return
/// type cannot express.
/// </summary>
/// <remarks>
/// Cove's authorization middleware refuses an uncredentialed caller with <c>401</c> and an
/// unpermitted one with <c>403</c>, both as <see cref="CoveAuthorizationDenial"/>. ASP.NET Core
/// refuses a wrong content type with <c>415</c> and an unbindable body with <c>400</c>, both with no
/// body. A document built from return types alone omits all four, and a generated client then parses
/// a denial against a schema it does not match.
/// <para>
/// A transformer rather than endpoint metadata, because the API-description layer keeps one response
/// per status code: the <c>403</c> here and the handler's own <c>ForbiddenProblem</c> are both
/// reachable and differ in media type, which only the document can hold.
/// </para>
/// </remarks>
internal static class HostWrittenResponses
{
    private const string JsonMediaType = "application/json";

    private const string DenialSchemaId = nameof(CoveAuthorizationDenial);

    /// <summary>
    /// Writes the denial schema into the document's components, where the operations reference it by
    /// name.
    /// </summary>
    /// <remarks>
    /// Unconditional: the host mounts an extension route with no Cove authorization convention as
    /// anonymous and warns at boot, so a document with no operation referencing this describes a
    /// surface that should not ship. Nothing promotes a type reached only from a transformer, so
    /// leaving it to <c>GetOrCreateSchemaAsync</c> inlines one copy per operation instead.
    /// </remarks>
    internal static async Task RegisterSchemasAsync(
        OpenApiDocument document,
        OpenApiDocumentTransformerContext context,
        CancellationToken cancellationToken)
    {
        var denial = await context
            .GetOrCreateSchemaAsync(typeof(CoveAuthorizationDenial), null, cancellationToken)
            .ConfigureAwait(false);

        document.Components ??= new OpenApiComponents();
        document.Components.Schemas ??= new Dictionary<string, IOpenApiSchema>(StringComparer.Ordinal);
        document.Components.Schemas[DenialSchemaId] = denial;
    }

    internal static void Apply(OpenApiOperation operation, OpenApiOperationTransformerContext context)
    {
        var metadata = context.Description.ActionDescriptor.EndpointMetadata;
        var responses = operation.Responses ??= [];

        if (metadata.OfType<CovePermissionRequirementMetadata>().Any())
        {
            // By name: the component is written by the document transformer, which runs after every
            // operation. A reference resolves on serialization, not here.
            var denial = new OpenApiSchemaReference(DenialSchemaId);

            AddJsonBody(
                responses,
                StatusCodes.Status401Unauthorized,
                denial,
                "The Cove host has authentication on and the caller presented no credential. Written by "
                    + "the host; the extension is never reached. A host with authentication off, which is "
                    + "its default, never answers this.");

            AddJsonBody(
                responses,
                StatusCodes.Status403Forbidden,
                denial,
                "The caller holds none of the permissions the route declares, so the Cove host refused it "
                    + "as application/json, or the extension's own handler refused the narrower permission "
                    + "the request names, as application/problem+json. Both are reachable, and the media "
                    + "type is what tells them apart.");
        }

        if (operation.RequestBody is null)
        {
            return;
        }

        Describe(
            responses,
            StatusCodes.Status415UnsupportedMediaType,
            "The request declared a content type the route does not accept. Written with no body.");

        if (BindsBodyToAParameter(context.Description))
        {
            Describe(
                responses,
                StatusCodes.Status400BadRequest,
                "A request body the framework could not bind is refused here with no body at all, before "
                    + "the handler runs. Where the handler itself refuses the request, the body is the "
                    + "problem document below.");
        }
    }

    // Whether minimal-API binding reads the body into a handler parameter, rather than the handler
    // reading the raw request itself. Both declare a body - the second through .Accepts<> - and only
    // the first carries a parameter descriptor, because only the first has a parameter to describe.
    private static bool BindsBodyToAParameter(ApiDescription description)
        => description.ParameterDescriptions.Any(
            parameter => parameter.Source == BindingSource.Body && parameter.ParameterDescriptor is not null);

    // The schema is added beside whatever media type the response already declares, never over it: a
    // status reached both in front of the handler and by the handler carries one media type for each.
    private static void AddJsonBody(
        OpenApiResponses responses, int status, IOpenApiSchema schema, string description)
    {
        var response = Describe(responses, status, description);
        response.Content ??= new Dictionary<string, OpenApiMediaType>(StringComparer.Ordinal);
        response.Content[JsonMediaType] = new OpenApiMediaType { Schema = schema };
    }

    private static OpenApiResponse Describe(OpenApiResponses responses, int status, string description)
    {
        var key = status.ToString(CultureInfo.InvariantCulture);
        if (responses.TryGetValue(key, out var existing) && existing is OpenApiResponse response)
        {
            response.Description = description;
            return response;
        }

        var created = new OpenApiResponse { Description = description };
        responses[key] = created;
        return created;
    }
}
