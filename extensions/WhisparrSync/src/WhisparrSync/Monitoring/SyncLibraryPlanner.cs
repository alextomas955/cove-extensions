using System.Globalization;
using Cove.Core.Interfaces;
using WhisparrSync.Contracts;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Monitoring;

/// <summary>What one offer established, and what the instance now calls the entry.</summary>
/// <remarks>
/// The classification travels with the answer rather than being derived from it here, because the
/// two passes establish it from different requests: the scene pass reads it off the add's own
/// refusal, and the site pass off the read that precedes the add. A single derivation would have to
/// treat one generation's success as the other's already-held.
/// </remarks>
/// <param name="Registration">What the offer established.</param>
/// <param name="Answer">Whatever the instance said, or null where nothing arrived.</param>
/// <param name="InstanceId">
/// The instance's own numeric id for the entry, or null where the answer names none. Read off the
/// answer the offer already has, so reaching the entry afterwards costs no further request.
/// </param>
internal sealed record SyncRegistration(
    SceneRegistration Registration, WhisparrResponse? Answer, int? InstanceId)
{
    /// <summary>What <paramref name="answered"/> says about an entry that was offered outright.</summary>
    internal static SyncRegistration Offered(WhisparrResponse? answered)
        => new(
            AddAllMissingPlanner.Classify(answered),
            answered,
            MonitoringProjector.EntityIdIn(answered?.Body));
}

/// <summary>What marking one registered entry's scenes wanted did, counted in scenes.</summary>
/// <remarks>
/// Counts and nothing else, for the reason the run's own result states. One entry the run registers
/// can carry any number of scenes - a site carries a whole studio's - so a member listing them would
/// grow with the library.
/// <para>
/// The three failures are counted apart because they are different facts a reader acts on
/// differently: a scene the metadata provider names no number for is one this product cannot address
/// at all, a scene the instance holds no row for is one the instance has never seen, and a refusal is
/// the instance declining to set a flag on a row it does hold.
/// </para>
/// </remarks>
/// <param name="Monitored">How many scenes were marked wanted.</param>
/// <param name="Unnumbered">
/// How many carried no number the metadata provider would answer, so nothing could address them.
/// </param>
/// <param name="Unresolved">How many the instance holds no row for, or would not answer about.</param>
/// <param name="Refused">How many the instance declined to flag.</param>
internal readonly record struct SceneMonitorTally(
    int Monitored, int Unnumbered, int Unresolved, int Refused)
{
    /// <summary>Nothing counted at all.</summary>
    internal static SceneMonitorTally Nothing => default;

    /// <summary>One scene, classified from what the instance answered.</summary>
    /// <remarks>
    /// An answer nothing could be read out of counts as refused. Reporting a flag as set that was not
    /// leaves a reader believing the instance is watching for a scene it is not.
    /// </remarks>
    internal static SceneMonitorTally For(WhisparrResponse? answer)
        => answer is not null
            && answer.Refusal is MonitorRefusalKind.None
            && MonitoringProjector.AcceptedStatus(answer.StatusCode) is MonitorRefusalKind.None
                ? new SceneMonitorTally(1, 0, 0, 0)
                : new SceneMonitorTally(0, 0, 0, 1);

    /// <summary>How many scenes were not marked wanted, however they failed.</summary>
    internal int NotMonitored => Unnumbered + Unresolved + Refused;

    /// <summary>This tally and <paramref name="other"/> added together.</summary>
    internal SceneMonitorTally Plus(SceneMonitorTally other)
        => new(
            Monitored + other.Monitored,
            Unnumbered + other.Unnumbered,
            Unresolved + other.Unresolved,
            Refused + other.Refused);
}

/// <summary>How a run over the whole library's identifiers ended.</summary>
internal enum SyncLibraryRunOutcome
{
    /// <summary>Every identifier was offered.</summary>
    Completed,

    /// <summary>Nothing in the library carries an identifier, so nothing was offered at all.</summary>
    NothingToRegister,

    /// <summary>
    /// The run was stopped part way. What it registered before that stays registered.
    /// </summary>
    Cancelled,
}

