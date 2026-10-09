using System.Text.Json;

namespace WhisparrSync.Tests.TestSupport;

// The document is emitted from the shipped registrations and a test fails when the committed copy
// differs, so an assertion enumerated from it covers a route mounted later without an edit. A
// hand-written route list would go on agreeing with itself.
internal static class WireDocument
{
    internal const string KindSegment = "{kind}";

    // The method is lower-case, as the document spells it.
    internal readonly record struct MountedRoute(string Method, string Template);

    internal static string Path()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = System.IO.Path.Combine(directory.FullName, "wire", "openapi.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException(
            $"No wire/openapi.json was found above {AppContext.BaseDirectory}, so the mounted route "
                + "set cannot be read and an assertion over it would hold over nothing.");
    }

    internal static IReadOnlyList<MountedRoute> KindTakingRoutes()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path()));

        return
        [
            .. document.RootElement.GetProperty("paths").EnumerateObject()
                .Where(path => path.Name.Contains(KindSegment, StringComparison.Ordinal))
                .SelectMany(path => path.Value.EnumerateObject()
                    .Select(operation => new MountedRoute(operation.Name, path.Name)))
                .OrderBy(route => route.Template, StringComparer.Ordinal)
                .ThenBy(route => route.Method, StringComparer.Ordinal)
        ];
    }
}
