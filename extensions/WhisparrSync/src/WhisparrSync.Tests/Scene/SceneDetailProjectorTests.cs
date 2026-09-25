using WhisparrSync.Scene;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.Scene;

// Each case names its own raw answer. A builder composing the value under test would agree with
// itself whatever the projection did.
public sealed class SceneDetailProjectorTests
{
    private const string JsonContentType = "application/json; charset=utf-8";

    private const string HeldWithAFile =
        """
        [{"id":41,"stashId":"9b6a0f8e-5f2c-4a1d-8b7e-2c3d4e5f6a7b","monitored":true,"hasFile":true,
          "qualityProfileId":6,
          "movieFile":{"id":9,"quality":{"quality":{"id":7,"name":"WEBDL-1080p"},"revision":{"version":1}}}}]
        """;

    private const string HeldWithNoFile =
        """
        [{"id":41,"stashId":"9b6a0f8e-5f2c-4a1d-8b7e-2c3d4e5f6a7b","monitored":true,"hasFile":false,
          "qualityProfileId":6}]
        """;

    // Whisparr's per-scene route answers an empty array for a scene it holds no entry for.
    private const string NotHeld = "[]";

    private const string ProfileWithALeafCutoff =
        """
        [{"id":6,"name":"HD-1080p","cutoff":7,"upgradeAllowed":true,
          "items":[{"quality":{"id":4,"name":"HDTV-720p"},"items":[],"allowed":true},
                   {"quality":{"id":7,"name":"WEBDL-1080p"},"items":[],"allowed":true}]}]
        """;

    private const string ProfileWithAGroupCutoff =
        """
        [{"id":6,"name":"HD-1080p","cutoff":1000,"upgradeAllowed":true,
          "items":[{"id":1000,"name":"WEB 1080p","allowed":true,
                    "items":[{"quality":{"id":7,"name":"WEBDL-1080p"},"items":[],"allowed":true},
                             {"quality":{"id":8,"name":"WEBRip-1080p"},"items":[],"allowed":true}]}]}]
        """;

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

    // The scene row carries no exclusion member, so the caller's value is the only source. Both
    // directions are asserted, because a projection answering a constant would agree with one.
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

    // A cutoff is an id on the profile, and a group is one of the things that id can name.
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

    // An unreadable answer establishes as little as no answer, so both report the same way.
    // Reporting the profile as absent would state contents no read ever reached.
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
