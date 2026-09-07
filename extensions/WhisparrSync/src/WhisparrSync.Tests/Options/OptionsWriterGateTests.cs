using Cove.Extensions.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Cove.Plugins;
using WhisparrSync.Client;
using WhisparrSync.Contracts;
using WhisparrSync.Options;
using WhisparrSync.Tests.TestSupport;
using Ext = global::WhisparrSync.WhisparrSync;

namespace WhisparrSync.Tests.Options;

/// <summary>
/// The options blob has four writers and the shipped settings page gates Save, Test and Register on three
/// independent flags, so two of them are in flight together by design. These drive the REAL handlers with one
/// writer's read parked across another's whole save, which is the shape that loses a saved connection.
/// </summary>
[Trait("Tier", "L0")]
public sealed class OptionsWriterGateTests
{
    private const string OldUrl = "http://old:6969";
    private const string NewUrl = "http://new:6969";
    private const string OldKey = "old-key";
    private const string NewKey = "new-key";
    private const string ProbedVersion = "3.0.2.4497";
    private const string CoveOrigin = "http://cove:5073";

    // The parked writer completes its save while the other writer is still in flight. Ungated, that other
    // writer finishes first and is then overwritten; gated, it waits. Either way the wait is on a store backed
    // by a dictionary, so a bound five orders of magnitude above the ungated cost cannot false-PASS the
    // unfixed code and only a stalled machine could false-FAIL it.
    private static readonly TimeSpan SettleBound = TimeSpan.FromMilliseconds(250);

    // The keying proofs wait on an update that either completes at dictionary speed or not at all, so this
    // bound only has to be far above the former.
    private static readonly TimeSpan KeyingBound = TimeSpan.FromSeconds(1);

    private static async Task<(Ext Ext, StalledReadStore Store)> SeededAsync(WhisparrOptions seed)
    {
        var inner = new ConcurrentFakeStore();
        await new OptionsStore(inner).SaveAsync(seed);
        var store = new StalledReadStore("options", inner);
        var ext = new Ext();
        ((IStatefulExtension)ext).SetStore(store);
        return (ext, store);
    }

    private static Task<WhisparrOptions> PersistedAsync(StalledReadStore store)
        => new OptionsStore(store).LoadAsync();

    /// <summary>
    /// A connection test's version stamp must not carry the pre-save connection back over a save that
    /// repointed it. The stamp itself is dropped here by <see cref="WhisparrOptions.WithSubmitted"/>, which
    /// clears a reading that described the old address; that a stamped write happened at all is what the
    /// same-host case below observes.
    /// </summary>
    [Fact]
    public async Task SaveInterleavedWithVersionStamp_KeepsTheSavedConnection()
    {
        var (ext, store) = await SeededAsync(new WhisparrOptions { BaseUrl = OldUrl, ApiKey = OldKey });

        var stamp = Ext.PersistDetectedVersionAsync(
            new OptionsStore(store), OldUrl, ProbedVersion, observedAt: 12345, CancellationToken.None);
        await store.Parked;
        var save = ext.SaveOptionsAsync(new OptionsSaveRequest(NewUrl, NewKey, "v3"), CancellationToken.None);
        await Task.WhenAny(save, Task.Delay(SettleBound));
        store.Release();
        await Task.WhenAll(stamp, save);

        var persisted = await PersistedAsync(store);
        // Asserted as one pair so a revert reports BOTH fields; asserting them in sequence would stop at the
        // URL and leave the key's fate unstated, which understates what a stale write costs.
        Assert.Equal((NewUrl, NewKey), (persisted.BaseUrl, persisted.ApiKey));
    }

