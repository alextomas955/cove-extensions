using Renamer.Options;

namespace Renamer.Tests.TestSupport;

/// <summary>Builds a <see cref="Destination"/> without spelling the object initializer at every site.</summary>
internal static class Dest
{
    /// <summary>A destination rooted at <paramref name="root"/>, with an optional relative template.</summary>
    internal static Destination At(string root, string template = "")
        => new() { Root = root, Template = template };

    /// <summary>A destination measured from the library path containing the file.</summary>
    internal static Destination Own(string template = "") => new() { Template = template };
}
