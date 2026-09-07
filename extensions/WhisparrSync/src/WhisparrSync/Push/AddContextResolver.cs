using System.Text.Json;
using WhisparrSync.Client;
using WhisparrSync.Options;
using WhisparrSync.Safety;
using WhisparrSync.State;

namespace WhisparrSync.Push;

/// <summary>
/// The shared "before an add" resolver for the extension's origin-tagged, root-folder-routed adds
/// (attribution + routing): it ensures the single <see cref="OriginTagLabel"/> tag exists
/// (look-up-else-create, cached per instance) and derives the add's root folder and quality profile PER-ADD from
/// the connected instance — there is no stored root id and no stored profile id. Both the entity monitor and the
/// scene service consume this ONE implementation, keeping the cove-sync origin tag and the two derivation rules
/// single-sourced. Constructed per request with the transport client + the already-loaded options, so it
/// unit-tests against a fake HTTP handler with no host.
/// </summary>
internal sealed class AddContextResolver(WhisparrClient client, WhisparrOptions options)
{
    /// <summary>
    /// The Whisparr tag label applied to every Cove-initiated add so AVAIL dedup and audit
    /// can distinguish a Cove-monitored/added entity from the user's own. The SINGLE source of truth for the
    /// literal — do NOT inline it elsewhere.
    /// </summary>
    internal const string OriginTagLabel = "cove-sync";

    // The origin tag id, ensured (looked up by label, else created) at most once per instance. The consumers
    // are constructed per request (they take the freshly-loaded options), so this caches within a single
    // operation; it is deliberately NOT a static process cache — that would leak across differently-configured
    // instances (different Whisparr hosts) and make the tag-ensure HTTP calls non-deterministic under test.
    private int? _originTagId;

    // The instance's first offered profile id, read at most once per instance for the same reason the origin tag
    // is: the consumers are per-request, so this caches within a single operation and never across differently
    // configured instances. A studio-derived profile is NOT cached here — it belongs to one studio, not to the
    // instance.
    private int? _firstProfileId;

    // The one root-folder read path, built here rather than threaded through this resolver's two consumers (each
    // constructed per operation at about ten call sites). The ZERO cache lifetime is deliberate: the resolver is
    // per-operation, so a cached set would collapse the reads a batch add issues today into one, and would queue
    // this path behind the process-wide refill gate the webhook guard's long-lived port holds.
    private readonly WhisparrRootsPort _roots = new(
        _ => Task.FromResult((options, options.BaseUrl, options.ApiKey)), TimeSpan.Zero);

    // Root derivation for an OWNED file: translate the Cove path into Whisparr's view (PathTranslation), then
    // pick the root whose path CONTAINS the translated path at a segment boundary — reusing RootOverlapDetector's
    // containment rule over EventLedger.NormalizePath (case-sensitive, the Linux/Docker target), never a raw
    // StartsWith, so "/data/media" never matches a file under "/data/media-extra". The read comes from the shared
    // port; no matching root is a classified Unreachable — an owned file is never routed to a wrong root.
    internal async Task<WhisparrResult<string>> ResolveRootForFileAsync(string ownedFilePath, CancellationToken ct)
    {
        var read = await _roots.ReadAsync(client, ct);
        if (read.Reason is not null)
        {
            return PropagateRootRead(read);
        }

        var translated = EventLedger.NormalizePath(
            PathTranslationService.ToWhisparrView(ownedFilePath, options.PathTranslation));
        var match = read.Roots.FirstOrDefault(
            r => RootOverlapDetector.Contains(EventLedger.NormalizePath(r.Path), translated));
        return match is not null
            ? WhisparrResult<string>.Ok(match.Path)
            : WhisparrResult<string>.Unreachable("no Whisparr root contains the owned file path");
    }

