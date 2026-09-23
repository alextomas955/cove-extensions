using Renamer.Options;

namespace Renamer.Tests.TestSupport;

// Builds a Destination without spelling the object initializer at every site.
internal static class Dest
{
    // A destination rooted at root, with an optional relative template.
    internal static Destination At(string root, string template = "")
        => new() { Root = root, Template = template };

    // A destination measured from the library path containing the file.
    internal static Destination Own(string template = "") => new() { Template = template };
}
