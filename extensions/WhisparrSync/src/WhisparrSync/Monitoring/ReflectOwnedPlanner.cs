using System.Text.Json;
using System.Text.Json.Nodes;
using WhisparrSync.Addressing;
using WhisparrSync.Contracts;
using WhisparrSync.Import;

namespace WhisparrSync.Monitoring;

/// <summary>Whether to ask the instance to link files into place, or why not.</summary>
/// <param name="Act">True when the instance links rather than copies.</param>
/// <param name="Reason">Why nothing is asked for, or null when <paramref name="Act"/> is true.</param>
internal sealed record ReflectOwnedDecision(bool Act, ReflectOwnedSkipReason? Reason)
{
    internal static ReflectOwnedDecision Acting { get; } = new(true, null);

    internal static ReflectOwnedDecision Skipped(ReflectOwnedSkipReason reason) => new(false, reason);
}

/// <summary>How a run over an entity's folders ended.</summary>
internal enum ReflectOwnedRunOutcome
{
    /// <summary>Every folder was read.</summary>
    Completed,

    /// <summary>The run was cancelled part-way. What was attached before that stays attached.</summary>
    Cancelled,
}

/// <summary>What one folder's importable listing answered.</summary>
/// <remarks>
/// A refused read and a folder holding nothing importable are different facts and must not travel
/// as one absent value. The first is a folder nothing was learned about, and reporting it as the
/// second leaves the run's own line describing a clean pass over a folder it never read.
/// </remarks>
/// <param name="Rows">What the instance listed, or null where nothing readable came back.</param>
/// <param name="WasRefused">True where no answer about the folder arrived at all.</param>
internal readonly record struct ImportableListing(string? Rows, bool WasRefused)
{
    /// <summary>No answer about the folder arrived.</summary>
    internal static ImportableListing Refused { get; } = new(null, true);

    /// <summary>The instance's own answer about the folder, whatever it listed.</summary>
    internal static ImportableListing Listed(string? rows) => new(rows, false);
}

/// <summary>Why one library root's folders could not be addressed on the instance.</summary>
/// <param name="CoveRoot">The library root, named once however many folders sit under it.</param>
/// <param name="Refusal">What the root could not establish.</param>
/// <param name="Tried">The paths the instance was asked about under it.</param>
internal sealed record FolderAddressRefusal(
    string CoveRoot, FolderAgreementRefusal Refusal, IReadOnlyList<string> Tried);

/// <summary>What a run over an entity's folders did.</summary>
/// <param name="Outcome">Whether every folder was read.</param>
/// <param name="FoldersAttached">How many folders' files the instance accepted.</param>
/// <param name="FoldersRefused">
/// How many folders the run could not carry out: the instance declined their files, or its listing
/// of them never arrived.
/// </param>
/// <param name="Skipped">
/// Why NOTHING was attempted. Null both for a run that ran and for a run that was not aimed for a
/// cause other than the instance's linking setting, because no setting was read on that path and
/// naming one would send a reader to a value nobody looked at.
/// </param>
/// <param name="FoldersNotAddressed">
/// How many folders never reached the instance, because no path it confirmed it can open was
/// established for them. Apart from <paramref name="FoldersRefused"/> on purpose: the instance
/// declined nothing here and was never asked about these folders at all.
/// </param>
/// <param name="AddressRefusals">
/// One line per library root that could not be addressed, with the paths tried under it. A line per
/// folder would grow with the entity and would put recorded filesystem paths somewhere nothing needs
/// them.
/// </param>
/// <param name="AddressedRoots">
/// The library roots the run did establish a path under, named once each. Carried apart from the
/// counts because a root that agreed is what clears that root's stored refusal, and a count cannot
/// say which root it was.
/// </param>
/// <param name="EntriesLeftUnderAnotherRoot">
/// How many files were left out because the instance holds the site they would join under a
/// different declared root from the file itself. Apart from <paramref name="FoldersRefused"/> on
/// purpose: the instance declined nothing here and was never asked about these files, and an import
/// across two roots copies the bytes in full rather than linking them.
/// </param>
/// <param name="RootsCouldNotBeRead">
/// Whether the run stopped because the instance declared no root to compare against. Every other
/// count is then zero: no folder was reached, so nothing was linked and nothing was refused.
/// </param>
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

/// <summary>What one folder's rows became, beside what was left out of them.</summary>
/// <remarks>
/// The two travel together because the entries reach the instance through an early continue when
/// there are none, and a count carried anywhere else would be lost exactly on the folder whose every
/// row was left out.
/// </remarks>
/// <param name="Entries">The entries to send, or null when no row can be attached.</param>
/// <param name="LeftUnderAnotherRoot">
/// How many rows were left out because the file and the site it matched sit under different declared
/// roots.
/// </param>
internal sealed record PlannedFiles(JsonArray? Entries, int LeftUnderAnotherRoot)
{
    /// <summary>A folder that produced nothing to send and left nothing out.</summary>
    internal static PlannedFiles Nothing { get; } = new(null, 0);
}

