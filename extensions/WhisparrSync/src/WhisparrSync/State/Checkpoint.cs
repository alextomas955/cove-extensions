using System.Text.Json;
using Cove.Plugins;

namespace WhisparrSync.State;

/// <summary>
/// The reconcile high-water mark (IMPT-02). <see cref="LastRecordId"/> is the id of the newest Whisparr
/// history record the reconcile has processed — a re-run only ingests records ABOVE it, so it is
/// incremental and never retro-ingests the whole history. <see cref="SeededAtUtcTicks"/> records when the
/// mark was first seeded. <see cref="Seeded"/> distinguishes "no checkpoint yet" (first run: seed at the
/// newest existing record and ingest nothing) from "seeded at record 0" (an empty instance whose next
/// import must be caught).
/// </summary>
internal readonly record struct CheckpointState(int LastRecordId, long SeededAtUtcTicks, bool Seeded);

/// <summary>
/// A minimal single-blob store for the reconcile <see cref="CheckpointState"/> over
/// <see cref="IExtensionStore"/> — structurally an <c>OptionsStore</c>: load returns a not-yet-seeded
/// default when the key is absent (first run) or the blob is corrupt (defensive <see cref="JsonException"/>
/// → default, never throws). Takes <see cref="IExtensionStore"/> directly so it is unit-testable host-free.
/// </summary>
internal sealed class Checkpoint(IExtensionStore store)
{
    private const string Key = "reconcile-checkpoint";

    /// <summary>Loads the checkpoint; a not-yet-seeded default on an absent key (first run) or a corrupt blob.</summary>
    public async Task<CheckpointState> LoadAsync(CancellationToken ct = default)
    {
        var json = await store.GetAsync(Key, ct);
        if (string.IsNullOrWhiteSpace(json))
        {
            return default; // Seeded == false → first run
        }

        try
        {
            return JsonSerializer.Deserialize(json, IngestJsonContext.Default.CheckpointState);
        }
        catch (JsonException)
        {
            return default; // corrupt/hand-edited blob → not-yet-seeded default, never throws
        }
    }

    /// <summary>Persists the checkpoint as its single JSON blob.</summary>
    public Task SaveAsync(CheckpointState state, CancellationToken ct = default)
        => store.SetAsync(Key, JsonSerializer.Serialize(state, IngestJsonContext.Default.CheckpointState), ct);
}
