using WhisparrSync.Scene;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.Scene;

/// <summary>
/// What the scene tab states, derived from the two answers the instance gave.
/// </summary>
/// <remarks>
/// Each case names its own raw answer. A builder composing the value under test would agree with
/// itself whatever the projection did with it.
/// <para>
/// The absent cases are the point. A fact with no value is named absent, and a row removed instead
/// would read as a failed read.
/// </para>
/// </remarks>
public sealed class SceneDetailProjectorTests
{
    private const string JsonContentType = "application/json; charset=utf-8";

    /// <summary>The scene as the instance reports it while it holds a file for it.</summary>
    private const string HeldWithAFile =
        """
        [{"id":41,"stashId":"9b6a0f8e-5f2c-4a1d-8b7e-2c3d4e5f6a7b","monitored":true,"hasFile":true,
          "qualityProfileId":6,
          "movieFile":{"id":9,"quality":{"quality":{"id":7,"name":"WEBDL-1080p"},"revision":{"version":1}}}}]
        """;

    /// <summary>The same scene before anything has been acquired for it.</summary>
    private const string HeldWithNoFile =
        """
        [{"id":41,"stashId":"9b6a0f8e-5f2c-4a1d-8b7e-2c3d4e5f6a7b","monitored":true,"hasFile":false,
          "qualityProfileId":6}]
        """;

    /// <summary>What the per-scene route answers for a scene the instance holds no entry for.</summary>
    private const string NotHeld = "[]";

    /// <summary>A profile whose cutoff names one of its own leaf qualities.</summary>
    private const string ProfileWithALeafCutoff =
        """
        [{"id":6,"name":"HD-1080p","cutoff":7,"upgradeAllowed":true,
          "items":[{"quality":{"id":4,"name":"HDTV-720p"},"items":[],"allowed":true},
                   {"quality":{"id":7,"name":"WEBDL-1080p"},"items":[],"allowed":true}]}]
        """;

    /// <summary>A profile whose cutoff names one of its own groups instead of a single quality.</summary>
    private const string ProfileWithAGroupCutoff =
        """
        [{"id":6,"name":"HD-1080p","cutoff":1000,"upgradeAllowed":true,
          "items":[{"id":1000,"name":"WEB 1080p","allowed":true,
                    "items":[{"quality":{"id":7,"name":"WEBDL-1080p"},"items":[],"allowed":true},
                             {"quality":{"id":8,"name":"WEBRip-1080p"},"items":[],"allowed":true}]}]}]
        """;

    /// <summary>A profile carrying a cutoff no item of its own answers to.</summary>
    private const string ProfileWhoseCutoffNamesNothing =
        """
        [{"id":6,"name":"HD-1080p","cutoff":99,"upgradeAllowed":true,
          "items":[{"quality":{"id":7,"name":"WEBDL-1080p"},"items":[],"allowed":true}]}]
        """;

    [Fact]
    public void AFileTheInstanceHoldsIsNamedByTheInstancesOwnQualityName()
    {
        var view = SceneDetailProjector.Project(
            Json(HeldWithAFile), Json(ProfileWithALeafCutoff), excluded: false);

        Assert.Equal("WEBDL-1080p", view.QualityName);
        Assert.True(view.Present);
        Assert.True(view.Monitored);
    }

    [Fact]
    public void ASceneWithNoFileNamesNoQuality()
    {
        var view = SceneDetailProjector.Project(
            Json(HeldWithNoFile), Json(ProfileWithALeafCutoff), excluded: false);

        Assert.Null(view.QualityName);
        Assert.True(view.Present);
    }

    /// <summary>
    /// The exclusion the caller established is carried through, and it is never asserted.
    /// </summary>
    /// <remarks>
    /// Both directions, because a projection that answered a constant would agree with one of them.
    /// The scene's own row carries no exclusion member at all, so a row naming an excluded scene is
    /// indistinguishable from a row naming any other one.
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheExclusionTheCallerEstablishedIsWhatIsReported(bool excluded)
    {
        var view = SceneDetailProjector.Project(
            Json(HeldWithAFile), Json(ProfileWithALeafCutoff), excluded);

        Assert.Equal(excluded, view.Excluded);
        Assert.True(view.Present);
        Assert.True(view.Monitored);
    }

    [Fact]
    public void ASceneTheInstanceDoesNotHoldNamesNoProfileAndNoCutoff()
    {
        var view = SceneDetailProjector.Project(
            Json(NotHeld), Json(ProfileWithALeafCutoff), excluded: false);

        Assert.False(view.Present);
        Assert.Null(view.QualityProfileName);
        Assert.Null(view.CutoffName);
        Assert.False(view.ProfileReadDidNotComplete);
    }

    [Fact]
    public void TheProfileTheSceneNamesCarriesItsOwnNameAndTheQualityItsCutoffResolvesTo()
    {
        var view = SceneDetailProjector.Project(
            Json(HeldWithAFile), Json(ProfileWithALeafCutoff), excluded: false);

        Assert.Equal("HD-1080p", view.QualityProfileName);
        Assert.Equal("WEBDL-1080p", view.CutoffName);
    }

    /// <summary>
    /// A cutoff is an id on the profile, and a group is one of the things an id can name.
    /// </summary>
    [Fact]
    public void ACutoffNamingAGroupResolvesToTheGroupsOwnName()
    {
        var view = SceneDetailProjector.Project(
            Json(HeldWithAFile), Json(ProfileWithAGroupCutoff), excluded: false);

        Assert.Equal("WEB 1080p", view.CutoffName);
    }

    [Fact]
    public void ACutoffNoItemAnswersToIsNamedByNothing()
    {
        var view = SceneDetailProjector.Project(
            Json(HeldWithAFile), Json(ProfileWhoseCutoffNamesNothing), excluded: false);

        Assert.Equal("HD-1080p", view.QualityProfileName);
        Assert.Null(view.CutoffName);
        Assert.False(view.ProfileReadDidNotComplete);
    }

    /// <summary>
    /// A profile read that established nothing keeps the scene's own facts and says so separately.
    /// </summary>
    /// <remarks>
    /// An answer that arrived and could not be read establishes as little as no answer at all, so
    /// both report the same way. Reporting a profile as absent for either would name the instance's
    /// contents on a read that never reached them.
    /// </remarks>
    [Fact]
    public void AProfileReadThatEstablishedNothingKeepsTheSceneFactsAndReportsItself()
    {
        var noAnswer = SceneDetailProjector.Project(Json(HeldWithAFile), null, excluded: false);

        Assert.True(noAnswer.ProfileReadDidNotComplete);
        Assert.True(noAnswer.Monitored);
        Assert.Equal("WEBDL-1080p", noAnswer.QualityName);
        Assert.Null(noAnswer.QualityProfileName);
        Assert.Null(noAnswer.CutoffName);

        var declined = SceneDetailProjector.Project(
            Json(HeldWithAFile),
            new WhisparrResponse(500, JsonContentType, "nope"),
            excluded: false);

        Assert.True(declined.ProfileReadDidNotComplete);
        Assert.Equal("WEBDL-1080p", declined.QualityName);
        Assert.Null(declined.QualityProfileName);
    }

    private static WhisparrResponse Json(string body) => new(200, JsonContentType, body);
}
