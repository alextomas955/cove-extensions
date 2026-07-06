using Renamer.Options;
using Renamer.Planner;

namespace Renamer.Engine;

/// <summary>
/// Pure multi-value (performers, tags) resolution.
/// Turns a list of raw values into a single joined string under the field's
/// <see cref="MultiValueOptions"/> controls, in this fixed order:
/// whitelist → blacklist → sort → max(KeepFirst/DropAll) → join.
///
/// The engine's Render pulls each multi-value field's list from a
/// <c>IReadOnlyDictionary&lt;string, IReadOnlyList&lt;string&gt;&gt;</c> side-input, calls
/// <see cref="Resolve(IReadOnlyList{string}, MultiValueOptions)"/>, and feeds the joined string
/// into the scalar token map. This is just the pure list→string resolver. No I/O, no host types.
/// </summary>
public static class MultiValue
{
    public static string Resolve(IReadOnlyList<string> values, MultiValueOptions m)
    {
        IEnumerable<string> seq = values;

        if (m.Whitelist.Count > 0)
        {
            seq = seq.Where(v => m.Whitelist.Contains(v, StringComparer.OrdinalIgnoreCase));
        }

        if (m.Blacklist.Count > 0)
        {
            seq = seq.Where(v => !m.Blacklist.Contains(v, StringComparer.OrdinalIgnoreCase));
        }

        if (m.Sort == SortOrder.NameAsc)
        {
            seq = seq.OrderBy(v => v, StringComparer.OrdinalIgnoreCase);
        }

        var list = seq.ToList();

        if (m.MaxCount > 0 && list.Count > m.MaxCount)
        {
            // KeepFirst takes the first N; DropAll empties the whole field (drives `{}` removal
            // in the engine since an empty resolution means an empty token).
            list = m.OnOverflow == OverflowPolicy.KeepFirst
                ? list.Take(m.MaxCount).ToList()
                : new List<string>();
        }

        return string.Join(m.Separator, list);
    }

    /// <summary>
    /// Performer-aware resolution. Like the string overload, but the richer per-performer records let
    /// it order by id or favorite and order/filter by gender. The cascade is
    /// whitelist → blacklist (by name) → gender-ignore → sort → gender-order → max → join, with
    /// gender-ignore and gender-order applied BEFORE the max-count limit so a dropped or
    /// down-ordered gender changes which performers survive the limit. The result is the joined
    /// performer NAMES, identical in shape to the string overload's output.
    /// </summary>
    public static string Resolve(IReadOnlyList<RenamerPerformer> performers, MultiValueOptions m)
    {
        IEnumerable<RenamerPerformer> seq = performers;

        if (m.Whitelist.Count > 0)
        {
            seq = seq.Where(p => m.Whitelist.Contains(p.Name, StringComparer.OrdinalIgnoreCase));
        }

        if (m.Blacklist.Count > 0)
        {
            seq = seq.Where(p => !m.Blacklist.Contains(p.Name, StringComparer.OrdinalIgnoreCase));
        }

        // Drop ignored genders before the limit so an ignored gender frees an overflow slot.
        // A performer with no gender set is always kept.
        if (m.IgnoreGenders.Count > 0)
        {
            seq = seq.Where(p => p.Gender is null
                || !m.IgnoreGenders.Contains(p.Gender, StringComparer.OrdinalIgnoreCase));
        }

        seq = m.Sort switch
        {
            SortOrder.NameAsc => seq.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase),
            SortOrder.IdAsc => seq.OrderBy(p => p.Id),
            SortOrder.FavoriteFirst => seq.OrderByDescending(p => p.Favorite)
                                          .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase),
            _ => seq, // None: preserve input order.
        };

        // Stable gender ordering layered on top of the chosen sort; unlisted/no gender sorts last.
        if (m.GenderOrder.Count > 0)
        {
            seq = seq.OrderBy(p => GenderRank(p.Gender, m.GenderOrder));
        }

        var list = seq.ToList();

        if (m.MaxCount > 0 && list.Count > m.MaxCount)
        {
            list = m.OnOverflow == OverflowPolicy.KeepFirst
                ? list.Take(m.MaxCount).ToList()
                : new List<RenamerPerformer>();
        }

        return string.Join(m.Separator, list.Select(p => p.Name));
    }

    /// <summary>
    /// The rank of <paramref name="gender"/> in <paramref name="order"/> (case-insensitive). A gender
    /// not in the list — including a null gender — ranks after every listed gender, so listed genders
    /// come first in the configured order and everything else trails.
    /// </summary>
    private static int GenderRank(string? gender, List<string> order)
    {
        if (gender is null)
        {
            return order.Count;
        }

        int idx = order.FindIndex(g => string.Equals(g, gender, StringComparison.OrdinalIgnoreCase));
        return idx < 0 ? order.Count : idx;
    }
}
