using System.Collections.Concurrent;
using System.Text.Json;
using Cove.Plugins;

namespace WhisparrSync.Matching;

/// <summary>
/// Persists the Cove↔Whisparr match map (MATCH-04) as a SINGLE JSON-array blob under one
/// <see cref="IExtensionStore"/> key, keyed on the StashDB UUID. Mirrors <c>RevertLog</c>'s single-writer
/// gate and <c>OptionsStore</c>'s defensive parse: a corrupt/hand-edited blob loads as an empty map and
/// never throws, and every write is a gated read-modify-write so concurrent confirm/reject cannot tear
/// the blob. Takes <see cref="IExtensionStore"/> directly so it is unit-testable host-free against a fake.
/// </summary>
/// <remarks>
/// <c>IExtensionStore</c> is a plain KV with no prefix scan (<c>GetAsync/SetAsync/DeleteAsync/GetAllAsync</c>),
/// so the whole map lives in ONE blob rather than per-entry keys (RESEARCH Pitfall 5 — acceptable at the
/// v1 library size; shard by key-prefix later if it outgrows a blob). A plain reconcile writes NOTHING
/// here (zero mutation); only confirm/reject persist, so a re-run reuses confirmed links and suppresses
/// rejected suggestions, and <c>GET /reconciliation</c> reflects prior decisions across a reload without
/// re-running preview-sync.
/// </remarks>
internal sealed class MatchStateStore(IExtensionStore store)
{
    private const string Key = "matchstate";

    // SINGLE-WRITER SERIALIZATION keyed on the store Key, process-lifetime (never disposed), exactly like
    // RevertLog: confirm/reject is a read-modify-write on one shared blob and must serialize process-wide.
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> StoreGates = new();
    private readonly SemaphoreSlim _gate = StoreGates.GetOrAdd(Key, _ => new SemaphoreSlim(1, 1));

    /// <summary>Loads the whole match map; empty on an absent key (first run) or a corrupt blob — never throws.</summary>
    public async Task<IReadOnlyList<MatchState>> LoadAllAsync(CancellationToken ct = default)
        => Parse(await store.GetAsync(Key, ct));

    /// <summary>Replaces the whole map with <paramref name="states"/> (gated write).</summary>
    public async Task SetAllAsync(IReadOnlyList<MatchState> states, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await PersistAsync(states, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Upserts <paramref name="state"/> keyed on its StashDB UUID with <see cref="MatchStatus.Confirmed"/> — a re-run then reuses the link.</summary>
    public Task ConfirmAsync(MatchState state, CancellationToken ct = default)
        => throw new NotImplementedException();

    /// <summary>Upserts <paramref name="state"/> keyed on its StashDB UUID with <see cref="MatchStatus.Rejected"/> — a re-run then suppresses the suggestion.</summary>
    public Task RejectAsync(MatchState state, CancellationToken ct = default)
        => throw new NotImplementedException();

    private static MatchState[] Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize(json, MatchJsonContext.Default.MatchStateArray) ?? [];
        }
        catch (JsonException)
        {
            return []; // corrupt/hand-edited blob → empty map, never throws (MATCH-04 resilience)
        }
    }

    private Task PersistAsync(IReadOnlyList<MatchState> states, CancellationToken ct)
    {
        var array = states as MatchState[] ?? [.. states];
        var json = JsonSerializer.Serialize(array, MatchJsonContext.Default.MatchStateArray);
        return store.SetAsync(Key, json, ct);
    }
}
