using System.Text;

namespace Renamer.Engine;

/// <summary>Kind of a parsed template segment.</summary>
public enum SegKind { Literal, Token, GroupOpen, GroupClose }

/// <summary>
/// One ordered piece of a parsed template. <see cref="Text"/> holds the literal
/// text for <see cref="SegKind.Literal"/>, or the token name for <see cref="SegKind.Token"/>;
/// it is <c>"{"</c>/<c>"}"</c> for the group markers.
/// </summary>
public readonly record struct Segment(SegKind Kind, string Text);

/// <summary>
/// Single-pass, regex-free template scanner. Turns a template
/// string into an ordered <see cref="Segment"/> list in one left-to-right O(n) pass:
/// <list type="bullet">
///   <item><c>$$</c> → one literal <c>$</c> (not a token).</item>
///   <item><c>$name</c> (letters/digits/underscore) → a <see cref="SegKind.Token"/> named <c>name</c>.</item>
///   <item>A lone <c>$</c> not followed by a name char → literal <c>$</c>.</item>
///   <item><c>{</c>/<c>}</c> → <see cref="SegKind.GroupOpen"/>/<see cref="SegKind.GroupClose"/> when balanced.</item>
///   <item>A stray <c>}</c> at depth 0 → literal <c>}</c> + <c>logUnbalanced</c>.</item>
///   <item>Unclosed <c>{</c> at EOF → <c>logUnbalanced</c>; never throws.</item>
/// </list>
/// Pure: no I/O, no host types. Regex is deliberately avoided — it mishandles <c>$$</c>
/// adjacency and brace balancing.
/// </summary>
public static class Tokenizer
{
    public static List<Segment> Scan(string template, Action<string>? logUnbalanced = null)
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
                // `$$` is the escape for a literal `$`. Emit one `$`, then:
                //  - if a token name follows the second `$` (e.g. `$$title`), consume only
                //    the FIRST `$` so the loop reprocesses the second as a token start
                //    -> literal `$` + Token `title`;
                //  - otherwise (`$$`, `$$ `, `$$$`) consume BOTH so `$$` is exactly one `$`.
                if (i + 1 < template.Length && template[i + 1] == '$')
                {
                    lit.Append('$');
                    bool tokenFollows = i + 2 < template.Length
                        && (char.IsLetterOrDigit(template[i + 2]) || template[i + 2] == '_');
                    if (!tokenFollows)
                    {
                        i++; // swallow the second $ (bare escape)
                    }

                    continue;
                }

                // Scan a token name: [A-Za-z0-9_]+.
                int j = i + 1;
                while (j < template.Length && (char.IsLetterOrDigit(template[j]) || template[j] == '_'))
                {
                    j++;
                }

                if (j == i + 1)
                {
                    // Lone $ not followed by a name char -> literal $.
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
                    // Stray } at depth 0 -> literal + log (unbalanced-brace safety).
                    lit.Append('}');
                    logUnbalanced?.Invoke("stray '}' treated as literal");
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
        if (depth > 0)
        {
            logUnbalanced?.Invoke($"{depth} unclosed '{{' — trailing group(s) rendered as opened");
        }

        return segs;
    }
}
