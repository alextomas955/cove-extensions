using System.Text.Json;
using WhisparrSync.Tests.TestSupport;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.Whisparr;

// Whisparr v2 publishes no API contract, so every fact here is transcribed by hand from what a
// named build answered; a pin computed from the module it checks agrees with itself forever. No
// assertion about v2 rests on an HTTP status code, because its statuses are not a published
// contract and reading one has already misreported in this project.
public sealed class NotificationPinTests
{
    private const string V3Build = "3.3.8.1097";

    private const string V2Build = "2.2.0.231";

    private const string V3SchemaFixture = "whisparr-v3-3.3.8.1097-notification-schema-webhook.json";
    private const string V2SchemaFixture = "whisparr-v2-2.2.0.231-notification-schema-webhook.json";

    // The expected lists are transcribed by hand, in the order each build declared its fields. The
    // absence of headers on the v2 build is why the two generations carry a secret differently.
    [Theory]
    [InlineData(V3Build, V3SchemaFixture, "url method username password headers")]
    [InlineData(V2Build, V2SchemaFixture, "url method username password")]
    public void EachBuildDeclaresTheWebhookFieldsTranscribedForIt(
        string build, string fixtureFileName, string expected)
    {
        Assert.EndsWith($"{build}-notification-schema-webhook.json", fixtureFileName, StringComparison.Ordinal);
        Assert.Equal(expected, string.Join(' ', DeclaredFieldNames(fixtureFileName)));
    }

    [Fact]
    public void TheHeadersFieldIsPresentOnTheV3BuildAndAbsentOnTheV2One()
    {
        var declared = DeclaredField(V3SchemaFixture, V3HeaderSecretRegistration.HeadersField);

        Assert.NotNull(declared);
        Assert.Equal("keyValueList", declared.Value.GetProperty("type").GetString());
        Assert.Equal("normal", declared.Value.GetProperty("privacy").GetString());
        Assert.True(declared.Value.GetProperty("advanced").GetBoolean());

        Assert.Null(DeclaredField(V2SchemaFixture, V3HeaderSecretRegistration.HeadersField));
    }

    // The user and password pair is declared on both builds, so the choice of carrier is about
    // which one delivers rather than which one saves.
    [Theory]
    [InlineData(V3SchemaFixture)]
    [InlineData(V2SchemaFixture)]
    public void TheUserAndPasswordFieldsAreDeclaredOnBothBuildsWithTheirOwnPrivacies(string fixtureFileName)
    {
        var user = DeclaredField(fixtureFileName, V2BasicAuthSecretRegistration.UserField);
        var password = DeclaredField(fixtureFileName, V2BasicAuthSecretRegistration.PasswordField);

        Assert.NotNull(user);
        Assert.NotNull(password);
        Assert.Equal("textbox", user.Value.GetProperty("type").GetString());
        Assert.Equal("userName", user.Value.GetProperty("privacy").GetString());
        Assert.Equal("password", password.Value.GetProperty("type").GetString());
        Assert.Equal("password", password.Value.GetProperty("privacy").GetString());
    }

    // Pinned here rather than in the port. The probe that measured the registration echoed these
    // values, so a literal in production code would be an unverified assumption.
    [Theory]
    [InlineData(V3SchemaFixture)]
    [InlineData(V2SchemaFixture)]
    public void BothBuildsDeclareTheWebhookImplementationIdentifiers(string fixtureFileName)
    {
        using var document = JsonDocument.Parse(ProbeFixtures.Read(fixtureFileName));

        Assert.Equal("Webhook", document.RootElement.GetProperty("implementation").GetString());
        Assert.Equal("Webhook", document.RootElement.GetProperty("implementationName").GetString());
        Assert.Equal("WebhookSettings", document.RootElement.GetProperty("configContract").GetString());
        Assert.Equal(
            NotificationPort.WebhookImplementation,
            document.RootElement.GetProperty("implementation").GetString());
    }

    // One trigger list shared across both builds under-subscribes on whichever carries a trigger
    // the other does not, so the port reads the flags off the schema entry.
    [Fact]
    public void TheTriggerFlagsDifferByTheCountsEachBuildDeclared()
    {
        var v3 = TriggerFlagNames(V3SchemaFixture);
        var v2 = TriggerFlagNames(V2SchemaFixture);

        Assert.Equal(13, v3.Count);
        Assert.Equal(14, v2.Count);
        Assert.Equal(9, v3.Intersect(v2, StringComparer.Ordinal).Count());
        Assert.Equal(
            ["onMovieAdded", "onMovieDelete", "onMovieFileDelete", "onMovieFileDeleteForUpgrade"],
            v3.Except(v2, StringComparer.Ordinal).Order(StringComparer.Ordinal));
        Assert.Equal(
            [
                "onEpisodeFileDelete",
                "onEpisodeFileDeleteForUpgrade",
                "onImportComplete",
                "onSeriesAdd",
                "onSeriesDelete",
            ],
            v2.Except(v3, StringComparer.Ordinal).Order(StringComparer.Ordinal));
    }

    // Both builds' refusal entries carry different key sets and orderings for the same refusal, so
    // the branch reads these two members and nothing else. The expected values are transcribed from
    // the refusals themselves, which is the only form in which a pin can disagree with the code.
    [Fact]
    public void ADuplicateNameRefusalNamesThePropertyAndErrorCodeBothBuildsReported()
    {
        Assert.Equal("Name", NotificationPort.DuplicateNameProperty);
        Assert.Equal("PredicateValidator", NotificationPort.DuplicateNameErrorCode);
    }

    // What the number names is not established. What is established is that deliveries arrived on
    // both builds with the method field set to this.
    [Fact]
    public void TheMethodFieldValueDeliveriesArrivedUnderIsPinned()
        => Assert.Equal(1, NotificationPort.PostMethod);

    // The retry policy table decides this, so a class granted retries by a table edit fails here
    // rather than silently re-issuing a write whose answer did not arrive.
    [Fact]
    public void AConfigureRequestIsNeverReIssued()
    {
        Assert.Equal(
            WhisparrRetryPolicy.NoRetry,
            WhisparrRetryPolicy.AttemptsFor(WhisparrVerbClass.Configure));
        Assert.True(WhisparrRetryPolicy.AttemptsFor(WhisparrVerbClass.Read) > WhisparrRetryPolicy.NoRetry);
    }

    private static IReadOnlyList<string> DeclaredFieldNames(string fixtureFileName)
    {
        using var document = JsonDocument.Parse(ProbeFixtures.Read(fixtureFileName));
        return
        [
            .. document.RootElement.GetProperty("fields").EnumerateArray()
                .Select(field => field.GetProperty("name").GetString()!)
        ];
    }

    private static JsonElement? DeclaredField(string fixtureFileName, string name)
    {
        using var document = JsonDocument.Parse(ProbeFixtures.Read(fixtureFileName));
        foreach (var field in document.RootElement.GetProperty("fields").EnumerateArray())
        {
            if (field.GetProperty("name").GetString() == name)
            {
                return field.Clone();
            }
        }

        return null;
    }

    private static IReadOnlyList<string> TriggerFlagNames(string fixtureFileName)
    {
        using var document = JsonDocument.Parse(ProbeFixtures.Read(fixtureFileName));
        return
        [
            .. document.RootElement.EnumerateObject()
                .Where(member =>
                    member.Value.ValueKind is JsonValueKind.True or JsonValueKind.False
                    && !member.Name.StartsWith("supports", StringComparison.OrdinalIgnoreCase))
                .Select(member => member.Name)
        ];
    }
}
