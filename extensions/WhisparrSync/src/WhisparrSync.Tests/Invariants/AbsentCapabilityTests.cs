using System.Reflection;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.Invariants;

/// <summary>
/// The safety invariants about capabilities this product does not hold.
/// </summary>
/// <remarks>
/// Every test here asserts an ABSENCE and not a behaviour. Nothing below drives an implementation,
/// and a pass says the capability cannot be expressed rather than that a correct one was exercised.
/// <para>
/// The absence is asserted on the seam's declared member set, on the verb-class vocabulary and on the
/// routes the client declares, rather than on a log of calls that were never made: an empty log
/// against a call nobody could place agrees with itself whatever the code does.
/// </para>
/// </remarks>
public sealed class AbsentCapabilityTests
{
    /// <summary>
    /// Every route the outbound client declares, transcribed by hand from its own constants.
    /// </summary>
    /// <remarks>
    /// The set is the claim. Whisparr issues a search, a grab and every other instance-side action
    /// through its command route, and no route here is one.
    /// </remarks>
    private static readonly string[] DeclaredRoutes =
    [
        "api/v3/system/status",
        "api/v3/notification",
        "api/v3/notification/schema",
        "api/v3/rootfolder",
        "api/v3/history",
    ];

    /// <summary>
    /// No member of the seam adds anything to an instance, so no add can be grabbing.
    /// </summary>
    /// <remarks>
    /// Asserts absence. The seam's whole member set is compared against the transcribed table, and its
    /// configuring half against the two members that register this product's own callback, so a member
    /// that added an entity to an instance's catalogue would fail here rather than be missing from a
    /// list.
    /// </remarks>
    [Fact]
    [Trait(SafetyInvariant.Trait, SafetyInvariant.EveryAddIsNonGrabbing)]
    public void TheProductDeclaresNoCapabilityToAddAnythingToAnInstance()
    {
        Assert.Equal(
            OutboundSeam.VerbClassByMember.Keys.Order().ToList(),
            typeof(IWhisparrClient).GetMethods().Select(method => method.Name).Order().ToList());

        Assert.Equal(
            [
                nameof(IWhisparrClient.CreateNotificationAsync),
                nameof(IWhisparrClient.UpdateNotificationAsync),
            ],
            OutboundSeam.MembersOf(WhisparrVerbClass.Configure));
    }

    /// <summary>
    /// No member of the seam makes an instance search or grab.
    /// </summary>
    /// <remarks>
    /// Asserts absence. The verb-class vocabulary declares no grabbing class to name one under, and
    /// the client declares no route one could be issued through.
    /// </remarks>
    [Fact]
    [Trait(SafetyInvariant.Trait, SafetyInvariant.OnlyAnExplicitSearchGrabs)]
    public void TheProductDeclaresNoCapabilityToMakeAnInstanceSearchOrGrab()
    {
        Assert.Equal(
            [WhisparrVerbClass.Read, WhisparrVerbClass.Configure], Enum.GetValues<WhisparrVerbClass>());

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
