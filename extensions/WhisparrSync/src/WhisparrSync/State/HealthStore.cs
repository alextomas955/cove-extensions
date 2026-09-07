using System.Text.Json;
using Cove.Extensions.Shared;
using Cove.Plugins;
using WhisparrSync.Client;
using WhisparrSync.Ingest;

namespace WhisparrSync.State;

/// <summary>
/// The closed set of dependency keys the health record may hold. Const strings rather than an enum: the values
/// ride the wire as-is, so there is no fourth JSON converter and no casing policy standing between the constant
/// and the response.
/// </summary>
internal static class HealthDependency
{
    /// <summary>The Whisparr instance itself — every outbound call made with the stored credentials.</summary>
    public const string Acquisition = "acquisition";

    /// <summary>The metadata provider behind discovery (StashDB on v3, ThePornDB on v2 — one row covers both).</summary>
    public const string Metadata = "metadata";

    /// <summary>The inbound import channel: the webhook and the poll backstop that share its outcome vocabulary.</summary>
    public const string Import = "import";

    /// <summary>The keys in projection order; also the whitelist a loaded blob is filtered against.</summary>
    public static readonly string[] All = [Acquisition, Metadata, Import];
}

/// <summary>What one observation proves about a dependency. Internal only — it never reaches the wire.</summary>
internal enum HealthVerdict
{
    Healthy,

    Failure,

    /// <summary>
    /// The outcome neither proves nor disproves the dependency — a duplicate-delivery skip, or a metadata read
    /// that never ran because no provider credential is configured. It records the outcome string and touches
    /// neither tick nor the failure count, so the surface never claims a provider is healthy when it was never
    /// called.
    /// </summary>
    NotChecked,
}

/// <summary>One classified outcome on its way into the record.</summary>
/// <remarks>The factories are the only construction path, so a call site cannot build a failure with no error text.</remarks>
internal readonly record struct HealthObservation(HealthVerdict Verdict, string Outcome, string Error)
{
    public static HealthObservation Healthy(string outcome) => new(HealthVerdict.Healthy, outcome, string.Empty);

    public static HealthObservation Failure(string outcome, string error) => new(HealthVerdict.Failure, outcome, error);

    public static HealthObservation NotChecked(string outcome) => new(HealthVerdict.NotChecked, outcome, string.Empty);
}

/// <summary>Maps an already-classified transport outcome onto a <see cref="HealthObservation"/>.</summary>
/// <remarks>
/// The single state→string map for the health record: the failure states delegate to
/// <c>WhisparrSync.FailureDiscriminator</c> rather than restating its switch, so the record and the UI's error
/// discriminator can never drift apart.
/// </remarks>
internal static class HealthOutcome
{
    internal static HealthObservation FromAcquisition(WhisparrResultState state, string? reason) => state switch
    {
        WhisparrResultState.Ok => HealthObservation.Healthy("ok"),

        // Absent (a not-added entity's 404) and Conflict (a create against an existing row) are documented DATA
        // outcomes: Whisparr answered. Counting either as degraded would make a healthy instance read broken.
        WhisparrResultState.Absent => HealthObservation.Healthy("absent"),
        WhisparrResultState.Conflict => HealthObservation.Healthy("conflict"),

        WhisparrResultState.VersionMismatch => HealthObservation.Failure(
            "versionMismatch", "Reached a Servarr instance whose version this build cannot manage."),

        _ => HealthObservation.Failure(WhisparrSync.FailureDiscriminator(state), AcquisitionError(state, reason)),
    };

    /// <summary>
    /// Classifies a discovery read of the connected generation's metadata provider — StashDB on v3, ThePornDB
    /// on v2. One row is honest on both, because one code path serves both.
    /// </summary>
    /// <remarks>
    /// A read that could not run because Cove holds no matching metadata server is NOT-CHECKED, not failed: the
    /// provider was never called, so calling it either healthy or broken would be a claim the code cannot make.
    /// </remarks>
    internal static HealthObservation FromMetadata(
        bool providerCredentialResolved, WhisparrResultState state, string? reason)
    {
        if (!providerCredentialResolved)
        {
            return HealthObservation.NotChecked("needsProviderKey");
        }

        return state == WhisparrResultState.Ok
            ? HealthObservation.Healthy("ok")
            : HealthObservation.Failure(WhisparrSync.FailureDiscriminator(state), MetadataError(state, reason));
    }

