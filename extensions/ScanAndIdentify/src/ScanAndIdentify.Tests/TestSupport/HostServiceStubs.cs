using Cove.Core.Interfaces;

namespace ScanAndIdentify.Tests.TestSupport;

// Resolution markers only: the extension records that the host registered these services and never
// calls one, so every member refuses rather than answering something a test could assert on.
internal sealed class StubJobService : IJobService
{
    public string Enqueue(
        string type, string description, Func<IJobProgress, CancellationToken, Task> work, bool exclusive = true)
        => throw new NotSupportedException();

    public bool Cancel(string jobId) => throw new NotSupportedException();

    public bool ReorderQueued(string jobId, string? beforeJobId) => throw new NotSupportedException();

    public JobInfo? GetJob(string jobId) => throw new NotSupportedException();

    public IReadOnlyList<JobInfo> GetAllJobs() => throw new NotSupportedException();

    public IReadOnlyList<JobInfo> GetJobHistory() => throw new NotSupportedException();
}

internal sealed class StubScanService : IScanService
{
    public string StartScan(ScanOperationOptions? options = null) => throw new NotSupportedException();

    public Task<int> ImportDownloadedVideoAsync(string path, int? videoId, CancellationToken ct = default)
        => throw new NotSupportedException();

    public Task<int> ImportDownloadedImageAsync(string path, int? imageId, CancellationToken ct = default)
        => throw new NotSupportedException();

    public Task<int> ImportDownloadedGalleryAsync(string path, int? galleryId, CancellationToken ct = default)
        => throw new NotSupportedException();

    public Task<int> ImportDownloadedAudioAsync(string path, int? audioId, CancellationToken ct = default)
        => throw new NotSupportedException();

    public Task<int> ImportDownloadedTextAsync(string path, int? textDocumentId, CancellationToken ct = default)
        => throw new NotSupportedException();
}
