using WhisparrSync.Contracts;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.Invariants;

// The capability enum is on the wire and the per-generation lists are what the settings page and
// the monitor menu render from. Nothing at run time reads them to decide what a caller may do: a
// caller tests the bound instance for the role interface it needs. Each list is read from the roles
// its instance implements, so a capability offering a control that always refuses is not
// expressible. What the compiler still cannot catch is a capability on the wire that no generation
// reaches, which is a menu entry with nothing behind it.
public sealed class CapabilityDeclarationTests
{
    // Not a wire capability: no control offers it and nothing renders it. It is stated here because
    // the same measurement was made on both generations across two drives, and because a generation
    // that lost the registration would silently stop following an entity's files rather than fail.
    [Theory]
    [InlineData(typeof(WhisparrV3Instance))]
    [InlineData(typeof(WhisparrV2Instance))]
    public void BothGenerationsRegisterTheRelocationRole(Type instance)
    {
        ArgumentNullException.ThrowIfNull(instance);

        Assert.Contains(typeof(IWhisparrEntityRelocationActing), instance.GetInterfaces());
    }

    // A capability neither generation declares is one no instance has a member for, or one no row
    // ties to a role at all. Either way a browser can be sent a value that no control can act on.
    [Fact]
    public void EveryCapabilityIsReachedByAGeneration()
        => Assert.Equal(
            Enum.GetValues<WhisparrCapability>().Order(),
            Enum.GetValues<WhisparrGeneration>()
                .SelectMany(GenerationCapabilities.CapabilitiesOf)
                .Distinct()
                .Order());

    // Whether a wider scope rewrites what is already monitored is declared beside the lists and
    // answered to the browser on the monitoring view. A generation left out throws where it is
    // read, so a monitoring read fails rather than the menu quietly dropping the warning a reader
    // sees before a back catalogue is marked wanted.
    [Fact]
    public void EveryGenerationDeclaresWhetherAScopeChangeIsRetroactive()
        => Assert.Equal(
            Enum.GetValues<WhisparrGeneration>().Order(),
            GenerationCapabilities.GenerationsDeclaringScopeBehaviour.Order());
}
