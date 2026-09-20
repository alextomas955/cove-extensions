using System.Globalization;
using Cove.Core.Interfaces;
using WhisparrSync.Contracts;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Monitoring;

// The classification travels with the answer rather than being derived here, because the two
// passes establish it from different requests: the scene pass reads it off the add's own refusal,
// the site pass off the read that precedes the add.
//
// InstanceId is read off the answer the offer already has, so reaching the entry afterwards costs
// no further request. Root is null on the scene pass, which registers a scene under a site the
// instance already placed.
internal sealed record SyncRegistration(
    SceneRegistration Registration,
    WhisparrResponse? Answer,
    int? InstanceId,
    EntityRoot? Root = null)
{
    internal static SyncRegistration Offered(WhisparrResponse? answered)
        => new(
            AddAllMissingPlanner.Classify(answered),
            answered,
            MonitoringProjector.EntityIdIn(answered?.Body));
}

// Counts only. One entry can carry any number of scenes, so a member listing them would grow with
// the library.
//
// The three failures are counted apart because a reader acts on them differently: Unnumbered is a
// scene the metadata provider names no number for, Unresolved is one the instance holds no row for
// or would not answer about, and Refused is the instance declining to flag a row it does hold.
internal readonly record struct SceneMonitorTally(
    int Monitored, int Unnumbered, int Unresolved, int Refused)
{
    internal static SceneMonitorTally Nothing => default;

    // An answer nothing could be read out of counts as refused. Reporting a flag as set that was
    // not leaves a reader believing the instance is watching for a scene it is not.
    internal static SceneMonitorTally For(WhisparrResponse? answer)
        => answer is not null
            && answer.Refusal is MonitorRefusalKind.None
            && MonitoringProjector.AcceptedStatus(answer.StatusCode) is MonitorRefusalKind.None
                ? new SceneMonitorTally(1, 0, 0, 0)
                : new SceneMonitorTally(0, 0, 0, 1);

    internal int NotMonitored => Unnumbered + Unresolved + Refused;

    internal SceneMonitorTally Plus(SceneMonitorTally other)
        => new(
            Monitored + other.Monitored,
            Unnumbered + other.Unnumbered,
            Unresolved + other.Unresolved,
            Refused + other.Refused);
}

internal enum SyncLibraryRunOutcome
{
    Completed,

    NothingToRegister,

    // Stopped part way. What was registered before the stop stays registered.
    Cancelled,
}

// Counts only. A member listing the identifiers would grow with the library.
//
// Monitored and MonitorRefused are counted in scenes on both passes, apart from Registered and
// Refused, which count entries. Moved counts entries the run relocated, apart from Registered and
// AlreadyHeld because the run changed the instance and added nothing.
//
// WithoutAnAgreedRoot is neither inside Refused nor apart from it: an entry the instance already
// holds is counted as already held and its scenes are still marked, while one it does not hold is
// refused, and both are counted here. What a reader fixes is the folder mapping.
//
// RootsLeftBehind is the one member naming anything rather than counting it. An operator creates
// those roots by hand, so it does not grow with the library.
internal sealed record SyncLibraryRun(
    SyncLibraryRunOutcome Outcome,
    int Registered,
    int AlreadyHeld,
    int Refused,
    int Monitored,
    int MonitorRefused,
    int Unnumbered,
    int Unresolved,
    int Offered,
    int Moved,
    int SplitAcrossRoots,
    int FilesLeftElsewhere,
    int WithoutAnAgreedRoot,
    IReadOnlyList<string> RootsLeftBehind);

