using Renamer.Options;

namespace Renamer.Tests.Options;

/// <summary>
/// Default-template lock: the out-of-box default <see cref="RenamerOptions.FilenameTemplate"/> is the
/// optional-grouped literal <c>{$date - }$title{ [$height]}</c>. The date group drops its <c>" - "</c>
/// when <c>$date</c> resolves empty and the height group drops the whole <c> [...]</c> when
/// <c>$height</c> resolves empty, so a fresh install never leaves a leading separator or a dangling
/// <c>[]</c>. The default lives in two hand-synced sources (this C# record +
/// <c>src/Renamer.Ui/src/options.ts</c> DEFAULT_OPTIONS); this test locks the C# side so an accidental
/// one-sided edit is caught. The TS side is covered by the live fresh-install verify.
/// <see cref="RenamerOptions.FolderTemplate"/> stays <c>""</c> — folder move remains opt-in. The two
/// cosmetic flags default on, asserted here as the C#-side default-parity lock against the TS mirror.
/// </summary>
public sealed class DefaultTemplateSyncTests
{
    [Fact]
    public void DefaultFilenameTemplate_IsTheGroupedDateTitleHeightString()
    {
        Assert.Equal("{$date - }$title{ [$height]}", new RenamerOptions().FilenameTemplate);
        Assert.Equal("", new RenamerOptions().FolderTemplate); // folder move stays opt-in
    }

    [Fact]
    public void DefaultFlags_FilenameAsTitle_And_PreventConsecutive_AreOn()
    {
        var o = new RenamerOptions();
        Assert.True(o.FilenameAsTitle);
        Assert.True(o.PreventConsecutiveSegments);
    }
}
