using System.Text.Json;
using System.Text.Json.Nodes;
using WhisparrSync.Contracts;

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
internal sealed record ReflectOwnedRun(
    ReflectOwnedRunOutcome Outcome,
    int FoldersAttached,
    int FoldersRefused,
    ReflectOwnedSkipReason? Skipped = null);

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
    /// own interface spells them, or null when no row can be attached.
    /// </summary>
    internal static JsonArray? Files(WhisparrGeneration generation, string? importable)
    {
        if (AsArray(importable) is not { } rows)
        {
            return null;
        }

        var files = new JsonArray();
        foreach (var row in rows.OfType<JsonObject>())
        {
            if (Entry(generation, row) is { } entry)
            {
                files.Add(entry);
            }
        }

        return files.Count == 0 ? null : files;
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
    /// Reads each of <paramref name="folders"/> through <paramref name="readImportable"/>, hands the
    /// rows that can be attached into one <paramref name="attach"/>, and drops them.
    /// </summary>
    /// <remarks>
    /// A cancellation classifies the run as cancelled rather than failed, and what was attached before
    /// it stays attached: the files are in place on the instance and there is nothing to undo.
    /// <para>
    /// A folder whose listing was refused counts as refused. A folder the instance listed nothing
    /// attachable in counts as neither, because that is a complete answer about the folder.
    /// </para>
    /// </remarks>
    internal static async Task<ReflectOwnedRun> RunAsync(
        WhisparrGeneration generation,
        IAsyncEnumerable<string> folders,
        Func<string, CancellationToken, Task<ImportableListing>> readImportable,
        Func<JsonArray, CancellationToken, Task<bool>> attach,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(folders);
        ArgumentNullException.ThrowIfNull(readImportable);
        ArgumentNullException.ThrowIfNull(attach);

        var attached = 0;
        var refused = 0;
        try
        {
            ct.ThrowIfCancellationRequested();
            await foreach (var folder in folders.WithCancellation(ct).ConfigureAwait(false))
            {
                ct.ThrowIfCancellationRequested();
                var listing = await readImportable(folder, ct).ConfigureAwait(false);
                if (listing.WasRefused)
                {
                    refused++;
                    continue;
                }

                if (Files(generation, listing.Rows) is not { } files)
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
            return new ReflectOwnedRun(ReflectOwnedRunOutcome.Cancelled, attached, refused);
        }

        return new ReflectOwnedRun(ReflectOwnedRunOutcome.Completed, attached, refused);
    }

    // Both spellings are transcribed from the interface bundle each build ships. The newer names one
    // scene; the older names a series and the episodes matched inside it.
    private static JsonObject? Entry(WhisparrGeneration generation, JsonObject row)
    {
        if (row["quality"] is not JsonObject quality || row["languages"] is not JsonArray languages)
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
                if (MatchedId(row, "movie") is not { } movieId)
                {
                    return null;
                }

                entry["movieId"] = movieId;
                entry["movieFileId"] = row["movieFileId"]?.DeepClone();
                return entry;

            case WhisparrGeneration.V2:
                if (MatchedId(row, "series") is not { } seriesId
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
