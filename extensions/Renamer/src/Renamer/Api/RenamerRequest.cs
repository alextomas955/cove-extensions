namespace Renamer.Api;

/// <summary>
/// The request body shape for both the dry-run <c>/preview</c> and the enqueue <c>/renamer</c>
/// endpoints: the entity type (lowercase singular, e.g. <c>"video"</c>) and the selected entity
/// ids. Options are NOT carried here — they come from the stored <c>RenamerOptions</c>; a per-call
/// options override is not supported.
/// </summary>
/// <param name="EntityType">Cove entity-type string: <c>"video"</c>/<c>"image"</c>/<c>"audio"</c>.</param>
/// <param name="EntityIds">The selected entity ids to renamer/preview.</param>
public sealed record RenamerRequest(string EntityType, int[] EntityIds);
