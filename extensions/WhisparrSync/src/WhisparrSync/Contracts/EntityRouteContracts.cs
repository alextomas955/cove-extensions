namespace WhisparrSync.Contracts;

/// <summary>The Cove entity a route segment names.</summary>
/// <remarks>
/// Both members bind by name from the route. <c>Kind</c> is the caller's spelling and is parsed by
/// the handler, so a segment naming no kind this product expresses is answered rather than thrown
/// on.
/// </remarks>
internal sealed record EntityRoute(string Kind, int CoveId);

/// <summary>The one catalogue scene a route names, under the entity whose tab listed it.</summary>
/// <remarks>Every member binds by name from the route. The identifier is the provider's, not the
/// instance's.</remarks>
internal sealed record MissingSceneRoute(string Kind, int CoveId, string ProviderSceneId);

/// <summary>The entity a verb acts on, once the route's spelling has been parsed.</summary>
internal sealed record MonitoredEntity(WhisparrEntityKind Kind, int CoveId);