/// <summary>What a run over the whole library's identifiers did.</summary>
/// <remarks>
/// Counts and nothing else. A member listing the identifiers would grow with the library, and a
/// library reaches millions of files.
/// </remarks>
/// <param name="Outcome">How the run ended.</param>
/// <param name="Registered">How many entries the instance's catalogue did not already hold.</param>
/// <param name="AlreadyHeld">How many it already held, which is not a failure.</param>
/// <param name="Refused">How many it would not take.</param>
/// <param name="Monitored">
/// How many scenes were marked wanted, which is zero unless monitoring was on. Counted in scenes on
/// both passes: what a reader owns on a site is its scenes, so a site's registration and the scenes
/// marked under it are different nouns and the summary states each as what it is.
/// </param>
/// <param name="MonitorRefused">
/// How many scenes the instance declined to flag. Counted apart from <paramref name="Refused"/>
/// because they are different facts: a scene the instance holds and will not flag is not a scene it
/// declined.
/// </param>
/// <param name="Unnumbered">
/// How many scenes carried no number the metadata provider would answer, so nothing could address
/// them on the instance.
/// </param>
/// <param name="Unresolved">
/// How many scenes the instance holds no row for, or would not answer about.
/// </param>
/// <param name="Offered">How many identifiers the run reached.</param>
/// <param name="Moved">
/// How many entries the instance already held at a root other than the one their own files sit
/// under, and were moved to it. Counted apart from <paramref name="Registered"/> and from
/// <paramref name="AlreadyHeld"/>: the run changed the instance, and it added nothing.
/// </param>
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
    int Moved);

