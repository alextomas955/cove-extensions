using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using WhisparrSync.Contracts;

namespace WhisparrSync.Whisparr;

/// <summary>What one registration attempt turned out to be, read back off the instance.</summary>
/// <remarks>
/// The status is decided by re-reading the notification list after the write, never by the status of
/// the write. An acceptance says a request was well formed; it does not say the notification now
/// points anywhere. The refusal names the instance's own property name and error code.
/// </remarks>
public sealed record CallbackRegistrationOutcome(
    RegistrationStatus Status,
    string? StoredAddress,
    bool Created,
    string? Refusal);

/// <summary>Registers and reads back this extension's callback on one Whisparr instance.</summary>
public interface IWhisparrNotificationPort
{
    /// <summary>Registers <paramref name="callbackAddress"/>, creating or updating in place.</summary>
    /// <remarks>
    /// The binding carries the generation, which selects the carrier the secret travels in where
    /// that generation can carry one off the address. It is a parameter rather than held state:
    /// which generation is connected is a stored setting, so an instance obtained ahead of the call
    /// would be one bound before the connection it describes was known.
    /// </remarks>
    Task<CallbackRegistrationOutcome> RegisterAsync(
        WhisparrBinding binding, string callbackAddress, string secret, CancellationToken ct);

    /// <summary>Whether the instance holds this extension's registration, as it answers now.</summary>
    Task<CallbackRegistrationOutcome> ReadAsync(WhisparrBinding binding, CancellationToken ct);
}

