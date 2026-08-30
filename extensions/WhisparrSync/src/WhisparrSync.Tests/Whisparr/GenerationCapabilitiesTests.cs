using System.Text.Json;
using WhisparrSync.Contracts;
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
            [WhisparrCapability.OutOfBandCallbackSecret],
            GenerationCapabilities.For(WhisparrGeneration.V3).Held);
    }

    /// <summary>
    /// The refusal is an answer of its own: not null, not a default role, and not an exception the
    /// caller has to catch to learn what happened.
    /// </summary>
    [Fact]
    public void TheV2SetRefusesTheOutOfBandSecretRoleAndNamesWhatItRefused()
    {
        var capabilities = GenerationCapabilities.For(WhisparrGeneration.V2);

        var refusal = capabilities.Obtain<IOutOfBandSecretRegistration>()
            .Match<CapabilityRefusal?>(_ => null, refused => refused);

        Assert.NotNull(refusal);
        Assert.Equal(WhisparrCapability.OutOfBandCallbackSecret, refusal.Capability);
        Assert.Equal(WhisparrGeneration.V2, refusal.Generation);
        Assert.Empty(capabilities.Held);
    }

    /// <summary>
    /// The split is the one the two builds declare: v3's Webhook schema carries a headers field and
    /// v2's does not, which is the whole reason one generation holds the role.
    /// </summary>
    [Fact]
    public void EachGenerationHoldsTheRoleExactlyWhenItsOwnSchemaDeclaresTheCarrierField()
    {
        Assert.Contains(V3HeaderSecretRegistration.HeadersField, DeclaredFields(V3SchemaFixture));
        Assert.DoesNotContain(V3HeaderSecretRegistration.HeadersField, DeclaredFields(V2SchemaFixture));

        Assert.Contains(
            WhisparrCapability.OutOfBandCallbackSecret,
            GenerationCapabilities.For(WhisparrGeneration.V3).Held);
        Assert.DoesNotContain(
            WhisparrCapability.OutOfBandCallbackSecret,
            GenerationCapabilities.For(WhisparrGeneration.V2).Held);
    }

    [Fact]
    public void TheRoleCarriesTheSecretInTheFieldThatSchemaDeclares()
    {
        var role = OutOfBandRoleOf(WhisparrGeneration.V3);
        Assert.NotNull(role);

        var carried = role.Carry("a-secret");

        Assert.Equal("headers", carried.FieldName);
        Assert.Equal("X-Cove-Whisparr-Sync-Secret", carried.HeaderName);
        Assert.Equal("a-secret", carried.HeaderValue);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ASecretWithNothingInItIsRefusedRatherThanRegisteredAsAnEmptyHeader(string? secret)
    {
        var role = OutOfBandRoleOf(WhisparrGeneration.V3);
        Assert.NotNull(role);

        Assert.ThrowsAny<ArgumentException>(() => role.Carry(secret!));
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
            "[\"outOfBandCallbackSecret\"]",
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
