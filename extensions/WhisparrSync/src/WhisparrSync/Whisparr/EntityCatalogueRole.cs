using WhisparrSync.Contracts;

namespace WhisparrSync.Whisparr;

/// <summary>One performer an instance names on a catalogue scene.</summary>
public sealed record WhisparrCataloguePerformer(string ForeignId, string Name, string? ImageUrl);

/// <summary>One scene of an entity's catalogue, as the connected instance holds it.</summary>
/// <remarks>
/// Every scene here is one the instance already has an entry for, so both flags are facts it stated
/// rather than an absence read as false.
/// <para>
/// InstanceSceneId is the id the instance addresses its own row by, which is not the number the
/// metadata source issued. Zero where the answer carried none: one generation lists a catalogue the
/// instance holds no row for, and a scene there can only be added rather than marked.
/// </para>
/// </remarks>
public sealed record WhisparrCatalogueScene(
    string ProviderSceneId,
    string Title,
    string? ReleaseDate,
    string? CoverUrl,
    string? StudioName,
    string? Description,
    IReadOnlyList<WhisparrCataloguePerformer> Performers,
    IReadOnlyList<string> Tags,
    bool Monitored,
    bool HasFile,
    int InstanceSceneId = 0);

/// <summary>Why an entity's catalogue could not be read from the instance.</summary>
public enum WhisparrCatalogueRefusal
{
    /// <summary>Nothing was refused.</summary>
    None,

    /// <summary>The instance was asked and no whole answer arrived.</summary>
    NotReached,

    /// <summary>
    /// The instance holds no entry for the entity, so it lists no scenes under it and has nothing to
    /// be missing from.
    /// </summary>
    /// <remarks>
    /// Not an empty catalogue: an instance that holds the entity and lists nothing under it answers
    /// an empty list, which is a measurement. This is the absence of the entity itself.
    /// </remarks>
    EntityNotHeld,
}

/// <summary>What the instance answered when one entity's catalogue was asked for.</summary>
public sealed record WhisparrEntityCatalogue(
    IReadOnlyList<WhisparrCatalogueScene>? Scenes, WhisparrCatalogueRefusal Refusal)
{
    /// <summary>The scenes the instance lists, which may be none.</summary>
    public static WhisparrEntityCatalogue Listing(IReadOnlyList<WhisparrCatalogueScene> scenes)
        => new(scenes, WhisparrCatalogueRefusal.None);

    /// <summary>Nothing was established about the entity's catalogue.</summary>
    public static WhisparrEntityCatalogue Refused(WhisparrCatalogueRefusal refusal)
        => new(null, refusal);
}

/// <summary>Reads the scenes the instance lists for one entity.</summary>
/// <remarks>
/// This is the catalogue the missing surface is composed from, so the metadata source is asked for
/// no scene list at all: what the instance holds is what a reader is offered to act on.
/// <para>
/// One request per entity, never one per scene. The answer grows with the entity's catalogue rather
/// than with the library, and the caller pages it.
/// </para>
/// </remarks>
public interface IWhisparrEntityCatalogueReading
{
    /// <summary>The scenes the instance lists under <paramref name="foreignId"/>.</summary>
    Task<WhisparrEntityCatalogue> ReadEntityCatalogueAsync(
        Uri baseAddress,
        string apiKey,
        WhisparrGeneration generation,
        WhisparrEntityKind kind,
        string foreignId,
        CancellationToken ct);
}
