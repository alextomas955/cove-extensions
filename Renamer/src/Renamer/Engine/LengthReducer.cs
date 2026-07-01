using Renamer.Options;

namespace Renamer.Engine;

/// <summary>
/// Pure MAX_PATH-aware length reduction.
/// Given a rendered folder + filename + ext, it measures BOTH the filename component
/// (<c>name + ext</c> ≤ <see cref="RenamerOptions.FilenameMax"/>) AND the full generated path
/// (<c>folder + "/" + name + ext</c> ≤ <see cref="RenamerOptions.FullPathMax"/>) — never just one.
/// When over, it drops fields in <see cref="RenamerOptions.DropOrder"/> one at a time (re-rendering
/// without that field) until both fit; if still too long after every drop, it hard-truncates the
/// filename as a last resort.
///
/// Pure: operates on string lengths only — NO filesystem call. These caps are a relative-path
/// budget; the executor independently re-checks the ABSOLUTE path including the library root,
/// which the engine cannot see, so the on-disk path can never overrun MAX_PATH.
/// </summary>
public static class LengthReducer
{
    /// <summary>
    /// True iff the filename component (name+ext) fits <see cref="RenamerOptions.FilenameMax"/> AND
    /// the full generated path (folder + "/" + name + ext) fits <see cref="RenamerOptions.FullPathMax"/>.
    /// The folder separator is only counted when a folder is present.
    /// </summary>
    public static bool FitsBoth(string folder, string name, string ext, RenamerOptions o)
    {
        int filenameLen = name.Length + ext.Length;
        int sep = folder.Length > 0 ? 1 : 0;
        int fullLen = folder.Length + sep + name.Length + ext.Length;
        return filenameLen <= o.FilenameMax && fullLen <= o.FullPathMax;
    }

    /// <summary>
    /// Reduces <paramref name="folder"/>/<paramref name="name"/>/<paramref name="ext"/> until
    /// <see cref="FitsBoth"/>. <paramref name="reRenderWithout"/> re-renders the (folder, name)
    /// pair with the given CUMULATIVE set of fields forced empty (the engine threads this from
    /// the resolved token map). Fields are dropped one at a time in <see cref="RenamerOptions.DropOrder"/>
    /// and accumulate — once a field is dropped it stays dropped. After every field is dropped and
    /// it still doesn't fit, the name is hard-truncated as a last resort.
    /// </summary>
    public static RenamerResult Fit(
        string folder,
        string name,
        string ext,
        RenamerOptions o,
        Func<IReadOnlyCollection<string>, (string folder, string name)> reRenderWithout)
        => FitWithDropped(folder, name, ext, o, reRenderWithout).result;

    /// <summary>
    /// Identical reduction to <see cref="Fit"/>, but also returns the ordered set of fields actually
    /// dropped (the <see cref="RenamerOptions.DropOrder"/> entries removed to make the name fit). The
    /// list is empty when the name fit both caps without dropping anything. This is the SINGLE drop
    /// loop — <see cref="Fit"/> delegates here and discards the dropped list: the data already
    /// exists inside the loop, so it is surfaced rather than re-derived by diffing output strings.
    /// </summary>
    public static (RenamerResult result, IReadOnlyList<string> dropped) FitWithDropped(
        string folder,
        string name,
        string ext,
        RenamerOptions o,
        Func<IReadOnlyCollection<string>, (string folder, string name)> reRenderWithout)
    {
        var dropped = new List<string>();
        foreach (var field in o.DropOrder)
        {
            if (FitsBoth(folder, name, ext, o))
            {
                break;
            }

            dropped.Add(field);
            (folder, name) = reRenderWithout(dropped);
        }

        if (!FitsBoth(folder, name, ext, o))
        {
            name = HardTruncate(folder, name, ext, o);
        }

        return (new RenamerResult(folder, name, ext), dropped);
    }

    /// <summary>
    /// Last resort: trims the filename component so that name+ext ≤ FilenameMax AND
    /// folder + "/" + name + ext ≤ FullPathMax. Computes the tightest of the two budgets and
    /// truncates the name to it (never below zero).
    /// </summary>
    private static string HardTruncate(string folder, string name, string ext, RenamerOptions o)
    {
        int byFilename = o.FilenameMax - ext.Length;
        int sep = folder.Length > 0 ? 1 : 0;
        int byFullPath = o.FullPathMax - folder.Length - sep - ext.Length;
        int budget = Math.Min(byFilename, byFullPath);
        if (budget < 0)
        {
            budget = 0;
        }

        return name.Length <= budget ? name : name.Substring(0, budget);
    }
}
