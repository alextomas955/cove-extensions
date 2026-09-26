using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using WhisparrSync.Addressing;
using WhisparrSync.Contracts;
using WhisparrSync.Import;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Monitoring;

/// <summary>One entry the run registered: the instance's own id for it, and the path it gave it.</summary>
/// <remarks>
/// The path is carried so a file can be compared against where its entry will be written before
/// anything is sent. A null path is one the instance answered without one.
/// </remarks>
internal sealed record RegisteredScene(int EntityId, string? Path);

internal sealed record ReflectOwnedDecision(bool Act, ReflectOwnedSkipReason? Reason)
{
    internal static ReflectOwnedDecision Acting { get; } = new(true, null);

    internal static ReflectOwnedDecision Skipped(ReflectOwnedSkipReason reason) => new(false, reason);
}

internal enum ReflectOwnedRunOutcome
{
    Completed,

    // Cancelled part-way. What was attached before the stop stays attached.
    Cancelled,
}

// A refused read and a folder holding nothing importable are different facts and must not travel
// as one absent value. Reporting the first as the second leaves the run's line describing a clean
// pass over a folder it never read.
internal readonly record struct ImportableListing(string? Rows, bool WasRefused)
{
    internal static ImportableListing Refused { get; } = new(null, true);

    internal static ImportableListing Listed(string? rows) => new(rows, false);
}

// One line per library root, named once however many folders sit under it.
internal sealed record FolderAddressRefusal(
    string CoveRoot, FolderAgreementRefusal Refusal, IReadOnlyList<string> Tried);

// Skipped is null for a run that ran and for one stopped by anything but the linking setting,
// no setting being read on that path. FoldersNotAddressed and EntriesLeftUnderAnotherRoot sit apart
// from FoldersRefused because the instance declined nothing and was never asked. AddressRefusals is
// one line per library root, not per folder, which would grow with the entity and record paths
// nothing needs; AddressedRoots rides beside it because a root that agreed clears its stored
// refusal. RootsCouldNotBeRead leaves every other count zero: no folder was reached.
internal sealed record ReflectOwnedRun(
    ReflectOwnedRunOutcome Outcome,
    int FoldersAttached,
    int FoldersRefused,
    ReflectOwnedSkipReason? Skipped = null,
    int FoldersNotAddressed = 0,
    IReadOnlyList<FolderAddressRefusal>? AddressRefusals = null,
    IReadOnlyList<string>? AddressedRoots = null,
    int EntriesLeftUnderAnotherRoot = 0,
    bool RootsCouldNotBeRead = false,
    // The figure a reader compares against the registered count, which is scenes. A folder count
    // beside it reads as files and understates a run by orders of magnitude.
    int FilesAttached = 0,
    // Files the instance listed that no entry could be addressed to. Counted rather than dropped:
    // a run that attached none of a folder's files and reported only a zero reads as a folder
    // holding nothing importable.
    int FilesWithoutAnEntry = 0,
    // Names in an entity's own folder that this product did not put there, which it therefore
    // leaves alone. A count rather than a list: a reader is told the folder holds them and the log
    // carries the rest.
    int NamesNotComposedHere = 0,
    // Names this product wrote and has taken back, because no library file answers to them any
    // more. Each one freed a file's bytes, so a reader is told the figure whatever else the run did.
    int LinksRemoved = 0,
    // Names whose file changed too recently to decide about, left where they are for the next run.
    int LinksWaiting = 0,
    // Entities this run gave a folder of its own and registered there, which is the figure the
    // phase's whole point is read off: an entity with a folder is one the instance accepted at a
    // path of this product's choosing rather than one of the reader's.
    int EntitiesGivenAFolder = 0,
    // Second names made, and second names already there from an earlier run. Apart, because the
    // first is what this run did and the second is what it found done.
    int LinksMade = 0,
    int LinksAlreadyThere = 0,
    // Files the folder for their entity is not on the drive of. Nothing is copied for one, so a
    // reader is told rather than left with a file the instance never records.
    int LinksOnAnotherDevice = 0,
    // Library roots no folder could be built under. One line per root, not per entity: a root that
    // cannot be written under refuses every entity beneath it, and a line per entity would grow
    // with the library.
    IReadOnlyList<string>? RootsWithNoTree = null)
{
    // Folds one folder's run into the total a library-wide walk carries. The counts add; the two
    // lists are unioned, because each names a library root and an operator creates those by hand,
    // so neither grows with the library. A cancelled folder makes the whole total cancelled.
    internal ReflectOwnedRun Plus(ReflectOwnedRun other)
    {
        ArgumentNullException.ThrowIfNull(other);

        return new ReflectOwnedRun(
            other.Outcome is ReflectOwnedRunOutcome.Cancelled ? other.Outcome : Outcome,
            FoldersAttached + other.FoldersAttached,
            FoldersRefused + other.FoldersRefused,
            Skipped ?? other.Skipped,
            FoldersNotAddressed + other.FoldersNotAddressed,
            Union(AddressRefusals, other.AddressRefusals, refusal => refusal.CoveRoot),
            Union(AddressedRoots, other.AddressedRoots, root => root),
            EntriesLeftUnderAnotherRoot + other.EntriesLeftUnderAnotherRoot,
            RootsCouldNotBeRead || other.RootsCouldNotBeRead,
            FilesAttached + other.FilesAttached,
            FilesWithoutAnEntry + other.FilesWithoutAnEntry,
            NamesNotComposedHere + other.NamesNotComposedHere,
            LinksRemoved + other.LinksRemoved,
            LinksWaiting + other.LinksWaiting,
            EntitiesGivenAFolder + other.EntitiesGivenAFolder,
            LinksMade + other.LinksMade,
            LinksAlreadyThere + other.LinksAlreadyThere,
            LinksOnAnotherDevice + other.LinksOnAnotherDevice,
            Union(RootsWithNoTree, other.RootsWithNoTree, root => root));
    }

    private static IReadOnlyList<T>? Union<T>(
        IReadOnlyList<T>? held, IReadOnlyList<T>? arrived, Func<T, string> keyed)
    {
        if (held is null || held.Count == 0)
        {
            return arrived;
        }

        if (arrived is null || arrived.Count == 0)
        {
            return held;
        }

        var byKey = new Dictionary<string, T>(StringComparer.Ordinal);
        foreach (var entry in held.Concat(arrived))
        {
            byKey.TryAdd(keyed(entry), entry);
        }

        return [.. byKey.Values];
    }
}