/// <summary>
/// Offers every identifier the library yields to the connected instance once, one bounded request
/// each, reporting on the host's own progress as it goes.
/// </summary>
/// <remarks>
/// Pure. It drives the delegates it is given and performs no I/O of its own, and nothing outlives
/// one identifier: each is offered, classified into a count and dropped, so nothing here grows with
/// the library.
/// <para>
/// Which kind of entry the identifiers name is the caller's, not this loop's. Whatever the run
/// registers, the counts and the lines are the same shape and every figure a reader sees is stated
/// in that generation's own noun.
/// </para>
/// <para>
/// Whether the instance already holds an entry is answered by the instance, one row at a time, rather
/// than computed from a catalogue listing read off it. That is what makes a second run over the same
/// library create no duplicate: a second offer of a scene the instance holds costs one request and
/// changes nothing.
/// </para>
/// <para>
/// A refusal never ends the run. Every identifier is offered once and the refusals are counted, so
/// an instance that declines a hundred scenes still leaves the rest of the library registered.
/// </para>
/// <para>
/// The classification arrives on <see cref="SyncRegistration"/> rather than being derived here. The
/// scene pass composes it through <see cref="AddAllMissingPlanner.Classify(WhisparrResponse)"/> and
/// its <see cref="AddAllMissingPlanner.AlreadyHeldErrorCode"/>, reused rather than restated: the code
/// is transcribed from what one instance answered, and a second transcription could drift from it
/// while both files still passed their own tests.
/// </para>
/// </remarks>
internal static class SyncLibraryPlanner
{
    /// <summary>
    /// Offers each identifier <paramref name="identities"/> yields once through
    /// <paramref name="register"/>, reporting one host unit per entry on <paramref name="progress"/>.
    /// </summary>
    /// <remarks>
    /// The three progress calls are in the order the host requires, and each order is load-bearing.
    /// The count comes first, because the host refuses a declaration made once a unit has started.
    /// The declaration comes before the first unit, because a run that never declares one never
    /// derives a fraction. The summary comes last, because the host writes its own aggregate line
    /// into the job's summary on every unit completion and copies that over the sub-task, so a
    /// summary set before the final completion is silently replaced by phrasing that counts units
    /// rather than scenes.
    /// <para>
    /// A unit is completed and disposed inside one scope. The host removes a completed unit's state
    /// only on disposal, so a run that completed every unit and disposed none would leave one entry
    /// per scene in a host dictionary - which is what would make a per-scene tick unaffordable here.
    /// </para>
    /// </remarks>
    /// <param name="identities">
    /// The identifier stream, as a factory rather than one enumerable. It is enumerated twice - once
    /// to count and once to offer - and both enumerations must come from the same derivation: the
    /// stream applies the host's same-source rule in memory after the query's own distinct, and two
    /// spellings of one source are present in real data, so a second cheaper count would disagree
    /// with the number of ticks.
    /// </param>
    /// <param name="registers">
    /// What the run registers, which is the noun every line and every summary figure is stated in.
    /// </param>
    /// <param name="named">
    /// What one identifier is called on the host's own unit. The unit name is what a reader sees
    /// beside the line, so it is the identifier itself rather than anything composed from it.
    /// </param>
    /// <param name="register">
    /// Offers one entry, answering what it established and whatever the instance said.
    /// </param>
    /// <param name="monitor">
    /// Marks one entry's scenes wanted and answers what that did, or null where monitoring is off.
    /// Called for an entry the instance already held as well as for one just registered: the choice
    /// means monitor what I own, not monitor what I just added. It is handed the offer's own answer,
    /// so the instance's numeric id costs no further request.
    /// <para>
    /// It answers a tally rather than one response because one entry can carry any number of scenes.
    /// On the pass whose entry IS a scene the tally is that one scene.
    /// </para>
    /// </param>
    /// <param name="progress">The host's own progress, which the units are reported on.</param>
    /// <param name="ct">Cancelled when the host stops the job.</param>
    /// <typeparam name="TIdentity">What one identifier the run offers carries.</typeparam>
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
                moved);
    }

    /// <summary>The one line a reader sees while the run works.</summary>
    /// <remarks>
    /// Whatever the run registers, and nothing else. Composed under the invariant culture with a
    /// grouped format, so the same figure reads the same wherever it appears - including beside the
    /// browser's own hand-written grouping on the settings page.
    /// <para>
    /// No word for a batch, a chunk, a unit or a slice appears here or in the summary. A reader could
    /// take any of them for a number of entries, which is the confusion SYNC-5 is about.
    /// </para>
    /// </remarks>
    internal static string LineFor(int offered, int total, SyncRegisters registers)
        => string.Create(
            CultureInfo.InvariantCulture, $"{Singular(registers)} {offered:N0} of {total:N0}");

    /// <summary>The one line the host's job list shows for <paramref name="run"/>.</summary>
    /// <remarks>
    /// Counts rather than a list of scenes. What was registered, what was already held and what was
    /// refused are stated apart, because they are different facts: one is work done, one is a
    /// catalogue that was already complete, and one is the instance declining. What was monitored is
    /// named only where monitoring was asked for, so a run with the choice off says nothing about a
    /// flag it never set.
    /// </remarks>
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
                + $"Whisparr{Relocated(run)}, {run.Refused:N0} "
                + $"refused{Monitoring(run, monitoring, registers)}{ending}.");
    }

    /// <summary>What the summary says about entries this run moved, or nothing where it moved none.</summary>
    /// <remarks>
    /// Stated only where there is a figure to act on. One of the two passes can never move anything,
    /// and a permanent zero there would read as a thing that failed rather than one that never
    /// applied.
    /// </remarks>
    private static string Relocated(SyncLibraryRun run)
        => run.Moved == 0
            ? string.Empty
            : string.Create(
                CultureInfo.InvariantCulture,
                $", {run.Moved:N0} moved to the root holding their files");

    /// <summary>What one entry the run registers is called.</summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="registers"/> is not something this product registers. A default noun would
    /// state the wrong one to a reader rather than failing.
    /// </exception>
    private static string Singular(SyncRegisters registers) => registers switch
    {
        SyncRegisters.Scenes => "Scene",
        SyncRegisters.Sites => "Site",
        _ => throw new ArgumentOutOfRangeException(
            nameof(registers), registers, "This is not something this product registers."),
    };

    /// <inheritdoc cref="Singular"/>
    private static string Plural(SyncRegisters registers) => registers switch
    {
        SyncRegisters.Scenes => "scenes",
        SyncRegisters.Sites => "sites",
        _ => throw new ArgumentOutOfRangeException(
            nameof(registers), registers, "This is not something this product registers."),
    };

    /// <summary>A run that reached no identifier at all.</summary>
    internal static SyncLibraryRun Nothing { get; } =
        new(SyncLibraryRunOutcome.NothingToRegister, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    /// <summary>Sets <paramref name="run"/>'s own summary as the last progress call, and answers it.</summary>
    private static SyncLibraryRun Ending(
        SyncLibraryRun run, bool monitoring, SyncRegisters registers, IJobProgress progress)
    {
        progress.SetSummary(SummaryOf(run, monitoring, registers));
        return run;
    }

    /// <summary>What the summary says about monitoring, or nothing where it was off.</summary>
    /// <remarks>
    /// What was monitored is scenes on both passes. Where the run registers scenes the noun is
    /// already the one every other figure in the sentence is stated in, so it is left implicit; where
    /// it registers sites it is stated, because a bare figure between two counts of sites would read
    /// as a third one.
    /// <para>
    /// The figure that could not be monitored covers every way a scene was not flagged - no number to
    /// address it by, no row on the instance, and the instance declining - because a reader is being
    /// told how many of their own scenes are not being watched for.
    /// </para>
    /// </remarks>
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

    /// <summary>The noun a monitoring figure is stated in, or nothing where it is already implicit.</summary>
    private static string Scenes(int counted, SyncRegisters registers) => registers switch
    {
        SyncRegisters.Scenes => string.Empty,
        _ => counted == 1 ? " scene" : " scenes",
    };

    /// <summary>The host outcome one scene's unit is completed under.</summary>
    /// <remarks>
    /// A scene the instance already held is skipped rather than succeeded: nothing about it changed,
    /// and the host's own aggregate counts the two apart. An entry this run moved succeeded, because
    /// the run did change the instance, which is the opposite of that skip.
    /// </remarks>
    private static JobUnitOutcome OutcomeFor(SceneRegistration registration) => registration switch
    {
        SceneRegistration.Registered => JobUnitOutcome.Succeeded,
        SceneRegistration.Moved => JobUnitOutcome.Succeeded,
        SceneRegistration.AlreadyHeld => JobUnitOutcome.Skipped,
        _ => JobUnitOutcome.Failed,
    };
}
