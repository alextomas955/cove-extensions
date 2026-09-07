using WhisparrSync.Client;
using WhisparrSync.Contracts;

namespace WhisparrSync.Adapters;

/// <summary>
/// The read-only catalogue-enumeration role the through-Whisparr discovery provider reads through: the full
/// set of scene rows a monitored entity offers, attributed by the SAME predicate the status/search paths use
/// (v3 studio by title, v3 performer by performer-foreign-id; v2 site → its episodes, keyed on TPDB). It issues
/// no add/refresh/search command — enumeration is a pure read, so it can never mutate Whisparr.
/// </summary>
/// <remarks>
/// The role names the QUESTION, not its first consumer: a <c>Push/</c> caller reads through it as well, and a
/// <c>Push/</c> slice must not reach into a role named for the <c>Discovery/</c> slice.
/// </remarks>
internal interface IWhisparrEntityCatalogue
{
    /// <summary>
    /// Enumerates the scene rows attributed to <paramref name="kind"/>/<paramref name="remoteId"/> (the
    /// entity's server-resolved StashDB/TPDB id), projected into the SAME <see cref="WhisparrMovie"/> shape on
    /// both versions so the missing list presents uniformly as "Scenes". An entity Whisparr does not know
    /// returns an empty set (never an error); a non-Ok upstream read propagates its classified state. A kind the
    /// connected version has no entity for (a performer on v2) enumerates nothing, cleanly and with no wire call.
    /// </summary>
    Task<WhisparrResult<EntityCatalogue>> ListEntityMoviesAsync(
        string baseUrl, string apiKey, EntityKind kind, string remoteId, CancellationToken ct);
}

/// <summary>What a per-entity catalogue read learned, and the rows it found.</summary>
/// <remarks>
/// The state exists because an empty row set alone is ambiguous, and the two readings are not
/// interchangeable: acting on <see cref="EntityCatalogueState.EntityUnknown"/> as though it were
/// <see cref="EntityCatalogueState.Known"/> with no rows makes every scene Cove owns under the entity read as
/// missing, and a registering caller would then push the entity's whole catalogue into the user's instance.
/// </remarks>
internal sealed record EntityCatalogue(EntityCatalogueState State, WhisparrMovie[] Movies)
{
    internal static EntityCatalogue Known(WhisparrMovie[] movies) => new(EntityCatalogueState.Known, movies);

    internal static EntityCatalogue Unknown { get; } = new(EntityCatalogueState.EntityUnknown, []);

    internal static EntityCatalogue NotEnumerable { get; } =
        new(EntityCatalogueState.NotEnumerableOnThisVersion, []);
}

/// <summary>How much a per-entity catalogue read established about the entity itself.</summary>
internal enum EntityCatalogueState
{
    /// <summary>Whisparr knows the entity; <c>Movies</c> is its catalogue, possibly empty.</summary>
    Known,

    /// <summary>
    /// Whisparr does not know the entity.
    /// </summary>
    /// <remarks>
    /// This means exactly "Whisparr has never heard of it", never "it does not exist" — an entity that was
    /// never successfully registered answers identically to one that never could be
    /// (e2e/fixtures/wire/sibling-endpoints.json). A caller must not read it as an empty catalogue.
    /// </remarks>
    EntityUnknown,

    /// <summary>
    /// The connected generation has no entity of this kind to enumerate — a performer or a tag on v2, which
    /// carries neither. Nothing was asked of the instance.
    /// </summary>
    NotEnumerableOnThisVersion,
}