// Both counts travel with the entries because the entries reach the instance through an early
// continue when there are none, and a count carried elsewhere would be lost exactly on the folder
// whose every row was left out. WithoutAnEntry is a row naming a file no entry could be addressed
// to, which is a file the instance will not record; reporting it as nothing found would describe
// a folder that holds files as one that holds none.
internal sealed record PlannedFiles(
    JsonArray? Entries, int LeftUnderAnotherRoot, int WithoutAnEntry = 0)
{
    internal static PlannedFiles Nothing { get; } = new(null, 0);
}

// The three requests a reflect-owned run makes per folder, and the optional read that names which
// entry each file in it belongs to.
//
// The addresses arrive a chunk at a time, because the folder they are for holds every file one
// entity owns. A run whose identify step is absent attaches what the instance itself matched, which
// is what a folder the reader arranged answers to.
internal sealed record ReflectOwnedSteps(
    Func<string, CancellationToken, Task<AddressedFolder>> Address,
    Func<string, CancellationToken, Task<ImportableListing>> ReadImportable,
    Func<JsonArray, CancellationToken, Task<bool>> Attach,
    Func<string, CancellationToken,
        IAsyncEnumerable<IReadOnlyDictionary<string, EntryAddress>>>? Identify = null);

// Without the hard-link decision every matched file would be copied in full: the import mode that
// links is labelled a copy, copies when it cannot link, and reports no distinct outcome for it.
// Neither generation offers a mode that only links. An unreadable setting therefore answers skipped,
// not act, which is stricter than either build's default; acting on a setting nobody read is how a
// silent full copy happens.
//
// Quality and languages are copied from the parse route's rows, never composed: the submit path
// refuses a row missing either, and an unmatched row carries no matched member, so exclusion is on
// absence. Nothing outlives one folder's command.
internal static class ReflectOwnedPlanner
{
    internal const string CommandName = "ManualImport";

