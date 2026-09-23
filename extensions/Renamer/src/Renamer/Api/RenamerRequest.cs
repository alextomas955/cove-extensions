namespace Renamer.Api;

/// <summary>
/// The body for both the dry-run <c>/preview</c> and the enqueue <c>/renamer</c> endpoints.
/// </summary>
/// <remarks>
/// <c>EntityType</c> is the Cove entity-type string in either case, singular or plural, such as
/// <c>video</c> or <c>texts</c>. There is no per-call options override: options come from the stored
/// <c>RenamerOptions</c>.
/// </remarks>
public sealed record RenamerRequest(string EntityType, int[] EntityIds);
