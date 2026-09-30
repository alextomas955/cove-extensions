using System.Text.Json;
using WhisparrSync.Contracts;
using V3Api = Whisparr3.Net.Api;

namespace WhisparrSync.Whisparr;

// What the instance holds, read back: the catalogue it tracks for an entity, the cards a page
// draws from it, and which of a batch it records a file for.
internal sealed partial class WhisparrV3Instance
{
    private static readonly JsonSerializerOptions HeldSceneRowShape = new(JsonSerializerDefaults.Web);

    // The members of an answered entry that are read. Declared with no others so a chunk's
    // answer costs one small object per hit rather than the whole resource the instance sent.
    // Whether the instance records a file rides this same answer, so asking costs no request.
    private sealed record HeldSceneRow(string? StashId, string? ForeignId, bool HasFile);

    // Adds the entity so the instance tracks its catalogue and wants none of it: the add carries the
    // flag governing whether an arrival is wanted, set so that nothing is.
    public Task<WhisparrResponse> TrackEntityAsync(
        WhisparrEntityKind kind, string foreignId, AddDefaults defaults, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(foreignId);
        ArgumentNullException.ThrowIfNull(defaults);

        var path = kind == WhisparrEntityKind.Studio ? StudioPath : PerformerPath;
        return transport.SendAsync(
            binding.BaseAddress,
            binding.ApiKey,
            HttpMethod.Post,
            path,
            V3BodyProjector.TrackEntity(foreignId, defaults),
            ct);
    }

    public Task<WhisparrResponse> AddSceneAsync(
        string foreignId, AddDefaults defaults, CancellationToken ct)
        => GeneratedActAsync(
            api => api.Api<V3Api.IMovieApi>().PostMovieAsync(
                V3BodyProjector.AddScene(foreignId, defaults), ct));

    public Task<WhisparrResponse> RefreshCatalogueAsync(
        WhisparrEntityKind kind, int entityId, CancellationToken ct)
        => GeneratedCommandAsync(V3BodyProjector.RefreshCatalogue(kind, entityId), ct);

    // The catalogue an entity's own scenes are read from, so the missing surface asks the metadata
    // source for no scene list. One request per entity. A works route answers only for an entity
    // the instance holds, so a not-found there is absence, not an empty catalogue.
    public async Task<WhisparrEntityCatalogue> ReadEntityCatalogueAsync(
        WhisparrEntityKind kind, string foreignId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(foreignId);

        var listed = kind == WhisparrEntityKind.Studio
            ? await GeneratedReadAsync(
                    api => api.Api<V3Api.IStudioApi>()
                        .GetStudioByStudioForeignIdWorksAsync(Named(foreignId), ct))
                .ConfigureAwait(false)
            : await GeneratedReadAsync(
                    api => api.Api<V3Api.IPerformerApi>()
                        .GetPerformerByPerformerForeignIdWorksAsync(Named(foreignId), ct))
                .ConfigureAwait(false);

        if (listed.StatusCode == 404)
        {
            return WhisparrEntityCatalogue.Refused(WhisparrCatalogueRefusal.EntityNotHeld);
        }

        if (WhisparrTransport.Refused(listed))
        {
            return WhisparrEntityCatalogue.Refused(WhisparrCatalogueRefusal.NotReached);
        }

        return CatalogueSceneProjector.V3Works(listed.Body) is { } scenes
            ? WhisparrEntityCatalogue.Listing(scenes)
            : WhisparrEntityCatalogue.Refused(WhisparrCatalogueRefusal.NotReached);
    }

    // One request for a page of entity cards, whatever the page holds, replacing a read per card: a
    // page of forty studios cost forty requests against a third party. One list route per kind, and
    // neither answers for the other, so the two reads are issued apart.
    public async Task<WhisparrHeldCards> ReadHeldEntitiesAsync(
        WhisparrEntityKind kind, IReadOnlyList<string> foreignIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(foreignIds);

        if (foreignIds.Count == 0)
        {
            return WhisparrHeldCards.Empty;
        }

        List<string> wanted = [.. foreignIds];
        var answered = kind == WhisparrEntityKind.Studio
            ? await GeneratedReadAsync(
                    api => api.Api<V3Api.IStudioApi>().PostStudioListAsync(wanted, ct))
                .ConfigureAwait(false)
            : await GeneratedReadAsync(
                    api => api.Api<V3Api.IPerformerApi>().PostPerformerListAsync(wanted, ct))
                .ConfigureAwait(false);

        return new WhisparrHeldCards(HeldIn(answered, foreignIds), WhisparrTransport.NothingUnanswered);
    }

