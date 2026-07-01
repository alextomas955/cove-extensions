using System.Globalization;
using Renamer.Engine;
using Renamer.Options;

namespace Renamer.Planner;

/// <summary>
/// The seam between Cove's entity model (already mapped to Renamer-owned DTOs by
/// <see cref="IRenamerDataPort"/>) and the pure <see cref="TemplateEngine"/>. For one
/// <see cref="RenamerFile"/> of a <see cref="RenamerEntity"/> it builds the
/// <c>(tokens, multiValues)</c> pair <see cref="TemplateEngine.Render"/> consumes. Each file
/// projects independently (an item can have many files).
///
/// PURE: no <c>System.IO</c>, no DB. Entity-type-aware degradation: a media token is emitted ONLY
/// when the file kind actually carries it (its DTO field is non-null). Absent scalar/media tokens
/// are OMITTED from the dict — never emitted as <c>""</c> — so the engine's <c>{}</c> groups
/// collapse cleanly. <c>$resolution</c> is NOT derived here; the engine derives it from
/// <c>$height</c> when present, so a heightless kind (audio) naturally never gets it.
/// </summary>
public static class MetadataProjector
{
    /// <summary>
    /// Projects one file of <paramref name="entity"/> into the engine's token inputs.
    /// </summary>
    /// <returns>
    /// <c>tokens</c>: case-insensitive single-value token map (keyed by <see cref="Tokens"/>
    /// constants, absent tokens omitted). <c>multiValues</c>: the performer/tag NAME side-input
    /// (so <c>$performers</c> rendering and the title-performer drop stay name-based).
    /// <c>performers</c>: the per-performer records carried alongside the names so the engine can
    /// order/filter performers by id/favorite/gender before the max-count limit.
    /// </returns>
    public static (IReadOnlyDictionary<string, string> tokens,
                   IReadOnlyDictionary<string, IReadOnlyList<string>> multiValues,
                   IReadOnlyList<RenamerPerformer> performers)
        Project(RenamerEntity entity, RenamerFile file, RenamerOptions options)
    {
        var tokens = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // --- Entity-level scalar tokens (omit when empty so {} groups degrade). ---
        // The fallback derives the title from the CURRENT source basename without re-applying the
        // template's own decorations, so re-projecting an already-renamerd item yields the same title —
        // no progressive re-append, no drift; a stable name then hits the executor's no-op-renamer skip.
        var title = string.IsNullOrEmpty(entity.Title) && options.FilenameAsTitle
            ? BasenameStem(file.Basename)
            : entity.Title;
        Put(tokens, Tokens.Title, title);
        Put(tokens, Tokens.StudioCode, entity.Code);
        Put(tokens, Tokens.Studio, entity.StudioName);

        // $parent_studio: the studio's NEAREST parent name (ParentStudios is nearest-first); omitted
        // when the studio has no parent so a `{}` group collapses cleanly.
        if (entity.ParentStudios is { Count: > 0 } parents)
        {
            Put(tokens, Tokens.ParentStudio, parents[0].Name);
        }

        // $director: the video's director (Video-only; null for other kinds).
        Put(tokens, Tokens.Director, entity.Director);

        if (entity.Date is DateOnly date)
        {
            // $date honors the configured DateFormat; $year is the calendar year.
            Put(tokens, Tokens.Date, date.ToString(options.DateFormat, CultureInfo.InvariantCulture));
            Put(tokens, Tokens.Year, date.Year.ToString(CultureInfo.InvariantCulture));
        }

        // --- Per-file media tokens: emitted ONLY when the kind carries them (nullable DTO). ---
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
            Put(tokens, Tokens.Duration, dur.ToString(CultureInfo.InvariantCulture));
        }

        // $bitrate: the file's stored overall bitrate, rendered in kbps (Cove stores bits/sec on
        // VideoFile.BitRate). Omitted when absent / non-video so a `{}` group collapses.
        if (file.BitRate is long bps && bps > 0)
        {
            Put(tokens, Tokens.Bitrate, (bps / 1000).ToString(CultureInfo.InvariantCulture));
        }

        // --- Extension: from the file Format if set, else the basename's extension. ---
        Put(tokens, Tokens.Ext, ResolveExt(file));

        // --- Multi-value side-input: performer/tag NAME lists (already JOIN-resolved upstream). ---
        // $performers keeps a plain name list so rendering and the title-performer drop stay
        // name-based; the per-performer records travel as a separate channel for ordering/filtering.
        var multi = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            [Tokens.Performers] = [.. entity.Performers.Select(p => p.Name)],
            [Tokens.Tags] = entity.Tags,
        };

        return (tokens, multi, entity.Performers);
    }

    /// <summary>Adds <paramref name="value"/> under <paramref name="key"/> only when non-empty (omit-not-blank).</summary>
    private static void Put(Dictionary<string, string> tokens, string key, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            tokens[key] = value;
        }
    }

    /// <summary>
    /// The basename with its extension stripped, for use as a fallback title. The extension is
    /// dropped only when a non-empty stem precedes the last dot (<c>dot &gt; 0</c>), so a dotless name
    /// (<c>README</c>) and a leading-dot name (<c>.gitignore</c>) keep their whole basename as the
    /// title — a leading-dot title reads better whole than split. (This is a title-readability rule,
    /// NOT the same split as <see cref="ResolveExt"/>, which treats a leading dot as the extension
    /// boundary; the two intentionally differ on that edge.) Pure string op — no <c>System.IO</c>.
    /// </summary>
    private static string BasenameStem(string basename)
    {
        var dot = basename.LastIndexOf('.');
        return dot > 0 ? basename[..dot] : basename;
    }

    /// <summary>
    /// Resolves the extension token: the file <c>Format</c> when present (it is an extension-like
    /// token, e.g. <c>"mkv"</c>), otherwise the basename's own extension. The engine normalizes
    /// the leading dot, so either form is accepted. Empty when neither is available. Kept as a
    /// pure string op (no <c>System.IO</c>) to preserve the projector's purity contract.
    /// </summary>
    private static string ResolveExt(RenamerFile file)
    {
        if (!string.IsNullOrEmpty(file.Format))
        {
            return file.Format;
        }

        var dot = file.Basename.LastIndexOf('.');
        return dot >= 0 && dot < file.Basename.Length - 1
            ? file.Basename[(dot + 1)..]
            : string.Empty;
    }
}