    // Root derivation for a FILE-LESS add (monitor-add, add-all-missing): these paths have no owned file to
    // prefix-match (the scene isn't owned yet), so fall back to the single root, else the first
    // Accessible root, else a classified Unreachable. The read comes from the shared port, which already drops
    // blank-path rows; first-match-wins over Whisparr's own order, never a sort.
    internal async Task<WhisparrResult<string>> ResolveFallbackRootAsync(CancellationToken ct)
    {
        var read = await _roots.ReadAsync(client, ct);
        if (read.Reason is not null)
        {
            return PropagateRootRead(read);
        }

        var roots = read.Roots;
        if (roots.Count == 1)
        {
            return WhisparrResult<string>.Ok(roots[0].Path);
        }

        var accessible = roots.FirstOrDefault(r => r.Accessible);
        return accessible is not null
            ? WhisparrResult<string>.Ok(accessible.Path)
            : WhisparrResult<string>.Unreachable("no root available");
    }

    // A read the port could not complete. A transport failure keeps the classification the caller sees today; the
    // two pre-read refusals the port added (no stored host, a persisted version this build cannot manage) carry
    // no transport state, so they surface as a classified Unreachable naming which one it was. Neither reaches
    // production through the monitor or scene paths: both refuse a null adapter before resolving a root.
    private static WhisparrResult<string> PropagateRootRead(WhisparrRootsResult read)
        => read.FailureState is { } state
            ? WhisparrResult<string>.FromFailure(state)
            : WhisparrResult<string>.Unreachable(read.Reason!);

