using System.Globalization;
using System.Text;

namespace Renamer.Execution;

// One sidecar file that rode along with a renamed file, recorded in the forward direction. Both paths
// are absolute and forward-slash.
public sealed record RevertSidecarDelta(string FromPath, string ToPath);

// One database-tracked caption whose stored filename the forward rename rewrote. OriginalFilename is
// the value an undo writes back. It is recorded because the forward transform only rewrites a caption
// whose name starts with the old stem, so it is not invertible from the new name alone.
public sealed record RevertCaptionDelta(int CaptionId, string OriginalFilename);

// Everything that rode along with one renamed file: the sidecar moves that happened on disk, in the
// order the mover made them, and the caption filenames the save rewrote.
//
// Undo replays this payload reversed and derives nothing from the old and new stems. The caption
// retarget is not invertible, since a caption that does not start with the old stem is left alone, and
// the forward path applies a caption rename only for a sidecar whose file really moved on disk.
//
// The serialized form is what the journal row's sidecar column holds. Records are separated by LF and
// fields by a pipe, in two shapes: s|fromPath|toPath for a sidecar move and c|captionId|originalFilename
// for a caption. An empty delta serializes to the empty string, which is the column's default, so a row
// written before deltas existed and a row whose file had no sidecars read identically.
//
// Every field is escaped on write, backslash first: backslash doubles, a pipe becomes \p, LF becomes
// \n and CR becomes \r. Reading applies the inverse. A path may legally contain any of those
// characters on the platforms Cove runs, so an unescaped separator would split one path into two
// fields. An unrecognised escape yields the escaped character itself and a trailing lone backslash is
// dropped, so a value written by a future variant of this format degrades instead of throwing.
//
// A record with an unknown tag, too few fields or a non-integer caption id is dropped and never thrown
// on. A row whose delta cannot be read in full must still restore the media file it names.
public sealed record RevertDelta(
    IReadOnlyList<RevertSidecarDelta> Sidecars,
    IReadOnlyList<RevertCaptionDelta> Captions)
{
    // The delta of a rename that moved nothing alongside its file.
    public static readonly RevertDelta Empty = new([], []);

    private const char RecordSep = '\n';
    private const char FieldSep = '|';
    private const char EscapeChar = '\\';
    private const char SidecarTag = 's';
    private const char CaptionTag = 'c';

    public bool IsEmpty => Sidecars.Count == 0 && Captions.Count == 0;

    // An empty delta renders to the empty string.
    public string Serialize()
    {
        if (IsEmpty)
        {
            return "";
        }

        var sb = new StringBuilder();
        foreach (var sidecar in Sidecars)
        {
            AppendSeparator(sb);
            sb.Append(SidecarTag).Append(FieldSep)
                .Append(Encode(sidecar.FromPath)).Append(FieldSep)
                .Append(Encode(sidecar.ToPath));
        }

        foreach (var caption in Captions)
        {
            AppendSeparator(sb);
            sb.Append(CaptionTag).Append(FieldSep)
                .Append(caption.CaptionId.ToString(CultureInfo.InvariantCulture)).Append(FieldSep)
                .Append(Encode(caption.OriginalFilename));
        }

        return sb.ToString();
    }

    // Reads a stored delta, dropping whatever it cannot understand. A null or empty input means nothing
    // rode along. The out value is always assigned and usable whatever the return says, so a caller
    // that only wants to replay what is there can ignore the bool; the bool says whether the row
    // carried anything at all.
    public static bool TryParse(string? serialized, out RevertDelta delta)
    {
        delta = Empty;
        if (string.IsNullOrEmpty(serialized))
        {
            return false;
        }

        var sidecars = new List<RevertSidecarDelta>();
        var captions = new List<RevertCaptionDelta>();

        foreach (var line in serialized.Split(RecordSep))
        {
            if (line.Length == 0)
            {
                continue;
            }

            var parts = line.Split(FieldSep);
            if (parts.Length < 3)
            {
                continue;
            }

            if (IsTag(parts[0], SidecarTag))
            {
                sidecars.Add(new RevertSidecarDelta(Decode(parts[1]), Decode(parts[2])));
            }
            else if (IsTag(parts[0], CaptionTag)
                     && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int captionId))
            {
                captions.Add(new RevertCaptionDelta(captionId, Decode(parts[2])));
            }
        }

        if (sidecars.Count == 0 && captions.Count == 0)
        {
            return false;
        }

        delta = new RevertDelta(sidecars, captions);
        return true;
    }

    private static bool IsTag(string field, char tag) => field.Length == 1 && field[0] == tag;

    private static void AppendSeparator(StringBuilder sb)
    {
        if (sb.Length > 0)
        {
            sb.Append(RecordSep);
        }
    }

    private static string Encode(string value)
    {
        if (value.IndexOfAny([EscapeChar, FieldSep, '\n', '\r']) < 0)
        {
            return value;
        }

        var sb = new StringBuilder(value.Length + 8);
        foreach (char ch in value)
        {
            switch (ch)
            {
                case EscapeChar: sb.Append(@"\\"); break;
                case FieldSep: sb.Append(@"\p"); break;
                case '\n': sb.Append(@"\n"); break;
                case '\r': sb.Append(@"\r"); break;
                default: sb.Append(ch); break;
            }
        }

        return sb.ToString();
    }

    private static string Decode(string value)
    {
        if (value.IndexOf(EscapeChar) < 0)
        {
            return value;
        }

        var sb = new StringBuilder(value.Length);
        for (int i = 0; i < value.Length; i++)
        {
            if (value[i] != EscapeChar)
            {
                sb.Append(value[i]);
                continue;
            }

            if (i + 1 >= value.Length)
            {
                // A trailing lone escape names no character. Dropping it keeps the rest readable.
                break;
            }

            char next = value[++i];
            sb.Append(next switch
            {
                EscapeChar => EscapeChar,
                'p' => FieldSep,
                'n' => '\n',
                'r' => '\r',
                _ => next,
            });
        }

        return sb.ToString();
    }
}
