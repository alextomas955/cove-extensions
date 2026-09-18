namespace Renamer.Execution;

// The legacy stored undo journal: a one-way migration source, never live storage. Nothing writes it
// now. What survives is the two store key names an installation may still carry and the tolerant
// parsers JournalBlobMigration needs to read them once before deleting them.
//
// The blob is newline-delimited in two line shapes. A batch header begins with '#' and carries runId,
// a server-written UTC-ticks timestamp, the run's RenamerFileKind and a lifecycle marker. A data row
// is entityId|fileId|old, where the entityId is the parent entity and the fileId the physical file
// row. The leading '#' cannot begin an integer entityId, so the two shapes are unambiguous. The header
// parser reads a prefix of its line, so a header carrying extra trailing fields still yields its
// entry. A blob with no header at all is one implicit, still-replayable Video batch whose rows are
// fileId|old|new with EntityId set to FileId. A header or data line with short fields or a
// non-integer id is skipped, never thrown.
public static class RevertLog
{
    // The store key the appended, newline-delimited blob lives under.
    public const string Key = "revertlog";

    // The store key holding the stamp that says whether Key may be parsed. Separate from the journal
    // because a journal written before the row cap can be hundreds of megabytes, while this value is a
    // few bytes and always safe to read.
    public const string SchemaKey = "journal-schema";

    // The stamp a journal written by the last version that still wrote one carries. A journal stamped
    // with anything else was written under a shape this code does not read, and is discarded unparsed.
    public const string CurrentSchema = "2";

    private const char FieldSep = '|';

    // Header line prefix + the marker a still-replayable batch carries.
    private const char HeaderPrefix = '#';
    private const string StatusOpen = "open";

    // One logged row of a stored journal. OldPath is forward-slash.
    public readonly record struct RevertEntry(int EntityId, int FileId, string OldPath);

    // Where the last still-replayable batch sits inside a stored journal, and what its header said.
    // RunId is empty and Kind is Video for a headerless blob, and WrittenAtUtcTicks is 0 when the
    // header carried none. RowStart is inclusive and RowEnd is one past the last row line.
    //
    // A location, not the rows, so a caller can parse the range in slices: the stored value is
    // unbounded input, and every row in one list would put a second structure of the blob's size beside
    // the blob.
    public readonly record struct LegacyBatch(
        string RunId,
        long WrittenAtUtcTicks,
        RenamerFileKind Kind,
        bool Headerless,
        int RowStart,
        int RowEnd);

    public static string[] SplitLines(string blob) => blob.Split('\n');

    // Null when the blob has headers but none still replayable, meaning every batch in it was spent. A
    // blob with no header at all is one implicit replayable Video batch spanning the whole value.
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

        if (!TryParseHeader(lines[lastOpenHeader], out var runId, out var kind, out _))
        {
            return null;
        }

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

    // Parses the data rows in lines[start..end) in append order, skipping header and short lines.
    //
    // The separator is legal in a path on every platform Cove runs on, so the two row shapes cannot
    // treat it alike. In the headered form the path is the last field, so everything past the second
    // separator is part of it and rejoining is lossless. In the headerless form the path is followed by
    // the destination it moved to, so it ends at the next separator; a path carrying a separator is
    // unrecoverable there, and rejoining would corrupt every row that has a destination.
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

                rows.Add(new RevertEntry(
                    entityId, fileId, string.Join(FieldSep, parts, 2, parts.Length - 2)));
            }
        }

        return rows;
    }

    private static bool IsHeader(string line) => line.Length > 0 && line[0] == HeaderPrefix;

    // Parses a #batch|runId|ticks|kind|status header. False when it has fewer than 5 fields; an
    // unrecognised kind reads as Video.
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
