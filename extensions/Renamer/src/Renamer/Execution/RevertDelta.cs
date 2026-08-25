using System.Globalization;
using System.Text;

namespace Renamer.Execution;

/// <summary>One sidecar file that rode along with a renamed file, recorded in the FORWARD direction.</summary>
/// <param name="FromPath">Where the sidecar sat before the rename (forward-slash, absolute).</param>
/// <param name="ToPath">Where the forward move actually put it (forward-slash, absolute).</param>
public sealed record RevertSidecarDelta(string FromPath, string ToPath);

/// <summary>One database-tracked caption whose stored filename the forward rename rewrote.</summary>
/// <param name="CaptionId">The caption row.</param>
/// <param name="OriginalFilename">
/// The filename the caption row held BEFORE the rename — the value an undo writes back. Recorded
/// rather than derived, because the forward transform only rewrites a caption whose name starts with
/// the old stem and is therefore not invertible from the new name alone.
/// </param>
public sealed record RevertCaptionDelta(int CaptionId, string OriginalFilename);

/// <summary>
/// Everything that rode along with ONE renamed file: the sidecar moves that actually happened on disk
/// and the caption filenames the save rewrote, as the forward path observed them.
/// </summary>
/// <remarks>
/// RECORDED, NEVER RECOMPUTED. Undo replays this payload reversed instead of deriving the reverse
/// names from the old and new stems, for two reasons no string arithmetic recovers: the caption
/// retarget is not invertible in general (a caption that does not start with the old stem is left
/// alone), and the forward path applies a caption rename ONLY for a sidecar whose file really moved on
/// disk — a runtime fact.
/// <para>
/// SERIALIZED FORM, which is what the journal row's sidecar column holds. Records are separated by
/// <c>\n</c> and fields by <c>|</c>, in two shapes: <c>s|fromPath|toPath</c> for a sidecar move and
/// <c>c|captionId|originalFilename</c> for a caption. An EMPTY delta serializes to the empty string —
/// the same value the column defaults to — so a row written before deltas existed and a row whose file
/// genuinely had no sidecars read identically, which removes a state rather than encoding one.
/// </para>
/// <para>
/// ESCAPING RULE, a contract no signature states. Every field is escaped on write, backslash first:
/// <c>\</c> → <c>\\</c>, <c>|</c> → <c>\p</c>, LF → <c>\n</c>, CR → <c>\r</c>. Reading applies the
/// inverse. A path may legally contain any of those characters on the platforms Cove runs, so an
/// unescaped separator would silently split one path into two fields. An UNRECOGNIZED escape yields
/// the escaped character itself and a trailing lone backslash is dropped, so a value written by a
/// future variant of this format degrades instead of throwing.
/// </para>
/// <para>
/// TOLERANCE, in the same direction the legacy journal parser is tolerant: a record with an unknown
/// tag, too few fields or a non-integer caption id is DROPPED, never thrown on. A row whose delta
/// cannot be read in full must still restore the media file it names — losing a subtitle is not a
/// reason to strand the film.
/// </para>
/// </remarks>
/// <param name="Sidecars">The sidecar moves that actually happened, in the order the mover made them.</param>
/// <param name="Captions">The captions whose stored filename the save rewrote.</param>
public sealed record RevertDelta(
    IReadOnlyList<RevertSidecarDelta> Sidecars,
    IReadOnlyList<RevertCaptionDelta> Captions)
{
    /// <summary>The delta of a rename that moved nothing alongside its file.</summary>
    public static readonly RevertDelta Empty = new([], []);

    private const char RecordSep = '\n';
    private const char FieldSep = '|';
    private const char EscapeChar = '\\';
    private const char SidecarTag = 's';
    private const char CaptionTag = 'c';

    /// <summary>True when nothing rode along with the file.</summary>
    public bool IsEmpty => Sidecars.Count == 0 && Captions.Count == 0;

    /// <summary>Renders this delta to the form the journal row stores; an empty delta renders to the empty string.</summary>
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

    /// <summary>Reads a stored delta, dropping whatever it cannot understand.</summary>
    /// <param name="serialized">The journal row's stored value; null and empty are both "nothing rode along".</param>
    /// <param name="delta">Always assigned — <see cref="Empty"/> when nothing readable was found.</param>
    /// <returns>True iff at least one record was read.</returns>
    /// <remarks>
    /// The out value is usable whatever the return says, so a caller that only wants to replay what is
    /// there can ignore the bool. The bool distinguishes "this row carried nothing" from "this row
    /// carried something", which is what a caller reporting an unreadable delta needs.
    /// </remarks>
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