    // How many of a folder's files one import carries. The reads that compose them cost about a
    // second each, and they are held in memory until the import goes, so a folder of a few thousand
    // would read for half an hour, show nothing while it did, and lose all of it on a stop.
    internal const int FilesAttachedAtOnce = 100;

    // The import mode that links when it can. The only other mode moves the file out of the
    // library, and is never composed.
    internal const string ImportMode = "copy";

    internal const string HardLinkSetting = "copyUsingHardlinks";

    // Two readings, and the linking one answers first, so an instance failing both reports what it
    // reported before the naming one existed rather than a reason that changed under a reader.
    internal static ReflectOwnedDecision Decide(
        WhisparrGeneration generation, string? mediaManagement, string? naming)
    {
        var linking = LinksIntoPlace(mediaManagement);

        return linking.Act ? LeavesNamesAlone(generation, naming) : linking;
    }

    // Whether the naming body is worth a request at all. The linking reading answers first, so a
    // refusal it already reached costs no second read.
    internal static bool NeedsTheNamingReading(string? mediaManagement)
        => LinksIntoPlace(mediaManagement).Act;

    private static ReflectOwnedDecision LinksIntoPlace(string? mediaManagement)
    {
        if (MonitoringProjector.AsObject(mediaManagement) is not { } settings
            || settings[HardLinkSetting] is not JsonValue setting
            || !setting.TryGetValue<bool>(out var linksIntoPlace))
        {
            return ReflectOwnedDecision.Skipped(ReflectOwnedSkipReason.HardLinkSettingUnreadable);
        }

        return linksIntoPlace
            ? ReflectOwnedDecision.Acting
            : ReflectOwnedDecision.Skipped(ReflectOwnedSkipReason.HardLinksOff);
    }

    // Measured on both builds. With renaming on, one generation's rename command moves every file
    // out of the folder this product named and into a format of its own; the other fails the attach
    // outright, because with a new name to write the import is no longer in place.
    //
    // Every member the generation states has to be read. A body that answered none of them folds to
    // null and refuses, rather than to an off nobody read.
    private static ReflectOwnedDecision LeavesNamesAlone(
        WhisparrGeneration generation, string? naming)
    {
        var settings = MonitoringProjector.AsObject(naming);
        bool? renaming = null;
        foreach (var member in WhisparrInstanceFactory.ReadingFor(generation).RenamingMembers)
        {
            if (settings?[member] is not JsonValue setting
                || !setting.TryGetValue<bool>(out var renames))
            {
                return ReflectOwnedDecision.Skipped(
                    ReflectOwnedSkipReason.RenameSettingUnreadable);
            }

            renaming = (renaming ?? false) || renames;
        }

        return renaming switch
        {
            true => ReflectOwnedDecision.Skipped(ReflectOwnedSkipReason.RenamingOn),
            false => ReflectOwnedDecision.Acting,
            null => ReflectOwnedDecision.Skipped(ReflectOwnedSkipReason.RenameSettingUnreadable),
        };
    }

    // The linking import mode copies the whole file whenever source and destination are not on one
    // filesystem, whatever the hard-link setting says, and reports it as a successful import with
    // no distinct outcome. Nothing downstream can tell a link from terabytes of copied bytes.
    //
    // Compared by declared root rather than by device, a conservative stand-in: two roots on one
    // device cost a link that would have been safe, and no arrangement costs data. Where either
    // path sits under no declared root there is no comparison to make and the entry stays. An
    // instance whose root list could not be read never reaches here; the run stops first.
    internal static PlannedFiles Files(
        WhisparrGeneration generation,
        string? importable,
        IReadOnlyList<string> instanceRoots)
    {
        ArgumentNullException.ThrowIfNull(instanceRoots);

        if (AsArray(importable) is not { } rows)
        {
            return PlannedFiles.Nothing;
        }

        var reading = WhisparrInstanceFactory.ReadingFor(generation);
        var files = new JsonArray();
        var leftUnderAnotherRoot = 0;
        var withoutAnEntry = 0;
        foreach (var row in rows.OfType<JsonObject>())
        {
            if (Entry(reading, row) is not { } entry)
            {
                withoutAnEntry++;
                continue;
            }

            if (UnderDifferentRoots(reading, row, instanceRoots))
            {
                leftUnderAnotherRoot++;
                continue;
            }

            files.Add(entry);
        }

        return new PlannedFiles(
            files.Count == 0 ? null : files, leftUnderAnotherRoot, withoutAnEntry);
    }

