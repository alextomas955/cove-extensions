using WhisparrSync.Addressing;
using WhisparrSync.Contracts;
using WhisparrSync.Import;

namespace WhisparrSync.Tests.TestSupport;

internal sealed class CountingSampleFiles(SampleFile? answer) : ISampleFilePort
{
    public int Reads { get; private set; }

    public Task<SampleFile?> ReadSampleFileAsync(string coveRoot, CancellationToken ct)
    {
        Reads++;
        return Task.FromResult(answer);
    }
}

internal sealed class CountingInstanceRoots(IReadOnlyList<string> roots) : IReportedRootPort
{
    public int Reads { get; private set; }

    public Task<IReadOnlyList<string>?> ReadAsync(WhisparrGeneration generation, CancellationToken ct)
    {
        Reads++;
        return Task.FromResult<IReadOnlyList<string>?>(roots);
    }
}
