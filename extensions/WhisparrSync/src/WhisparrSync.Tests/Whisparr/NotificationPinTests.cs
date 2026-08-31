using System.Text.Json;
using WhisparrSync.Tests.TestSupport;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.Whisparr;

/// <summary>
/// The wire facts the registration rests on, each transcribed by hand from what a NAMED build
/// actually answered.
/// </summary>
/// <remarks>
/// The older generation publishes no API contract, so nothing about it may be generated and every
/// fact is a hand-written expectation naming the build it came from. A pin computed from the module
/// it checks agrees with itself forever and reports nothing.
/// <para>
/// No assertion about the older generation is written against an HTTP status code. That generation's
/// statuses are not a contract it publishes, and a status-only reading of it has already misreported
/// once in this project: four documents were recorded as existing that do not.
/// </para>
/// </remarks>
public sealed class NotificationPinTests
{
    /// <summary>The build each fixture was captured from, named in the file it is read from.</summary>
    private const string V3Build = "3.3.8.1097";

    /// <inheritdoc cref="V3Build"/>
    private const string V2Build = "2.2.0.231";

    private const string V3SchemaFixture = "whisparr-v3-3.3.8.1097-notification-schema-webhook.json";
    private const string V2SchemaFixture = "whisparr-v2-2.2.0.231-notification-schema-webhook.json";

    /// <summary>
    /// The Webhook settings fields each build declares, in the order it declared them.
    /// </summary>
    /// <remarks>
    /// Transcribed by hand. The absence of <c>headers</c> on the older build is the finding: a list of
    /// the fields that exist does not say that one is missing, and the missing one is why the two
    /// generations carry a secret differently.
    /// </remarks>
    [Theory]
    [InlineData(V3Build, V3SchemaFixture, "url method username password headers")]
    [InlineData(V2Build, V2SchemaFixture, "url method username password")]
    public void EachBuildDeclaresTheWebhookFieldsTranscribedForIt(
        string build, string fixtureFileName, string expected)
    {
        Assert.EndsWith($"{build}-notification-schema-webhook.json", fixtureFileName, StringComparison.Ordinal);
        Assert.Equal(expected, string.Join(' ', DeclaredFieldNames(fixtureFileName)));
    }

    /// <summary>
    /// The carrier field is present on one build and absent on the other, with the type and advanced
    /// flag that build declared for it.
    /// </summary>
    [Fact]
    public void TheHeadersFieldIsPresentOnTheNewerBuildAndAbsentOnTheOlder()
    {
        var declared = DeclaredField(V3SchemaFixture, V3HeaderSecretRegistration.HeadersField);

        Assert.NotNull(declared);
        Assert.Equal("keyValueList", declared.Value.GetProperty("type").GetString());
        Assert.Equal("normal", declared.Value.GetProperty("privacy").GetString());
        Assert.True(declared.Value.GetProperty("advanced").GetBoolean());

        Assert.Null(DeclaredField(V2SchemaFixture, V3HeaderSecretRegistration.HeadersField));
    }

    /// <summary>
    /// The older build's carrier pair, with the privacies it declared, on both builds.
    /// </summary>
    /// <remarks>
    /// Present on both, which is why the choice of carrier is about which one DELIVERS rather than
    /// which one saves.
    /// </remarks>
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

    /// <summary>The implementation identifiers each build declared, which the port ECHOES.</summary>
    /// <remarks>
    /// Pinned here so the echo is checked against something, and NOT written into the port: the probe
    /// that measured the registration never recorded these values because it echoed them too, so a
    /// literal in production code would be an unverified assumption.
    /// </remarks>
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

    /// <summary>
    /// The trigger-flag asymmetry, transcribed as the counts each build produced.
    /// </summary>
    /// <remarks>
    /// One list shared across both silently under-subscribes on whichever build carries a trigger the
    /// other does not, which is why the port reads the flags off the schema entry rather than writing
    /// them down.
    /// </remarks>
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

    /// <summary>
    /// What a duplicate-name refusal names, on BOTH builds, transcribed from the refusals themselves.
    /// </summary>
    /// <remarks>
    /// The two builds' refusal entries carry different key sets and different orderings for the same
    /// refusal, so the branch reads these two members and nothing else. Written as a comparison of the
    /// production constants against hand-transcribed values, because that is the only form in which a
    /// pin can disagree with the code it checks.
    /// </remarks>
    [Fact]
    public void ADuplicateNameRefusalNamesThePropertyAndErrorCodeBothBuildsReported()
    {
        Assert.Equal("Name", NotificationPort.DuplicateNameProperty);
        Assert.Equal("PredicateValidator", NotificationPort.DuplicateNameErrorCode);
    }

    /// <summary>
    /// The value the method field was set to when deliveries arrived, on both builds.
    /// </summary>
    /// <remarks>
    /// What the number names is not established. What is established is that deliveries arrived with
    /// it set to this, which is the claim the port rests on.
    /// </remarks>
    [Fact]
    public void TheMethodFieldValueDeliveriesArrivedUnderIsPinned()
        => Assert.Equal(1, NotificationPort.PostMethod);

    /// <summary>
    /// A request that changes the instance is issued once.
    /// </summary>
    /// <remarks>
    /// The policy table is what decides this, so a class granted retries by a table edit fails here
    /// rather than silently re-issuing a write whose answer did not arrive.
    /// </remarks>
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
