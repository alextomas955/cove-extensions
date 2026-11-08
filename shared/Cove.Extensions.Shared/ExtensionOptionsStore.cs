using System.Collections;
using System.Reflection;
using System.Text.Json;
using Cove.Plugins;
using Microsoft.Extensions.Logging;

namespace Cove.Extensions.Shared;

/// <summary>
/// Thin async load/save layer for an extension's options model over Cove's
/// <see cref="IExtensionStore"/>. Stores a single JSON blob under the <c>"options"</c> key.
/// </summary>
/// <remarks>
/// Each extension supplies its own <typeparamref name="TOptions"/> model, its own
/// <see cref="JsonSerializerOptions"/>, and a default-value factory, so serialization behavior is
/// identical to a per-extension store.
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
    /// Public because a one-time conversion has to reach the raw blob: a legacy shape does not bind to
    /// the current model, and <see cref="LoadAsync"/> answers a bind failure with defaults, so a
    /// converter going through the typed load would rewrite defaults over the stored configuration.
    /// </remarks>
    public const string Key = "options";

    /// <summary>
    /// Loads the persisted options. Returns defaults when the key is absent (first run) or when the
    /// stored blob is corrupt (catches <see cref="JsonException"/>) - a hand-edited/garbage blob never throws.
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

            return Repair(loaded);
        }
        catch (JsonException ex)
        {
            // Loud, because this is the one place a whole stored configuration is discarded with
            // nothing observable happening: defaults are indistinguishable from a correct empty
            // configuration at every layer above. The blob itself is never logged - it is the user's
            // configuration.
            ExtensionOptionsStoreLog.StoredOptionsDiscarded(logger, typeof(TOptions).Name, ex);
            return defaultFactory();
        }
    }

    /// <summary>
    /// Gives options bound from anywhere else, such as a request body, the repair <see cref="LoadAsync"/>
    /// gives a stored blob: declared non-null members restored, then the caller's normalizer.
    /// </summary>
    public TOptions Repair(TOptions bound)
    {
        RestoreDeclaredNonNull(bound, defaultFactory(), new NullabilityInfoContext());
        return normalize is null ? bound : normalize(bound);
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
    /// A property initializer runs only for an absent key, so a stored <c>"DropOrder": null</c> binds
    /// to null and the member contradicts its own declaration; the first consumer to dereference it
    /// throws, and <see cref="LoadAsync"/> catches only <see cref="JsonException"/>. The criterion is
    /// the declared nullability, so a member whose null is a real state keeps it and a member added to
    /// the model later is covered without an edit here.
    /// </remarks>
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
            if (fallback is not null)
            {
                RepairMember(loaded, property, fallback, nullability);
            }
        }
    }

    /// <summary>Repairs one member: its own null, a nested object's, or a collection entry's.</summary>
    private static void RepairMember(
        object loaded, PropertyInfo property, object fallback, NullabilityInfoContext nullability)
    {
        var value = property.GetValue(loaded);
        if (value is null)
        {
            if (property.CanWrite && nullability.Create(property).ReadState == NullabilityState.NotNull)
            {
                property.SetValue(loaded, fallback);
            }

            return;
        }

        if (IsNestedOptions(property.PropertyType))
        {
            RestoreDeclaredNonNull(value, fallback, nullability);
            return;
        }

        DropDeclaredNonNullEntries(value, nullability.Create(property));
    }

    /// <summary>
    /// Drops every entry a collection holds as <c>null</c> whose element the model declares non-null.
    /// </summary>
    /// <remarks>
    /// Dropped rather than replaced, because an element has no counterpart in the defaults to restore
    /// from: a list of rules has no rule that stands for "the one that should have been here". An entry
    /// the model allows to be null is kept, on the same criterion the members above use.
    /// <para>
    /// Without this a stored <c>[null]</c> survives binding and a consumer dereferences it, which is a
    /// <c>NullReferenceException</c> rather than the <see cref="JsonException"/> <see cref="LoadAsync"/>
    /// catches - so it surfaces wherever the list is read rather than where it was loaded.
    /// </para>
    /// </remarks>
    private static void DropDeclaredNonNullEntries(object value, NullabilityInfo info)
    {
        if (value is IDictionary map)
        {
            // The value is the second argument; a null key cannot be stored at all.
            if (DeclaredNonNull(info, index: 1))
            {
                DropNullValues(map);
            }

            return;
        }

        // A fixed-size list is an array, which cannot be shortened in place. No options model here
        // declares one, and rebuilding the member would be a second way to write a collection.
        if (value is IList { IsReadOnly: false, IsFixedSize: false } list && DeclaredNonNull(info, index: 0))
        {
            DropNullItems(list);
        }
    }

    private static bool DeclaredNonNull(NullabilityInfo info, int index)
        => info.GenericTypeArguments.Length > index
            && info.GenericTypeArguments[index].ReadState == NullabilityState.NotNull;

    private static void DropNullValues(IDictionary map)
    {
        var nullKeys = map.Keys.Cast<object>().Where(key => map[key] is null).ToList();
        foreach (var key in nullKeys)
        {
            map.Remove(key);
        }
    }

    private static void DropNullItems(IList list)
    {
        for (int i = list.Count - 1; i >= 0; i--)
        {
            if (list[i] is null)
            {
                list.RemoveAt(i);
            }
        }
    }

    /// <summary>
    /// True for a member that is itself an options object - one whose own members can carry the same
    /// null. A collection is excluded: its elements are handled by
    /// <see cref="DropDeclaredNonNullEntries"/>, which has no counterpart in the defaults to restore from.
    /// </summary>
    private static bool IsNestedOptions(Type type)
        => type.IsClass && type != typeof(string) && !typeof(IEnumerable).IsAssignableFrom(type);
}

/// <summary>
/// The source-generated log message for the one failure <see cref="ExtensionOptionsStore{TOptions}"/>
/// swallows. Non-generic, because the generator declares its partial methods per closed type; the type
/// being loaded travels as an argument instead.
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