    /// <summary>Classifies one import-channel outcome from the vocabulary the shipped import log already uses.</summary>
    /// <remarks>
    /// Exactly one flag means the sync is broken — Whisparr reported a file at a path Cove cannot open. Every
    /// other flag, a path outside every known root above all, is NOT-CHECKED: the shipped import log
    /// deliberately does not trip its banner on those, and this record must preserve that distinction rather
    /// than quietly widening it. A duplicate delivery is neither a success nor a failure.
    /// </remarks>
    internal static HealthObservation FromImport(string result, string? reason)
    {
        if (string.Equals(result, "Imported", StringComparison.Ordinal))
        {
            return HealthObservation.Healthy("imported");
        }

        if (string.Equals(reason, IngestCoordinator.PathNotVisibleReason, StringComparison.Ordinal))
        {
            return HealthObservation.Failure(
                "pathNotVisible", "Whisparr reported an imported file at a path Cove cannot open.");
        }

        return HealthObservation.NotChecked(
            string.Equals(result, "Flagged", StringComparison.Ordinal) ? "flagged" : "skipped");
    }

    /// <summary>Classifies a webhook delivery rejected against a configured secret.</summary>
    /// <remarks>
    /// The error is a fixed synthesized sentence. It must never carry the presented token, the stored secret or
    /// the webhook URL, which embeds the secret — this record is projected onto the settings page.
    /// </remarks>
    internal static HealthObservation WebhookRejected()
        => HealthObservation.Failure(
            "unauthorized", "A webhook delivery presented a token that does not match the configured secret.");

    // BadKey and NotWhisparr carry no reason at all — the probe answers with a bare discriminator — so storing
    // result.Reason verbatim would show an EMPTY error for the single most common misconfiguration. A fixed,
    // non-secret sentence per state is synthesized instead; Unreachable and Rejected do carry provider text.
    // The sentences differ per dependency because they name different systems; the state→OUTCOME map does not,
    // which is why both classifiers route it through the one FailureDiscriminator.
    private static string AcquisitionError(WhisparrResultState state, string? reason) => state switch
    {
        WhisparrResultState.BadKey => "Whisparr rejected the stored API key.",
        WhisparrResultState.NotWhisparr => "The address answered, but not with the Whisparr API.",
        _ => string.IsNullOrWhiteSpace(reason) ? "Whisparr could not be reached." : reason,
    };

    private static string MetadataError(WhisparrResultState state, string? reason) => state switch
    {
        WhisparrResultState.BadKey => "The metadata provider rejected the credential stored in Cove.",
        WhisparrResultState.NotWhisparr => "The metadata endpoint answered, but not with the expected API.",
        _ => string.IsNullOrWhiteSpace(reason) ? "The metadata provider could not be reached." : reason,
    };
}

/// <summary>
/// The bounded, sticky status record for one dependency: its last classified outcome, when it was last healthy,
/// when it last failed, how many failures have run consecutively, and the last error text.
/// </summary>
/// <remarks>
/// A zero tick means never. <see cref="LastFailureTicks"/> and <see cref="LastError"/> are STICKY — a success
/// resets <see cref="ConsecutiveFailures"/> and advances <see cref="LastHealthyTicks"/> and changes neither of
/// them, so a dependency that failed and recovered still shows what went wrong. That retention is finite for the
/// text alone: once a RECOVERED entry's failure is older than <see cref="RecoveredErrorRetentionTicks"/>,
/// <see cref="LastError"/> reads empty, while both ticks and <see cref="Outcome"/> stay readable indefinitely.
/// </remarks>
internal readonly record struct DependencyHealth(
    string Dependency,
    string Outcome,
    long LastHealthyTicks,
    long LastFailureTicks,
    int ConsecutiveFailures,
    string LastError)
{
    // A live-captured unreachable reason is a host-and-port fragment of roughly fifty characters, while a
    // rejection carries Whisparr's own message field and is the only genuinely unbounded axis in the record.
    // 512 holds a full provider sentence and still pins the whole blob under two kilobytes.
    internal const int MaxErrorLength = 512;

    // The dependency key set is closed at three (acquisition, metadata, import), so the entry count is a
    // property of the vocabulary rather than a retention policy.
    internal const int MaxEntries = 3;

    // How long a RECOVERED dependency keeps the text explaining its last failure. An hour is where the readout
    // stops describing that failure in minutes and starts describing it in hours, so the surface cannot hold a
    // "recently recovered" heading over a line that reads otherwise; and the 15-minute reconcile backstop
    // re-confirms a recovery four times inside the window, so an entry surviving it is history, not news.
    // It governs the error TEXT alone: both ticks and the outcome string are kept indefinitely, and a dependency
    // whose consecutive count is above zero is failing NOW and is never aged.
    internal const long RecoveredErrorRetentionTicks = TimeSpan.TicksPerHour;

    /// <summary>The never-observed default: no outcome, no ticks, no failures, no error.</summary>
    public static DependencyHealth Empty(string dependency) => new(dependency, string.Empty, 0L, 0L, 0, string.Empty);
}

