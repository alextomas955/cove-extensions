using System.Text.Json.Serialization;

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
