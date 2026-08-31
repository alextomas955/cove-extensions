using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using WhisparrSync.Contracts;

namespace WhisparrSync.Whisparr;

/// <summary>What one registration attempt turned out to be, read back off the instance.</summary>
/// <remarks>
/// <paramref name="Status"/> is decided by re-reading the notification list AFTER the write, never by
/// the status of the write. An acceptance says a request was well formed; it does not say the
/// notification now points anywhere.
/// </remarks>
/// <param name="Status">Whether the list, read back, holds this product's registration.</param>
/// <param name="StoredAddress">The address the read-back found, or null when it found no entry.</param>
/// <param name="Created">Whether the write created an entry rather than updating one.</param>
/// <param name="Refusal">
/// What the instance refused, named by its own property name and error code, or null when nothing was
/// refused.
/// </param>
public sealed record CallbackRegistrationOutcome(
    RegistrationStatus Status,
    string? StoredAddress,
    bool Created,
    string? Refusal);

/// <summary>Registers and reads back this product's callback on one Whisparr instance.</summary>
public interface IWhisparrNotificationPort
{
    /// <summary>Registers <paramref name="callbackAddress"/>, creating or updating in place.</summary>
    /// <param name="generation">The generation the instance reported, which selects the carrier.</param>
    /// <param name="baseAddress">The instance's base address.</param>
    /// <param name="apiKey">The instance's API key.</param>
    /// <param name="callbackAddress">The address to register, secret already stripped or not.</param>
    /// <param name="secret">The secret to carry out of band, where the generation can carry one.</param>
    /// <param name="ct">Cancels the operation.</param>
    Task<CallbackRegistrationOutcome> RegisterAsync(
        WhisparrGeneration generation,
        Uri baseAddress,
        string apiKey,
        string callbackAddress,
        string secret,
        CancellationToken ct);

    /// <summary>Whether the instance holds this product's registration, as it answers now.</summary>
    Task<CallbackRegistrationOutcome> ReadAsync(Uri baseAddress, string apiKey, CancellationToken ct);
}

/// <inheritdoc cref="IWhisparrNotificationPort"/>
/// <remarks>
/// Extends the outbound client seam rather than bypassing it, so the retry policy and the loop-safety
/// rules keep one home.
/// </remarks>
internal sealed class NotificationPort(IWhisparrClient client, ILogger log) : IWhisparrNotificationPort
{
    /// <summary>
    /// The name this product's registration is held under, which is also how it is found again.
    /// </summary>
    /// <remarks>
    /// FROZEN. The instance enforces name uniqueness and this is the only key the find step has, so
    /// changing it would leave the old registration in place and delivering, and create a second.
    /// </remarks>
    internal const string RegistrationName = "Cove Whisparr Sync";

    /// <summary>The settings field carrying the address a delivery is posted to.</summary>
    internal const string UrlField = "url";

    /// <summary>The settings field carrying the HTTP method a delivery is posted with.</summary>
    internal const string MethodField = "method";

    /// <summary>The value of <see cref="MethodField"/> that both generations delivered under.</summary>
    /// <remarks>
    /// What the number NAMES is not established. What is established is that deliveries arrived on
    /// both generations with it set to this.
    /// </remarks>
    internal const int PostMethod = 1;

    /// <summary>The schema entry this product registers against, found by its implementation name.</summary>
    internal const string WebhookImplementation = "Webhook";

    /// <summary>The named property a duplicate-name refusal reports.</summary>
    internal const string DuplicateNameProperty = "Name";

    /// <summary>The error code a duplicate-name refusal reports.</summary>
    internal const string DuplicateNameErrorCode = "PredicateValidator";

    // A trigger the instance raises on its own schedule tells this product nothing about its library
    // and would arrive whether or not anything happened, so those are the ones left off. Everything
    // else the generation's own schema declares is subscribed, which is what stops one shared list
    // under-subscribing on whichever generation carries a trigger the other does not.
    private static bool IsSelfRaised(string flag)
        => flag.Contains("health", StringComparison.OrdinalIgnoreCase)
            || flag.Contains("applicationupdate", StringComparison.OrdinalIgnoreCase);

    public async Task<CallbackRegistrationOutcome> RegisterAsync(
        WhisparrGeneration generation,
        Uri baseAddress,
        string apiKey,
        string callbackAddress,
        string secret,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(callbackAddress);
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);

        var schema = await ReadWebhookSchemaAsync(baseAddress, apiKey, ct).ConfigureAwait(false);
        if (schema is null)
        {
            return new CallbackRegistrationOutcome(
                RegistrationStatus.NotCheckedYet, null, false, "the instance declared no Webhook connection");
        }

        // The carrier is obtained rather than assumed, so a generation holding no role registers the
        // address form and no field for a secret, with no branch here to forget.
        var carried = GenerationCapabilities.For(generation)
            .Obtain<IOutOfBandSecretRegistration>()
            .Match<OutOfBandSecretField?>(role => role.Carry(secret), _ => null);

