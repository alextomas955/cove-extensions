using System.Collections;
using System.Reflection;
using System.Text.Json;
using Cove.Plugins;
using Microsoft.Extensions.Logging;
using System.Runtime.CompilerServices;

namespace Cove.Extensions.Shared;

/// <summary>
/// The per-store writer gate the options stores serialize their read-modify-writes on.
/// </summary>
/// <remarks>
/// <para>
/// Non-generic on purpose. A static field on <c>ExtensionOptionsStore&lt;TOptions&gt;</c> exists once per
/// CLOSED generic type, so two options models over one store would resolve two independent gates and
/// serialize against neither.
/// </para>
/// <para>
/// Keyed on the store INSTANCE rather than the store key: every extension persists its options under the
/// same <c>"options"</c> key, so a key-string gate would serialize unrelated extensions against each other.
/// A <see cref="ConditionalWeakTable{TKey,TValue}"/> keys on reference identity by construction and holds no
/// strong reference to the host's store.
/// </para>
/// </remarks>
internal static class OptionsStoreGates
{
    private static readonly ConditionalWeakTable<IExtensionStore, SemaphoreSlim> Gates = new();

    internal static SemaphoreSlim For(IExtensionStore store)
        => Gates.GetValue(store, static _ => new SemaphoreSlim(1, 1));
}

