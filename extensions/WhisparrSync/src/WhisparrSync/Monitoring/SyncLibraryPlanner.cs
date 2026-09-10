using System.Globalization;
using Cove.Core.Interfaces;
using WhisparrSync.Contracts;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Monitoring;

/// <summary>How a run over the whole library's scene identifiers ended.</summary>
internal enum SyncLibraryRunOutcome
{
    /// <summary>Every identifier was offered.</summary>
    Completed,

    /// <summary>No scene in the library carries an identifier, so nothing was offered at all.</summary>
    NothingToRegister,

    /// <summary>
    /// The run was stopped part way. What it registered before that stays registered.
    /// </summary>
    Cancelled,
}

/// <summary>What a run over the whole library's scene identifiers did.</summary>
/// <remarks>
/// Counts and nothing else. A member listing the identifiers would grow with the library, and a
/// library reaches millions of files.
/// </remarks>
/// <param name="Outcome">How the run ended.</param>
/// <param name="Registered">How many scenes the instance's catalogue did not already hold.</param>
/// <param name="AlreadyHeld">How many it already held, which is not a failure.</param>
/// <param name="Refused">How many it would not take.</param>
/// <param name="Monitored">How many were marked wanted, which is zero unless monitoring was on.</param>
/// <param name="MonitorRefused">
/// How many could not be marked wanted. Counted apart from <paramref name="Refused"/> because they
/// are different facts: a scene the instance holds and will not flag is not a scene it declined.
/// </param>
/// <param name="Offered">How many identifiers the run reached.</param>
internal sealed record SyncLibraryRun(
    SyncLibraryRunOutcome Outcome,
    int Registered,
    int AlreadyHeld,
    int Refused,
    int Monitored,
    int MonitorRefused,
    int Offered);

/// <summary>
/// Offers every identified scene in the library to the connected instance once, one bounded request
/// each, reporting on the host's own progress as it goes.
/// </summary>
/// <remarks>
/// Pure. It drives the delegates it is given and performs no I/O of its own, and nothing outlives
/// one identifier: each is offered, classified into a count and dropped, so nothing here grows with
/// the library.
/// <para>
/// Whether the instance already holds a scene is ANSWERED by the instance, one row at a time, rather
/// than computed from a catalogue listing read off it. That is what makes a second run over the same
/// library create no duplicate: a second offer of a scene the instance holds costs one request and
/// changes nothing.
/// </para>
/// <para>
/// A refusal never ends the run. Every identifier is offered once and the refusals are counted, so
/// an instance that declines a hundred scenes still leaves the rest of the library registered.
/// </para>
/// <para>
/// The classification is <see cref="AddAllMissingPlanner.Classify(WhisparrResponse)"/> and its
/// <see cref="AddAllMissingPlanner.AlreadyHeldErrorCode"/>, reused rather than restated. The code is
/// transcribed from what one instance answered, and a second transcription here could drift from it
/// while both files still passed their own tests.
/// </para>
/// </remarks>
internal static class SyncLibraryPlanner
{
    /// <summary>
    /// Offers each identifier <paramref name="identities"/> yields once through
    /// <paramref name="register"/>, reporting one host unit per scene on <paramref name="progress"/>.
    /// </summary>
    /// <remarks>
    /// The three progress calls are in the order the host requires, and each order is load-bearing.
    /// The count comes first, because the host refuses a declaration made once a unit has started.
    /// The declaration comes before the first unit, because a run that never declares one never
    /// derives a fraction. The summary comes LAST, because the host writes its own aggregate line
    /// into the job's summary on every unit completion and copies that over the sub-task, so a
    /// summary set before the final completion is silently replaced by phrasing that counts units
    /// rather than scenes.
    /// <para>
    /// A unit is completed AND disposed inside one scope. The host removes a completed unit's state
    /// only on disposal, so a run that completed every unit and disposed none would leave one entry
    /// per scene in a host dictionary - which is what would make a per-scene tick unaffordable here.
    /// </para>
    /// </remarks>
    /// <param name="identities">
    /// The identifier stream, as a factory rather than one enumerable. It is enumerated TWICE - once
    /// to count and once to offer - and both enumerations must come from the same derivation: the
    /// stream applies the host's same-source rule in memory after the query's own distinct, and two
    /// spellings of one source are present in real data, so a second cheaper count would disagree
    /// with the number of ticks.
    /// </param>
    /// <param name="register">Offers one scene, answering whatever the instance said.</param>
    /// <param name="monitor">
    /// Marks one scene wanted, or null where monitoring is off. Called for a scene the instance
    /// already held as well as for one just registered: the choice means monitor what I own, not
    /// monitor what I just added.
    /// </param>
    /// <param name="progress">The host's own progress, which the units are reported on.</param>
    /// <param name="ct">Cancelled when the host stops the job.</param>
    internal static async Task<SyncLibraryRun> RunAsync(
        Func<CancellationToken, IAsyncEnumerable<string>> identities,
        Func<string, CancellationToken, Task<WhisparrResponse?>> register,
        Func<string, WhisparrResponse?, CancellationToken, Task<WhisparrResponse?>>? monitor,
        IJobProgress progress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(identities);
        ArgumentNullException.ThrowIfNull(register);
        ArgumentNullException.ThrowIfNull(progress);

        var registered = 0;
        var alreadyHeld = 0;
        var refused = 0;
        var monitored = 0;
        var monitorRefused = 0;
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
                return Ending(
                    Nothing, monitor is not null, progress);
            }

            progress.DeclareUnitCount(total);

