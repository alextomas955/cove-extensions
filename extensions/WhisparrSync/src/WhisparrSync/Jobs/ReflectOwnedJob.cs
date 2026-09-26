using System.Globalization;
using System.Text.Json.Nodes;
using Cove.Extensions.Shared;
using Microsoft.Extensions.DependencyInjection;
using WhisparrSync.Addressing;
using WhisparrSync.Contracts;
using WhisparrSync.Import;
using WhisparrSync.Linking;
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
    Func<OwnedFilePlacement, CancellationToken, Task<WhisparrResponse?>>? ReadFile = null,
    Func<WhisparrEntityKind, int, EntityTreeFolder, CancellationToken,
        IAsyncEnumerable<IReadOnlyDictionary<string, EntryAddress>>>? SupplyEntries = null);

/// <summary>One entity's own folder in the tree, and the library root the tree sits under.</summary>
/// <remarks>
/// The root travels with the folder because an entity's links are made from files under that root
/// and nowhere else, so it is the only root a name in this folder can be resolved against.
/// </remarks>
internal sealed record EntityTreeFolder(string CoveRoot, string Folder, string RemoteId);

/// <summary>What one linking run came to, as the sentence describing it is composed from.</summary>
/// <remarks>
/// A single entity's run and a selection's linking step both compose their line from this, so a
/// selection cannot report the same work in different words from a click.
/// </remarks>
internal sealed record LinkedTally(
    ReflectOwnedSkipReason? Skipped,
    int FilesAttached,
    int FoldersRefused,
    IReadOnlyList<FolderAddressRefusal>? Unaddressed,
    int LeftUnderAnotherRoot,
    bool RootsCouldNotBeRead = false,
    int WithoutAnEntry = 0,
    int NamesNotComposedHere = 0,
    int GivenAFolder = 0,
    int Linked = 0,
    int OnAnotherDevice = 0,
    int Removed = 0,
    int Waiting = 0,
    IReadOnlyList<string>? RootsWithNoTree = null)
{
    // A run that did none of these reached the instance for nothing, and its line leads with why
    // instead of a row of zeros.
    internal bool DidNothing
        => FilesAttached == 0
            && FoldersRefused == 0
            && GivenAFolder == 0
            && Linked == 0
            && Removed == 0
            && Waiting == 0;
}

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

    // Names no file: the line is durable and must not grow with the library. Names no system
    // either: a file reaches this where the metadata source numbered its scene and where Whisparr
    // holds a row for it alike, and blaming one of them would send a reader to the wrong settings
    // page half the time. The log carries which of the two it was.
    internal const string WithoutAnEntrySentence =
        "Some files were not linked: the scenes they belong to could not be matched to Whisparr's "
        + "own catalogue rows.";

    // Names no file and no folder: the line is durable and must not grow with the library. It says
    // what was not done rather than asking for anything, because the files are the reader's and
    // this product removes only the names it wrote itself. A download the product could not place
    // in the library appears here on every run, which is how a reader finds out about it.
    internal const string NamesNotComposedHereSentence =
        "Some files in the folders Whisparr was given were not put there by Cove, so they were "
        + "left alone.";

    // Names no file and no drive: the line is durable. It says nothing was copied because a copy is
    // the act a reader would otherwise take for granted, and a copy is a second set of their bytes.
    internal const string OnAnotherDeviceSentence =
        "Some files were not linked: they are not on the drive Cove keeps their entity's folder "
        + "on, and nothing was copied.";

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

        var walk = await FoldersToWalkAsync(services, aimed.Generation, kind, coveId, ct)
            .ConfigureAwait(false);

        // Only a folder in the tree carries names the instance can parse nothing out of, so only
        // there is an entry supplied. A library folder keeps the instance's own reading, which is
        // what its file names were written for.
        var identify = walk.InTree is { } inTree && aimed.SupplyEntries is { } supply
            ? (string _, CancellationToken identifyCt)
                => supply(kind, coveId, inTree, identifyCt)
            : (Func<string, CancellationToken,
                IAsyncEnumerable<IReadOnlyDictionary<string, EntryAddress>>>?)null;

        return await ReflectOwnedPlanner.RunAsync(
            aimed.Generation,
            instanceRoots,
            walk.Folders,
            new ReflectOwnedSteps(aimed.Address, aimed.ReadImportable, aimed.Attach, identify),
            ct).ConfigureAwait(false);
    }

    // A null InTree means no tree folder holds a name for the entity, so its library folders are
    // walked as they were before a tree existed.
    private sealed record FolderWalk(IAsyncEnumerable<string> Folders, EntityTreeFolder? InTree);

    // The entity's own folder where this product has built one, and the library folders its files
    // sit in where it has not. The folder holds links to exactly this entity's files, so the
    // instance is asked to read a directory whose every entry belongs to the entity, rather than a
    // library folder holding whatever else the reader keeps beside them.
    //
    // One probe per configured library root, which an operator creates by hand. The first tree
    // holding a name for the entity answers, because a tree lives on the drive its files live on
    // and an entity's files are linked under one.
    private static async Task<FolderWalk> FoldersToWalkAsync(
        IServiceProvider services,
        WhisparrGeneration generation,
        WhisparrEntityKind kind,
        int coveId,
        CancellationToken ct)
    {
        var folders = services.GetRequiredService<IEntityFolderPort>()
            .FoldersFor(kind, coveId, ct);

        var named = await services.GetRequiredService<IEntityIdentityPort>()
            .ResolveAsync(kind, coveId, generation, ct).ConfigureAwait(false);
        if (named.ForeignId is not { } remoteId)
        {
            return new FolderWalk(folders, null);
        }

        var links = services.GetRequiredService<ITreeLinkPort>();
        foreach (var coveRoot in services.GetRequiredService<ICoveLibraryPort>().LibraryRoots)
        {
            if (TreePathGuard.TreeRootUnder(coveRoot, generation) is { } treeRoot
                && TreePathGuard.EntityFolderIn(treeRoot, remoteId) is { } entityFolder
                && links.NamesIn(entityFolder).Any())
            {
                return new FolderWalk(
                    OnlyAsync(entityFolder),
                    new EntityTreeFolder(coveRoot, entityFolder, remoteId));
            }
        }

        return new FolderWalk(folders, null);
    }

    private static async IAsyncEnumerable<string> OnlyAsync(string folder)
    {
        yield return folder;
        await Task.CompletedTask.ConfigureAwait(false);
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

        return await AttachAsync(
            aimed,
            instanceRoots,
            addressed.CoveRoot,
            onInstance,
            await aimed.Identify(folder, registered, ct).ConfigureAwait(false),
            ct).ConfigureAwait(false);
    }

    // The files of one folder the instance can open, each already paired with the entry it belongs
    // to. What decides which entry a file reaches is settled by the caller: the instance reads a
    // studio and a date out of a file name, so a library whose names it cannot parse gets nothing
    // attached however certainly the library knows which entry the file is.
    internal static async Task<ReflectOwnedRun> AttachAsync(
        ReflectOwnedAiming aimed,
        IReadOnlyList<string> instanceRoots,
        string coveRoot,
        string onInstance,
        IReadOnlyDictionary<string, RegisteredScene> identified,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(aimed);
        ArgumentNullException.ThrowIfNull(instanceRoots);
        ArgumentException.ThrowIfNullOrWhiteSpace(onInstance);
        ArgumentNullException.ThrowIfNull(identified);

        if (aimed.ReadFile is null)
        {
            return Untaken;
        }

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
            AddressedRoots: [coveRoot],
            EntriesLeftUnderAnotherRoot: leftUnderAnotherRoot,
            FilesAttached: filesAttached);
    }


    // Counts, never a list of folders: the line must not grow with the entity, and it would put
    // filesystem paths in a durable place nothing needs them in.
    internal static string SummaryOf(ReflectOwnedRun run)
    {
        ArgumentNullException.ThrowIfNull(run);

        return LineFor(
            new LinkedTally(
                run.Skipped,
                run.FilesAttached,
                run.FoldersRefused,
                run.AddressRefusals,
                run.EntriesLeftUnderAnotherRoot,
                run.RootsCouldNotBeRead,
                run.FilesWithoutAnEntry,
                run.NamesNotComposedHere,
                run.EntitiesGivenAFolder,
                run.LinksMade + run.LinksAlreadyThere,
                run.LinksOnAnotherDevice,
                run.LinksRemoved,
                run.LinksWaiting,
                run.RootsWithNoTree),
            run.Outcome == ReflectOwnedRunOutcome.Cancelled);
    }

    // Read by the entity's own enqueued run and by a selection's linking step alike, so a selection
    // cannot report a run in different words from a click.
    // A run that reached the instance for nothing leads with why instead of its counts: two zeros
    // read as a clean pass over every folder.
    internal static string LineFor(LinkedTally tally, bool cancelled)
    {
        ArgumentNullException.ThrowIfNull(tally);

        if (tally.Skipped is { } reason)
        {
            return SentenceFor(reason);
        }

        if (tally.RootsCouldNotBeRead)
        {
            return NoRootToCompareSentence;
        }

        var reasons = ReasonsIn(tally);
        if (tally.DidNothing && reasons.Length > 0)
        {
            return cancelled ? reasons + " The run was then stopped." : reasons;
        }

        var counts = CountsIn(tally, cancelled);

        return reasons.Length == 0 ? counts : counts + " " + reasons;
    }

    // Linked is a second name in the tree; recorded is Whisparr holding the file against an entry
    // of its own. They are different acts and a run can do either without the other, so they are
    // different figures rather than one word covering both.
    //
    // The recorded pair is always stated, because a run that reached the instance and did nothing
    // is itself a fact a reader acts on. Every other figure is left out where it is zero: a row of
    // zeros says nothing and buries the one figure that is not.
    private static string CountsIn(LinkedTally tally, bool cancelled)
    {
        var counts = Figured(string.Empty, tally.GivenAFolder, "given a folder of their own");
        counts = Figured(counts, tally.Linked, "linked");
        counts += counts.Length == 0 ? string.Empty : ", ";
        counts += string.Create(
            CultureInfo.InvariantCulture,
            $"{tally.FilesAttached:N0} recorded by Whisparr, {tally.FoldersRefused:N0} refused");
        counts = Figured(counts, tally.Removed, "taken back");
        counts = Figured(counts, tally.Waiting, "left until they settle");

        return counts + (cancelled ? ", then stopped." : ".");
    }

    // One paragraph, however many sentences the run carries.
    private static string ReasonsIn(LinkedTally tally)
    {
        var linked = tally.FilesAttached > 0 || tally.Linked > 0;
        var reasons = string.Join(
            ' ', (tally.Unaddressed ?? []).Select(refusal => SentenceFor(refusal, linked)));

        foreach (var root in tally.RootsWithNoTree ?? [])
        {
            reasons = Carrying(reasons, 1, NoTreeSentence(root, linked));
        }

        reasons = Carrying(reasons, tally.LeftUnderAnotherRoot, LeftUnderAnotherRootSentence);
        reasons = Carrying(reasons, tally.OnAnotherDevice, OnAnotherDeviceSentence);
        reasons = Carrying(reasons, tally.WithoutAnEntry, WithoutAnEntrySentence);

        return Carrying(reasons, tally.NamesNotComposedHere, NamesNotComposedHereSentence);
    }

    // One library root, named once. The roots are few and operator-created, and every entity under
    // one that cannot be written inside meets the same refusal.
    //
    // A refusal naming no root speaks for the whole run only where the run linked nothing. Beside a
    // non-zero count it has to be scoped to what it covers, or it contradicts the figure in front
    // of it.
    internal static string NoTreeSentence(string coveRoot, bool anythingLinked)
    {
        if (!string.IsNullOrWhiteSpace(coveRoot))
        {
            return "Nothing under " + coveRoot + " was given a folder of its own: Cove could not "
                + "write inside that library path.";
        }

        return (anythingLinked ? "Some entities were" : "No entity was")
            + " given a folder of its own: Cove could not write inside the library path they sit "
            + "under.";
    }

    // A figure is carried where it is not zero, and reads as one clause among the counts.
    private static string Figured(string counts, int figure, string what)
    {
        if (figure == 0)
        {
            return counts;
        }

        var clause = string.Create(CultureInfo.InvariantCulture, $"{figure:N0} {what}");

        return counts.Length == 0 ? clause : counts + ", " + clause;
    }

    internal static string SentenceFor(ReflectOwnedSkipReason reason)
        => reason switch
        {
            ReflectOwnedSkipReason.HardLinksOff
                => "No files were linked: Whisparr's hard-link setting is off.",
            ReflectOwnedSkipReason.HardLinkSettingUnreadable
                => "No files were linked: Whisparr's hard-link setting could not be read.",
            ReflectOwnedSkipReason.RenamingOn
                => "No files were linked: Whisparr is set to rename files. Turn renaming off in "
                    + "Whisparr's Settings, Media Management.",
            ReflectOwnedSkipReason.RenameSettingUnreadable
                => "No files were linked: Whisparr's rename setting could not be read.",
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

    // A sentence is carried where its count is not zero, and the run's reasons read as one
    // paragraph however many of them there are.
    private static string Carrying(string reasons, int counted, string sentence)
    {
        if (counted == 0)
        {
            return reasons;
        }

        return reasons.Length == 0 ? sentence : reasons + " " + sentence;
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
