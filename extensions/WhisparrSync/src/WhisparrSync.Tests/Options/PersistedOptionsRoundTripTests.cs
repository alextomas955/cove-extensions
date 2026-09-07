using WhisparrSync.Contracts;
using WhisparrSync.Options;

namespace WhisparrSync.Tests.Options;

/// <summary>
/// Pins that an existing persisted options blob — one whose <c>DefaultMonitorScope</c> holds the byte-literal
/// PascalCase enum name — still loads to the correct <see cref="MonitorScope"/> member through the real store
/// read path (<see cref="OptionsStore"/> over the same <see cref="WhisparrOptions.JsonOptions"/> instance).
/// The stored blob keeps its own spelling independently of the Cove-facing wire, so re-casing a wire
/// serializer must never orphan a user's stored options. The scope names are hard-coded strings (not
/// <c>nameof</c>) precisely because they model the on-disk bytes, which stay frozen even though the wire
/// casing may differ and the C# identifiers keep PascalCase.
/// </summary>
[Trait("Tier", "L0")]
public sealed class PersistedOptionsRoundTripTests
{
    private static async Task<WhisparrOptions> LoadBlobAsync(string blob)
    {
        var store = new FakeStore();
        await store.SetAsync("options", blob);
        return await new OptionsStore(store).LoadAsync();
    }

    [Fact]
    public async Task OldBlob_NewReleasesScope_StillLoads()
    {
        var loaded = await LoadBlobAsync(
            """
            {
              "BaseUrl": "http://whisparr:6969",
              "SelectedVersion": "v3",
              "QualityProfileId": 4,
              "DefaultMonitorScope": "NewReleases"
            }
            """);

        Assert.Equal(MonitorScope.NewReleases, loaded.DefaultMonitorScope);
    }

    [Fact]
    public async Task OldBlob_AllScenesScope_StillLoads()
    {
        var loaded = await LoadBlobAsync(
            """
            {
              "BaseUrl": "http://whisparr:6969",
              "SelectedVersion": "v3",
              "QualityProfileId": 4,
              "DefaultMonitorScope": "AllScenes"
            }
            """);

        Assert.Equal(MonitorScope.AllScenes, loaded.DefaultMonitorScope);
    }
}
