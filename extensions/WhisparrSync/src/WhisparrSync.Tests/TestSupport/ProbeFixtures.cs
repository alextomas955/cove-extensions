namespace WhisparrSync.Tests.TestSupport;

/// <summary>
/// Reads a captured Whisparr response document out of the test output directory.
/// </summary>
/// <remarks>
/// The documents are INPUTS, never expectations: each is a response body taken verbatim from the
/// build its file name states, so a classifier runs against a document an instance produced rather
/// than one invented to suit it. An expectation computed from one of these would agree with it
/// whatever either said, so a pin's expected value is transcribed by hand instead.
/// </remarks>
internal static class ProbeFixtures
{
    /// <summary>The document <paramref name="fileName"/> names, as text.</summary>
    /// <exception cref="InvalidOperationException">
    /// The file is not beside the test assembly. It gets there through the test project's own copy
    /// rule, so a miss means that rule stopped matching rather than that the caller asked wrongly.
    /// </exception>
    internal static string Read(string fileName)
    {
        string path = Path.Combine(AppContext.BaseDirectory, fileName);
        if (!File.Exists(path))
        {
            throw new InvalidOperationException(
                $"{fileName} is not next to the test assembly ({path}). It reaches the test output "
                    + "through this project's TestSupport/Fixtures copy rule, so that copy has been dropped.");
        }

        return File.ReadAllText(path);
    }
}
