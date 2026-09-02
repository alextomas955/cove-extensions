using System.Reflection;
using WhisparrSync.Monitoring;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.Invariants;

/// <summary>
/// The safety invariants that hold over what this product can express at all.
/// </summary>
/// <remarks>
/// Nothing here drives an implementation. Every test states which requests the declared surface can
/// express and which it cannot, so a pass never says that a correct request was exercised.
/// <para>
/// Asserted on the seam's declared member set, on the verb-class vocabulary and on the routes the
/// client declares, rather than on a log of calls that were never made: an empty log against a call
/// nobody could place agrees with itself whatever the code does.
/// </para>
/// </remarks>
public sealed class AbsentCapabilityTests
{
    /// <summary>
    /// Every route the outbound client declares, transcribed by hand from its own constants.
    /// </summary>
    /// <remarks>
    /// The set is the claim. The command route <c>api/v3/command</c> is declared here, because every
    /// instance-side action an instance takes is issued through it, so its presence is not by itself
    /// evidence of anything: the claim is that exactly one member of the whole seam can send a
    /// grabbing command name and only the separately obtained role declares that member, which
    /// <see cref="SafetyInvariantTests.ExactlyOneSeamMemberGrabsAndOnlyTheGrabbingRoleDeclaresIt"/>
    /// asserts. That no body off a monitoring path names one of those commands is
    /// <see cref="SafetyInvariantTests.NoBodyOffAMonitoringPathCanNameAGrabbingCommand"/>.
    /// </remarks>
    private static readonly string[] DeclaredRoutes =
    [
        "api/v3/system/status",
        "api/v3/notification",
        "api/v3/notification/schema",
        "api/v3/rootfolder",
        "api/v3/history",
        "api/v3/qualityprofile",
        "api/v3/studio",
        "api/v3/studio/editor",
        "api/v3/performer",
        "api/v3/performer/editor",
        "api/v3/series",
        "api/v3/series/lookup",
        "api/v3/series/editor",
        "api/v3/seasonpass",
        "api/v3/command",
        "api/v3/movie",
        "api/v3/manualimport",
        "api/v3/config/mediamanagement",
    ];

    /// <summary>
    /// Every member that can add to an instance is an acting member, and none is on the read seam.
    /// </summary>
    /// <remarks>
    /// The acting members are named here rather than gathered, so a member that could add something
    /// and was not written down fails this test. Keeping them off the read-and-configure interface is
    /// what lets a reader of that interface hold it without holding an add.
    /// <para>
    /// The behavioural half — that every composed add body carries both of its generation's
    /// acquisition-suppressing flags, present and false, over every generation, kind and scope the
    /// registered capabilities allow — is
    /// <see cref="SafetyInvariantTests.EveryAddThisProductCanComposeSuppressesAcquisitionInBothSpellings"/>.
    /// This test is the type-level half: it says which members can add, not what they send.
    /// </para>
    /// </remarks>
    [Fact]
    [Trait(SafetyInvariant.Trait, SafetyInvariant.EveryAddIsNonGrabbing)]
    public void EveryMemberThatCanAddToAnInstanceIsAnActingMember()
    {
        Assert.Equal(
            [
                nameof(IWhisparrPerformerActing.AddMonitoredPerformerAsync),
                nameof(IWhisparrStudioActing.AddMonitoredStudioAsync),
                nameof(IWhisparrMissingSceneActing.AddSceneAsync),
                nameof(IWhisparrReflectOwnedActing.AttachOwnedFilesAsync),
                nameof(IWhisparrMissingSceneActing.RefreshCatalogueAsync),
                nameof(IWhisparrPerformerActing.SetPerformerMonitoredAsync),
                nameof(IWhisparrStudioActing.SetStudioMonitoredAsync),
                nameof(IWhisparrStudioActing.SetStudioScopeAsync),
            ],
            OutboundSeam.MembersOf(WhisparrVerbClass.Act));

        Assert.DoesNotContain(
            typeof(IWhisparrClient).GetMethods().Select(method => method.Name),
            name => OutboundSeam.VerbClassByMember[name] == WhisparrVerbClass.Act);
    }

    /// <summary>
    /// The verb-class vocabulary and the declared route set are exactly what was written down.
    /// </summary>
    /// <remarks>
    /// Exact equality both times. A fifth verb class added later fails here rather than being classed
    /// by whoever added it, and a route the client can issue that nobody transcribed fails here rather
    /// than reaching an instance unnamed.
    /// </remarks>
    [Fact]
    [Trait(SafetyInvariant.Trait, SafetyInvariant.OnlyAnExplicitSearchGrabs)]
    public void TheVerbClassVocabularyAndTheDeclaredRoutesAreExactlyTheTranscribedSets()
    {
        Assert.Equal(
            [
                WhisparrVerbClass.Read,
                WhisparrVerbClass.Configure,
                WhisparrVerbClass.Act,
                WhisparrVerbClass.Grab,
            ],
            Enum.GetValues<WhisparrVerbClass>());

        Assert.Equal(DeclaredRoutes.Order().ToList(), RoutesDeclaredByTheClient().Order().ToList());
    }

    /// <summary>
    /// Nothing outside the one seam can reach an instance at all.
    /// </summary>
    /// <remarks>
    /// Asserts absence. The seam's configuring half is the callback registration and nothing else, and
    /// it is the only type in this extension holding a client to make a request with, so there is no
    /// second call site at which a mutation could be expressed and no mutation to tag an origin onto.
    /// </remarks>
    [Fact]
    [Trait(SafetyInvariant.Trait, SafetyInvariant.EveryMutationIsOriginTagged)]
    public void TheProductDeclaresNoCapabilityToMutateAnythingOnAnInstance()
    {
        Assert.Equal(
            [
                nameof(IWhisparrClient.CreateNotificationAsync),
                nameof(IWhisparrClient.UpdateNotificationAsync),
            ],
            OutboundSeam.MembersOf(WhisparrVerbClass.Configure));

        Assert.Equal([nameof(WhisparrClient)], TypesHoldingAnHttpClient().Order().ToList());
    }

    /// <summary>The relative routes the outbound client declares, read off its own constants.</summary>
    private static IEnumerable<string> RoutesDeclaredByTheClient()
        => typeof(WhisparrClient)
            .GetFields(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string?)field.GetRawConstantValue())
            .OfType<string>()
            .Where(value => value.StartsWith("api/", StringComparison.Ordinal));

    /// <summary>Every type in this extension that holds something it could make a request with.</summary>
    private static IEnumerable<string> TypesHoldingAnHttpClient()
        => typeof(IWhisparrClient).Assembly
            .GetTypes()
            .Where(type => type
                .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                .Any(field => typeof(HttpClient).IsAssignableFrom(field.FieldType)
                    || typeof(IHttpClientFactory).IsAssignableFrom(field.FieldType)))
            .Select(type => type.Name);
}