    // One request for a page of scene cards. Declared by the generation that addresses a scene
    // without its site; the other reads a scene only as a row under one.
    public async Task<WhisparrHeldCards> ReadHeldSceneCardsAsync(
        IReadOnlyList<string> foreignIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(foreignIds);

        if (foreignIds.Count == 0)
        {
            return WhisparrHeldCards.Empty;
        }

        List<string> wanted = [.. foreignIds];
        var answered = await GeneratedReadAsync(
                api => api.Api<V3Api.IMovieApi>().PostMovieListAsync(wanted, ct))
            .ConfigureAwait(false);

        return new WhisparrHeldCards(HeldIn(answered, foreignIds), WhisparrTransport.NothingUnanswered);
    }

    // Raised rather than reduced to an empty set, for the reason ReduceHeldScenesAsync gives.
    private static IReadOnlyDictionary<string, WhisparrHeldCard> HeldIn(
        WhisparrResponse answered, IReadOnlyCollection<string> asked)
    {
        if (WhisparrTransport.Refused(answered))
        {
            throw new HttpRequestException(
                "The instance did not answer what it holds for the cards asked about.");
        }

        return HeldCardProjector.ByForeignId(answered.Body, asked)
            ?? throw new HttpRequestException(
                "The instance's answer could not be read as the entries it holds.");
    }

    // One key, single-valued. Repeating it answers only the first value's row, comma-joining
    // answers nothing, and the two plural spellings v3 accepts are ignored and answer with the
    // whole catalogue.
    public Task<WhisparrResponse> ReadSceneByRemoteIdAsync(string remoteId, CancellationToken ct)
        => GeneratedReadAsync(
            api => api.Api<V3Api.IMovieApi>().GetMovieAsync(
                stashId: Named(remoteId), cancellationToken: ct));

    // Each answered row is reduced to one question, so what this holds is the caller's set and
    // never the instance's, and there is no row cap for the reason the exclusion reduce has none.
    // The body is a bare JSON array of identifier strings; an object naming the ids as a member is
    // answered 400, measured against whisparr:v3-3.3.8-release.1097.
    public async Task<ScenesHeld> ReduceHeldScenesAsync(
        IReadOnlyCollection<string> foreignIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(foreignIds);

        // Keyed without regard to case: an identifier is a hexadecimal uuid and each side stored its
        // own spelling. The caller's spelling is what is answered back.
        var asked = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in foreignIds)
        {
            if (!string.IsNullOrWhiteSpace(id))
            {
                asked[id] = id;
            }
        }

        if (asked.Count == 0)
        {
            return new ScenesHeld(
                new HashSet<string>(StringComparer.Ordinal),
                new HashSet<string>(StringComparer.Ordinal));
        }

        List<string> wanted = [.. asked.Values];
        var answered = await GeneratedReadAsync(
                api => api.Api<V3Api.IMovieApi>().PostMovieListAsync(wanted, ct))
            .ConfigureAwait(false);

        // Raised rather than reduced to an empty set: a caller comparing its library against an
        // empty set would report every scene it asked about as one the instance does not hold.
        if (answered.StatusCode is < 200 or > 299
            || answered.Refusal is not MonitorRefusalKind.None)
        {
            throw new HttpRequestException(
                "The instance did not answer which of the asked-about scenes it holds.");
        }

        List<HeldSceneRow?>? rows;
        try
        {
            rows = JsonSerializer.Deserialize<List<HeldSceneRow?>>(answered.Body, HeldSceneRowShape);
        }
        catch (JsonException failure)
        {
            throw new HttpRequestException(
                "The instance's answer could not be read as the entries it holds.", failure);
        }

        return ReducedTo(rows, asked);
    }

    // Each answered row reduced to the two questions the caller asked, and dropped. A row naming an
    // identifier nobody asked about is skipped, so both sets stay bounded by the caller's own batch.
    private static ScenesHeld ReducedTo(
        List<HeldSceneRow?>? rows, Dictionary<string, string> asked)
    {
        var held = new HashSet<string>(StringComparer.Ordinal);
        var recordingNoFile = new HashSet<string>(StringComparer.Ordinal);

        foreach (var row in rows ?? [])
        {
            if (row is null)
            {
                continue;
            }

            // The identifier is read off the row's stash id, falling back to its foreign id: both
            // carry the same uuid and which one an instance fills in varies.
            var named = row.StashId is { Length: > 0 } stashed ? stashed : row.ForeignId;
            if (named is not { Length: > 0 } spelled || !asked.TryGetValue(spelled, out var asAsked))
            {
                continue;
            }

            held.Add(asAsked);

            // A row carrying no member for it reads as one recording no file: the instance stating
            // nothing is the same fact to a reader as it stating a no, and the other reading would
            // report a file it never claimed.
            if (!row.HasFile)
            {
                recordingNoFile.Add(asAsked);
            }
        }

        return new ScenesHeld(held, recordingNoFile);
    }
}
