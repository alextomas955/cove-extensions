using WhisparrSync.Import;

namespace WhisparrSync.Tests.Import;

/// <summary>
/// Which renderings of an identifier name a scene, and which name none.
/// </summary>
/// <remarks>
/// Both channels read through the guard, so a rendering accepted here is accepted on both and a
/// rendering refused here is refused on both.
/// </remarks>
public sealed class RemoteIdGuardTests
{
    /// <summary>An identifier a metadata source really issued names a scene.</summary>
    /// <remarks>
    /// The discriminating control for the refusals below: without it every assertion here would hold
    /// against a guard that refused everything, and both channels would match on nothing.
    /// </remarks>
    [Theory]
    [InlineData("1703a150-ceec-4953-ac10-d7ebc7d0974f")]
    [InlineData("4149372")]
    [InlineData("31875")]
    public void AnIssuedIdentifierNamesAScene(string rendered)
        => Assert.Equal(rendered, RemoteIdGuard.Identifying(rendered));

    /// <summary>An absent or blank rendering names none.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AnAbsentRenderingNamesNoScene(string? rendered)
        => Assert.Null(RemoteIdGuard.Identifying(rendered));

    /// <summary>
    /// A zero names none, however it was written.
    /// </summary>
    /// <remarks>
    /// One lineage carries its identifier as a number whose unset value is zero, so an entity that was
    /// never matched still renders one. Taken as an identifier it would make every unmatched scene the
    /// same scene.
    /// </remarks>
    [Theory]
    [InlineData("0")]
    [InlineData("00")]
    public void AZeroNamesNoScene(string rendered)
        => Assert.Null(RemoteIdGuard.Identifying(rendered));

    /// <summary>
    /// A rendering that merely begins with a zero still names a scene.
    /// </summary>
    /// <remarks>
    /// The bound on the refusal above: an identifier is refused for being zero, not for looking like
    /// one, and a source is free to issue an identifier whose text starts with a zero digit.
    /// </remarks>
    [Theory]
    [InlineData("01")]
    [InlineData("0abc")]
    [InlineData("0e2e0e2e")]
    public void ARenderingThatOnlyBeginsWithAZeroNamesAScene(string rendered)
        => Assert.Equal(rendered, RemoteIdGuard.Identifying(rendered));
}
