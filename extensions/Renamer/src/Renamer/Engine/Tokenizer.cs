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

        void Flush()
        {
            if (lit.Length > 0)
            {
                segs.Add(new Segment(SegKind.Literal, lit.ToString()));
                lit.Clear();
            }
        }

        for (int i = 0; i < template.Length; i++)
        {
            char c = template[i];
            if (c == '$')
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

                    continue;
                }

                int j = i + 1;
                while (j < template.Length && (char.IsLetterOrDigit(template[j]) || template[j] == '_'))
                {
                    j++;
                }

                if (j == i + 1)
                {
                    lit.Append('$');
                    continue;
                }

                Flush();
                segs.Add(new Segment(SegKind.Token, template.Substring(i + 1, j - i - 1)));
                i = j - 1;
            }
            else if (c == '{')
            {
                Flush();
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
                    Flush();
                    segs.Add(new Segment(SegKind.GroupClose, "}"));
                    depth--;
                }
            }
            else
            {
                lit.Append(c);
            }
        }

        Flush();
        return segs;
    }
}