    /// <summary>
    /// The same interleaving with a save that keeps the address: the saved key must survive AND the version
    /// stamp must land. A gate that serialized by dropping the later write would satisfy the first assertion
    /// alone, so the second is what distinguishes a gate from a silent loss.
    /// </summary>
    [Fact]
    public async Task SaveInterleavedWithVersionStamp_SameHost_KeepsBothWrites()
    {
        var (ext, store) = await SeededAsync(new WhisparrOptions { BaseUrl = OldUrl, ApiKey = OldKey });

        var stamp = Ext.PersistDetectedVersionAsync(
            new OptionsStore(store), OldUrl, ProbedVersion, observedAt: 12345, CancellationToken.None);
        await store.Parked;
        var save = ext.SaveOptionsAsync(new OptionsSaveRequest(OldUrl, NewKey, "v3"), CancellationToken.None);
        await Task.WhenAny(save, Task.Delay(SettleBound));
        store.Release();
        await Task.WhenAll(stamp, save);

        var persisted = await PersistedAsync(store);
        Assert.Equal(NewKey, persisted.ApiKey);
        Assert.Equal(ProbedVersion, persisted.DetectedVersion);
    }

    /// <summary>
    /// Webhook registration is the worst-shaped writer: it mints the secret in one write and stamps the
    /// resolved host in another, off options loaded before either. Both of its own fields must land and
    /// neither may carry the pre-save connection back.
    /// </summary>
    [Fact]
    public async Task SaveInterleavedWithWebhookRegistration_KeepsTheSavedConnection()
    {
        var (ext, store) = await SeededAsync(new WhisparrOptions { BaseUrl = OldUrl, ApiKey = OldKey });
        var client = new WhisparrClient(new HttpClient(FakeHttpMessageHandler.Json("[]")));

        var register = ext.RegisterWebhookAsync(CoveOrigin, client, CancellationToken.None);
        await store.Parked;
        var save = ext.SaveOptionsAsync(new OptionsSaveRequest(NewUrl, NewKey, "v3"), CancellationToken.None);
        await Task.WhenAny(save, Task.Delay(SettleBound));
        store.Release();
        await Task.WhenAll(register, save);

        var persisted = await PersistedAsync(store);
        Assert.Equal((NewUrl, NewKey), (persisted.BaseUrl, persisted.ApiKey));
        Assert.Equal(CoveOrigin, persisted.WebhookHost);
        Assert.NotEmpty(persisted.WebhookSecret);
    }

    /// <summary>
    /// Every extension persists its options under the same <c>"options"</c> key, so a gate keyed on that
    /// string would serialize unrelated extensions against each other.
    /// </summary>
    [Fact]
    public async Task StoresOverDifferentStoreInstances_DoNotSerialize()
    {
        var parked = new StalledReadStore("options");
        var other = new ExtensionOptionsStore<WhisparrOptions>(
            new ConcurrentFakeStore(),
            WhisparrOptions.JsonOptions,
            static () => new WhisparrOptions(),
            NullLogger.Instance);

        var held = new OptionsStore(parked).UpdateAsync(current => current with { ApiKey = OldKey });
        await parked.Parked;
        var free = other.UpdateAsync(current => current with { ApiKey = NewKey });
        await Task.WhenAny(free, Task.Delay(KeyingBound));

        // Sound in this direction only: a shared gate could never let the second store finish while the
        // first is parked inside it, so there is no false PASS. Only a stalled machine could false-FAIL,
        // and the bound absorbs that.
        Assert.True(free.IsCompletedSuccessfully);
        parked.Release();
        await held;
    }

    /// <summary>
    /// Two options models over ONE store must share one gate. A static gate declared on the generic store
    /// exists once per closed generic type, so this is what a per-model gate would fail.
    /// </summary>
    [Fact]
    public async Task StoresOverOneStoreInstance_SerializeAcrossOptionsModels()
    {
        var store = new StalledReadStore("options");
        var probe = new ExtensionOptionsStore<ProbeOptions>(
            store,
            WhisparrOptions.JsonOptions,
            static () => new ProbeOptions(),
            NullLogger.Instance);

        var held = new OptionsStore(store).UpdateAsync(current => current with { ApiKey = OldKey });
        await store.Parked;
        var waiting = probe.UpdateAsync(current => current with { Marker = "written" });
        await Task.WhenAny(waiting, Task.Delay(KeyingBound));

        Assert.False(waiting.IsCompleted); // still behind the first store's gate
        store.Release();
        await Task.WhenAll(held, waiting);
        Assert.Equal("written", (await probe.LoadAsync()).Marker);
    }

    /// <summary>A second options model over the same store, so the two closed generic types differ.</summary>
    private sealed record ProbeOptions
    {
        public string Marker { get; init; } = "";
    }
}
