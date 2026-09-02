using System.Text.Json;
using WhisparrSync.Contracts;
using WhisparrSync.Monitoring;
using WhisparrSync.Tests.TestSupport;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.Whisparr;

/// <summary>
/// Which roles each generation holds, and what a caller gets when it asks for one that is absent.
/// </summary>
/// <remarks>
/// The capability split is checked against the notification schemas the two builds themselves
/// returned, so this product's table is compared with the instances rather than with itself.
/// </remarks>
public sealed class GenerationCapabilitiesTests
{
    /// <summary>The build each fixture was captured from, named in the file it is read from.</summary>
    private const string V3SchemaFixture = "whisparr-v3-3.3.8.1097-notification-schema-webhook.json";

    /// <inheritdoc cref="V3SchemaFixture"/>
    private const string V2SchemaFixture = "whisparr-v2-2.2.0.231-notification-schema-webhook.json";

    private static readonly JsonSerializerOptions HostJsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void TheV3SetHoldsTheOutOfBandSecretRole()
    {
        var role = OutOfBandRoleOf(WhisparrGeneration.V3);

        Assert.NotNull(role);
        Assert.Equal(
            [WhisparrCapability.OutOfBandCallbackSecret, WhisparrCapability.MonitorStudio],
            GenerationCapabilities.For(WhisparrGeneration.V3).Held);
    }

    [Fact]
    public void TheV2SetAlsoHoldsTheOutOfBandSecretRole()
    {
        var role = OutOfBandRoleOf(WhisparrGeneration.V2);

        Assert.NotNull(role);
        Assert.Equal(
            [WhisparrCapability.OutOfBandCallbackSecret],
            GenerationCapabilities.For(WhisparrGeneration.V2).Held);
    }

    /// <summary>
    /// A refusal is an answer of its own: not null, not a default role, and not an exception the
    /// caller has to catch to learn what happened.
    /// </summary>
    /// <remarks>
    /// Taken through a real generation gap: the older generation addresses no studio at all, so its
    /// set holds no studio-acting role. A generation whose capability list is empty would be a
    /// generation nothing manages, so the refusal is asserted where a user actually meets one.
    /// </remarks>
    [Fact]
    public void ASetHoldingNoRoleRefusesAndNamesWhatItRefused()
    {
        var capabilities = GenerationCapabilities.For(
            WhisparrGeneration.V2,
            WhisparrRoleSet.From(new RecordingWhisparrClient(RecordingWhisparrClient.Json(200, "{}"))));

        var refusal = capabilities.Obtain<IWhisparrStudioActing>()
            .Match<CapabilityRefusal?>(_ => null, refused => refused);

        Assert.NotNull(refusal);
        Assert.Equal(WhisparrCapability.MonitorStudio, refusal.Capability);
        Assert.Equal(WhisparrGeneration.V2, refusal.Generation);
        Assert.DoesNotContain(WhisparrCapability.MonitorStudio, capabilities.Held);
    }

    /// <summary>
    /// A capability the generation HOLDS, asked of a set built with no source for it, is a fault
    /// rather than a refusal.
    /// </summary>
    /// <remarks>
    /// The two answers are what a caller would otherwise be unable to tell apart: one says the
    /// instance cannot do this and the other says this product was wired wrong, and only the first is
    /// something to tell a user about their Whisparr.
    /// </remarks>
    [Fact]
    public void ACapabilityHeldButUnsourcedIsAFaultRatherThanARefusal()
        => Assert.Throws<InvalidOperationException>(
            () => GenerationCapabilities.For(WhisparrGeneration.V3).Obtain<IWhisparrStudioActing>());

    /// <summary>The capability table is per generation, and the newer one holds strictly more.</summary>
    [Fact]
    public void EachGenerationsCapabilitiesAreWrittenDownPerGeneration()
    {
        Assert.Equal(
            [WhisparrCapability.OutOfBandCallbackSecret, WhisparrCapability.MonitorStudio],
            GenerationCapabilities.CapabilitiesOf(WhisparrGeneration.V3));
        Assert.Equal(
            [WhisparrCapability.OutOfBandCallbackSecret],
            GenerationCapabilities.CapabilitiesOf(WhisparrGeneration.V2));
        Assert.Empty(GenerationCapabilities.CapabilitiesOf((WhisparrGeneration)(-1)));
    }

    /// <summary>
    /// Each generation carries the secret in fields its OWN schema declares, and the two sets are
    /// disjoint: a list-of-headers field on one, a user-and-password pair on the other.
    /// </summary>
    /// <remarks>
    /// Checked against the schemas the two builds themselves returned, so this product's table is
    /// compared with the instances rather than with itself. The absence half matters as much as the
    /// presence half: registering one generation's field on the other is a save that is accepted and
    /// delivers nothing.
    /// </remarks>
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

    /// <summary>The newer generation sets one field, a list of headers, carrying this product's own.</summary>
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

    /// <summary>The older generation sets two fields, and the secret is the PASSWORD half.</summary>
    /// <remarks>
    /// That the instance then sends them as an authorization header, and that Cove delivers one to a
    /// route declaring the anonymous convention, are both measured on the fixture rather than
    /// inferred. The header named here is what such a delivery arrives in.
    /// </remarks>
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

    /// <summary>
    /// A role this product does not declare is a build error rather than an answer about the
    /// connected instance, so it is not expressible as a refusal.
    /// </summary>
    [Fact]
    public void ARoleThisProductDoesNotDeclareIsNotAnsweredAsARefusal()
        => Assert.Throws<InvalidOperationException>(
            () => GenerationCapabilities.For(WhisparrGeneration.V3).Obtain<IWhisparrClient>());

    /// <summary>The wire spelling, transcribed by hand from the convention it must follow.</summary>
    [Fact]
    public void TheCapabilityTravelsInTheCamelCaseSpelling()
        => Assert.Equal(
            "[\"outOfBandCallbackSecret\",\"monitorStudio\"]",
            JsonSerializer.Serialize(
                GenerationCapabilities.For(WhisparrGeneration.V3).Held, HostJsonOptions));

    /// <summary>The role <paramref name="generation"/> holds, or null when it is refused.</summary>
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