        var listed = await FindRegistrationAsync(baseAddress, apiKey, ct).ConfigureAwait(false);
        var created = listed is null;

        var written = listed is null
            ? await client.CreateNotificationAsync(
                baseAddress, apiKey, CreateBody(schema, callbackAddress, carried), ct).ConfigureAwait(false)
            : await client.UpdateNotificationAsync(
                baseAddress,
                apiKey,
                IdOf(listed),
                UpdateBody(listed, callbackAddress, carried),
                ct).ConfigureAwait(false);

        // The read-back, and it is the answer. The write's status is read only to name a refusal the
        // read-back would otherwise report as a bare absence.
        var readBack = await ReadAsync(baseAddress, apiKey, ct).ConfigureAwait(false);
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
        Uri baseAddress, string apiKey, CancellationToken ct)
    {
        var listed = await FindRegistrationAsync(baseAddress, apiKey, ct).ConfigureAwait(false);
        return listed is null
            ? new CallbackRegistrationOutcome(RegistrationStatus.NotRegistered, null, false, null)
            : new CallbackRegistrationOutcome(
                RegistrationStatus.Registered, FieldValue(listed, UrlField)?.ToString(), false, null);
    }

    /// <summary>The Webhook schema entry, or null when the instance declared none.</summary>
    private async Task<JsonObject?> ReadWebhookSchemaAsync(
        Uri baseAddress, string apiKey, CancellationToken ct)
    {
        var answered = await client.ReadNotificationSchemaAsync(baseAddress, apiKey, ct).ConfigureAwait(false);
        return ParseArray(answered)?
            .OfType<JsonObject>()
            .FirstOrDefault(entry => StringOf(entry, "implementation") == WebhookImplementation);
    }

    /// <summary>This product's own listed registration, or null when the instance holds none.</summary>
    private async Task<JsonObject?> FindRegistrationAsync(
        Uri baseAddress, string apiKey, CancellationToken ct)
    {
        var answered = await client.ListNotificationsAsync(baseAddress, apiKey, ct).ConfigureAwait(false);
        return ParseArray(answered)?
            .OfType<JsonObject>()
            .FirstOrDefault(entry => StringOf(entry, "name") == RegistrationName);
    }

    /// <summary>
    /// A fresh registration body, built from the schema entry the instance itself returned.
    /// </summary>
    /// <remarks>
    /// The implementation identifiers are ECHOED rather than written as literals: the values were
    /// never recorded because the probe echoed them too, so a literal would be an unverified
    /// assumption. The trigger flags are read off the same entry for the same reason, and because the
    /// two generations declare different sets.
    /// </remarks>
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

    /// <summary>
    /// The listed entry with only the fields being changed replaced.
    /// </summary>
    /// <remarks>
    /// Built by spreading what the instance returned rather than from a fresh object, so whatever
    /// fields that build carries and this one does not survive. That is what makes the write an
    /// update rather than a replacement.
    /// </remarks>
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

    /// <summary>The field values one registration sets: the address, the method, and any carrier.</summary>
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

    /// <summary>
    /// Every boolean the schema entry declares that is a trigger rather than a report of support for
    /// one.
    /// </summary>
    private static IEnumerable<string> TriggerFlagsOf(JsonObject schema)
        => schema
            .Where(member =>
                member.Value?.GetValueKind() is JsonValueKind.True or JsonValueKind.False
                && !member.Key.StartsWith("supports", StringComparison.OrdinalIgnoreCase))
            .Select(member => member.Key);

    /// <summary>
    /// What a refusal named, or null when the answer was not one.
    /// </summary>
    /// <remarks>
    /// Keyed on the named property and the error code and on nothing else. The two generations carry
    /// different key sets and different orderings in the same refusal, so a branch on the entry's
    /// shape would read one of them wrong.
    /// </remarks>
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

    // The write's STATUS is named beside what the read-back found, because "accepted, and it did not
    // take" and "refused" send a user somewhere different. No part of the write's body is quoted: the
    // instance echoes the registration back, carrier fields included.
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

    /// <summary>The named member as a string, or null when it is absent or is not one.</summary>
    /// <remarks>
    /// The older generation publishes no contract, so a member's type is whatever it sent. Reading one
    /// as a string it is not would throw rather than report a shape this code does not handle.
    /// </remarks>
    private static string? StringOf(JsonObject entry, string name)
        => entry[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static JsonNode? FieldValue(JsonObject entry, string name)
        => (entry["fields"] as JsonArray)?
            .OfType<JsonObject>()
            .FirstOrDefault(field => StringOf(field, "name") == name)?["value"];

    /// <summary>
    /// The answer as a JSON array, or null when it was not one.
    /// </summary>
    /// <remarks>
    /// Parsed shape rather than status. The older generation publishes no contract, so every fact
    /// taken off it has to come from what it actually sent.
    /// </remarks>
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
