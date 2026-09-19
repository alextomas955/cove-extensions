using Renamer.Options;

namespace Renamer.Tests.Options;

/// <summary>
/// Default-template lock: the out-of-box default <see cref="RenamerOptions.FilenameTemplate"/> is the
/// optional-grouped literal <c>{$date - }$title{ [$resolution]}</c>. The date group drops its <c>" - "</c>
/// when <c>$date</c> resolves empty and the resolution group drops the whole <c> [...]</c> when
/// <c>$resolution</c> resolves empty, so a fresh install never leaves a leading separator or a dangling
/// <c>[]</c>. ($resolution — the bucketed label 4k/1080p/… — is the default rather than $height's raw
/// pixel count, so a library already tagged [1080p] is not churned to [1080]; $height stays available
/// as a token for anyone who wants the raw height.)
/// <see cref="RenamerOptions.FolderTemplate"/> stays <c>""</c> — folder move remains opt-in.
/// </summary>
public sealed class DefaultTemplateTests
{
    [Fact]
    public void DefaultFilenameTemplate_IsTheGroupedDateTitleResolutionString()
    {
        Assert.Equal("{$date - }$title{ [$resolution]}", new RenamerOptions().FilenameTemplate);
        Assert.Equal("", new RenamerOptions().FolderTemplate); // folder move stays opt-in
    }

}
