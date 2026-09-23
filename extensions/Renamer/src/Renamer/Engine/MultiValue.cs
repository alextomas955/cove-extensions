using Renamer.Options;

namespace Renamer.Engine;

// Resolves the $performers and $tags lists into one joined string under the field's
// MultiValueOptions. The order is whitelist, blacklist, sort, max count, then join with the
// configured separator.
public static class MultiValue
{
    // Values carrying no entity identity: sort, then max, then join. The whitelist and blacklist are
    // skipped because this overload serves the fixed preview samples, whose values name no library
    // entity, so matching them against a rule that identifies a real tag could only agree by chance.
    public static string Resolve(IReadOnlyList<string> values, MultiValueOptions m)
    {
        IEnumerable<string> seq = values;

        if (m.Sort == SortOrder.NameAsc)
        {
            seq = seq.OrderBy(v => v, StringComparer.OrdinalIgnoreCase);
        }

        return string.Join(m.Separator, Capped(seq, m));
    }

    // The whitelist and blacklist match on each tag's stable id, and the surviving tags render their
    // current names. Sorting is by name, which is what the token renders.
    public static string Resolve(IReadOnlyList<(int Id, string Name)> tags, MultiValueOptions m)
    {
        var seq = Filtered(tags, t => t.Id, m);

        if (m.Sort == SortOrder.NameAsc)
        {
            seq = seq.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase);
        }

        return string.Join(m.Separator, Capped(seq, m).Select(t => t.Name));
    }

    // Performer records also carry id, favorite and gender. The order is whitelist, blacklist by id,
    // gender-ignore, sort, gender-order, max count, then join of the names. Gender-ignore and
    // gender-order run before the max count, so they change which performers survive the limit.
    public static string Resolve(IReadOnlyList<RenamerPerformer> performers, MultiValueOptions m)
    {
        var seq = Filtered(performers, p => p.Id, m);

        // Ignored genders drop before the limit, so an ignored gender frees an overflow slot. A
        // performer with no gender set is kept.
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
            _ => seq,
        };

        // A stable ordering layered over the chosen sort, so performers of the same gender keep it.
        if (m.GenderOrder.Count > 0)
        {
            seq = seq.OrderBy(p => GenderRank(p.Gender, m.GenderOrder));
        }

        return string.Join(m.Separator, Capped(seq, m).Select(p => p.Name));
    }

    private static IEnumerable<T> Filtered<T>(IEnumerable<T> values, Func<T, int> id, MultiValueOptions m)
    {
        if (m.WhitelistIds.Count > 0)
        {
            values = values.Where(v => m.WhitelistIds.Contains(id(v)));
        }

        return m.BlacklistIds.Count > 0 ? values.Where(v => !m.BlacklistIds.Contains(id(v))) : values;
    }

    // Over the max count, KeepFirst keeps the first N and DropAll empties the field, which makes the
    // token empty and so collapses any {} group around it.
    private static List<T> Capped<T>(IEnumerable<T> values, MultiValueOptions m)
    {
        var list = values.ToList();
        if (m.MaxCount <= 0 || list.Count <= m.MaxCount)
        {
            return list;
        }

        return m.OnOverflow == OverflowPolicy.KeepFirst ? list.Take(m.MaxCount).ToList() : [];
    }

    // A gender absent from the list, null included, ranks after every listed gender.
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
