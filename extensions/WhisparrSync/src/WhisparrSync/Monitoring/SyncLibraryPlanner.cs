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

// Counts only: one entry can carry any number of scenes, so a member listing them would grow with
// the library. The three failures are counted apart because a reader acts on them differently:
// Unnumbered is a scene the provider names no number for, Unresolved one the instance holds no row
// for or would not answer about, Refused the instance declining to flag a row it holds.
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

// Counts only; a member listing identifiers would grow with the library. Monitored and
// MonitorRefused count scenes, the rest count entries, and Moved is apart from Registered and
// AlreadyHeld because the run changed the instance without adding. WithoutAnAgreedRoot overlaps
// both Refused and AlreadyHeld, since either can lack a root; what a reader fixes is the folder
// mapping. RootsLeftBehind names roots an operator created by hand, so it too is bounded.
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

// What a run walks and what it does with each identifier.
//
// Identities is a factory because it is enumerated twice, to count and to offer, and both must come
// from one derivation: the stream applies the host's same-source rule in memory after the query's
// distinct, so a cheaper count would disagree with the number of ticks.
//
// Monitor is called for an entry already held as well as one just registered, the choice meaning
// monitor what I own. It takes the offer's own answer, so the instance's id costs no further
// request, and answers a tally because one entry can carry any number of scenes.
internal sealed record SyncLibrarySource<TIdentity>(
    Func<CancellationToken, IAsyncEnumerable<TIdentity>> Identities,
    Func<TIdentity, string> Named,
    Func<TIdentity, CancellationToken, Task<SyncRegistration>> Register,
    Func<TIdentity, SyncRegistration, CancellationToken, Task<SceneMonitorTally>>? Monitor = null);

// The linking half, and which rows the walk offers at all. A pass that links nothing passes none of
// it.
//
// A folder is linked once the walk has left it, so the entries its files attach to are already
// registered. A row carrying nothing to register is a folder the walk placed no identifier under:
// it takes no unit and is counted in no tally, because a reader is being told about their scenes
// rather than about the shape of their directories.
internal sealed record SyncLibraryWalk<TIdentity>(
    Func<TIdentity, bool>? Offers = null,
    Func<TIdentity, string?>? FolderOf = null,
    Func<string, CancellationToken, Task>? LinkFolder = null)
{
    internal static SyncLibraryWalk<TIdentity> EveryRow { get; } = new();

    internal bool Offered(TIdentity identity) => Offers is null || Offers(identity);
}

