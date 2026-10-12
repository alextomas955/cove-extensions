using WhisparrSync.Import;

namespace WhisparrSync.Tests.TestSupport;

// The one spelling both tree doubles key on, so neither can drift from the guard the shipped port
// normalises with.
internal static class TreePathSpelling
{
    public static string Spelled(string path) => PathCandidateGuard.Normalize(path);

    public static string PrefixOf(string folder) => Spelled(folder).TrimEnd('/') + "/";
}