/// <summary>Whether, and with what, an instance is asked to link files the library already holds.</summary>
/// <remarks>
/// Pure. Reads the instance's answers as text and composes what is sent back; the run below drives
/// the delegates it is given and performs no I/O of its own. Without the decision here every matched
/// file would be copied in full on an instance whose hard-link setting is off: the import mode that
/// links is labelled as a copy, links when it can and copies with no error and no distinct outcome
/// when it cannot, and neither generation offers a mode that only links.
/// <para>
/// An unreadable setting answers skipped, not act. That is stricter than the default both builds
/// ship with, and deliberately so: acting on a setting nobody read is how a full copy of every
/// matched file happens silently.
/// </para>
/// <para>
/// A file's quality and languages are the parse route's own and are copied from its rows, never
/// composed. The instance's own submit path refuses a row missing either, and a row the parse could
/// not match carries no matched member at all rather than a null one, so exclusion is on absence.
/// </para>
/// <para>
/// Nothing outlives one folder's command. The rows are read per folder, handed into one command and
/// dropped, so nothing here grows with the library and nothing is persisted.
/// </para>
/// </remarks>
internal static class ReflectOwnedPlanner
{
    /// <summary>The command both generations import files through.</summary>
    internal const string CommandName = "ManualImport";

    /// <summary>
    /// The import mode that links when it can. The only other mode moves the file out of the library,
    /// and is never composed.
    /// </summary>
    internal const string ImportMode = "copy";

    /// <summary>The media-management member both generations report the setting under.</summary>
    internal const string HardLinkSetting = "copyUsingHardlinks";

    /// <summary>The member v3 names the scene a row matched under.</summary>
    private const string V3MatchedMember = "movie";

    /// <summary>The member v2 names the series a row matched under.</summary>
    private const string V2MatchedMember = "series";

    /// <summary>Whether to act on what the media-management read answered.</summary>
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

    /// <summary>
    /// The file entries one folder's parsed rows become, spelled as <paramref name="generation"/>'s
    /// own interface spells them, beside the rows left out because the instance holds their site
    /// under a different root.
    /// </summary>
    /// <remarks>
    /// The import mode that links copies the whole file instead whenever the source and the
    /// destination are not on one filesystem, and the instance's own hard-link setting being on does
    /// not prevent it. The copy is reported as a successful import carrying no distinct outcome, so
    /// nothing downstream can tell a link from terabytes of copied bytes.
    /// <para>
    /// Compared by declared root rather than by device, which is a conservative stand-in: two roots
    /// on one device cost a link that would have been safe, and no arrangement costs data.
    /// Where either path sits under no declared root, or the instance declares none at all, there
    /// is no comparison to make and the entry stays. An instance whose root list could not be read
    /// is a different fact and never reaches here: the run stops before a folder is, because a
    /// guard nobody could apply is not a guard.
    /// </para>
    /// </remarks>
    /// <param name="generation">Whose row spellings the rows are read under.</param>
    /// <param name="importable">The instance's own listing of the folder.</param>
    /// <param name="instanceRoots">The roots the instance declares for itself.</param>
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

    /// <summary>The command that attaches <paramref name="files"/>.</summary>
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

    /// <summary>
    /// Addresses each of <paramref name="folders"/> on the instance through
    /// <paramref name="address"/>, reads it through <paramref name="readImportable"/>, hands the rows
    /// that can be attached into one <paramref name="attach"/>, and drops them.
    /// </summary>
    /// <remarks>
    /// A cancellation classifies the run as cancelled rather than failed, and what was attached before
    /// it stays attached: the files are in place on the instance and there is nothing to undo.
    /// <para>
    /// A folder whose listing was refused counts as refused. A folder the instance listed nothing
    /// attachable in counts as neither, because that is a complete answer about the folder.
    /// </para>
    /// <para>
    /// The path handed to the read is always the instance's own. A folder with none reaches the
    /// instance not at all: the library's own spelling would produce a legitimately empty listing and
    /// a run reporting a clean zero over a folder nothing ever looked in.
    /// </para>
    /// </remarks>
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

    /// <summary>
    /// Whether the row's own file and the site it matched sit under different declared roots.
    /// </summary>
    /// <remarks>
    /// The most specific containing root answers for each path. Roots nest - an instance declaring
    /// both a parent and two children under it is the arrangement this guard exists for - and taking
    /// the first declared would answer the parent for both paths and see one root where there are
    /// two.
    /// </remarks>
    private static bool UnderDifferentRoots(
        WhisparrGeneration generation, JsonObject row, IReadOnlyList<string> instanceRoots)
    {
        var file = RootOf(Text(row, "path"), instanceRoots);
        var site = RootOf(Text(row[MatchedMember(generation)] as JsonObject, "path"), instanceRoots);

        return file is not null
            && site is not null
            && !string.Equals(file, site, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The most specific declared root <paramref name="path"/> sits under, or null when it sits
    /// under none.
    /// </summary>
    private static string? RootOf(string? path, IReadOnlyList<string> roots)
        => path is null
            ? null
            : roots
                .Where(root => PathCandidateGuard.TailBelow(path, root) is not null)
                .OrderByDescending(root => PathCandidateGuard.Normalize(root).Length)
                .FirstOrDefault();

    /// <summary>The member a row names the entity it matched under.</summary>
    private static string MatchedMember(WhisparrGeneration generation)
        => generation switch
        {
            WhisparrGeneration.V3 => V3MatchedMember,
            WhisparrGeneration.V2 => V2MatchedMember,
            _ => throw new ArgumentOutOfRangeException(nameof(generation)),
        };

    /// <summary>
    /// The non-blank text <paramref name="member"/> names, or null when it names none.
    /// </summary>
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

    /// <summary>The id of the entity <paramref name="member"/> names, or null when the row carries none.</summary>
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
