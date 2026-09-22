using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using WhisparrSync.Contracts;
using WhisparrSync.Tests.TestSupport;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.Whisparr;

// The arguments are asserted rather than the call counts, because an update against the found
// identifier and a second create are both one call. An acceptance status says only that a request
// was well formed, so success is reported where a re-read of the list found the address sent.
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

    [Fact]
    public async Task AFirstRegistrationReadsTheListAndThenCreates()
    {
        var client = ClientAnswering(listBefore: "[]", listAfter: ListHolding(Address));

        var outcome = await new NotificationPort(new FixedInstanceFactory(client), NullLogger.Instance)
            .RegisterAsync(Bound(client), Address, Secret, TestCt);

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

    [Fact]
    public async Task ASecondRegistrationUpdatesTheFoundEntryRatherThanCreatingASecond()
    {
        var client = ClientAnswering(
            listBefore: ListHolding(Address), listAfter: ListHolding(MovedAddress));

        var outcome = await new NotificationPort(new FixedInstanceFactory(client), NullLogger.Instance)
            .RegisterAsync(Bound(client), MovedAddress, Secret, TestCt);

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

    // The instance enforces uniqueness itself, so this code does not reimplement the check.
    [Fact]
    public async Task TheListIsReadToFindAndToReadBackAndForNothingElse()
    {
        var client = ClientAnswering(
            listBefore: ListHolding(Address), listAfter: ListHolding(MovedAddress));

        await new NotificationPort(new FixedInstanceFactory(client), NullLogger.Instance)
            .RegisterAsync(Bound(client), MovedAddress, Secret, TestCt);

        Assert.Equal(
            2,
            client.Notifications.Count(call => call.Verb == nameof(IWhisparrClient.ListNotificationsAsync)));
    }

    // The update spreads the listed entry. An update is a replacement, so a fresh body would
    // silently drop a member the connected build carries and this code does not know about.
    [Fact]
    public async Task TheUpdateKeepsMembersOfTheListedEntryThisCodeDoesNotKnowAbout()
    {
        var client = ClientAnswering(
            listBefore: ListHolding(Address, "\"aFieldThisBuildCarries\":\"keep-me\","),
            listAfter: ListHolding(MovedAddress));

        await new NotificationPort(new FixedInstanceFactory(client), NullLogger.Instance)
            .RegisterAsync(Bound(client), MovedAddress, Secret, TestCt);

        var update = client.Notifications
            .Single(call => call.Verb == nameof(IWhisparrClient.UpdateNotificationAsync))
            .Body!;

        Assert.Equal("keep-me", update["aFieldThisBuildCarries"]!.GetValue<string>());
        Assert.Equal(7, update["id"]!.GetValue<int>());
        Assert.Equal(MovedAddress, UrlFieldOf(update));
    }

    // The case the read-back exists for: the write answers 202 and the notification still points
    // somewhere else.
    [Fact]
    public async Task AnAcceptedWriteWhoseEffectTheListDoesNotShowIsNotReportedAsRegistered()
    {
        var client = ClientAnswering(
            listBefore: ListHolding(Address), listAfter: ListHolding(Address));
        client.Answering(
            nameof(IWhisparrClient.UpdateNotificationAsync), RecordingWhisparrClient.Json(202, "{}"));

        var outcome = await new NotificationPort(new FixedInstanceFactory(client), NullLogger.Instance)
            .RegisterAsync(Bound(client), MovedAddress, Secret, TestCt);

        Assert.Equal(RegistrationStatus.NotRegistered, outcome.Status);
        Assert.NotNull(outcome.Refusal);
        Assert.Contains(Address, outcome.Refusal, StringComparison.Ordinal);
        Assert.Equal(Address, outcome.StoredAddress);
    }

    // The refusal entry here is given the other generation's key set and ordering, so a branch on
    // the entry's shape would miss it.
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

        var outcome = await new NotificationPort(new FixedInstanceFactory(client), NullLogger.Instance)
            .RegisterAsync(Bound(client), Address, Secret, TestCt);

        Assert.Equal(RegistrationStatus.NotRegistered, outcome.Status);
        Assert.Equal(
            "the instance already holds a differently-addressed connection under this name",
            outcome.Refusal);
    }

    [Theory]
    [InlineData(WhisparrGeneration.V3, "headers")]
    [InlineData(WhisparrGeneration.V2, "username password")]
    public async Task TheRegistrationCarriesTheSecretInTheFieldsThatGenerationUses(
        WhisparrGeneration generation, string expectedExtraFields)
    {
        var client = ClientAnswering(
            listBefore: "[]", listAfter: ListHolding(Address), generation: generation);

        await new NotificationPort(new FixedInstanceFactory(client), NullLogger.Instance)
            .RegisterAsync(Bound(client), Address, Secret, TestCt);

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

    [Fact]
    public async Task TheTriggerFlagsComeFromTheSchemaEntryWithTheSelfRaisedOnesOff()
    {
        var client = ClientAnswering(listBefore: "[]", listAfter: ListHolding(Address));

        await new NotificationPort(new FixedInstanceFactory(client), NullLogger.Instance)
            .RegisterAsync(Bound(client), Address, Secret, TestCt);

        var body = (JsonObject)client.Notifications
            .Single(call => call.Verb == nameof(IWhisparrClient.CreateNotificationAsync))
            .Body!;

        Assert.True(body["onDownload"]!.GetValue<bool>());
        Assert.True(body["onRename"]!.GetValue<bool>());
        Assert.False(body["onHealthIssue"]!.GetValue<bool>());
        Assert.False(body.ContainsKey("supportsOnDownload"));
    }

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

        await new NotificationPort(new FixedInstanceFactory(client), NullLogger.Instance)
            .RegisterAsync(Bound(client), Address, Secret, TestCt);

        var body = client.Notifications
            .Single(call => call.Verb == nameof(IWhisparrClient.CreateNotificationAsync))
            .Body!;

        Assert.Equal("An Echoed Name", body["implementationName"]!.GetValue<string>());
        Assert.Equal("AnEchoedContract", body["configContract"]!.GetValue<string>());
        Assert.Equal("Cove Whisparr Sync", body["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task AnInstanceHoldingNoRegistrationReadsAsNotRegistered()
    {
        var client = new RecordingWhisparrClient(RecordingWhisparrClient.Json(200, "[]"));

        var outcome = await new NotificationPort(new FixedInstanceFactory(client), NullLogger.Instance)
            .ReadAsync(Bound(client), TestCt);

        Assert.Equal(RegistrationStatus.NotRegistered, outcome.Status);
        Assert.Null(outcome.StoredAddress);
    }

    private static RecordingWhisparrClient ClientAnswering(
        string listBefore, string listAfter, WhisparrGeneration generation = WhisparrGeneration.V3)
    {
        var client = new RecordingWhisparrClient(
            RecordingWhisparrClient.Json(200, "[]"),
            new WhisparrBinding(generation, Instance, ApiKey));
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

    private static WhisparrBinding Bound(RecordingWhisparrClient client) => client.Binding;

    private static string? UrlFieldOf(JsonNode body)
        => ((JsonArray)body["fields"]!)
            .OfType<JsonObject>()
            .FirstOrDefault(field => field["name"]!.GetValue<string>() == "url")?["value"]
            ?.GetValue<string>();
}
