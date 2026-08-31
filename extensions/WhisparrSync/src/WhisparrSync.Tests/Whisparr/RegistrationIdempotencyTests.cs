using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using WhisparrSync.Contracts;
using WhisparrSync.Tests.TestSupport;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.Whisparr;

/// <summary>
/// What a registration actually SENDS, and what it reports, driven through the one outbound seam.
/// </summary>
/// <remarks>
/// The arguments are asserted rather than the call counts: a count says a request was made, and the
/// question idempotency has to answer is which request. An update against the found identifier and a
/// second create are both "one call".
/// <para>
/// The read-back is the other half. An acceptance status says a request was well formed; every case
/// here that reports success does so because a re-read of the list found the address that was sent.
/// </para>
/// </remarks>
public sealed class RegistrationIdempotencyTests
{
    private static readonly Uri Instance = new("http://whisparr-v3:6969/");
    private const string ApiKey = "7c7c7c7c7c7c7c7c7c7c7c7c7c7c7c7c";
    private const string Secret = "not-a-real-secret";
    private const string Address = "http://cove:5073/api/extensions/com.alextomas955.whisparrsync/callback";
    private const string MovedAddress =
        "https://media.example.com/cove/api/extensions/com.alextomas955.whisparrsync/callback";

    private const string Schema =
        """
        [{"implementation":"Webhook","implementationName":"Webhook","configContract":"WebhookSettings",
          "onDownload":false,"onRename":false,"onHealthIssue":false,"supportsOnDownload":true,
          "fields":[{"name":"url"},{"name":"method"},{"name":"headers"}]}]
        """;

    private static string ListHolding(string url, string extra = "")
        => $$"""
        [{"id":7,"name":"Cove Whisparr Sync","onDownload":true,"tags":[],{{extra}}
          "fields":[{"name":"url","value":"{{url}}"},{"name":"method","value":1}]}]
        """;

    private static CancellationToken TestCt => TestContext.Current.CancellationToken;

    /// <summary>An instance holding no registration is CREATED against, after the list was read.</summary>
    [Fact]
    public async Task AFirstRegistrationReadsTheListAndThenCreates()
    {
        var client = ClientAnswering(listBefore: "[]", listAfter: ListHolding(Address));

        var outcome = await new NotificationPort(client, NullLogger.Instance)
            .RegisterAsync(WhisparrGeneration.V3, Instance, ApiKey, Address, Secret, TestCt);

        Assert.Equal(RegistrationStatus.Registered, outcome.Status);
        Assert.True(outcome.Created);
        Assert.Null(outcome.Refusal);
        Assert.Equal(Address, outcome.StoredAddress);

        Assert.Equal(
            [
                nameof(IWhisparrClient.ReadNotificationSchemaAsync),
                nameof(IWhisparrClient.ListNotificationsAsync),
                nameof(IWhisparrClient.CreateNotificationAsync),
                nameof(IWhisparrClient.ListNotificationsAsync),
            ],
            client.Notifications.Select(call => call.Verb));
    }

    /// <summary>
    /// An instance already holding one is UPDATED against the identifier the list gave, never created
    /// a second time.
    /// </summary>
    [Fact]
    public async Task ASecondRegistrationUpdatesTheFoundEntryRatherThanCreatingASecond()
    {
        var client = ClientAnswering(
            listBefore: ListHolding(Address), listAfter: ListHolding(MovedAddress));

        var outcome = await new NotificationPort(client, NullLogger.Instance)
            .RegisterAsync(WhisparrGeneration.V3, Instance, ApiKey, MovedAddress, Secret, TestCt);

        Assert.Equal(RegistrationStatus.Registered, outcome.Status);
        Assert.False(outcome.Created);
        Assert.Equal(MovedAddress, outcome.StoredAddress);

        Assert.DoesNotContain(
            nameof(IWhisparrClient.CreateNotificationAsync),
            client.Notifications.Select(call => call.Verb));

        var update = Assert.Single(
            client.Notifications, call => call.Verb == nameof(IWhisparrClient.UpdateNotificationAsync));
        Assert.Equal(7, update.Id);
        Assert.Equal(Instance, update.BaseAddress);
        Assert.Equal(MovedAddress, UrlFieldOf(update.Body!));
    }

