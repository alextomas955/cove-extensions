using System.Globalization;
using System.Text.Json.Nodes;
using Cove.Extensions.Shared;
using Microsoft.Extensions.DependencyInjection;
using WhisparrSync.Addressing;
using WhisparrSync.Contracts;
using WhisparrSync.Import;
using WhisparrSync.Monitoring;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Jobs;

/// <summary>Which entity one enqueued run is about.</summary>
/// <remarks>A null kind or a zero id means the parameter map carried none that could be read.</remarks>
public sealed record ReflectOwnedBatch(WhisparrEntityKind? Kind, int CoveId);

// Resolved when the run starts, not when it was enqueued. The hard-link setting decides whether a
// matched file is linked or copied in full, and it is the instance's to change at any time.
// Identify pairs the files of one folder with the instance ids the run registered for them, by file
// name. Null for a run that attaches whatever the instance matched, which is every run but the
// library one.
internal sealed record ReflectOwnedAiming(
    WhisparrGeneration Generation,
    Func<string, CancellationToken, Task<AddressedFolder>> Address,
    Func<string, CancellationToken, Task<ImportableListing>> ReadImportable,
    Func<JsonArray, CancellationToken, Task<bool>> Attach,
    Func<string, IReadOnlyDictionary<string, RegisteredScene>, CancellationToken,
        Task<IReadOnlyDictionary<string, RegisteredScene>>>? Identify = null,
    Func<OwnedFilePlacement, CancellationToken, Task<WhisparrResponse?>>? ReadFile = null);

// A null Through with a Skipped reason means the instance's linking setting stopped the run. A null
// Through with no reason reports as a completed run that attached nothing.
internal sealed record ReflectOwnedAim(
    ReflectOwnedAiming? Through, ReflectOwnedSkipReason? Skipped);

/// <summary>
/// The reflect-owned job's id, its (de)serialization onto the host's string-only parameter map, and
/// the folder loop one entity's run goes through.
/// </summary>
public static class ReflectOwnedJob
{
    public const string JobId = "reflect-owned";

    // Names no file, folder or site: the line is durable and must not grow with the library.
    internal const string LeftUnderAnotherRootSentence =
        "Some files were not linked: Whisparr holds their site under a different root from the "
        + "files, and nothing was copied.";

    // States that the check was not made, not that nothing was found to link. An instance that could
    // not be asked answers the same empty root list as one declaring none, and linking across two
    // roots copies the bytes in full.
    internal const string NoRootToCompareSentence =
        "No files were linked: Whisparr declared no root folder, so whether a link would copy the "
        + "data could not be checked.";

    private const string KindKey = "kind";
    private const string CoveIdKey = "coveId";

    public static Dictionary<string, string> Encode(WhisparrEntityKind kind, int coveId)
        => new(StringComparer.Ordinal)
        {
            [KindKey] = kind.ToString(),
            [CoveIdKey] = coveId.ToString(CultureInfo.InvariantCulture),
        };

    /// <summary>Reads one entity's run back off the host's parameter map.</summary>
    /// <remarks>
    /// Never throws; it runs inside the host's job runner, where a throw faults the job. An
    /// unreadable kind answers null rather than the first kind declared, so a run never acts on an
    /// entity nobody named.
    /// </remarks>
    public static ReflectOwnedBatch Decode(IReadOnlyDictionary<string, string>? parameters)
    {
        if (parameters is null)
        {
            return new ReflectOwnedBatch(null, 0);
        }

        var kind = Enum.TryParse<WhisparrEntityKind>(Read(parameters, KindKey), ignoreCase: true, out var named)
            && Enum.IsDefined(named)
                ? named
                : (WhisparrEntityKind?)null;

        var coveId = int.TryParse(
            Read(parameters, CoveIdKey), CultureInfo.InvariantCulture, out var parsed) && parsed > 0
                ? parsed
                : 0;

        return new ReflectOwnedBatch(kind, coveId);
    }

    // Runs as System: the job carries no principal, and Cove's per-principal filters answer an
    // anonymous reader with zero rows and no error, so an entity holding files would read as empty.
    internal static Task<ReflectOwnedRun> RunAsync(
        ReflectOwnedBatch batch,
        IServiceScopeFactory scopes,
        Func<IServiceProvider, CancellationToken, Task<ReflectOwnedAim>> aiming,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(aiming);

        return RunAsSystem.RunInSystemScopeAsync(scopes, async services =>
        {
            if (batch.Kind is not { } kind)
            {
                return Untaken;
            }

            var aim = await aiming(services, ct).ConfigureAwait(false);
            if (aim.Through is not { } aimed)
            {
                return Untaken with { Skipped = aim.Skipped };
            }

            return await RunOneAsync(services, aimed, kind, batch.CoveId, ct).ConfigureAwait(false);
        });
    }

