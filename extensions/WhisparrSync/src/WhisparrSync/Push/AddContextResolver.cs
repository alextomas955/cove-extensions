using System.Text.Json;
using WhisparrSync.Client;
using WhisparrSync.Options;

namespace WhisparrSync.Push;

/// <summary>
/// The shared "before an add" resolver for the extension's origin-tagged, root-folder-routed adds
/// (attribution + routing): it ensures the single <see cref="OriginTagLabel"/> tag exists
/// (look-up-else-create, cached per instance) and maps the stored <see cref="WhisparrOptions.RootFolderId"/>
/// to its live path. Both the entity monitor and the scene service consume this ONE
/// implementation, so the cove-sync origin tag and the root-resolution rule are single-sourced. Constructed
/// per request with the transport client + the already-loaded options, so it
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

    // Root-folder resolution: read the live root-folder list and map the stored RootFolderId to its
    // path. A non-Ok list propagates; an id with no matching row is a classified Unreachable (the stored
    // config points at a root Whisparr no longer has). Uses the transport client directly — every adapter
    // delegates ListRootFolders to the same client verbatim, so this is version-agnostic.
    internal async Task<WhisparrResult<string>> ResolveRootFolderPathAsync(CancellationToken ct)
    {
        var listResult = await client.ListRootFoldersAsync(options.BaseUrl, options.ApiKey, ct);
        if (!listResult.IsOk)
        {
            return Propagate<RootFolder[], string>(listResult);
        }

        var match = Array.Find(listResult.Value!, f => f.Id == options.RootFolderId);
        return match?.Path is { Length: > 0 } path
            ? WhisparrResult<string>.Ok(path)
            : WhisparrResult<string>.Unreachable($"configured root folder {options.RootFolderId} not found");
    }

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

    // Re-shape a non-Ok result of one payload type into the same state for the resolver's return type.
    private static WhisparrResult<TTo> Propagate<TFrom, TTo>(WhisparrResult<TFrom> source)
        => WhisparrResult<TTo>.PropagateFrom(source);
}
