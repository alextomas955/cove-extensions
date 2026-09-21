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

    [Fact]
    public void TheV3SetHoldsTheOutOfBandSecretRole()
    {
        var role = OutOfBandRoleOf(WhisparrGeneration.V3);

        Assert.NotNull(role);
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
                WhisparrCapability.ReadInstanceFilesystem,
            ],
            GenerationCapabilities.For(WhisparrGeneration.V3).Held);
    }

    [Fact]
    public void TheV2SetAlsoHoldsTheOutOfBandSecretRole()
    {
        var role = OutOfBandRoleOf(WhisparrGeneration.V2);

        Assert.NotNull(role);
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
                WhisparrCapability.ReadInstanceFilesystem,
            ],
            GenerationCapabilities.For(WhisparrGeneration.V2).Held);
    }

    // No route on v2 adds a catalogue item, so its set holds no scene-registration role. The
    // refusal is an answer of its own: not null, not a default role, and not an exception.
    [Fact]
    public void ASetHoldingNoRoleRefusesAndNamesWhatItRefused()
    {
        var capabilities = GenerationCapabilities.For(
            WhisparrGeneration.V2,
            WhisparrRoleSet.From(new RecordingWhisparrClient(RecordingWhisparrClient.Json(200, "{}"))));

        var refusal = capabilities.Obtain<IWhisparrMissingSceneActing>()
            .Match<CapabilityRefusal?>(_ => null, refused => refused);

        Assert.NotNull(refusal);
        Assert.Equal(WhisparrCapability.RegisterMissingScenes, refusal.Capability);
        Assert.Equal(WhisparrGeneration.V2, refusal.Generation);
        Assert.DoesNotContain(WhisparrCapability.RegisterMissingScenes, capabilities.Held);
    }

    // Whisparr v2 addresses no performer at all. The refusal is the absence of a registration
    // rather than a check.
    [Fact]
    public void TheOlderGenerationHoldsNoPerformerCapabilityAndRefusesTheRoleByName()
    {
        var capabilities = GenerationCapabilities.For(
            WhisparrGeneration.V2,
            WhisparrRoleSet.From(new RecordingWhisparrClient(RecordingWhisparrClient.Json(200, "{}"))));

        var refusal = capabilities.Obtain<IWhisparrPerformerActing>()
            .Match<CapabilityRefusal?>(_ => null, refused => refused);

        Assert.NotNull(refusal);
        Assert.Equal(WhisparrCapability.MonitorPerformer, refusal.Capability);
        Assert.Equal(WhisparrGeneration.V2, refusal.Generation);
        Assert.DoesNotContain(WhisparrCapability.MonitorPerformer, capabilities.Held);
        Assert.DoesNotContain(
            WhisparrCapability.MonitorPerformer,
            GenerationCapabilities.CapabilitiesOf(WhisparrGeneration.V2));
    }

    [Fact]
    public void TheNewerGenerationHoldsThePerformerCapabilityAndHandsOutTheRole()
    {
        var capabilities = GenerationCapabilities.For(
            WhisparrGeneration.V3,
            WhisparrRoleSet.From(new RecordingWhisparrClient(RecordingWhisparrClient.Json(200, "{}"))));

        Assert.Contains(WhisparrCapability.MonitorPerformer, capabilities.Held);
        Assert.NotNull(
            capabilities.Obtain<IWhisparrPerformerActing>()
                .Match<IWhisparrPerformerActing?>(held => held, _ => null));
    }

    // A held capability with no source is a wiring fault, not an answer about the connected
    // instance, so it throws instead of refusing.
    [Fact]
    public void ACapabilityHeldButUnsourcedIsAFaultRatherThanARefusal()
        => Assert.Throws<InvalidOperationException>(
            () => GenerationCapabilities.For(WhisparrGeneration.V3).Obtain<IWhisparrStudioActing>());

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
                WhisparrCapability.ReadInstanceFilesystem,
            ],
            GenerationCapabilities.CapabilitiesOf(WhisparrGeneration.V2));
        Assert.Empty(GenerationCapabilities.CapabilitiesOf((WhisparrGeneration)(-1)));
    }

    // v2 keeps no scene records, so it has no scene to search for. Both generations are asserted
    // over one client implementing every role, so the answers differ by generation rather than by
    // what each set was built with.
    [Fact]
    public void OnlyTheNewerGenerationObtainsThePerSceneSearchRole()
    {
        var client = new RecordingWhisparrClient(RecordingWhisparrClient.Json(200, "{}"));

        Assert.NotNull(SceneSearchRoleOn(WhisparrGeneration.V3, client));

        var refusal = GenerationCapabilities
            .For(WhisparrGeneration.V2, WhisparrRoleSet.From(client))
            .Obtain<IWhisparrSceneSearchGrabbing>()
            .Match<CapabilityRefusal?>(_ => null, refused => refused);

        Assert.NotNull(refusal);
        Assert.Equal(WhisparrCapability.SearchScene, refusal.Capability);
        Assert.Equal(WhisparrGeneration.V2, refusal.Generation);
    }

    // v3 answers presence for a site through a route naming the site, so it needs no list and has
    // nothing to implement here.
    [Fact]
    public void OnlyTheOlderGenerationObtainsTheHeldSiteReadRole()
    {
        var client = new RecordingWhisparrClient(RecordingWhisparrClient.Json(200, "{}"));

        Assert.NotNull(
            GenerationCapabilities
                .For(WhisparrGeneration.V2, WhisparrRoleSet.From(client))
                .Obtain<IWhisparrHeldSiteReading>()
                .Match<IWhisparrHeldSiteReading?>(held => held, _ => null));

        var refusal = GenerationCapabilities
            .For(WhisparrGeneration.V3, WhisparrRoleSet.From(client))
            .Obtain<IWhisparrHeldSiteReading>()
            .Match<CapabilityRefusal?>(_ => null, refused => refused);

        Assert.NotNull(refusal);
        Assert.Equal(WhisparrCapability.ReadHeldSites, refusal.Capability);
        Assert.Equal(WhisparrGeneration.V3, refusal.Generation);
    }

    private static IWhisparrSceneSearchGrabbing? SceneSearchRoleOn(
        WhisparrGeneration generation, RecordingWhisparrClient client)
        => GenerationCapabilities
            .For(generation, WhisparrRoleSet.From(client))
            .Obtain<IWhisparrSceneSearchGrabbing>()
            .Match<IWhisparrSceneSearchGrabbing?>(held => held, _ => null);

    // v2's catalogue arrives only by re-reading its own metadata source, so there is no way to
    // register an item this library holds and the instance does not.
    [Fact]
    public void TheOlderGenerationHoldsNoMissingSceneCapabilityAndRefusesTheRoleByName()
    {
        var capabilities = GenerationCapabilities.For(
            WhisparrGeneration.V2,
            WhisparrRoleSet.From(new RecordingWhisparrClient(RecordingWhisparrClient.Json(200, "{}"))));

        var refusal = capabilities.Obtain<IWhisparrMissingSceneActing>()
            .Match<CapabilityRefusal?>(_ => null, refused => refused);

        Assert.NotNull(refusal);
        Assert.Equal(WhisparrCapability.RegisterMissingScenes, refusal.Capability);
        Assert.Equal(WhisparrGeneration.V2, refusal.Generation);
    }

    [Fact]
    public void TheOlderGenerationHoldsTheStudioCapabilityAndHandsOutTheRole()
    {
        var client = new RecordingWhisparrClient(RecordingWhisparrClient.Json(200, "{}"));
        var capabilities = GenerationCapabilities.For(
            WhisparrGeneration.V2, WhisparrRoleSet.From(client));

        Assert.Contains(WhisparrCapability.MonitorStudio, capabilities.Held);
        Assert.Same(
            client,
            capabilities.Obtain<IWhisparrStudioActing>()
                .Match<IWhisparrStudioActing?>(held => held, _ => null));
    }

    // Checked against the notification schemas the two builds themselves returned, so the table is
    // compared with the instances rather than with itself. Registering one generation's field on the
    // other is a save that is accepted and delivers nothing.
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
            var carried = OutOfBandRoleOf(generation)!.Carry("a-secret");

            Assert.All(carried.Fields, field => Assert.Contains(field.Name, declared));
            Assert.Contains(
                WhisparrCapability.OutOfBandCallbackSecret,
                GenerationCapabilities.For(generation).Held);
        }
    }

    [Fact]
    public void TheV3RoleCarriesTheSecretAsACustomHeader()
    {
        var carried = OutOfBandRoleOf(WhisparrGeneration.V3)!.Carry("a-secret");

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
        var carried = OutOfBandRoleOf(WhisparrGeneration.V2)!.Carry("a-secret");

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
            Assert.NotNull(role);

            Assert.ThrowsAny<ArgumentException>(() => role.Carry(secret!));
        }
    }

    [Fact]
    public void ARoleThisProductDoesNotDeclareIsNotAnsweredAsARefusal()
        => Assert.Throws<InvalidOperationException>(
            () => GenerationCapabilities.For(WhisparrGeneration.V3).Obtain<IWhisparrClient>());

    // The expected spelling is transcribed by hand from the camelCase wire convention, not computed
    // from the enum.
    [Fact]
    public void TheCapabilityTravelsInTheCamelCaseSpelling()
        => Assert.Equal(
            "[\"outOfBandCallbackSecret\",\"monitorStudio\",\"monitorPerformer\","
                + "\"registerMissingScenes\",\"reflectOwnedFiles\",\"searchMonitored\","
                + "\"readSceneStatus\",\"readSceneExclusions\",\"searchScene\","
                + "\"monitorScene\",\"excludeScene\",\"readInstanceFilesystem\"]",
            JsonSerializer.Serialize(
                GenerationCapabilities.For(WhisparrGeneration.V3).Held, HostJsonOptions));

    private static IOutOfBandSecretRegistration? OutOfBandRoleOf(WhisparrGeneration generation)
        => GenerationCapabilities.For(generation)
            .Obtain<IOutOfBandSecretRegistration>()
            .Match<IOutOfBandSecretRegistration?>(held => held, _ => null);

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