/// <summary>
/// Thin async load/save layer for an extension's options model over Cove's
/// <see cref="IExtensionStore"/>. Stores a single JSON blob under the <c>"options"</c> key.
/// </summary>
/// <remarks>
/// Takes an <see cref="IExtensionStore"/> directly (not <c>FullExtensionBase.Store</c>) so it is
/// unit-testable host-free against an in-memory fake. <see cref="IExtensionStore"/> is fully async —
/// these methods never block on the store. Each extension supplies its own <typeparamref name="TOptions"/>
/// model, its own <see cref="JsonSerializerOptions"/>, and a default-value factory, so serialization
/// behavior is identical to a per-extension store.
/// <para>
/// The optional <c>normalize</c> runs on a blob that bound, after the non-nullable members are
/// restored, so an extension can replace a stored value its own model cannot honor. Such a rule
/// belongs on the load path rather than at each read: a check the consumer performs is a check the
/// next consumer can omit.
/// </para>
/// </remarks>
/// <typeparam name="TOptions">The extension's options model.</typeparam>
public class ExtensionOptionsStore<TOptions>(
    IExtensionStore store,
    JsonSerializerOptions jsonOptions,
    Func<TOptions> defaultFactory,
    ILogger logger,
    Func<TOptions, TOptions>? normalize = null)
    where TOptions : class
{
    /// <summary>The single store key the options blob lives under.</summary>
    /// <remarks>
    /// Public because a one-time conversion has to reach the RAW blob: a legacy shape does not bind to
    /// the current model, and <see cref="LoadAsync"/> answers a bind failure with defaults, so a
    /// converter going through the typed load would rewrite defaults over the stored configuration.
    /// </remarks>
    public const string Key = "options";

    private readonly SemaphoreSlim _gate = OptionsStoreGates.For(store);

    /// <summary>
    /// Loads the persisted options. Returns defaults when the key is absent (first run) or when the
    /// stored blob is corrupt (catches <see cref="JsonException"/>) — a hand-edited/garbage blob never throws.
    /// A blob that binds is returned with every member the model declares non-nullable restored to its
    /// default where the blob set it to <c>null</c> (see <see cref="RestoreDeclaredNonNull"/>), and then
    /// passed through the caller's normalizer.
    /// </summary>
    public async Task<TOptions> LoadAsync(CancellationToken ct = default)
    {
        var json = await store.GetAsync(Key, ct);
        if (string.IsNullOrWhiteSpace(json))
        {
            return defaultFactory();
        }

        try
        {
            var loaded = JsonSerializer.Deserialize<TOptions>(json, jsonOptions);
            if (loaded is null)
            {
                return defaultFactory();
            }

            RestoreDeclaredNonNull(loaded, defaultFactory(), new NullabilityInfoContext());
            return normalize is null ? loaded : normalize(loaded);
        }
        catch (JsonException ex)
        {
            // Loud, because this is the one place a whole stored configuration can be discarded with
            // nothing observable happening: defaults are indistinguishable from a correct empty
            // configuration at every layer above, so the symptom is settings that read as unset and a
            // panel, an API response and an end-to-end test that all agree on the wrong answer. The blob
            // itself is never logged — it is the user's configuration.
            ExtensionOptionsStoreLog.StoredOptionsDiscarded(logger, typeof(TOptions).Name, ex);
            return defaultFactory();
        }
    }

    /// <summary>Serializes the options to the single <c>"options"</c> JSON blob.</summary>
    public async Task SaveAsync(TOptions options, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(options, jsonOptions);
        await store.SetAsync(Key, json, ct);
    }

    /// <summary>
    /// Replaces with its default every member the deserialized options left <c>null</c> that the model
    /// declares as non-nullable, then recurses into the nested option objects.
    /// </summary>
    /// <remarks>
    /// A property initializer runs only for an ABSENT key, so a stored <c>"DropOrder": null</c> binds
    /// to null and the member contradicts its own declaration; the first consumer to dereference it
    /// throws, and <see cref="LoadAsync"/> catches only <see cref="JsonException"/>. The criterion is
    /// the declared nullability, so a member whose null is a real state keeps it and a member added to
    /// the model later is covered without an edit here — a hand-written member list is a list that
    /// goes stale.
    /// </remarks>
    /// <summary>
    /// Loads the options, applies <paramref name="mutate"/>, and saves the result — the whole read-modify-write
    /// under this store's writer gate, so no other writer can save between the load and the save. Returns the
    /// saved value, or the loaded value when <paramref name="mutate"/> returns <c>null</c> (nothing is written).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <paramref name="mutate"/> is synchronous so no I/O can run while the gate is held: a caller that
    /// awaited an outbound call inside it would hold every other options writer for that call's duration.
    /// </para>
    /// <para>
    /// A semaphore serializes writers within ONE process. Cove hosts its extensions in a single process, so
    /// that covers every writer this assembly has — but it claims nothing about a second process, and nothing
    /// about a writer that reaches the same blob without coming through here.
    /// </para>
    /// </remarks>
    public async Task<TOptions> UpdateAsync(Func<TOptions, TOptions?> mutate, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var current = await LoadAsync(ct);
            if (mutate(current) is not { } next)
            {
                return current;
            }

            await SaveAsync(next, ct);
            return next;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static void RestoreDeclaredNonNull(object loaded, object defaults, NullabilityInfoContext nullability)
    {
        if (loaded.GetType() != defaults.GetType())
        {
            return;
        }

        foreach (var property in loaded.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length > 0 || property.GetMethod is null)
            {
                continue;
            }

            var fallback = property.GetValue(defaults);
            if (fallback is null)
            {
                continue;
            }

            var value = property.GetValue(loaded);
            if (value is null)
            {
                if (property.CanWrite && nullability.Create(property).ReadState == NullabilityState.NotNull)
                {
                    property.SetValue(loaded, fallback);
                }
            }
            else if (IsNestedOptions(property.PropertyType))
            {
                RestoreDeclaredNonNull(value, fallback, nullability);
            }
        }
    }

    /// <summary>
    /// True for a member that is itself an options object — one whose own members can carry the same
    /// null. A collection is excluded: its ELEMENTS have no counterpart in the defaults to restore from.
    /// </summary>
    private static bool IsNestedOptions(Type type)
        => type.IsClass && type != typeof(string) && !typeof(IEnumerable).IsAssignableFrom(type);
}

/// <summary>
/// The source-generated log message for the one failure <see cref="ExtensionOptionsStore{TOptions}"/>
/// swallows. Non-generic and static because the generator's partial methods are declared per type and a
/// generic owner buys nothing here — the type being loaded travels as an argument instead.
/// </summary>
internal static partial class ExtensionOptionsStoreLog
{
    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Warning,
        Message = "[options] the stored {Options} blob could not be read and DEFAULTS were used instead; "
            + "every configured setting will read as unset until it is saved again")]
    public static partial void StoredOptionsDiscarded(ILogger logger, string options, Exception ex);
}
