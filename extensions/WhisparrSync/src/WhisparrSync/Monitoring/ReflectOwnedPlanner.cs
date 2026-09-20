using System.Text.Json;
using System.Text.Json.Nodes;
using WhisparrSync.Addressing;
using WhisparrSync.Contracts;
using WhisparrSync.Import;

namespace WhisparrSync.Monitoring;

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

// Skipped is null both for a run that ran and for a run stopped for a cause other than the
// instance's linking setting, because no setting was read on that path.
//
// FoldersNotAddressed and EntriesLeftUnderAnotherRoot are apart from FoldersRefused on purpose:
// the instance declined nothing and was never asked about those folders or files.
//
// AddressRefusals carries one line per library root rather than per folder, which would grow with
// the entity and record filesystem paths nothing needs. AddressedRoots is carried beside the
// counts because a root that agreed is what clears that root's stored refusal.
//
// RootsCouldNotBeRead leaves every other count zero: no folder was reached.
internal sealed record ReflectOwnedRun(
    ReflectOwnedRunOutcome Outcome,
    int FoldersAttached,
    int FoldersRefused,
    ReflectOwnedSkipReason? Skipped = null,
    int FoldersNotAddressed = 0,
    IReadOnlyList<FolderAddressRefusal>? AddressRefusals = null,
    IReadOnlyList<string>? AddressedRoots = null,
    int EntriesLeftUnderAnotherRoot = 0,
    bool RootsCouldNotBeRead = false);

// The count travels with the entries because the entries reach the instance through an early
// continue when there are none, and a count carried elsewhere would be lost exactly on the folder
// whose every row was left out.
internal sealed record PlannedFiles(JsonArray? Entries, int LeftUnderAnotherRoot)
{
    internal static PlannedFiles Nothing { get; } = new(null, 0);
}

// Without the decision here every matched file would be copied in full on an instance whose
// hard-link setting is off: the import mode that links is labelled as a copy, and it copies with no
// error and no distinct outcome when it cannot link. Neither generation offers a mode that only
// links.
//
// An unreadable setting answers skipped, not act, which is stricter than the default both builds
// ship. Acting on a setting nobody read is how a full copy of every matched file happens silently.
//
// A file's quality and languages are copied from the parse route's rows, never composed. The submit
// path refuses a row missing either, and an unmatched row carries no matched member at all, so
// exclusion is on absence.
//
// Nothing outlives one folder's command, so nothing grows with the library and nothing is persisted.
internal static class ReflectOwnedPlanner
{
    internal const string CommandName = "ManualImport";

    // The import mode that links when it can. The only other mode moves the file out of the
    // library, and is never composed.
    internal const string ImportMode = "copy";

    internal const string HardLinkSetting = "copyUsingHardlinks";

    private const string V3MatchedMember = "movie";

    private const string V2MatchedMember = "series";

    internal static ReflectOwnedDecision Decide(string? mediaManagement)
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

