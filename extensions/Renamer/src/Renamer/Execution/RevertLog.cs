using Renamer.Planner;

namespace Renamer.Execution;

/// <summary>
/// The legacy stored undo journal: a ONE-WAY migration source, never live storage.
/// </summary>
/// <remarks>
/// Until the journal moved into Cove's database this type WAS the journal — an append-only blob under
/// one <c>IExtensionStore</c> key. Nothing writes it now. What survives is the two key names an
/// installation may still carry and the tolerant parsers <see cref="JournalBlobMigration"/> needs to
/// read them exactly once before deleting them; there is deliberately no second reader for the legacy
/// format, because a one-shot path is the worst place to have two things that can disagree.
/// <para>
/// ON-DISK FORMAT, as the parsers below read it. The blob is newline-delimited, in two line shapes:
/// a <em>batch header</em> begins with <c>#</c> and carries <c>runId</c>, a server-written UTC-ticks
/// timestamp, the run's <see cref="RenamerFileKind"/> and a lifecycle marker; a <em>data row</em> is
/// <c>entityId|fileId|old</c>, where the entityId is the PARENT entity (e.g. the Video) and the fileId
/// the physical file row. The leading <c>#</c> cannot begin an integer entityId, so the two are
/// unambiguous. Both parsers read a PREFIX of a line, so a row carrying extra trailing fields still
/// yields its entry, and a blob with NO header at all is one implicit, still-replayable
/// <see cref="RenamerFileKind.Video"/> batch whose rows are <c>fileId|old</c> with EntityId = FileId.
/// A header or data line with missing/short fields or a non-integer id is skipped, never thrown.
/// </para>
/// </remarks>
public static class RevertLog
{
    /// <summary>The store key the appended, newline-delimited blob lives under.</summary>
    public const string Key = "revertlog";

    /// <summary>The store key holding the stamp that says whether <see cref="Key"/> may be parsed.</summary>
    /// <remarks>
    /// A SEPARATE key on purpose. A journal written before the row cap can be hundreds of megabytes,
    /// and this value is a few bytes and always safe to read — so the decision about the journal cannot
    /// be carried by the journal's own content.
    /// </remarks>
    public const string SchemaKey = "journal-schema";

    /// <summary>The stamp a journal written by the last version that still wrote one carries.</summary>
    /// <remarks>
    /// A stored journal stamped with anything else was written under a shape this code does not read,
    /// and is discarded unparsed rather than guessed at.
    /// </remarks>
    public const string CurrentSchema = "2";

    // The field separator. Paths are forward-slash and never contain '|' on the platforms Cove runs.
    private const char FieldSep = '|';

    // Header line prefix + the marker a still-replayable batch carries.
    private const char HeaderPrefix = '#';
    private const string StatusOpen = "open";

    /// <summary>One logged row of a stored journal.</summary>
    /// <param name="EntityId">The PARENT entity id (e.g. Video id) the forward event was published for.</param>
    /// <param name="FileId">The renamed physical file row's id.</param>
    /// <param name="OldPath">The path the file moved FROM (forward-slash).</param>
    public readonly record struct RevertEntry(int EntityId, int FileId, string OldPath);

    /// <summary>
    /// Where the last still-replayable batch sits inside a stored journal, and what its header said.
    /// </summary>
    /// <remarks>
    /// A LOCATION rather than the rows themselves, so a caller can parse the range in slices. The
    /// stored value is unbounded input — that is the incident the row cap was added for — so handing
    /// back every row in one list would put a second structure of the blob's size beside the blob.
    /// </remarks>
    /// <param name="RunId">The header's run id, or empty for a headerless blob.</param>
    /// <param name="WrittenAtUtcTicks">The header's own UTC ticks, or 0 when it carried none.</param>
    /// <param name="Kind">The run's entity kind; <see cref="RenamerFileKind.Video"/> for a headerless blob.</param>
    /// <param name="Headerless">True when the blob predates batch headers, which changes the row shape.</param>
    /// <param name="RowStart">First line index of the batch's rows, inclusive.</param>
    /// <param name="RowEnd">One past the last line index of the batch's rows.</param>
    public readonly record struct LegacyBatch(
        string RunId,
        long WrittenAtUtcTicks,
        RenamerFileKind Kind,
        bool Headerless,
        int RowStart,
        int RowEnd);

