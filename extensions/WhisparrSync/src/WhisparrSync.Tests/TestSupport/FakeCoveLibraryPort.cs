using WhisparrSync.Library;

namespace WhisparrSync.Tests.TestSupport;

/// <summary>
/// In-memory <see cref="ICoveLibraryPort"/> for DB-free unit tests of the downstream matcher /
/// reconciliation logic (02-02): the seam faked so that logic is testable without a live CoveContext.
/// Returns caller-seeded <see cref="CoveVideo"/> DTOs from <see cref="LoadAllVideosAsync"/>. It references
/// only the WhisparrSync-owned DTO — no Cove.Core type — so it compiles on every test path. No disk, no DB.
/// </summary>
internal sealed class FakeCoveLibraryPort : ICoveLibraryPort
{
    private readonly List<CoveVideo> _videos = new();

    /// <summary>Number of <see cref="LoadAllVideosAsync"/> calls — lets a test prove the reconciliation reads the library once.</summary>
    public int LoadAllVideosCallCount { get; private set; }

    /// <summary>Seeds the videos <see cref="LoadAllVideosAsync"/> returns (additive across calls).</summary>
    public void Seed(params CoveVideo[] videos) => _videos.AddRange(videos);

    public Task<IReadOnlyList<CoveVideo>> LoadAllVideosAsync(CancellationToken ct = default)
    {
        LoadAllVideosCallCount++;
        return Task.FromResult<IReadOnlyList<CoveVideo>>([.. _videos]);
    }
}