// Each identifier is offered, counted and dropped, so nothing grows with the library. Whether an
// entry is already held is the instance's own answer per row, never computed from a listing, so a
// second run creates no duplicate. A refusal never ends the run; every identifier is offered once
// and refusals are counted.
//
// The classification rides on SyncRegistration. The scene pass composes it through
// AddAllMissingPlanner.Classify, reused rather than restated: the error code is transcribed from
// what one instance answered, and a second transcription could drift while both files stayed green.
internal static class SyncLibraryPlanner
{
    // The host requires this order: count, declaration, units, summary last. A declaration after a
    // unit starts is refused, a run declaring none derives no fraction, and a summary set before
    // the last unit is overwritten by the host's aggregate line. Each unit is completed and
    // disposed in one scope: the host clears a unit's state only on disposal, so a run disposing
    // none leaves an entry per scene in a host dictionary.
    internal static async Task<SyncLibraryRun> RunAsync<TIdentity>(
        SyncRegisters registers,
        SyncLibrarySource<TIdentity> source,
        IJobProgress progress,
        CancellationToken ct,
        SyncLibraryWalk<TIdentity>? walk = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(progress);

        var walking = walk ?? SyncLibraryWalk<TIdentity>.EveryRow;
        var monitors = source.Monitor is not null;
        var tally = new SyncLibraryTally();

        try
        {
            ct.ThrowIfCancellationRequested();

            var total = await CountAsync(source, walking, ct).ConfigureAwait(false);

            // Answered before anything is declared: the host returns immediately from its progress
            // refresh at a zero total, so a run declaring zero would derive no fraction and be
            // given no ending.
            if (total == 0)
            {
                return Ending(Nothing, monitors, registers, progress);
            }

            progress.DeclareUnitCount(total);
            await OfferAsync(registers, source, walking, total, tally, progress, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Cancelled rather than failed: what was registered before the stop is in the instance's
            // catalogue and there is nothing to undo.
            return Ending(
                tally.Ended(SyncLibraryRunOutcome.Cancelled), monitors, registers, progress);
        }

        return Ending(
            tally.Ended(SyncLibraryRunOutcome.Completed), monitors, registers, progress);
    }

    private static async Task<int> CountAsync<TIdentity>(
        SyncLibrarySource<TIdentity> source, SyncLibraryWalk<TIdentity> walk, CancellationToken ct)
    {
        var total = 0;
        await foreach (var counting in source.Identities(ct).WithCancellation(ct).ConfigureAwait(false))
        {
            if (walk.Offered(counting))
            {
                total++;
            }
        }

        return total;
    }

    private static async Task OfferAsync<TIdentity>(
        SyncRegisters registers,
        SyncLibrarySource<TIdentity> source,
        SyncLibraryWalk<TIdentity> walk,
        int total,
        SyncLibraryTally tally,
        IJobProgress progress,
        CancellationToken ct)
    {
        var folders = new FolderWalk<TIdentity>(walk);
        var offered = 0;

        await foreach (var identity in source.Identities(ct).WithCancellation(ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            await folders.ArriveAsync(identity, ct).ConfigureAwait(false);

            if (!walk.Offered(identity))
            {
                continue;
            }

            offered++;
            var line = LineFor(offered, total, registers);
            using var unit = progress.StartUnit(source.Named(identity), line);

            var answered = await source.Register(identity, ct).ConfigureAwait(false);
            tally.Record(answered);

            // Skipped only where the offer was refused: there is no entry on the instance there to
            // set a flag on.
            if (source.Monitor is not null
                && answered.Registration is not SceneRegistration.Refused)
            {
                tally.RecordMonitoring(
                    await source.Monitor(identity, answered, ct).ConfigureAwait(false));
            }

            unit.Complete(OutcomeFor(answered.Registration), line);
        }

        tally.Offered = offered;
        await folders.LeaveAsync(ct).ConfigureAwait(false);
    }

    // The folder the walk is inside, linked as the walk leaves it and once more after the stream
    // ends so the last is not left out. A row naming no folder leaves the walk where it is: it is
    // an identifier the library holds no file for.
    private sealed class FolderWalk<TIdentity>(SyncLibraryWalk<TIdentity> walk)
    {
        private string? _inside;

        private bool Links => walk.FolderOf is not null && walk.LinkFolder is not null;

        internal async Task ArriveAsync(TIdentity identity, CancellationToken ct)
        {
            if (!Links || walk.FolderOf!(identity) is not { } arrived)
            {
                return;
            }

            if (_inside is not null && !string.Equals(_inside, arrived, StringComparison.Ordinal))
            {
                await walk.LinkFolder!(_inside, ct).ConfigureAwait(false);
            }

            _inside = arrived;
        }

        internal async Task LeaveAsync(CancellationToken ct)
        {
            if (_inside is not null && walk.LinkFolder is not null)
            {
                await walk.LinkFolder(_inside, ct).ConfigureAwait(false);
            }
        }
    }

    // The counters one run accumulates. Mutable and private to the run, so the offer loop states
    // what happened rather than carrying thirteen locals through it.
    private sealed class SyncLibraryTally
    {
        // Bounded by the configured library root count, which an operator creates by hand. It holds
        // root names only, never an entry and never a file, so it does not grow with the library.
        private readonly List<string> _rootsLeftBehind = [];

        private SceneMonitorTally _monitoring = SceneMonitorTally.Nothing;
        private int _registered;
        private int _alreadyHeld;
        private int _refused;
        private int _moved;
        private int _splitAcrossRoots;
        private int _filesLeftElsewhere;
        private int _withoutAnAgreedRoot;

        internal int Offered { get; set; }

        internal void Record(SyncRegistration answered)
        {
            switch (answered.Registration)
            {
                case SceneRegistration.Registered:
                    _registered++;
                    break;
                case SceneRegistration.AlreadyHeld:
                    _alreadyHeld++;
                    break;
                case SceneRegistration.Moved:
                    _moved++;
                    break;
                default:
                    _refused++;
                    break;
            }

            if (answered.Root is { } root)
            {
                RecordRoot(root);
            }
        }

        internal void RecordMonitoring(SceneMonitorTally marked)
            => _monitoring = _monitoring.Plus(marked);

        internal SyncLibraryRun Ended(SyncLibraryRunOutcome outcome)
            => new(
                outcome,
                _registered,
                _alreadyHeld,
                _refused,
                _monitoring.Monitored,
                _monitoring.Refused,
                _monitoring.Unnumbered,
                _monitoring.Unresolved,
                Offered,
                _moved,
                _splitAcrossRoots,
                _filesLeftElsewhere,
                _withoutAnAgreedRoot,
                _rootsLeftBehind);

        private void RecordRoot(EntityRoot root)
        {
            if (root.Refusal is MonitorRefusalKind.NoAgreedRootForThisEntity)
            {
                _withoutAnAgreedRoot++;
            }

            if (root.RootsLeftBehind.Count == 0)
            {
                return;
            }

            _splitAcrossRoots++;
            _filesLeftElsewhere += root.FilesLeftElsewhere;

            foreach (var left in root.RootsLeftBehind)
            {
                if (!_rootsLeftBehind.Contains(left, StringComparer.Ordinal))
                {
                    _rootsLeftBehind.Add(left);
                }
            }
        }
    }

    // Composed under the invariant culture with a grouped format, so one figure reads the same
    // everywhere, including beside the browser's own grouping. No word for a batch, chunk, unit or
    // slice appears here or in the summary: a reader could take any for a number of entries.
    internal static string LineFor(int offered, int total, SyncRegisters registers)
        => string.Create(
            CultureInfo.InvariantCulture, $"{Singular(registers)} {offered:N0} of {total:N0}");

    // Counts, not a list of scenes. Registered, already held and refused are stated apart as
    // different facts. Monitoring is named only where it was asked for, so a run with the choice
    // off says nothing about a flag it never set.
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

    // Absent where nothing was split. Only the library roots are named, and the line says the files
    // were left where they are: a reader told an entry moved could otherwise take it that the files
    // moved with it.
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

    // Monitoring counts scenes on both passes. The scene pass leaves the noun implicit, every other
    // figure already being in it; the site pass states it, a bare figure between two counts of
    // sites reading as a third. The could-not figure covers every way a scene was not flagged, a
    // reader being told how many of their scenes are unwatched.
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

    // A scene the instance already held is skipped rather than succeeded: nothing changed, and the
    // host's aggregate counts the two apart. An entry this run moved succeeded, because the run did
    // change the instance.
    private static JobUnitOutcome OutcomeFor(SceneRegistration registration) => registration switch
    {
        SceneRegistration.Registered => JobUnitOutcome.Succeeded,
        SceneRegistration.Moved => JobUnitOutcome.Succeeded,
        SceneRegistration.AlreadyHeld => JobUnitOutcome.Skipped,
        _ => JobUnitOutcome.Failed,
    };
}