    /// <summary>
    /// The uniqueness check the instance already enforces is not reimplemented: the list is read once
    /// to find the entry, and once more to read the result back.
    /// </summary>
    [Fact]
    public async Task TheListIsReadToFindAndToReadBackAndForNothingElse()
    {
        var client = ClientAnswering(
            listBefore: ListHolding(Address), listAfter: ListHolding(MovedAddress));

        await new NotificationPort(client, NullLogger.Instance)
            .RegisterAsync(WhisparrGeneration.V3, Instance, ApiKey, MovedAddress, Secret, TestCt);

        Assert.Equal(
            2,
            client.Notifications.Count(call => call.Verb == nameof(IWhisparrClient.ListNotificationsAsync)));
    }

    /// <summary>
    /// The update is built by spreading the LISTED entry, so a member that build carries and this one
    /// does not survives the write.
    /// </summary>
    /// <remarks>
    /// Constructing a fresh body would drop it silently, which is what makes an update a replacement.
    /// </remarks>
    [Fact]
    public async Task TheUpdateKeepsMembersOfTheListedEntryThisCodeDoesNotKnowAbout()
    {
        var client = ClientAnswering(
            listBefore: ListHolding(Address, "\"aFieldThisBuildCarries\":\"keep-me\","),
            listAfter: ListHolding(MovedAddress));

        await new NotificationPort(client, NullLogger.Instance)
            .RegisterAsync(WhisparrGeneration.V3, Instance, ApiKey, MovedAddress, Secret, TestCt);

        var update = client.Notifications
            .Single(call => call.Verb == nameof(IWhisparrClient.UpdateNotificationAsync))
            .Body!;

        Assert.Equal("keep-me", update["aFieldThisBuildCarries"]!.GetValue<string>());
        Assert.Equal(7, update["id"]!.GetValue<int>());
        Assert.Equal(MovedAddress, UrlFieldOf(update));
    }

    /// <summary>
    /// A write the instance accepted whose effect the list does not show reports NOT registered.
    /// </summary>
    /// <remarks>
    /// This is the case the whole read-back exists for. The write answers 202 and the notification
    /// still points somewhere else.
    /// </remarks>
    [Fact]
    public async Task AnAcceptedWriteWhoseEffectTheListDoesNotShowIsNotReportedAsRegistered()
    {
        var client = ClientAnswering(
            listBefore: ListHolding(Address), listAfter: ListHolding(Address));
        client.Answering(
            nameof(IWhisparrClient.UpdateNotificationAsync), RecordingWhisparrClient.Json(202, "{}"));

        var outcome = await new NotificationPort(client, NullLogger.Instance)
            .RegisterAsync(WhisparrGeneration.V3, Instance, ApiKey, MovedAddress, Secret, TestCt);

        Assert.Equal(RegistrationStatus.NotRegistered, outcome.Status);
        Assert.NotNull(outcome.Refusal);
        Assert.Contains(Address, outcome.Refusal, StringComparison.Ordinal);
        Assert.Equal(Address, outcome.StoredAddress);
    }

    /// <summary>
    /// A duplicate-name refusal is read off the named property and the error code.
    /// </summary>
    /// <remarks>
    /// The refusal entry is given the OTHER generation's key set and ordering, so a branch on the
    /// entry's shape would miss it.
    /// </remarks>
    [Fact]
    public async Task ADuplicateNameRefusalIsReadOffThePropertyAndErrorCode()
    {
        var client = ClientAnswering(listBefore: "[]", listAfter: "[]");
        client.Answering(
            nameof(IWhisparrClient.CreateNotificationAsync),
            RecordingWhisparrClient.Json(
                400,
                """
                [{"formattedMessageArguments":[],"severity":"error","errorCode":"PredicateValidator",
                  "attemptedValue":"Cove Whisparr Sync","errorMessage":"Should be unique",
                  "propertyName":"Name"}]
                """));

        var outcome = await new NotificationPort(client, NullLogger.Instance)
            .RegisterAsync(WhisparrGeneration.V3, Instance, ApiKey, Address, Secret, TestCt);

        Assert.Equal(RegistrationStatus.NotRegistered, outcome.Status);
        Assert.Equal(
            "the instance already holds a differently-addressed connection under this name",
            outcome.Refusal);
    }

