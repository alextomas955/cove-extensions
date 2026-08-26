using Cove.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace Renamer.Tests.TestSupport;

/// <summary>
/// Registers the host configuration the library anchor is read from, so an integration test can
/// declare which folders Cove treats as library paths.
/// </summary>
internal static class LibraryPathsFixture
{
    internal static IServiceCollection AddLibraryPaths(
        this IServiceCollection services, params string[] paths)
        => services.AddSingleton(Config(paths));

    /// <summary>A configuration declaring <paramref name="paths"/> as Cove's library paths.</summary>
    internal static CoveConfiguration Config(params string[] paths)
        => new() { CovePaths = [.. paths.Select(p => new CovePath { Path = p })] };
}