    // Origin-tag ensure: look the tag up by label, else create it; cache the id for this instance. A
    // failure to both find AND create the tag propagates (an add must carry the origin tag — never add untagged).
    internal async Task<WhisparrResult<int>> EnsureOriginTagAsync(CancellationToken ct)
    {
        if (_originTagId is { } cached)
        {
            return WhisparrResult<int>.Ok(cached);
        }

        var listResult = await client.ListTagsAsync(options.BaseUrl, options.ApiKey, ct);
        if (!listResult.IsOk)
        {
            return Propagate<WhisparrTag[], int>(listResult);
        }

        var existing = Array.Find(
            listResult.Value!,
            t => string.Equals(t.Label, OriginTagLabel, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            _originTagId = existing.Id;
            return WhisparrResult<int>.Ok(existing.Id);
        }

        var createResult = await client.CreateTagAsync(
            options.BaseUrl, options.ApiKey,
            JsonSerializer.Serialize(new { label = OriginTagLabel }), ct);
        if (!createResult.IsOk)
        {
            return Propagate<WhisparrTag, int>(createResult);
        }

        _originTagId = createResult.Value!.Id;
        return WhisparrResult<int>.Ok(createResult.Value.Id);
    }

    /// <summary>
    /// Ensures the origin tag PLUS the caller's <paramref name="extraLabels"/> (the OPT add-defaults "tags on
    /// add"), returning the combined distinct tag id set (the origin id always first). Reads the tag list ONCE
    /// and finds-or-creates each label, so an add carrying extra tags costs one list (plus a create per genuinely
    /// missing label) rather than a round-trip per tag. With no extra labels this is exactly one list + the
    /// origin resolve — identical wire to <see cref="EnsureOriginTagAsync"/>, so the add path is unchanged by
    /// default. Blank labels and any label equal to the origin are ignored (the origin is never double-applied).
    /// </summary>
    internal async Task<WhisparrResult<IReadOnlyList<int>>> EnsureTagIdsAsync(
        IReadOnlyList<string> extraLabels, CancellationToken ct)
    {
        var listResult = await client.ListTagsAsync(options.BaseUrl, options.ApiKey, ct);
        if (!listResult.IsOk)
        {
            return Propagate<WhisparrTag[], IReadOnlyList<int>>(listResult);
        }

        var tags = listResult.Value!;
        var originResult = await ResolveLabelAsync(tags, OriginTagLabel, ct);
        if (!originResult.IsOk)
        {
            return Propagate<int, IReadOnlyList<int>>(originResult);
        }

        _originTagId = originResult.Value;
        var ids = new List<int> { originResult.Value };

        foreach (var label in extraLabels)
        {
            if (string.IsNullOrWhiteSpace(label)
                || string.Equals(label, OriginTagLabel, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var extraResult = await ResolveLabelAsync(tags, label, ct);
            if (!extraResult.IsOk)
            {
                return Propagate<int, IReadOnlyList<int>>(extraResult);
            }

            if (!ids.Contains(extraResult.Value))
            {
                ids.Add(extraResult.Value);
            }
        }

        return WhisparrResult<IReadOnlyList<int>>.Ok(ids);
    }

    // Find a tag id by label in the already-fetched list, else create it. Shared by the extra-tags resolve so
    // the find-or-create rule is single-sourced with the origin-tag ensure.
    private async Task<WhisparrResult<int>> ResolveLabelAsync(
        WhisparrTag[] tags, string label, CancellationToken ct)
    {
        var existing = Array.Find(tags, t => string.Equals(t.Label, label, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            return WhisparrResult<int>.Ok(existing.Id);
        }

        var createResult = await client.CreateTagAsync(
            options.BaseUrl, options.ApiKey,
            JsonSerializer.Serialize(new { label }), ct);
        return createResult.IsOk
            ? WhisparrResult<int>.Ok(createResult.Value!.Id)
            : Propagate<WhisparrTag, int>(createResult);
    }

    /// <summary>
    /// Resolves the quality profile an add carries, preferring the profile of
    /// <paramref name="parentStudioForeignId"/>'s Whisparr studio row when the caller knows the scene's parent
    /// studio and that row exists.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both generations' create validators declare <c>qualityProfileId</c> with <c>ValidId</c> plus an exists
    /// check and no conditional guard: an add may neither omit it nor carry an id this instance does not offer.
    /// That is the same constraint the root folder is derived per add for.
    /// </para>
    /// <para>
    /// Whisparr's own studio sync stamps the studio's profile onto every scene it syncs for that studio, which
    /// is what the studio editor labels that field for. The parent is a refinement rather than a precondition;
    /// an absent or unanswered lookup falls through to the instance's first offered profile.
    /// </para>
    /// <para>
    /// An instance offering NO profile classifies <see cref="WhisparrResultState.Unreachable"/>. The
    /// configuration gap is nameable; the validator rejection it would otherwise become is not.
    /// </para>
    /// </remarks>
    internal async Task<WhisparrResult<int>> ResolveQualityProfileAsync(
        string? parentStudioForeignId, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(parentStudioForeignId))
        {
            var studioResult = await client.GetStudioByStashIdAsync(
                options.BaseUrl, options.ApiKey, parentStudioForeignId, ct);
            if (studioResult.IsOk
                && Array.Find(studioResult.Value!, s => s.QualityProfileId > 0)?.QualityProfileId is { } studioProfile)
            {
                return WhisparrResult<int>.Ok(studioProfile);
            }
        }

        if (_firstProfileId is { } cached)
        {
            return WhisparrResult<int>.Ok(cached);
        }

        var listResult = await client.ListQualityProfilesAsync(options.BaseUrl, options.ApiKey, ct);
        if (!listResult.IsOk)
        {
            return Propagate<QualityProfile[], int>(listResult);
        }

        // Whisparr's own order, first match wins — the same rule the file-less root fallback uses, never a sort.
        if (listResult.Value is not [{ Id: var first }, ..])
        {
            return WhisparrResult<int>.Unreachable("the Whisparr instance offers no quality profile");
        }

        _firstProfileId = first;
        return WhisparrResult<int>.Ok(first);
    }

    // Re-shape a non-Ok result of one payload type into the same state for the resolver's return type.
    private static WhisparrResult<TTo> Propagate<TFrom, TTo>(WhisparrResult<TFrom> source)
        => WhisparrResult<TTo>.PropagateFrom(source);
}