    /// <summary>Splits a stored journal into its lines, the form both parsers below index into.</summary>
    public static string[] SplitLines(string blob) => blob.Split('\n');

    /// <summary>
    /// Finds the last still-replayable batch in <paramref name="lines"/>, or null when there is none.
    /// </summary>
    /// <remarks>
    /// A blob with headers but none still replayable is null — every batch in it was already spent. A
    /// blob with no header at all is one implicit replayable Video batch spanning the whole value.
    /// </remarks>
    public static LegacyBatch? LocateLastOpenBatch(string[] lines)
    {
        int lastOpenHeader = -1;
        bool anyHeader = false;
        for (int i = 0; i < lines.Length; i++)
        {
            if (!IsHeader(lines[i]))
            {
                continue;
            }

            anyHeader = true;
            if (TryParseHeader(lines[i], out _, out _, out var status) && status == StatusOpen)
            {
                lastOpenHeader = i;
            }
        }

        if (!anyHeader)
        {
            return new LegacyBatch("", 0, RenamerFileKind.Video, Headerless: true, 0, lines.Length);
        }

        if (lastOpenHeader < 0)
        {
            return null;
        }

        // Re-parse the chosen header for its run id and kind; a failure here would mean the line stopped
        // being a valid header between the scan above and now — treat it as "no open batch".
        if (!TryParseHeader(lines[lastOpenHeader], out var runId, out var kind, out _))
        {
            return null;
        }

        // The rows run from just after this header to the next header, or to the end.
        int end = lines.Length;
        for (int i = lastOpenHeader + 1; i < lines.Length; i++)
        {
            if (IsHeader(lines[i]))
            {
                end = i;
                break;
            }
        }

        return new LegacyBatch(
            runId, ParseHeaderTicks(lines[lastOpenHeader]), kind, Headerless: false, lastOpenHeader + 1, end);
    }

    /// <summary>
    /// Parses the data rows in <c>lines[start..end)</c>, in append order. When
    /// <paramref name="headerless"/> the row form is <c>fileId|old</c> and EntityId is set to FileId;
    /// otherwise <c>entityId|fileId|old</c>. Header lines and malformed/short lines are skipped.
    /// </summary>
    public static List<RevertEntry> ParseRows(string[] lines, int start, int end, bool headerless)
    {
        var rows = new List<RevertEntry>();
        for (int i = start; i < end; i++)
        {
            var line = lines[i];
            if (line.Length == 0 || IsHeader(line))
            {
                continue;
            }

            var parts = line.Split(FieldSep);

            if (headerless)
            {
                if (parts.Length < 2 || !int.TryParse(parts[0], out var fileId))
                {
                    continue;
                }

                rows.Add(new RevertEntry(fileId, fileId, parts[1]));
            }
            else
            {
                if (parts.Length < 3
                    || !int.TryParse(parts[0], out var entityId)
                    || !int.TryParse(parts[1], out var fileId))
                {
                    continue;
                }

                rows.Add(new RevertEntry(entityId, fileId, parts[2]));
            }
        }

        return rows;
    }

    private static bool IsHeader(string line) => line.Length > 0 && line[0] == HeaderPrefix;

    /// <summary>Parses a <c>#batch|runId|ticks|kind|status</c> header. False (skip) if fewer than 5 fields. Unknown kind → Video.</summary>
    private static bool TryParseHeader(string line, out string runId, out RenamerFileKind kind, out string status)
    {
        runId = "";
        kind = RenamerFileKind.Video;
        status = "";

        var parts = line.Split(FieldSep);
        if (parts.Length < 5)
        {
            return false;
        }

        runId = parts[1];
        if (!Enum.TryParse(parts[3], ignoreCase: true, out kind))
        {
            kind = RenamerFileKind.Video;
        }

        status = parts[4];
        return true;
    }

    private static long ParseHeaderTicks(string line)
    {
        var parts = line.Split(FieldSep);
        return parts.Length >= 3 && long.TryParse(parts[2], out var t) ? t : 0;
    }
}
