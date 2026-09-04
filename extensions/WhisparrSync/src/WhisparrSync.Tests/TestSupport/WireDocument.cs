using System.Text.Json;

namespace WhisparrSync.Tests.TestSupport;

/// <summary>The committed wire document, read as the record of what this build mounts.</summary>
/// <remarks>
/// The document is emitted from the shipped registrations and a test fails when the committed copy
/// differs, so an assertion enumerated from it covers a route mounted later without an edit. A
/// hand-written route list would go on agreeing with itself while that route did whatever it liked.
/// </remarks>
internal static class WireDocument
{
    /// <summary>The route segment every one-entity route names its target kind through.</summary>
    internal const string KindSegment = "{kind}";

    /// <summary>One mounted route: the method a caller sends, and the path template it is at.</summary>
    /// <param name="Method">The lower-case HTTP method, as the document spells it.</param>
    /// <param name="Template">The path template, kind segment and all.</param>
    internal readonly record struct MountedRoute(string Method, string Template);

    /// <summary>Where the committed document lives, above this test assembly.</summary>
    /// <exception cref="InvalidOperationException">
    /// It was not found. An enumeration over a document that is not there would answer an empty
    /// route list, and every assertion driven from it would then hold over nothing.
    /// </exception>
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

    /// <summary>Every mounted route whose path names an entity kind, in a stable order.</summary>
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
