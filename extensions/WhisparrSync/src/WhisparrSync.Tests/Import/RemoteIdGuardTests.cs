using WhisparrSync.Import;

namespace WhisparrSync.Tests.Import;

// Both channels read through the guard, so a rendering accepted here is accepted on both and a
// rendering refused here is refused on both.
public sealed class RemoteIdGuardTests
{
    // The control for the refusals below: without it every assertion here would hold against a guard
    // that refused everything, and both channels would match on nothing.
    [Theory]
    [InlineData("1703a150-ceec-4953-ac10-d7ebc7d0974f")]
    [InlineData("4149372")]
    [InlineData("31875")]
    public void AnIssuedIdentifierNamesAScene(string rendered)
        => Assert.Equal(rendered, RemoteIdGuard.Identifying(rendered));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AnAbsentRenderingNamesNoScene(string? rendered)
        => Assert.Null(RemoteIdGuard.Identifying(rendered));

    // One lineage carries its identifier as a number whose unset value is zero, so an entity that was
    // never matched still renders one. Taken as an identifier it would make every unmatched scene the
    // same scene.
    [Theory]
    [InlineData("0")]
    [InlineData("00")]
    public void AZeroNamesNoScene(string rendered)
        => Assert.Null(RemoteIdGuard.Identifying(rendered));

    // The bound on the refusal above: an identifier is refused for being zero, not for looking like
    // one, and a source is free to issue an identifier whose text starts with a zero digit.
    [Theory]
    [InlineData("01")]
    [InlineData("0abc")]
    [InlineData("0e2e0e2e")]
    public void ARenderingThatOnlyBeginsWithAZeroNamesAScene(string rendered)
        => Assert.Equal(rendered, RemoteIdGuard.Identifying(rendered));
}