            await foreach (var identity in identities(ct).WithCancellation(ct).ConfigureAwait(false))
            {
                ct.ThrowIfCancellationRequested();
                offered++;

                var line = LineFor(offered, total);
                using var unit = progress.StartUnit(identity, line);

                var answered = await register(identity, ct).ConfigureAwait(false);
                var registration = AddAllMissingPlanner.Classify(answered);

                switch (registration)
                {
                    case SceneRegistration.Registered:
                        registered++;
                        break;
                    case SceneRegistration.AlreadyHeld:
                        alreadyHeld++;
                        break;
                    default:
                        refused++;
                        break;
                }

                // Skipped only where the offer was refused: there is no scene on the instance there
                // to set a flag on.
                if (monitor is not null && registration is not SceneRegistration.Refused)
                {
                    if (Accepted(await monitor(identity, answered, ct).ConfigureAwait(false)))
                    {
                        monitored++;
                    }
                    else
                    {
                        monitorRefused++;
                    }
                }

                unit.Complete(OutcomeFor(registration), line);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Cancelled rather than failed: what was registered before the stop is in the instance's
            // catalogue and there is nothing to undo.
            return Ending(
                new SyncLibraryRun(
                    SyncLibraryRunOutcome.Cancelled,
                    registered,
                    alreadyHeld,
                    refused,
                    monitored,
                    monitorRefused,
                    offered),
                monitor is not null,
                progress);
        }

        return Ending(
            new SyncLibraryRun(
                SyncLibraryRunOutcome.Completed,
                registered,
                alreadyHeld,
                refused,
                monitored,
                monitorRefused,
                offered),
            monitor is not null,
            progress);
    }

    /// <summary>The one line a reader sees while the run works.</summary>
    /// <remarks>
    /// Scenes and nothing else. Composed under the invariant culture with a grouped format, so the
    /// same figure reads the same wherever it appears - including beside the browser's own
    /// hand-written grouping on the settings page.
    /// <para>
    /// No word for a batch, a chunk, a unit or a slice appears here or in the summary. A reader could
    /// take any of them for a number of scenes, which is the confusion SYNC-5 is about.
    /// </para>
    /// </remarks>
    internal static string LineFor(int offered, int total)
        => string.Create(CultureInfo.InvariantCulture, $"Scene {offered:N0} of {total:N0}");

    /// <summary>The one line the host's job list shows for <paramref name="run"/>.</summary>
    /// <remarks>
    /// Counts rather than a list of scenes. What was registered, what was already held and what was
    /// refused are stated apart, because they are different facts: one is work done, one is a
    /// catalogue that was already complete, and one is the instance declining. What was monitored is
    /// named only where monitoring was asked for, so a run with the choice off says nothing about a
    /// flag it never set.
    /// </remarks>
    internal static string SummaryOf(SyncLibraryRun run, bool monitoring)
    {
        ArgumentNullException.ThrowIfNull(run);

        if (run.Outcome == SyncLibraryRunOutcome.NothingToRegister)
        {
            return "No scene in the library carries an identifier this Whisparr names scenes by.";
        }

        var ending = run.Outcome == SyncLibraryRunOutcome.Cancelled ? ", then stopped" : string.Empty;

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{run.Registered:N0} scenes registered, {run.AlreadyHeld:N0} already in Whisparr, "
                + $"{run.Refused:N0} refused{Monitoring(run, monitoring)}{ending}.");
    }

    /// <summary>A run that reached no identifier at all.</summary>
    internal static SyncLibraryRun Nothing { get; } =
        new(SyncLibraryRunOutcome.NothingToRegister, 0, 0, 0, 0, 0, 0);

    /// <summary>Sets <paramref name="run"/>'s own summary as the last progress call, and answers it.</summary>
    private static SyncLibraryRun Ending(SyncLibraryRun run, bool monitoring, IJobProgress progress)
    {
        progress.SetSummary(SummaryOf(run, monitoring));
        return run;
    }

    /// <summary>What the summary says about monitoring, or nothing where it was off.</summary>
    private static string Monitoring(SyncLibraryRun run, bool monitoring)
    {
        if (!monitoring)
        {
            return string.Empty;
        }

        var couldNot = run.MonitorRefused == 0
            ? string.Empty
            : string.Create(CultureInfo.InvariantCulture, $", {run.MonitorRefused:N0} not monitored");

        return string.Create(CultureInfo.InvariantCulture, $", {run.Monitored:N0} monitored{couldNot}");
    }

    /// <summary>Whether <paramref name="answer"/> is the instance having set the flag.</summary>
    /// <remarks>
    /// An answer nothing could be read out of counts as not monitored. Reporting a flag as set that
    /// was not leaves a reader believing the instance is watching for a scene it is not.
    /// </remarks>
    private static bool Accepted(WhisparrResponse? answer)
        => answer is not null
            && answer.Refusal is MonitorRefusalKind.None
            && MonitoringProjector.AcceptedStatus(answer.StatusCode) is MonitorRefusalKind.None;

    /// <summary>The host outcome one scene's unit is completed under.</summary>
    /// <remarks>
    /// A scene the instance already held is skipped rather than succeeded: nothing about it changed,
    /// and the host's own aggregate counts the two apart.
    /// </remarks>
    private static JobUnitOutcome OutcomeFor(SceneRegistration registration) => registration switch
    {
        SceneRegistration.Registered => JobUnitOutcome.Succeeded,
        SceneRegistration.AlreadyHeld => JobUnitOutcome.Skipped,
        _ => JobUnitOutcome.Failed,
    };
}