    // Takes an open services rather than opening its own scope: a selection is already inside one
    // elevated to System.
    // The instance's declared roots are read once for the run, never once per folder. A list that
    // could not be established stops the run before a folder is read, because an import made without
    // the comparison copies the bytes in full rather than linking them.
    internal static async Task<ReflectOwnedRun> RunOneAsync(
        IServiceProvider services,
        ReflectOwnedAiming aimed,
        WhisparrEntityKind kind,
        int coveId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(aimed);

        var instanceRoots = await services.GetRequiredService<IReportedRootPort>()
            .ReadAsync(aimed.Generation, ct).ConfigureAwait(false);
        if (instanceRoots is null)
        {
            return new ReflectOwnedRun(
                ReflectOwnedRunOutcome.Completed, 0, 0, RootsCouldNotBeRead: true);
        }

        return await ReflectOwnedPlanner.RunAsync(
            aimed.Generation,
            instanceRoots,
            services.GetRequiredService<IEntityFolderPort>().FoldersFor(kind, coveId, ct),
            aimed.Address,
            aimed.ReadImportable,
            aimed.Attach,
            ct).ConfigureAwait(false);
    }

    // One folder, for the library run that registers and links as it walks. The declared roots are
    // read by the caller once for the whole walk rather than once per folder.
    // One folder, for the library run that registers and links as it walks. The files are the ones
    // the library owns there, so the cost follows what the reader holds rather than the size of the
    // directory, and the instance is never asked to walk a folder it would take minutes to answer
    // about.
    internal static async Task<ReflectOwnedRun> LinkOneFolderAsync(
        ReflectOwnedAiming aimed,
        IReadOnlyList<string> instanceRoots,
        string folder,
        IReadOnlyDictionary<string, RegisteredScene> registered,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(aimed);
        ArgumentNullException.ThrowIfNull(instanceRoots);
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        ArgumentNullException.ThrowIfNull(registered);

        if (aimed.Identify is null || aimed.ReadFile is null || registered.Count == 0)
        {
            return Untaken;
        }

        var addressed = await aimed.Address(folder, ct).ConfigureAwait(false);
        if (addressed.InstancePath is not { } onInstance)
        {
            return new ReflectOwnedRun(
                ReflectOwnedRunOutcome.Completed,
                0,
                0,
                FoldersNotAddressed: 1,
                AddressRefusals: addressed.Refusal is { } refusal
                    ? [new FolderAddressRefusal(addressed.CoveRoot, refusal, addressed.Tried)]
                    : null);
        }

        var identified = await aimed.Identify(folder, registered, ct).ConfigureAwait(false);

        var filesAttached = 0;
        var leftUnderAnotherRoot = 0;
        var anyAttached = false;
        var anyRefused = false;

        // Each batch is sent as it is composed. The whole folder held back until the last file was
        // read would show nothing for as long as the reads took and lose all of it on a stop.
        await foreach (var planned in ReflectOwnedPlanner
            .ComposedFilesAsync(onInstance, identified, instanceRoots, aimed.ReadFile, ct)
            .ConfigureAwait(false))
        {
            leftUnderAnotherRoot += planned.LeftUnderAnotherRoot;
            if (planned.Entries is not { } files)
            {
                continue;
            }

            if (await aimed.Attach(files, ct).ConfigureAwait(false))
            {
                anyAttached = true;
                filesAttached += files.Count;
            }
            else
            {
                anyRefused = true;
            }
        }

        return new ReflectOwnedRun(
            ReflectOwnedRunOutcome.Completed,
            anyAttached ? 1 : 0,
            anyAttached || !anyRefused ? 0 : 1,
            AddressedRoots: [addressed.CoveRoot],
            EntriesLeftUnderAnotherRoot: leftUnderAnotherRoot,
            FilesAttached: filesAttached);
    }


    // Counts, never a list of folders: the line must not grow with the entity, and it would put
    // filesystem paths in a durable place nothing needs them in.
    internal static string SummaryOf(ReflectOwnedRun run)
    {
        ArgumentNullException.ThrowIfNull(run);

        return LineFor(
            run.Skipped,
            run.FilesAttached,
            run.FoldersRefused,
            run.AddressRefusals,
            run.EntriesLeftUnderAnotherRoot,
            run.Outcome == ReflectOwnedRunOutcome.Cancelled,
            run.RootsCouldNotBeRead);
    }