/// <summary>
/// The per-dependency health record: at most three <see cref="DependencyHealth"/> entries in one blob under its
/// own <see cref="IExtensionStore"/> key. Rides <see cref="SingleWriterBlobStore{T}"/> for the process-wide gate
/// so the five concurrent write points cannot tear the blob.
/// </summary>
/// <remarks>
/// Bounded by construction through <c>Normalize</c>, which both the load and the persist path call — the base
/// <c>Compact</c> hook stays the no-op it ships as. That same function carries the recovered-error horizon, so
/// there is one place an error can expire and no write can route around it. Takes <see cref="IExtensionStore"/>
/// directly rather than the SDK base class's member, so the type is testable with no host.
/// </remarks>
internal sealed class HealthStore(IExtensionStore store) : SingleWriterBlobStore<DependencyHealth>(store, Key)
{
    private const string Key = "health";

    /// <summary>Loads the bounded record; an absent, corrupt or hand-edited blob loads as no entries, never a throw.</summary>
    /// <remarks>
    /// The load expires an out-of-window recovered error on the way out but never writes the aged form back:
    /// <c>/import-log</c> is a read route and stays side-effect-free, so every response is already correct while
    /// the stored blob converges on the next write.
    /// </remarks>
    public Task<DependencyHealth[]> LoadAsync(CancellationToken ct = default)
        => LoadAsync(DateTime.UtcNow.Ticks, ct);

    /// <summary>Loads the bounded record against an explicit clock, so the horizon is drivable without ambient time.</summary>
    internal async Task<DependencyHealth[]> LoadAsync(long utcNow, CancellationToken ct = default)
        => Normalize(ParseArray(await LoadBlobAsync(ct), IngestJsonContext.Default.DependencyHealthArray), utcNow);

    /// <summary>
    /// Folds one classified observation into the record (gated read-modify-write). A healthy observation advances
    /// the healthy tick and resets the consecutive count; a failure advances the failure tick, increments the
    /// count and replaces the truncated error; a not-checked observation records the outcome string alone.
    /// </summary>
    /// <remarks>
    /// An already-cancelled token writes nothing. A shutdown cancellation propagates as a throw rather than as a
    /// classified result, so a tap that recorded here would file a normal shutdown as a dependency failure.
    /// </remarks>
    public Task RecordAsync(
        string dependency, HealthObservation observation, long utcTicks, CancellationToken ct = default)
    {
        if (ct.IsCancellationRequested)
        {
            return Task.CompletedTask;
        }

        return RunExclusiveAsync(async () =>
        {
            var entries = Normalize(
                ParseArray(await LoadBlobAsync(ct), IngestJsonContext.Default.DependencyHealthArray), utcTicks);
            var index = Array.FindIndex(entries, e => string.Equals(e.Dependency, dependency, StringComparison.Ordinal));
            var folded = Fold(index >= 0 ? entries[index] : DependencyHealth.Empty(dependency), observation, utcTicks);

            DependencyHealth[] next;
            if (index >= 0)
            {
                next = [.. entries];
                next[index] = folded;
            }
            else
            {
                next = [.. entries, folded];
            }

            await PersistAsync(next, utcTicks, ct);
        }, ct);
    }

