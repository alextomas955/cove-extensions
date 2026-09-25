namespace WhisparrSync.Tests.TestSupport;

// The documents are inputs, never expectations: each is a response body taken verbatim from the
// build its file name states. An expectation computed from one would agree with it whatever it
// said, so a pin's expected value is transcribed by hand instead.
internal static class ProbeFixtures
{
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