    // Read by the entity's own enqueued run and by a selection's linking step alike, so a selection
    // cannot report a run in different words from a click.
    // A run that reached the instance for nothing leads with why instead of its counts: two zeros
    // read as a clean pass over every folder.
    internal static string LineFor(
        ReflectOwnedSkipReason? skipped,
        int filesAttached,
        int foldersRefused,
        IReadOnlyList<FolderAddressRefusal>? unaddressed,
        int leftUnderAnotherRoot,
        bool cancelled,
        bool rootsCouldNotBeRead = false)
    {
        if (skipped is { } reason)
        {
            return SentenceFor(reason);
        }

        if (rootsCouldNotBeRead)
        {
            return NoRootToCompareSentence;
        }

        var reasons = string.Join(
            ' ', (unaddressed ?? []).Select(refusal => SentenceFor(refusal, filesAttached > 0)));
        if (leftUnderAnotherRoot > 0)
        {
            reasons = reasons.Length == 0
                ? LeftUnderAnotherRootSentence
                : reasons + " " + LeftUnderAnotherRootSentence;
        }

        if (filesAttached == 0 && foldersRefused == 0 && reasons.Length > 0)
        {
            return cancelled ? reasons + " The run was then stopped." : reasons;
        }

        var ending = cancelled ? ", then stopped" : string.Empty;
        var counts = string.Create(
            CultureInfo.InvariantCulture,
            $"{filesAttached:N0} linked, {foldersRefused:N0} refused{ending}.");

        return reasons.Length == 0 ? counts : counts + " " + reasons;
    }

    internal static string SentenceFor(ReflectOwnedSkipReason reason)
        => reason switch
        {
            ReflectOwnedSkipReason.HardLinksOff
                => "No files were linked: Whisparr's hard-link setting is off.",
            ReflectOwnedSkipReason.HardLinkSettingUnreadable
                => "No files were linked: Whisparr's hard-link setting could not be read.",
            _ => throw new ArgumentOutOfRangeException(
                nameof(reason),
                reason,
                "This skip reason has no sentence written down for it."),
        };

    // One library root, named once, with at most one path tried under it. The roots are few and
    // operator-created; the folders under them grow with the library.
    //
    // A refusal naming no root speaks for the whole run only where the run linked nothing. Beside a
    // non-zero count it has to be scoped to the folders it covers, or it contradicts the figure in
    // front of it: a library run links most of its folders and still carries one of these for every
    // root it could not address.
    internal static string SentenceFor(FolderAddressRefusal refusal, bool anythingLinked)
    {
        ArgumentNullException.ThrowIfNull(refusal);

        if (!string.IsNullOrWhiteSpace(refusal.CoveRoot))
        {
            return "Nothing under " + refusal.CoveRoot + " could be linked: "
                + Because(refusal.Refusal, refusal.Tried.Count > 0 ? refusal.Tried[0] : null);
        }

        var opening = anythingLinked ? "Some folders were not linked" : "Nothing could be linked";

        return opening + ": "
            + Because(refusal.Refusal, refusal.Tried.Count > 0 ? refusal.Tried[0] : null);
    }

    private static string Because(FolderAgreementRefusal refusal, string? tried)
        => refusal switch
        {
            FolderAgreementRefusal.NothingResolved => tried is null
                ? "Whisparr holds nothing at the paths it was asked about."
                : "Whisparr holds nothing at " + tried + ".",
            FolderAgreementRefusal.MoreThanOneResolved => tried is null
                ? "Whisparr holds a file of that size at more than one of the paths asked about."
                : "Whisparr holds a file of that size at more than one of the paths asked about, "
                    + "including " + tried + ".",
            FolderAgreementRefusal.InstanceDeclaresNoRoot
                => "Whisparr declares no root folder to build a path under.",
            FolderAgreementRefusal.InstanceCannotBeAsked
                => "Whisparr could not be asked what it holds.",
            FolderAgreementRefusal.NoFileToProbeWith
                => "Cove holds no file under it to establish Whisparr's spelling of it from.",
            FolderAgreementRefusal.ProbeCouldNotBeRead => tried is null
                ? "Whisparr's answer could not be read."
                : "Whisparr's answer about " + tried + " could not be read.",
            FolderAgreementRefusal.FolderUnderNoLibraryRoot
                => "Cove holds these folders under none of its library paths.",
            _ => throw new ArgumentOutOfRangeException(
                nameof(refusal),
                refusal,
                "This refusal has no sentence written down for it."),
        };

    // Stands for a run that reached no folder for any reason. A reason a reader can act on rides on
    // the record rather than on this instance.
    internal static ReflectOwnedRun Untaken { get; } =
        new(ReflectOwnedRunOutcome.Completed, 0, 0);

    private static string? Read(IReadOnlyDictionary<string, string> parameters, string key)
        => parameters.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;
}