// Nothing outlives one identifier: each is offered, classified into a count and dropped, so
// nothing grows with the library.
//
// Whether the instance already holds an entry is the instance's own answer, one row at a time,
// never computed from a catalogue listing. A second run over the same library therefore creates no
// duplicate.
//
// A refusal never ends the run. Every identifier is offered once and the refusals are counted.
//
// The classification arrives on SyncRegistration rather than being derived here. The scene pass
// composes it through AddAllMissingPlanner.Classify and its AlreadyHeldErrorCode, reused rather
// than restated: the code is transcribed from what one instance answered, and a second
// transcription could drift while both files still passed their own tests.
internal static class SyncLibraryPlanner
{
    // The three progress calls are in the order the host requires. The count comes first, because
    // the host refuses a declaration made once a unit has started. The declaration comes before the
    // first unit, because a run that never declares one never derives a fraction. The summary comes
    // last, because the host writes its own aggregate line into the job's summary on every unit
    // completion and copies that over the sub-task, so a summary set earlier is silently replaced
    // by phrasing that counts units rather than scenes.
    //
    // A unit is completed and disposed inside one scope. The host removes a completed unit's state
    // only on disposal, so a run that disposed none would leave one entry per scene in a host
    // dictionary.
    //
    // identities is a factory rather than one enumerable because it is enumerated twice, once to
    // count and once to offer, and both enumerations must come from the same derivation: the stream
    // applies the host's same-source rule in memory after the query's own distinct, so a cheaper
    // count would disagree with the number of ticks.
    //
    // monitor is called for an entry the instance already held as well as for one just registered:
    // the choice means monitor what I own, not monitor what I just added. It is handed the offer's
    // own answer, so the instance's numeric id costs no further request, and it answers a tally
    // because one entry can carry any number of scenes.
    internal static async Task<SyncLibraryRun> RunAsync<TIdentity>(
        SyncRegisters registers,
        Func<CancellationToken, IAsyncEnumerable<TIdentity>> identities,
        Func<TIdentity, string> named,
        Func<TIdentity, CancellationToken, Task<SyncRegistration>> register,
        Func<TIdentity, SyncRegistration, CancellationToken, Task<SceneMonitorTally>>? monitor,
        IJobProgress progress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(identities);
        ArgumentNullException.ThrowIfNull(named);
        ArgumentNullException.ThrowIfNull(register);
        ArgumentNullException.ThrowIfNull(progress);

        var registered = 0;
        var alreadyHeld = 0;
        var refused = 0;
        var moved = 0;
        var splitAcrossRoots = 0;
        var filesLeftElsewhere = 0;
        var withoutAnAgreedRoot = 0;

        // Bounded by the configured library root count, which an operator creates by hand. It holds
        // root names only, never an entry and never a file, so it does not grow with the library.
        var rootsLeftBehind = new List<string>();
        var monitoring = SceneMonitorTally.Nothing;
        var offered = 0;

        try
        {
            ct.ThrowIfCancellationRequested();

            var total = 0;
            await foreach (var _ in identities(ct).WithCancellation(ct).ConfigureAwait(false))
            {
                total++;
            }

            // Answered before anything is declared. The host returns immediately from its progress
            // refresh at a zero total, so a run declaring zero would never derive a fraction and
            // would never be given an ending at all.
            if (total == 0)
            {
                return Ending(Nothing, monitor is not null, registers, progress);
            }

            progress.DeclareUnitCount(total);

            await foreach (var identity in identities(ct).WithCancellation(ct).ConfigureAwait(false))
            {
                ct.ThrowIfCancellationRequested();
                offered++;

                var line = LineFor(offered, total, registers);
                using var unit = progress.StartUnit(named(identity), line);

                var answered = await register(identity, ct).ConfigureAwait(false);
                var registration = answered.Registration;

                switch (registration)
                {
                    case SceneRegistration.Registered:
                        registered++;
                        break;
                    case SceneRegistration.AlreadyHeld:
                        alreadyHeld++;
                        break;
                    case SceneRegistration.Moved:
                        moved++;
                        break;
                    default:
                        refused++;
                        break;
                }

                if (answered.Root is { } root)
                {
                    if (root.Refusal is MonitorRefusalKind.NoAgreedRootForThisEntity)
                    {
                        withoutAnAgreedRoot++;
                    }

                    if (root.RootsLeftBehind.Count > 0)
                    {
                        splitAcrossRoots++;
                        filesLeftElsewhere += root.FilesLeftElsewhere;

                        foreach (var left in root.RootsLeftBehind)
                        {
                            if (!rootsLeftBehind.Contains(left, StringComparer.Ordinal))
                            {
                                rootsLeftBehind.Add(left);
                            }
                        }
                    }
                }

                // Skipped only where the offer was refused: there is no entry on the instance there
                // to set a flag on.
                if (monitor is not null && registration is not SceneRegistration.Refused)
                {
                    monitoring = monitoring.Plus(
                        await monitor(identity, answered, ct).ConfigureAwait(false));
                }

                unit.Complete(OutcomeFor(registration), line);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Cancelled rather than failed: what was registered before the stop is in the instance's
            // catalogue and there is nothing to undo.
            return Ending(
                Ended(SyncLibraryRunOutcome.Cancelled), monitor is not null, registers, progress);
        }

        return Ending(
            Ended(SyncLibraryRunOutcome.Completed), monitor is not null, registers, progress);

        SyncLibraryRun Ended(SyncLibraryRunOutcome outcome)
            => new(
                outcome,
                registered,
                alreadyHeld,
                refused,
                monitoring.Monitored,
                monitoring.Refused,
                monitoring.Unnumbered,
                monitoring.Unresolved,
                offered,
                moved,
                splitAcrossRoots,
                filesLeftElsewhere,
                withoutAnAgreedRoot,
                rootsLeftBehind);
    }

    // Composed under the invariant culture with a grouped format, so the same figure reads the
    // same wherever it appears, including beside the browser's own hand-written grouping on the
    // settings page. No word for a batch, a chunk, a unit or a slice appears here or in the
    // summary: a reader could take any of them for a number of entries.
    internal static string LineFor(int offered, int total, SyncRegisters registers)
        => string.Create(
            CultureInfo.InvariantCulture, $"{Singular(registers)} {offered:N0} of {total:N0}");

    // Counts rather than a list of scenes. Registered, already held and refused are stated apart
    // because they are different facts. What was monitored is named only where monitoring was asked
    // for, so a run with the choice off says nothing about a flag it never set.
    internal static string SummaryOf(SyncLibraryRun run, bool monitoring, SyncRegisters registers)
    {
        ArgumentNullException.ThrowIfNull(run);

        if (run.Outcome == SyncLibraryRunOutcome.NothingToRegister)
        {
            return registers switch
            {
                SyncRegisters.Sites =>
                    "No studio in the library carries an identifier this Whisparr names sites by.",
                _ => "No scene in the library carries an identifier this Whisparr names scenes by.",
            };
        }

        var ending = run.Outcome == SyncLibraryRunOutcome.Cancelled ? ", then stopped" : string.Empty;

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{run.Registered:N0} {Plural(registers)} registered, {run.AlreadyHeld:N0} already in "
                + $"Whisparr{Relocated(run)}, {run.Refused:N0} refused{Unagreed(run)}"
                + $"{Monitoring(run, monitoring, registers)}{ending}.")
            + Split(run, registers);
    }

