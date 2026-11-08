using Renamer.Options;

namespace Renamer.Tests.TestSupport;

internal static class Dest
{
    internal static Destination At(string root, string template = "")
        => new() { Root = root, Template = template };
}
