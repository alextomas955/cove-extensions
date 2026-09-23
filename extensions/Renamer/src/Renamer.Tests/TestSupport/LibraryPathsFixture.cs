using Cove.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace Renamer.Tests.TestSupport;

// Registers the host configuration the library anchor is read from, so an integration test can
// declare which folders Cove treats as library paths.
internal static class LibraryPathsFixture
{
    internal static IServiceCollection AddLibraryPaths(
        this IServiceCollection services, params string[] paths)
        => services.AddSingleton(Config(paths));

    // A configuration declaring paths as Cove's library paths.
    internal static CoveConfiguration Config(params string[] paths)
        => new() { CovePaths = [.. paths.Select(p => new CovePath { Path = p })] };
}