    // Stated beside the other figures rather than inside the refused one: these entries are spread
    // across two of the figures, so a parenthetical inside either would read as a subset of it.
    private static string Unagreed(SyncLibraryRun run)
        => run.WithoutAnAgreedRoot == 0
            ? string.Empty
            : string.Create(
                CultureInfo.InvariantCulture,
                $", {run.WithoutAnAgreedRoot:N0} with no agreed root");

    // Absent where nothing was split. The library roots are named and nothing else is, and the
    // line says the files were left where they are: an operator reading that an entry moved could
    // otherwise take it to mean the files moved with it.
    private static string Split(SyncLibraryRun run, SyncRegisters registers)
    {
        if (run.SplitAcrossRoots == 0 || run.RootsLeftBehind.Count == 0)
        {
            return string.Empty;
        }

        var entries = run.SplitAcrossRoots == 1
            ? Singular(registers).ToLowerInvariant() + " has"
            : Plural(registers) + " have";
        var files = run.FilesLeftElsewhere == 1 ? "file" : "files";

        return string.Create(
            CultureInfo.InvariantCulture,
            $" {run.SplitAcrossRoots:N0} {entries} files under more than one library root: "
                + $"{run.FilesLeftElsewhere:N0} {files} were left where they are under "
                + $"{string.Join(", ", run.RootsLeftBehind)}, and nothing was copied.");
    }

