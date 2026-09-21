using System.Globalization;
using Renamer.Engine;
using Renamer.Options;

namespace Renamer.Planner;

// The seam between Cove's entity model, already mapped to Renamer-owned DTOs by IRenamerDataPort, and
// the pure template engine. For one file of an entity it builds the token inputs the engine consumes.
// Each file projects independently, because an item can have many files.
//
// Pure: no System.IO, no DB. A media token is emitted only when the file kind actually carries it, and
// an absent token is omitted from the dictionary rather than emitted as "", so the engine's {} groups
// collapse cleanly. $resolution is not derived here; the engine derives it from $width and $height,
// falling back to the height alone, so a heightless kind never gets it.
public static class MetadataProjector
{
    // Projects one file into the engine's token inputs: the case-insensitive single-value token map, the
    // performer and tag name side-input that keeps $performers rendering and the title-performer drop
    // name-based, the per-performer records the engine orders and filters by before the max-count limit,
    // and the tag id/name pairs the tag whitelist and blacklist match on by id.
    public static (IReadOnlyDictionary<string, string> tokens,
                   IReadOnlyDictionary<string, IReadOnlyList<string>> multiValues,
                   IReadOnlyList<RenamerPerformer> performers,
                   IReadOnlyList<(int Id, string Name)> tagRefs)
        Project(RenamerEntity entity, RenamerFile file, RenamerOptions options)
    {
        var tokens = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var title = string.IsNullOrEmpty(entity.Title) ? DerivedTitle(entity, options) : entity.Title;
        Put(tokens, Tokens.Title, title);
        Put(tokens, Tokens.StudioCode, entity.Code);
        Put(tokens, Tokens.Studio, entity.StudioName);

        // ParentStudios is nearest-first, so $parent_studio is the studio's nearest parent name.
        if (entity.ParentStudios is { Count: > 0 } parents)
        {
            Put(tokens, Tokens.ParentStudio, parents[0].Name);
        }

        // Video-only; null for other kinds.
        Put(tokens, Tokens.Director, entity.Director);

        if (entity.Date is DateOnly date)
        {
            Put(tokens, Tokens.Date, date.ToString(options.DateFormat, CultureInfo.InvariantCulture));
            Put(tokens, Tokens.Year, date.Year.ToString(CultureInfo.InvariantCulture));
        }

        // Per-file media tokens, emitted only when the kind carries them.
        if (file.Width is int w)
        {
            Put(tokens, Tokens.Width, w.ToString(CultureInfo.InvariantCulture));
        }

        if (file.Height is int h)
        {
            Put(tokens, Tokens.Height, h.ToString(CultureInfo.InvariantCulture));
        }

        if (file.VideoCodec is { Length: > 0 } vc)
        {
            Put(tokens, Tokens.VideoCodec, vc);
        }

        if (file.AudioCodec is { Length: > 0 } ac)
        {
            Put(tokens, Tokens.AudioCodec, ac);
        }

        if (file.FrameRate is double fr)
        {
            Put(tokens, Tokens.FrameRate, fr.ToString(CultureInfo.InvariantCulture));
        }

        if (file.Duration is double dur)
        {
            Put(tokens, Tokens.Duration, FormatDuration(dur, options.DurationFormat));
        }

        // Cove stores overall bitrate in bits per second; $bitrate renders kbps.
        if (file.BitRate is long bps && bps > 0)
        {
            Put(tokens, Tokens.Bitrate, (bps / 1000).ToString(CultureInfo.InvariantCulture));
        }

        Put(tokens, Tokens.Ext, ResolveExt(file));

        // $performers keeps a plain name list so rendering and the title-performer drop stay name-based;
        // the per-performer records travel as a separate channel for ordering and filtering.
        var multi = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            [Tokens.Performers] = [.. entity.Performers.Select(p => p.Name)],
            [Tokens.Tags] = entity.Tags,
        };

        return (tokens, multi, entity.Performers, entity.TagRefs);
    }

    // The title an item with none falls back to: its first file's basename without the extension, or
    // null when the item already has a title, the FilenameAsTitle fallback is off, or the item has no
    // files. This is the canonical statement of why the fallback is recorded rather than repeated; the
    // sites that carry the value onward point here.
    //
    // Derived per run, the title is a function of the basename the previous run wrote, and the rename is
    // a function of the title, so any template rendering more than a bare $title wraps its own
    // decorations again on every pass and the name grows without bound. No cure keeps both directions
    // live: parsing the template back out of its own output is post-hoc cleanup, and refusing the
    // fallback for a decorated template only converts the runaway into a required-fields skip. So the
    // derivation is broken: the executor records this value on the entity in the same save as the
    // rename, after which the item has a title and this path never runs for it again.
    //
    // Entity-level, not per-file: a title belongs to the item, so deriving it from the file being
    // projected gives a multi-file item as many titles as it has files, and leaves the recorded one
    // decided by whichever file the executor saved last.
    internal static string? DerivedTitle(RenamerEntity entity, RenamerOptions options)
        => string.IsNullOrEmpty(entity.Title) && options.FilenameAsTitle && entity.Files.Count > 0
            ? BasenameStem(entity.Files[0].Basename)
            : null;

    // Renders a stored duration in seconds through the user-configured format. Both inputs are untrusted
    // and this runs once per file of every item in a plan, so a throw would abort a whole plan over one
    // bad setting: a malformed format string throws FormatException, and a non-finite or out-of-range
    // stored duration throws out of TimeSpan.FromSeconds. Either way the token degrades to the raw
    // invariant seconds, the one rendering that cannot itself fail for any double. The format is not
    // pre-validated, because only formatting it can decide whether .NET accepts it.
    private static string FormatDuration(double seconds, string format)
    {
        try
        {
            return TimeSpan.FromSeconds(seconds).ToString(format, CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or OverflowException)
        {
            return seconds.ToString(CultureInfo.InvariantCulture);
        }
    }

    // Omit rather than blank, so the engine's {} groups collapse.
    private static void Put(Dictionary<string, string> tokens, string key, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            tokens[key] = value;
        }
    }

    // The basename with its extension stripped, for use as a fallback title. The extension is dropped
    // only when a non-empty stem precedes the last dot, so a dotless name ("README") and a leading-dot
    // name (".gitignore") keep their whole basename: a leading-dot title reads better whole than split.
    // This is a title-readability rule, and it differs on that edge from ResolveExt, which treats a
    // leading dot as the extension boundary.
    private static string BasenameStem(string basename)
    {
        var dot = basename.LastIndexOf('.');
        return dot > 0 ? basename[..dot] : basename;
    }

    // The file's actual on-disk extension, falling back to the metadata Format only when the basename
    // has none. Cove's Format field is the container name, which is often not the file extension: an
    // .mkv file reports Format "matroska", and using it as the extension produces a non-standard
    // extension that breaks player and OS association and Cove's own kind detection. A rename changes
    // the name, not the container type. A pure string op, to preserve projector purity.
    private static string ResolveExt(RenamerFile file)
    {
        var dot = file.Basename.LastIndexOf('.');
        if (dot >= 0 && dot < file.Basename.Length - 1)
        {
            return file.Basename[(dot + 1)..];
        }

        return file.Format ?? string.Empty;
    }
}
