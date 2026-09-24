using System.Text;

namespace Renamer.Engine;

public enum SegKind { Literal, Token, GroupOpen, GroupClose }

// Text holds the literal text for a Literal, the token name for a Token, and the brace for a group
// marker.
public readonly record struct Segment(SegKind Kind, string Text);

// Template syntax, scanned in one left-to-right pass:
//   $$ emits one literal $; a name right after it still starts a token.
//   $name, where name is letters, digits and underscores, is a token.
//   A lone $ with no name char after it is a literal $.
//   { and } open and close a group when balanced.
//   A stray } at depth 0 becomes a literal }, and a { left unclosed renders as opened. The scan never
//   throws.
public static class Tokenizer
{
    public static List<Segment> Scan(string template)
    {
        var segs = new List<Segment>();
        var lit = new StringBuilder();
        int depth = 0;

        for (int i = 0; i < template.Length; i++)
        {
            char c = template[i];
            if (c == '$')
            {
                i = ScanDollar(template, i, segs, lit);
            }
            else if (c == '{')
            {
                Flush(segs, lit);
                segs.Add(new Segment(SegKind.GroupOpen, "{"));
                depth++;
            }
            else if (c == '}')
            {
                if (depth == 0)
                {
                    lit.Append('}');
                }
                else
                {
                    Flush(segs, lit);
                    segs.Add(new Segment(SegKind.GroupClose, "}"));
                    depth--;
                }
            }
            else
            {
                lit.Append(c);
            }
        }

        Flush(segs, lit);
        return segs;
    }

    // Scans from the $ at index i and returns the index of the last char it consumed, which the
    // caller's loop then steps past.
    private static int ScanDollar(string template, int i, List<Segment> segs, StringBuilder lit)
    {
        // When a token name follows the second $, as in "$$title", only the first $ is
        // consumed, so the loop reprocesses the second as a token start. Otherwise both are
        // consumed and "$$" yields exactly one "$".
        if (i + 1 < template.Length && template[i + 1] == '$')
        {
            lit.Append('$');
            bool tokenFollows = i + 2 < template.Length
                && (char.IsLetterOrDigit(template[i + 2]) || template[i + 2] == '_');
            if (!tokenFollows)
            {
                i++;
            }

            return i;
        }

        int j = i + 1;
        while (j < template.Length && (char.IsLetterOrDigit(template[j]) || template[j] == '_'))
        {
            j++;
        }

        if (j == i + 1)
        {
            lit.Append('$');
            return i;
        }

        Flush(segs, lit);
        segs.Add(new Segment(SegKind.Token, template.Substring(i + 1, j - i - 1)));
        return j - 1;
    }

    private static void Flush(List<Segment> segs, StringBuilder lit)
    {
        if (lit.Length > 0)
        {
            segs.Add(new Segment(SegKind.Literal, lit.ToString()));
            lit.Clear();
        }
    }
}