internal sealed class NotificationPort(IWhisparrInstanceFactory instances, ILogger log)
    : IWhisparrNotificationPort
{
    // The registration is found again by this name alone, so changing it leaves the old registration
    // in place and delivering, and creates a second. The instance does not refuse a second entry
    // under this name when the address matches, so RegistrationGate serialises find-then-write.
    internal const string RegistrationName = "Cove Whisparr Sync";

    internal const string UrlField = "url";

    internal const string MethodField = "method";

    // What this number names is not established; deliveries arrived on both v2 and v3 with the
    // method field set to it.
    internal const int PostMethod = 1;

    internal const string WebhookImplementation = "Webhook";

    // The property name and error code a duplicate-name refusal reports.
    internal const string DuplicateNameProperty = "Name";

    internal const string DuplicateNameErrorCode = "PredicateValidator";

    // A trigger the instance raises on its own schedule says nothing about its library, so those are
    // left off. Everything else the generation's own schema declares is subscribed, because v2 and
    // v3 declare different trigger sets.
    private static bool IsSelfRaised(string flag)
        => flag.Contains("health", StringComparison.OrdinalIgnoreCase)
            || flag.Contains("applicationupdate", StringComparison.OrdinalIgnoreCase);

    public async Task<CallbackRegistrationOutcome> RegisterAsync(
        WhisparrBinding binding, string callbackAddress, string secret, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentException.ThrowIfNullOrWhiteSpace(callbackAddress);
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);

        var generation = binding.Generation;
        var instance = instances.Bound(binding);
        var schema = await ReadWebhookSchemaAsync(instance, ct).ConfigureAwait(false);
        if (schema is null)
        {
            return new CallbackRegistrationOutcome(
                RegistrationStatus.NotCheckedYet, null, false, "the instance declared no Webhook connection");
        }

        // A generation holding no carrier role registers the address and no field for a secret.
        var carried = GenerationCapabilities.For(generation)
            .Obtain<IOutOfBandSecretRegistration>()
            .Match<OutOfBandSecretField?>(role => role.Carry(secret), _ => null);

        var listed = await FindRegistrationAsync(instance, ct).ConfigureAwait(false);
        var created = listed is null;

        var written = listed is null
            ? await instance.CreateNotificationAsync(
                CreateBody(schema, callbackAddress, carried), ct).ConfigureAwait(false)
            : await instance.UpdateNotificationAsync(
                IdOf(listed),
                UpdateBody(listed, callbackAddress, carried),
                ct).ConfigureAwait(false);

        // The read-back is the answer. The write's status is read only to name a refusal the
        // read-back would otherwise report as a bare absence.
        var readBack = await ReadAsync(binding, ct).ConfigureAwait(false);
        var refusal = readBack.Status == RegistrationStatus.Registered
            && string.Equals(readBack.StoredAddress, callbackAddress, StringComparison.Ordinal)
                ? null
                : RefusalIn(written)
                    ?? DescribeMismatch(written.StatusCode, readBack.StoredAddress, callbackAddress);

        if (refusal is not null)
        {
            WhisparrSyncLog.CallbackRegistrationDidNotTake(log, generation, written.StatusCode);
        }

        return readBack with
        {
            Created = created,
            Refusal = refusal,
            Status = refusal is null ? RegistrationStatus.Registered : RegistrationStatus.NotRegistered,
        };
    }

    public async Task<CallbackRegistrationOutcome> ReadAsync(
        WhisparrBinding binding, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(binding);

        var listed = await FindRegistrationAsync(instances.Bound(binding), ct).ConfigureAwait(false);
        return listed is null
            ? new CallbackRegistrationOutcome(RegistrationStatus.NotRegistered, null, false, null)
            : new CallbackRegistrationOutcome(
                RegistrationStatus.Registered, FieldValue(listed, UrlField)?.ToString(), false, null);
    }

    private static async Task<JsonObject?> ReadWebhookSchemaAsync(
        IWhisparrClient instance, CancellationToken ct)
    {
        var answered = await instance.ReadNotificationSchemaAsync(ct).ConfigureAwait(false);
        return ParseArray(answered)?
            .OfType<JsonObject>()
            .FirstOrDefault(entry => StringOf(entry, "implementation") == WebhookImplementation);
    }

    private static async Task<JsonObject?> FindRegistrationAsync(
        IWhisparrClient instance, CancellationToken ct)
    {
        var answered = await instance.ListNotificationsAsync(ct).ConfigureAwait(false);
        return ParseArray(answered)?
            .OfType<JsonObject>()
            .FirstOrDefault(entry => StringOf(entry, "name") == RegistrationName);
    }

    // The implementation identifiers and the trigger flags are echoed from the schema entry the
    // instance returned, never written as literals: v2 and v3 declare different values.
    private static JsonObject CreateBody(
        JsonObject schema, string callbackAddress, OutOfBandSecretField? carried)
    {
        var body = new JsonObject
        {
            ["name"] = RegistrationName,
            ["implementation"] = schema["implementation"]?.DeepClone(),
            ["implementationName"] = schema["implementationName"]?.DeepClone(),
            ["configContract"] = schema["configContract"]?.DeepClone(),
            ["tags"] = new JsonArray(),
        };

        foreach (var flag in TriggerFlagsOf(schema))
        {
            body[flag] = !IsSelfRaised(flag);
        }

        body["fields"] = FieldsFor(callbackAddress, carried);
        return body;
    }

    // Built from what the instance returned rather than from a fresh object, so fields that build
    // carries and this one does not survive the write.
    private static JsonObject UpdateBody(
        JsonObject listed, string callbackAddress, OutOfBandSecretField? carried)
    {
        var body = (JsonObject)listed.DeepClone();
        var replacing = FieldsFor(callbackAddress, carried)
            .OfType<JsonObject>()
            .ToDictionary(field => StringOf(field, "name")!, field => field, StringComparer.Ordinal);

        var fields = body["fields"] as JsonArray ?? [];
        var merged = new JsonArray();
        foreach (var field in fields.OfType<JsonObject>())
        {
            var name = StringOf(field, "name");
            if (name is not null && replacing.Remove(name, out var replacement))
            {
                merged.Add(replacement.DeepClone());
                continue;
            }

            merged.Add(field.DeepClone());
        }

        foreach (var added in replacing.Values)
        {
            merged.Add(added.DeepClone());
        }

        body["fields"] = merged;
        return body;
    }

    private static JsonArray FieldsFor(string callbackAddress, OutOfBandSecretField? carried)
    {
        var fields = new JsonArray
        {
            new JsonObject { ["name"] = UrlField, ["value"] = callbackAddress },
            new JsonObject { ["name"] = MethodField, ["value"] = PostMethod },
        };

        foreach (var field in carried?.Fields ?? [])
        {
            fields.Add(new JsonObject
            {
                ["name"] = field.Name,
                ["value"] = JsonSerializer.SerializeToNode(field.Value),
            });
        }

        return fields;
    }

    // Every boolean the schema entry declares that is a trigger rather than a supports-* report.
    private static IEnumerable<string> TriggerFlagsOf(JsonObject schema)
        => schema
            .Where(member =>
                member.Value?.GetValueKind() is JsonValueKind.True or JsonValueKind.False
                && !member.Key.StartsWith("supports", StringComparison.OrdinalIgnoreCase))
            .Select(member => member.Key);

    // Keyed on the named property and the error code and on nothing else: v2 and v3 carry different
    // key sets and different orderings in the same refusal.
    private static string? RefusalIn(WhisparrResponse answered)
    {
        var first = ParseArray(answered)?.OfType<JsonObject>().FirstOrDefault();
        var property = first is null ? null : StringOf(first, "propertyName");
        var errorCode = first is null ? null : StringOf(first, "errorCode");
        if (property is null && errorCode is null)
        {
            return null;
        }

        return property == DuplicateNameProperty && errorCode == DuplicateNameErrorCode
            ? "the instance already holds a differently-addressed connection under this name"
            : $"the instance refused {property ?? "an unnamed property"} ({errorCode ?? "no error code"})";
    }

    // The write's status is named beside what the read-back found, because "accepted, and it did not
    // take" and "refused" send a user somewhere different. No part of the write's body is quoted:
    // the instance echoes the registration back, carrier fields included.
    private static string DescribeMismatch(int writeStatus, string? storedAddress, string callbackAddress)
        => storedAddress is null
            ? $"the write answered {writeStatus} and the instance holds no connection under this name"
            : $"the write answered {writeStatus} and the instance holds '{storedAddress}' under this "
                + $"name rather than '{callbackAddress}'";

    private static int IdOf(JsonObject listed)
        => listed["id"] is JsonValue value && value.TryGetValue<int>(out var id)
            ? id
            : throw new InvalidOperationException(
                "A listed notification carried no id, so there is nothing to update in place.");

    // Whisparr v2 publishes no contract, so a member's type is whatever it sent. A member of another
    // kind reads as null rather than throwing.
    private static string? StringOf(JsonObject entry, string name)
        => entry[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static JsonNode? FieldValue(JsonObject entry, string name)
        => (entry["fields"] as JsonArray)?
            .OfType<JsonObject>()
            .FirstOrDefault(field => StringOf(field, "name") == name)?["value"];

    // The shape is parsed rather than the status read: Whisparr v2 publishes no contract, so every
    // fact taken off it comes from what it sent.
    private static JsonArray? ParseArray(WhisparrResponse answered)
    {
        try
        {
            return JsonNode.Parse(answered.Body) as JsonArray;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
