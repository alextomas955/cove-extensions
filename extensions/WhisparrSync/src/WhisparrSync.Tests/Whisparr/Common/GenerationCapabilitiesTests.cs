using System.Net;
using System.Text.Json;
using WhisparrSync.Contracts;
using WhisparrSync.Tests.TestSupport;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.Whisparr;

public sealed class GenerationCapabilitiesTests
{
    private const string V3SchemaFixture = "whisparr-v3-3.3.8.1097-notification-schema-webhook.json";

    private const string V2SchemaFixture = "whisparr-v2-2.2.0.231-notification-schema-webhook.json";

    private static readonly JsonSerializerOptions HostJsonOptions = new(JsonSerializerDefaults.Web);

    // Both lists are the whole claim rather than a sample, so a capability added to a generation
    // fails here rather than arriving unnoticed. v2 addresses no performer, adds no catalogue item
    // and keeps no scene records, which is why the six capabilities resting on those are absent
    // from its list.
    [Fact]
    public void EachGenerationsCapabilitiesAreWrittenDownPerGeneration()
    {
        Assert.Equal(
            [
                WhisparrCapability.OutOfBandCallbackSecret,
                WhisparrCapability.MonitorStudio,
                WhisparrCapability.MonitorPerformer,
                WhisparrCapability.RegisterMissingScenes,
                WhisparrCapability.ReflectOwnedFiles,
                WhisparrCapability.SearchMonitored,
                WhisparrCapability.ReadSceneStatus,
                WhisparrCapability.ReadSceneExclusions,
                WhisparrCapability.SearchScene,
                WhisparrCapability.MonitorScene,
                WhisparrCapability.ExcludeScene,
                WhisparrCapability.ReadEntityCardsInBatch,
                WhisparrCapability.ReadSceneCardsInBatch,
                WhisparrCapability.TrackEntityCatalogue,
                WhisparrCapability.ReadEntityCatalogue,
                WhisparrCapability.ReadInstanceFilesystem,
            ],
            GenerationCapabilities.CapabilitiesOf(WhisparrGeneration.V3));
        Assert.Equal(
            [
                WhisparrCapability.OutOfBandCallbackSecret,
                WhisparrCapability.MonitorStudio,
                WhisparrCapability.ReflectOwnedFiles,
                WhisparrCapability.SearchMonitored,
                WhisparrCapability.MonitorScene,
                WhisparrCapability.RegisterOwnedSites,
                WhisparrCapability.ReadSiteSceneRows,
                WhisparrCapability.ReadHeldSites,
                WhisparrCapability.ReadEntityCardsInBatch,
                WhisparrCapability.TrackEntityCatalogue,
                WhisparrCapability.ReadEntityCatalogue,
                WhisparrCapability.ReadInstanceFilesystem,
            ],
            GenerationCapabilities.CapabilitiesOf(WhisparrGeneration.V2));
        Assert.Empty(GenerationCapabilities.CapabilitiesOf((WhisparrGeneration)(-1)));
    }

    // Checked against the notification schemas the two builds themselves returned, so what each
    // instance carries is compared with the instances rather than with itself. Registering one
    // generation's field on the other is a save that is accepted and delivers nothing.
    [Fact]
    public void EachGenerationCarriesTheSecretInFieldsItsOwnSchemaDeclares()
    {
        var v3Fields = DeclaredFields(V3SchemaFixture);
        var v2Fields = DeclaredFields(V2SchemaFixture);

        Assert.Contains(V3HeaderSecretRegistration.HeadersField, v3Fields);
        Assert.DoesNotContain(V3HeaderSecretRegistration.HeadersField, v2Fields);

        Assert.Contains(V2BasicAuthSecretRegistration.UserField, v2Fields);
        Assert.Contains(V2BasicAuthSecretRegistration.PasswordField, v2Fields);

        foreach (var generation in new[] { WhisparrGeneration.V3, WhisparrGeneration.V2 })
        {
            var declared = generation == WhisparrGeneration.V3 ? v3Fields : v2Fields;
            var carried = OutOfBandRoleOf(generation).Carry("a-secret");

            Assert.All(carried.Fields, field => Assert.Contains(field.Name, declared));
            Assert.Contains(
                WhisparrCapability.OutOfBandCallbackSecret,
                GenerationCapabilities.CapabilitiesOf(generation));
        }
    }

    [Fact]
    public void TheV3RoleCarriesTheSecretAsACustomHeader()
    {
        var carried = OutOfBandRoleOf(WhisparrGeneration.V3).Carry("a-secret");

        var field = Assert.Single(carried.Fields);
        Assert.Equal("headers", field.Name);
        Assert.Equal("X-Cove-Whisparr-Sync-Secret", carried.ArrivesAsHeader);
        Assert.Equal(
            """[{"key":"X-Cove-Whisparr-Sync-Secret","value":"a-secret"}]""",
            JsonSerializer.Serialize(field.Value, HostJsonOptions));
    }

    // v2 takes a user and password pair and sends it as an authorization header. The secret is the
    // password half, and the header name is measured against an instance rather than inferred.
    [Fact]
    public void TheV2RoleCarriesTheSecretAsTheBasicAuthPassword()
    {
        var carried = OutOfBandRoleOf(WhisparrGeneration.V2).Carry("a-secret");

        Assert.Equal("Authorization", carried.ArrivesAsHeader);
        Assert.Equal(
            [("username", "cove-whisparr-sync"), ("password", "a-secret")],
            carried.Fields.Select(field => (field.Name, (string)field.Value)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ASecretWithNothingInItIsRefusedRatherThanRegisteredAsAnEmptyHeader(string? secret)
    {
        foreach (var generation in new[] { WhisparrGeneration.V3, WhisparrGeneration.V2 })
        {
            var role = OutOfBandRoleOf(generation);

            Assert.ThrowsAny<ArgumentException>(() => role.Carry(secret!));
        }
    }

    // The expected spelling is transcribed by hand from the camelCase wire convention, not computed
    // from the enum.
    [Fact]
    public void TheCapabilityTravelsInTheCamelCaseSpelling()
        => Assert.Equal(
            "[\"outOfBandCallbackSecret\",\"monitorStudio\",\"monitorPerformer\","
                + "\"registerMissingScenes\",\"reflectOwnedFiles\",\"searchMonitored\","
                + "\"readSceneStatus\",\"readSceneExclusions\",\"searchScene\","
                + "\"monitorScene\",\"excludeScene\",\"readEntityCardsInBatch\","
                + "\"readSceneCardsInBatch\",\"trackEntityCatalogue\",\"readEntityCatalogue\","
                + "\"readInstanceFilesystem\"]",
            JsonSerializer.Serialize(
                GenerationCapabilities.CapabilitiesOf(WhisparrGeneration.V3), HostJsonOptions));

    // Taken off the bound instance, so the fields asserted below are the ones that generation's
    // instance would register. Nothing is sent: the role composes field values and makes no request.
    private static IOutOfBandSecretRegistration OutOfBandRoleOf(WhisparrGeneration generation)
        => (IOutOfBandSecretRegistration)TestWhisparrClient.Over(
            BodyRecordingHandler.Answering(HttpStatusCode.OK, "[]"), generation: generation);

    private static IReadOnlyList<string> DeclaredFields(string fixtureFileName)
    {
        using var document = JsonDocument.Parse(ProbeFixtures.Read(fixtureFileName));
        return
        [
            .. document.RootElement.GetProperty("fields").EnumerateArray()
                .Select(field => field.GetProperty("name").GetString()!)
        ];
    }
}