    // The entries for the rows one chunk of addresses answers for, handed over a batch at a time.
    // The folder these rows come from holds every file one entity owns, and an entity reaches the
    // size of the library, so neither the composing nor the command it feeds may follow it.
    //
    // A row this chunk names no address for is left where it is. The instance reads a studio and a
    // date out of a file name and a link is named for the identity of the file it points at, so
    // attaching one by what the instance parsed out of that name attaches the file to a guess.
    internal static IEnumerable<PlannedFiles> AddressedFiles(
        WhisparrGeneration generation,
        JsonArray rows,
        IReadOnlyList<string> instanceRoots,
        IReadOnlyDictionary<string, EntryAddress> addressed)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(instanceRoots);
        ArgumentNullException.ThrowIfNull(addressed);

        return Addressing(generation, rows, instanceRoots, addressed);
    }

    private static IEnumerable<PlannedFiles> Addressing(
        WhisparrGeneration generation,
        JsonArray rows,
        IReadOnlyList<string> instanceRoots,
        IReadOnlyDictionary<string, EntryAddress> addressed)
    {
        var reading = WhisparrInstanceFactory.ReadingFor(generation);
        var files = new JsonArray();
        var leftUnderAnotherRoot = 0;
        foreach (var row in rows.OfType<JsonObject>())
        {
            if (AddressedEntry(reading, row, addressed) is not { } entry)
            {
                continue;
            }

            if (UnderDifferentRoots(reading, row, instanceRoots))
            {
                leftUnderAnotherRoot++;
                continue;
            }

            files.Add(entry);
            if (files.Count < FilesAttachedAtOnce)
            {
                continue;
            }

            yield return new PlannedFiles(files, leftUnderAnotherRoot);
            files = [];
            leftUnderAnotherRoot = 0;
        }

        if (files.Count > 0 || leftUnderAnotherRoot > 0)
        {
            yield return new PlannedFiles(files.Count == 0 ? null : files, leftUnderAnotherRoot);
        }
    }

    // One folder's files, composed from what the library owns rather than from a listing of the
    // directory. The instance answers a folder listing only once it has walked and parsed every
    // entry in it, which on a directory of a few hundred files takes minutes and answers about files
    // the reader does not own; this asks about the files the reader does own and nothing else.
    //
    // The quality and the languages are the instance's own reading of each file, never composed
    // here. A file it reads no quality for is left out rather than sent with one nobody stated.
    internal static IAsyncEnumerable<PlannedFiles> ComposedFilesAsync(
        string instanceFolder,
        IReadOnlyDictionary<string, RegisteredScene> identified,
        IReadOnlyList<string> instanceRoots,
        Func<OwnedFilePlacement, CancellationToken, Task<WhisparrResponse?>> readFile,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceFolder);
        ArgumentNullException.ThrowIfNull(identified);
        ArgumentNullException.ThrowIfNull(instanceRoots);
        ArgumentNullException.ThrowIfNull(readFile);

        return ComposingAsync(instanceFolder, identified, instanceRoots, readFile, ct);
    }

    private static async IAsyncEnumerable<PlannedFiles> ComposingAsync(
        string instanceFolder,
        IReadOnlyDictionary<string, RegisteredScene> identified,
        IReadOnlyList<string> instanceRoots,
        Func<OwnedFilePlacement, CancellationToken, Task<WhisparrResponse?>> readFile,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (identified.Count == 0)
        {
            yield break;
        }

        var folderName = FolderNameOf(instanceFolder);
        var files = new JsonArray();
        var leftUnderAnotherRoot = 0;
        foreach (var (fileName, scene) in identified)
        {
            ct.ThrowIfCancellationRequested();

            var path = PathCandidateGuard.CandidateUnder(instanceFolder, fileName);
            if (path is null)
            {
                continue;
            }

            // The linking import mode copies the whole file whenever the file and the entry it is
            // written to are not on one filesystem, whatever the hard-link setting says, and reports
            // it as a successful import. Compared by declared root, the same conservative stand-in
            // the folder-listing path uses.
            if (UnderDifferentRoots(path, scene.Path, instanceRoots))
            {
                leftUnderAnotherRoot++;
                continue;
            }

            var read = await readFile(new OwnedFilePlacement(path, scene.EntityId), ct)
                .ConfigureAwait(false);
            if (ReadingOf(read?.Body) is not { } reading)
            {
                continue;
            }

            files.Add(new JsonObject
            {
                ["path"] = path,
                ["folderName"] = folderName,
                ["quality"] = reading.Quality.DeepClone(),
                ["languages"] = reading.Languages.DeepClone(),
                ["releaseGroup"] = string.Empty,
                ["indexerFlags"] = 0,
                ["movieId"] = scene.EntityId,
            });

            if (files.Count < FilesAttachedAtOnce)
            {
                continue;
            }

            yield return new PlannedFiles(files, leftUnderAnotherRoot);
            files = [];
            leftUnderAnotherRoot = 0;
        }

        if (files.Count > 0 || leftUnderAnotherRoot > 0)
        {
            yield return new PlannedFiles(files.Count == 0 ? null : files, leftUnderAnotherRoot);
        }
    }

    // What the instance read for the file it was asked about, or null where it read no quality. The
    // quality is required by the submit, and the instance answers the unknown one back unchanged
    // where it could read none, so an unknown answer is not a reading.
    private static (JsonObject Quality, JsonArray Languages)? ReadingOf(string? body)
    {
        if (AsArray(body) is not { } rows
            || rows.OfType<JsonObject>().FirstOrDefault() is not { } row
            || row["quality"] is not JsonObject quality
            || QualityIdIn(quality) is null or V3BodyProjector.UnknownQualityId)
        {
            return null;
        }

        return (quality, row["languages"] as JsonArray ?? []);
    }

    private static int? QualityIdIn(JsonObject quality)
        => quality["quality"] is JsonObject named && named["id"] is JsonValue id
            && id.TryGetValue<int>(out var value)
                ? value
                : null;

    private static string FolderNameOf(string instanceFolder)
    {
        var trimmed = instanceFolder.TrimEnd('/');
        var cut = trimmed.LastIndexOf('/');
        return cut < 0 ? trimmed : trimmed[(cut + 1)..];
    }

    internal static JsonObject Command(JsonNode files)
    {
        ArgumentNullException.ThrowIfNull(files);
        return new JsonObject
        {
            ["name"] = CommandName,
            ["files"] = files,
            ["importMode"] = ImportMode,
        };
    }

    // A cancellation classifies the run as cancelled, never failed, and what was attached stays
    // attached. A folder whose listing was refused counts as refused; a folder listing nothing
    // attachable counts as neither, because that is a complete answer about the folder.
    //
    // The path handed to the read is always the instance's own. A folder with none is not read at
    // all: the library's spelling would produce a legitimately empty listing, and the run would
    // report a clean zero over a folder nothing looked in.
    // One line per library root, keyed by the root: every folder under one root reaches the same
    // reason, so a second folder there adds nothing a reader has not been told.
    private static void NoteUnaddressed(
        Dictionary<string, FolderAddressRefusal> refusalByRoot, AddressedFolder addressed)
    {
        if (addressed.Refusal is { } reason)
        {
            refusalByRoot.TryAdd(
                addressed.CoveRoot,
                new FolderAddressRefusal(addressed.CoveRoot, reason, addressed.Tried));
        }
    }

    // What one folder came to. Attached and refused are apart because a folder that sent nothing is
    // neither; the figures beside them add across the walk.
    private readonly record struct ReflectedFolder(
        bool Attached,
        bool Refused,
        int FilesAttached,
        int LeftUnderAnotherRoot,
        int WithoutAnEntry);

    // A folder the reader arranged. What each file belongs to is the instance's own match, and one
    // command carries the folder.
    private static async Task<ReflectedFolder> MatchedAsync(
        WhisparrGeneration generation,
        string? rows,
        IReadOnlyList<string> instanceRoots,
        Func<JsonArray, CancellationToken, Task<bool>> attach,
        CancellationToken ct)
    {
        var planned = Files(generation, rows, instanceRoots);
        if (planned.Entries is not { } files)
        {
            return new ReflectedFolder(
                false, false, 0, planned.LeftUnderAnotherRoot, planned.WithoutAnEntry);
        }

        var sent = await attach(files, ct).ConfigureAwait(false);

        return new ReflectedFolder(
            sent,
            !sent,
            sent ? files.Count : 0,
            planned.LeftUnderAnotherRoot,
            planned.WithoutAnEntry);
    }

    // An entity's own folder. Its addresses arrive a chunk at a time and each chunk's entries go as
    // they are composed, so neither what is held nor what one command carries follows the entity.
    //
    // Counters over the chunks, never a record of which rows a chunk answered for. A row no chunk
    // addressed is a file no entry could be found for, which is the subtraction at the end; a name
    // two identifier spellings both reach is composed under each, so it is floored rather than
    // allowed to run negative.
    private static async Task<ReflectedFolder> AddressedAsync(
        WhisparrGeneration generation,
        string? listing,
        IReadOnlyList<string> instanceRoots,
        IAsyncEnumerable<IReadOnlyDictionary<string, EntryAddress>> addressing,
        Func<JsonArray, CancellationToken, Task<bool>> attach,
        CancellationToken ct)
    {
        if (AsArray(listing) is not { } rows)
        {
            return default;
        }

        var listed = rows.OfType<JsonObject>().Count();
        var composed = 0;
        var filesAttached = 0;
        var left = 0;
        var anyAttached = false;
        var anyRefused = false;

        await foreach (var addressed in addressing.WithCancellation(ct).ConfigureAwait(false))
        {
            foreach (var planned in AddressedFiles(generation, rows, instanceRoots, addressed))
            {
                left += planned.LeftUnderAnotherRoot;
                if (planned.Entries is not { } files)
                {
                    continue;
                }

                composed += files.Count;
                if (await attach(files, ct).ConfigureAwait(false))
                {
                    anyAttached = true;
                    filesAttached += files.Count;
                }
                else
                {
                    anyRefused = true;
                }
            }
        }

        return new ReflectedFolder(
            anyAttached,
            !anyAttached && anyRefused,
            filesAttached,
            left,
            Math.Max(0, listed - composed - left));
    }

    internal static async Task<ReflectOwnedRun> RunAsync(
        WhisparrGeneration generation,
        IReadOnlyList<string> instanceRoots,
        IAsyncEnumerable<string> folders,
        ReflectOwnedSteps steps,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(instanceRoots);
        ArgumentNullException.ThrowIfNull(folders);
        ArgumentNullException.ThrowIfNull(steps);
        var (address, readImportable, attach, identify) = steps;
        ArgumentNullException.ThrowIfNull(readImportable);
        ArgumentNullException.ThrowIfNull(attach);

        var attached = 0;
        var filesAttached = 0;
        var withoutAnEntry = 0;
        var refused = 0;
        var unaddressed = 0;
        var leftUnderAnotherRoot = 0;
        var refusalByRoot = new Dictionary<string, FolderAddressRefusal>(StringComparer.Ordinal);
        var addressedRoots = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            ct.ThrowIfCancellationRequested();
            await foreach (var folder in folders.WithCancellation(ct).ConfigureAwait(false))
            {
                ct.ThrowIfCancellationRequested();

                var addressed = await address(folder, ct).ConfigureAwait(false);
                if (addressed.InstancePath is not { } onInstance)
                {
                    unaddressed++;
                    NoteUnaddressed(refusalByRoot, addressed);
                    continue;
                }

                addressedRoots.Add(addressed.CoveRoot);

                var listing = await readImportable(onInstance, ct).ConfigureAwait(false);
                if (listing.WasRefused)
                {
                    refused++;
                    continue;
                }

                var reflected = identify is null
                    ? await MatchedAsync(generation, listing.Rows, instanceRoots, attach, ct)
                        .ConfigureAwait(false)
                    : await AddressedAsync(
                        generation, listing.Rows, instanceRoots, identify(folder, ct), attach, ct)
                        .ConfigureAwait(false);

                attached += reflected.Attached ? 1 : 0;
                refused += reflected.Refused ? 1 : 0;
                filesAttached += reflected.FilesAttached;
                leftUnderAnotherRoot += reflected.LeftUnderAnotherRoot;
                withoutAnEntry += reflected.WithoutAnEntry;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return Ended(ReflectOwnedRunOutcome.Cancelled);
        }

        return Ended(ReflectOwnedRunOutcome.Completed);

        ReflectOwnedRun Ended(ReflectOwnedRunOutcome outcome)
            => new(
                outcome,
                attached,
                refused,
                null,
                unaddressed,
                [.. refusalByRoot.Values],
                [.. addressedRoots],
                leftUnderAnotherRoot,
                RootsCouldNotBeRead: false,
                FilesAttached: filesAttached,
                FilesWithoutAnEntry: withoutAnEntry);
    }

    // The most specific containing root answers for each path. Roots nest, and an instance
    // declaring a parent and two children under it is the arrangement this guard exists for:
    // taking the first declared root would answer the parent for both paths and see one root where
    // there are two.
    private static bool UnderDifferentRoots(
        IWhisparrPayloadReading reading, JsonObject row, IReadOnlyList<string> instanceRoots)
        => UnderDifferentRoots(
            Text(row, "path"),
            Text(row[reading.MatchedMember] as JsonObject, "path"),
            instanceRoots);

    private static bool UnderDifferentRoots(
        string? filePath, string? entryPath, IReadOnlyList<string> instanceRoots)
    {
        var file = RootOf(filePath, instanceRoots);
        var entry = RootOf(entryPath, instanceRoots);

        return file is not null
            && entry is not null
            && !string.Equals(file, entry, StringComparison.OrdinalIgnoreCase);
    }

    private static string? RootOf(string? path, IReadOnlyList<string> roots)
        => path is null
            ? null
            : roots
                .Where(root => PathCandidateGuard.TailBelow(path, root) is not null)
                .OrderByDescending(root => PathCandidateGuard.Normalize(root).Length)
                .FirstOrDefault();

    private static string? Text(JsonObject? owner, string member)
        => owner?[member] is JsonValue value
            && value.TryGetValue<string>(out var text)
            && !string.IsNullOrWhiteSpace(text)
                ? text
                : null;

    // The members every generation carries are composed here; the matched entity and its file are
    // the reader's, which answers null for a row it can attach nothing from.
    private static JsonObject? Entry(IWhisparrPayloadReading reading, JsonObject row)
        => Composed(row) is { } entry ? reading.MatchedEntry(row, entry) : null;

    // The entry Cove identified rather than the one the instance managed to parse. A generation
    // that attaches by more than one row refuses an address naming only the first, and that refusal
    // leaves the file where it is: a row the instance parsed its own answer for would be that
    // answer, not the reader's.
    private static JsonObject? AddressedEntry(
        IWhisparrPayloadReading reading,
        JsonObject row,
        IReadOnlyDictionary<string, EntryAddress> addressed)
        => PayloadMember.NameIn(row) is { } name
            && addressed.TryGetValue(name, out var address)
            && Composed(row) is { } entry
                ? reading.IdentifiedEntry(entry, address)
                : null;

    private static JsonObject? Composed(JsonObject row)
    {
        if (row["quality"] is not JsonObject quality
            || row["languages"] is not JsonArray languages
            || Text(row, "path") is null)
        {
            return null;
        }

        return new JsonObject
        {
            ["path"] = row["path"]?.DeepClone(),
            ["folderName"] = row["folderName"]?.DeepClone(),
            ["releaseGroup"] = row["releaseGroup"]?.DeepClone(),
            ["quality"] = quality.DeepClone(),
            ["languages"] = languages.DeepClone(),
            ["indexerFlags"] = row["indexerFlags"]?.DeepClone(),
            ["downloadId"] = row["downloadId"]?.DeepClone(),
        };
    }

    private static JsonArray? AsArray(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(body) as JsonArray;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