    /// <summary>
    /// The carrier fields go in for the generation that was connected, and the registered address
    /// carries no secret.
    /// </summary>
    [Theory]
    [InlineData(WhisparrGeneration.V3, "headers")]
    [InlineData(WhisparrGeneration.V2, "username password")]
    public async Task TheRegistrationCarriesTheSecretInTheFieldsThatGenerationUses(
        WhisparrGeneration generation, string expectedExtraFields)
    {
        var client = ClientAnswering(listBefore: "[]", listAfter: ListHolding(Address));

        await new NotificationPort(client, NullLogger.Instance)
            .RegisterAsync(generation, Instance, ApiKey, Address, Secret, TestCt);

        var body = client.Notifications
            .Single(call => call.Verb == nameof(IWhisparrClient.CreateNotificationAsync))
            .Body!;
        var fields = (JsonArray)body["fields"]!;

        Assert.Equal(
            $"url method {expectedExtraFields}".TrimEnd(),
            string.Join(' ', fields.Select(field => field!["name"]!.GetValue<string>())));
        Assert.DoesNotContain(Secret, UrlFieldOf(body), StringComparison.Ordinal);
        Assert.Contains(Secret, body.ToJsonString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The trigger flags are read off the schema entry, and the self-raised ones are left off.
    /// </summary>
    [Fact]
    public async Task TheTriggerFlagsComeFromTheSchemaEntryWithTheSelfRaisedOnesOff()
    {
        var client = ClientAnswering(listBefore: "[]", listAfter: ListHolding(Address));

        await new NotificationPort(client, NullLogger.Instance)
            .RegisterAsync(WhisparrGeneration.V3, Instance, ApiKey, Address, Secret, TestCt);

        var body = (JsonObject)client.Notifications
            .Single(call => call.Verb == nameof(IWhisparrClient.CreateNotificationAsync))
            .Body!;

        Assert.True(body["onDownload"]!.GetValue<bool>());
        Assert.True(body["onRename"]!.GetValue<bool>());
        Assert.False(body["onHealthIssue"]!.GetValue<bool>());
        Assert.False(body.ContainsKey("supportsOnDownload"));
    }

    /// <summary>The identifiers are echoed from the schema rather than written as literals.</summary>
    [Fact]
    public async Task TheImplementationIdentifiersAreEchoedFromTheSchema()
    {
        var client = ClientAnswering(listBefore: "[]", listAfter: ListHolding(Address));
        client.Answering(
            nameof(IWhisparrClient.ReadNotificationSchemaAsync),
            RecordingWhisparrClient.Json(
                200,
                """
                [{"implementation":"Webhook","implementationName":"An Echoed Name",
                  "configContract":"AnEchoedContract","onDownload":false,"fields":[]}]
                """));

        await new NotificationPort(client, NullLogger.Instance)
            .RegisterAsync(WhisparrGeneration.V3, Instance, ApiKey, Address, Secret, TestCt);

        var body = client.Notifications
            .Single(call => call.Verb == nameof(IWhisparrClient.CreateNotificationAsync))
            .Body!;

        Assert.Equal("An Echoed Name", body["implementationName"]!.GetValue<string>());
        Assert.Equal("AnEchoedContract", body["configContract"]!.GetValue<string>());
        Assert.Equal("Cove Whisparr Sync", body["name"]!.GetValue<string>());
    }

    /// <summary>An instance holding nothing under this name reads as not registered, not unknown.</summary>
    [Fact]
    public async Task AnInstanceHoldingNoRegistrationReadsAsNotRegistered()
    {
        var client = new RecordingWhisparrClient(RecordingWhisparrClient.Json(200, "[]"));

        var outcome = await new NotificationPort(client, NullLogger.Instance).ReadAsync(Instance, ApiKey, TestCt);

        Assert.Equal(RegistrationStatus.NotRegistered, outcome.Status);
        Assert.Null(outcome.StoredAddress);
    }

    private static RecordingWhisparrClient ClientAnswering(string listBefore, string listAfter)
    {
        var client = new RecordingWhisparrClient(RecordingWhisparrClient.Json(200, "[]"));
        client.Answering(
            nameof(IWhisparrClient.ReadNotificationSchemaAsync),
            RecordingWhisparrClient.Json(200, Schema));
        client.Answering(
            nameof(IWhisparrClient.ListNotificationsAsync),
            RecordingWhisparrClient.Json(200, listBefore),
            RecordingWhisparrClient.Json(200, listAfter));
        client.Answering(
            nameof(IWhisparrClient.CreateNotificationAsync), RecordingWhisparrClient.Json(201, "{}"));
        client.Answering(
            nameof(IWhisparrClient.UpdateNotificationAsync), RecordingWhisparrClient.Json(202, "{}"));
        return client;
    }

    private static string? UrlFieldOf(JsonNode body)
        => ((JsonArray)body["fields"]!)
            .OfType<JsonObject>()
            .FirstOrDefault(field => field["name"]!.GetValue<string>() == "url")?["value"]
            ?.GetValue<string>();
}