    // Stated only where there is a figure to act on. One of the two passes can never move
    // anything, and a permanent zero there would read as something that failed.
    private static string Relocated(SyncLibraryRun run)
        => run.Moved == 0
            ? string.Empty
            : string.Create(
                CultureInfo.InvariantCulture,
                $", {run.Moved:N0} moved to the root holding their files");

    // Throws rather than defaulting: a default noun would state the wrong one to a reader.
    private static string Singular(SyncRegisters registers) => registers switch
    {
        SyncRegisters.Scenes => "Scene",
        SyncRegisters.Sites => "Site",
        _ => throw new ArgumentOutOfRangeException(
            nameof(registers), registers, "This is not something this product registers."),
    };

    private static string Plural(SyncRegisters registers) => registers switch
    {
        SyncRegisters.Scenes => "scenes",
        SyncRegisters.Sites => "sites",
        _ => throw new ArgumentOutOfRangeException(
            nameof(registers), registers, "This is not something this product registers."),
    };

    internal static SyncLibraryRun Nothing { get; } =
        new(SyncLibraryRunOutcome.NothingToRegister, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, []);

    private static SyncLibraryRun Ending(
        SyncLibraryRun run, bool monitoring, SyncRegisters registers, IJobProgress progress)
    {
        progress.SetSummary(SummaryOf(run, monitoring, registers));
        return run;
    }

    // What was monitored is scenes on both passes. On the scene pass the noun is already the one
    // every other figure is stated in, so it is left implicit; on the site pass it is stated,
    // because a bare figure between two counts of sites would read as a third one.
    //
    // The figure that could not be monitored covers every way a scene was not flagged, because a
    // reader is being told how many of their own scenes are not being watched for.
    private static string Monitoring(SyncLibraryRun run, bool monitoring, SyncRegisters registers)
    {
        if (!monitoring)
        {
            return string.Empty;
        }

        var notMonitored = run.MonitorRefused + run.Unnumbered + run.Unresolved;
        var couldNot = notMonitored == 0
            ? string.Empty
            : string.Create(
                CultureInfo.InvariantCulture,
                $", {notMonitored:N0}{Scenes(notMonitored, registers)} not monitored");

        return string.Create(
            CultureInfo.InvariantCulture,
            $", {run.Monitored:N0}{Scenes(run.Monitored, registers)} monitored{couldNot}");
    }

    private static string Scenes(int counted, SyncRegisters registers) => registers switch
    {
        SyncRegisters.Scenes => string.Empty,
        _ => counted == 1 ? " scene" : " scenes",
    };

    // A scene the instance already held is skipped rather than succeeded: nothing about it
    // changed, and the host's own aggregate counts the two apart. An entry this run moved
    // succeeded, because the run did change the instance.
    private static JobUnitOutcome OutcomeFor(SceneRegistration registration) => registration switch
    {
        SceneRegistration.Registered => JobUnitOutcome.Succeeded,
        SceneRegistration.Moved => JobUnitOutcome.Succeeded,
        SceneRegistration.AlreadyHeld => JobUnitOutcome.Skipped,
        _ => JobUnitOutcome.Failed,
    };
}
