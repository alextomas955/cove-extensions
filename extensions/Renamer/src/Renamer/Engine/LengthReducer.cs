using Renamer.Options;

namespace Renamer.Engine;

// Measures two limits together: the filename component (name + ext) against FilenameMax, and the
// full generated path (folder + "/" + name + ext) against FullPathMax. Over either, it drops fields
// in DropOrder one at a time, re-rendering without them, and hard-truncates the filename when every
// drop still leaves it too long.
//
// The caps are a relative-path budget. The executor re-checks the absolute path including the
// library root, which the engine never sees.
public static class LengthReducer
{
    // The folder separator counts only when a folder is present.
    public static bool FitsBoth(string folder, string name, string ext, RenamerOptions o)
    {
        int filenameLen = name.Length + ext.Length;
        int sep = folder.Length > 0 ? 1 : 0;
        int fullLen = folder.Length + sep + name.Length + ext.Length;
        return filenameLen <= o.FilenameMax && fullLen <= o.FullPathMax;
    }

    // Returns the fitted result and the DropOrder entries removed to make it fit, in drop order.
    // reRenderWithout re-renders the folder and name with the cumulative set of dropped fields forced
    // empty; once a field is dropped it stays dropped.
    public static (RenamerResult result, IReadOnlyList<string> dropped) Fit(
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

    // Truncates the name to the tighter of the two budgets, never below zero.
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

        if (name.Length <= budget)
        {
            return name;
        }

        // Cutting between the halves of a surrogate pair leaves a lone surrogate. This runs after
        // sanitization, so nothing downstream repairs it before the name becomes a basename.
        if (budget > 0 && char.IsHighSurrogate(name[budget - 1]))
        {
            budget--;
        }

        return name[..budget];
    }
}