    // The linking import mode copies the whole file whenever source and destination are not on one
    // filesystem, whatever the hard-link setting says, and reports it as a successful import with
    // no distinct outcome. Nothing downstream can tell a link from terabytes of copied bytes.
    //
    // Compared by declared root rather than by device, a conservative stand-in: two roots on one
    // device cost a link that would have been safe, and no arrangement costs data. Where either
    // path sits under no declared root there is no comparison to make and the entry stays. An
    // instance whose root list could not be read never reaches here; the run stops first.
    internal static PlannedFiles Files(
        WhisparrGeneration generation, string? importable, IReadOnlyList<string> instanceRoots)
    {
        ArgumentNullException.ThrowIfNull(instanceRoots);

        if (AsArray(importable) is not { } rows)
        {
            return PlannedFiles.Nothing;
        }

        var files = new JsonArray();
        var leftUnderAnotherRoot = 0;
        foreach (var row in rows.OfType<JsonObject>())
        {
            if (Entry(generation, row) is not { } entry)
            {
                continue;
            }

            if (UnderDifferentRoots(generation, row, instanceRoots))
            {
                leftUnderAnotherRoot++;
                continue;
            }

            files.Add(entry);
        }

        return new PlannedFiles(files.Count == 0 ? null : files, leftUnderAnotherRoot);
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
    internal static async Task<ReflectOwnedRun> RunAsync(
        WhisparrGeneration generation,
        IReadOnlyList<string> instanceRoots,
        IAsyncEnumerable<string> folders,
        Func<string, CancellationToken, Task<AddressedFolder>> address,
        Func<string, CancellationToken, Task<ImportableListing>> readImportable,
        Func<JsonArray, CancellationToken, Task<bool>> attach,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(instanceRoots);
        ArgumentNullException.ThrowIfNull(folders);
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(readImportable);
        ArgumentNullException.ThrowIfNull(attach);

        var attached = 0;
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
                    if (addressed.Refusal is { } reason)
                    {
                        refusalByRoot.TryAdd(
                            addressed.CoveRoot,
                            new FolderAddressRefusal(addressed.CoveRoot, reason, addressed.Tried));
                    }

                    continue;
                }

                addressedRoots.Add(addressed.CoveRoot);

                var listing = await readImportable(onInstance, ct).ConfigureAwait(false);
                if (listing.WasRefused)
                {
                    refused++;
                    continue;
                }

                var planned = Files(generation, listing.Rows, instanceRoots);
                leftUnderAnotherRoot += planned.LeftUnderAnotherRoot;
                if (planned.Entries is not { } files)
                {
                    continue;
                }

                if (await attach(files, ct).ConfigureAwait(false))
                {
                    attached++;
                }
                else
                {
                    refused++;
                }
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
                RootsCouldNotBeRead: false);
    }

    // The most specific containing root answers for each path. Roots nest, and an instance
    // declaring a parent and two children under it is the arrangement this guard exists for:
    // taking the first declared root would answer the parent for both paths and see one root where
    // there are two.
    private static bool UnderDifferentRoots(
        WhisparrGeneration generation, JsonObject row, IReadOnlyList<string> instanceRoots)
    {
        var file = RootOf(Text(row, "path"), instanceRoots);
        var site = RootOf(Text(row[MatchedMember(generation)] as JsonObject, "path"), instanceRoots);

        return file is not null
            && site is not null
            && !string.Equals(file, site, StringComparison.OrdinalIgnoreCase);
    }

    private static string? RootOf(string? path, IReadOnlyList<string> roots)
        => path is null
            ? null
            : roots
                .Where(root => PathCandidateGuard.TailBelow(path, root) is not null)
                .OrderByDescending(root => PathCandidateGuard.Normalize(root).Length)
                .FirstOrDefault();

    private static string MatchedMember(WhisparrGeneration generation)
        => generation switch
        {
            WhisparrGeneration.V3 => V3MatchedMember,
            WhisparrGeneration.V2 => V2MatchedMember,
            _ => throw new ArgumentOutOfRangeException(nameof(generation)),
        };

    private static string? Text(JsonObject? owner, string member)
        => owner?[member] is JsonValue value
            && value.TryGetValue<string>(out var text)
            && !string.IsNullOrWhiteSpace(text)
                ? text
                : null;

    // Both spellings are transcribed from the interface bundle each build ships. v3 names one scene;
    // v2 names a series and the episodes matched inside it.
    private static JsonObject? Entry(WhisparrGeneration generation, JsonObject row)
    {
        if (row["quality"] is not JsonObject quality
            || row["languages"] is not JsonArray languages
            || Text(row, "path") is null)
        {
            return null;
        }

        var entry = new JsonObject
        {
            ["path"] = row["path"]?.DeepClone(),
            ["folderName"] = row["folderName"]?.DeepClone(),
            ["releaseGroup"] = row["releaseGroup"]?.DeepClone(),
            ["quality"] = quality.DeepClone(),
            ["languages"] = languages.DeepClone(),
            ["indexerFlags"] = row["indexerFlags"]?.DeepClone(),
            ["downloadId"] = row["downloadId"]?.DeepClone(),
        };

        switch (generation)
        {
            case WhisparrGeneration.V3:
                if (MatchedId(row, V3MatchedMember) is not { } movieId)
                {
                    return null;
                }

                entry["movieId"] = movieId;
                entry["movieFileId"] = row["movieFileId"]?.DeepClone();
                return entry;

            case WhisparrGeneration.V2:
                if (MatchedId(row, V2MatchedMember) is not { } seriesId
                    || row["episodes"] is not JsonArray episodes)
                {
                    return null;
                }

                var episodeIds = new JsonArray();
                foreach (var episode in episodes.OfType<JsonObject>())
                {
                    if (episode["id"] is JsonValue named && named.TryGetValue<int>(out var episodeId))
                    {
                        episodeIds.Add(episodeId);
                    }
                }

                if (episodeIds.Count == 0)
                {
                    return null;
                }

                entry["seriesId"] = seriesId;
                entry["episodeIds"] = episodeIds;
                entry["episodeFileId"] = row["episodeFileId"]?.DeepClone();
                return entry;

            default:
                throw new ArgumentOutOfRangeException(nameof(generation));
        }
    }

    private static int? MatchedId(JsonObject row, string member)
        => row[member] is JsonObject matched
            && matched["id"] is JsonValue named
            && named.TryGetValue<int>(out var id)
            && id > 0
                ? id
                : null;

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