    private static DependencyHealth Fold(DependencyHealth entry, HealthObservation observation, long utcTicks)
        => observation.Verdict switch
        {
            // LastError and LastFailureTicks are deliberately left alone. Clearing the failure window on success
            // is the shipped import-log behaviour (State/ImportLog.cs zeroes the count AND empties the ring) and
            // is exactly what must not happen here: a recovered dependency must still show what went wrong.
            HealthVerdict.Healthy => entry with
            {
                Outcome = observation.Outcome,
                LastHealthyTicks = Math.Max(entry.LastHealthyTicks, utcTicks),
                ConsecutiveFailures = 0,
            },
            HealthVerdict.Failure => entry with
            {
                Outcome = observation.Outcome,
                LastFailureTicks = Math.Max(entry.LastFailureTicks, utcTicks),
                ConsecutiveFailures = entry.ConsecutiveFailures + 1,
                LastError = Truncate(observation.Error),
            },
            _ => entry with { Outcome = observation.Outcome },
        };

    // The bound, applied on BOTH the load and the persist path so no write can bypass it: entries outside the
    // closed key set are dropped, a hand-edited blob's duplicates collapse to the last one written, every error
    // is re-truncated, an out-of-window recovered error expires, and the survivors come out in
    // HealthDependency.All order so the projection is stable. The result can never exceed
    // DependencyHealth.MaxEntries, and expiring an error can only shrink the serialized blob.
    private static DependencyHealth[] Normalize(DependencyHealth[] entries, long utcNow)
    {
        var kept = new List<DependencyHealth>(DependencyHealth.MaxEntries);
        foreach (var dependency in HealthDependency.All)
        {
            var index = Array.FindLastIndex(
                entries, e => string.Equals(e.Dependency, dependency, StringComparison.Ordinal));
            if (index < 0)
            {
                continue;
            }

            var entry = entries[index];
            kept.Add(entry with
            {
                Outcome = entry.Outcome ?? string.Empty,
                LastError = ErrorExpired(entry, utcNow) ? string.Empty : Truncate(entry.LastError),
            });
        }

        return [.. kept];
    }

    // A consecutive count above zero means the dependency is failing RIGHT NOW, so no age expires its error —
    // a single failure twenty hours ago that nothing has re-probed must still alarm with what went wrong.
    // Two arithmetic edges the expression cannot show:
    //   a zero failure tick beside a retained error can only come from a hand-edited blob, and reads as
    //   infinitely old, so a hostile blob cannot pin a string on the settings page forever;
    //   a future-dated tick yields a negative age and does NOT expire, because a clock that stepped back is not
    //   evidence of age and must not delete a real error.
    private static bool ErrorExpired(DependencyHealth entry, long utcNow)
        => entry.ConsecutiveFailures == 0
            && utcNow - entry.LastFailureTicks > DependencyHealth.RecoveredErrorRetentionTicks;

    // Applied at write time, not only at render: the transport builds its unreachable and rejected results from
    // arbitrary provider text, so a design that bounds only the entry count has not bounded the record.
    private static string Truncate(string? value)
        => string.IsNullOrEmpty(value) || value.Length <= DependencyHealth.MaxErrorLength
            ? value ?? string.Empty
            : value[..DependencyHealth.MaxErrorLength];

    private Task PersistAsync(DependencyHealth[] entries, long utcNow, CancellationToken ct)
        => StoreBlobAsync(
            JsonSerializer.Serialize(Normalize(entries, utcNow), IngestJsonContext.Default.DependencyHealthArray),
            ct);

    /// <summary>The best-effort write seam every tap uses, stamped at <c>DateTime.UtcNow</c>.</summary>
    /// <remarks>
    /// A diagnostic record must never break the ingest, reconcile or discovery path it is observing, so a store
    /// fault is contained. It is not a silent swallow: <paramref name="onFailure"/> receives the failure text so
    /// the caller emits exactly one structured log line with it as a PARAMETER — a newline in it must not become
    /// a second log entry. A cancellation is a normal shutdown rather than a store fault, so it propagates
    /// untouched and is never logged or recorded as a dependency failure.
    /// </remarks>
    internal static async Task TryRecordAsync(
        IExtensionStore store,
        string dependency,
        HealthObservation observation,
        Action<string> onFailure,
        CancellationToken ct)
    {
        try
        {
            await new HealthStore(store).RecordAsync(dependency, observation, DateTime.UtcNow.Ticks, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            onFailure(ex.Message);
        }
    }
}
