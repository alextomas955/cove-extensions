using System.Text.Json;
using Renamer.Options;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Options;

public sealed class OptionsRoundTripTests
{
    // A settings PUT body: the panel sends the wire spelling, camelCase names and camelCase enum values,
    // and the endpoint binds it with the persisted blob's serializer settings. The PascalCase names mixed
    // in are the persisted spelling, which binds the same way.
    private const string PanelJson =
        """
        {
          "filenameTemplate": "$studio - $title [$resolution]",
          "FolderTemplate": "$studio/$year",
          "dateFormat": "yyyy-MM-dd",
          "Case": "title",
          "asciiTransliterate": true,
          "filenameMax": 200,
          "FullPathMax": 240,
          "onlyOrganized": true,
          "autoRenamerOnUpdate": true,
          "duplicateSuffixFormat": " ({n})",
          "performers": {
            "separator": " & ",
            "maxCount": 3,
            "onOverflow": "keepFirst",
            "sort": "none",
            "whitelistIds": [11, 12],
            "blacklistIds": [13]
          },
          "Tags": { "separator": "_", "sort": "nameAsc" },
          "dropOrder": ["title", "studio", "tags"],
          "requiredFields": ["title", "studio"]
        }
        """;

    private static RenamerOptions ExpectedFromPanel() => new()
    {
        FilenameTemplate = "$studio - $title [$resolution]",
        FolderTemplate = "$studio/$year",
        DateFormat = "yyyy-MM-dd",
        Case = CaseTransform.Title,
        AsciiTransliterate = true,
        FilenameMax = 200,
        FullPathMax = 240,
        OnlyOrganized = true,
        AutoRenamerOnUpdate = true,
        DuplicateSuffixFormat = " ({n})",
        Performers = new MultiValueOptions
        {
            Separator = " & ",
            MaxCount = 3,
            OnOverflow = OverflowPolicy.KeepFirst,
            Sort = SortOrder.None,
            WhitelistIds = [11, 12],
            BlacklistIds = [13],
        },
        Tags = new MultiValueOptions { Separator = "_", Sort = SortOrder.NameAsc },
        DropOrder = ["title", "studio", "tags"],
        RequiredFields = ["title", "studio"],
    };

    [Fact]
    public void PanelJson_Deserializes_Into_ExpectedOptions_CaseInsensitively()
    {
        var loaded = JsonSerializer.Deserialize<RenamerOptions>(PanelJson, RenamerOptions.JsonOptions);

        Assert.NotNull(loaded);
        // Compared as the persisted document, which covers every panel field (mixed casing) that bound.
        Assert.Equal(OptionsJson.Canonical(ExpectedFromPanel()), OptionsJson.Canonical(loaded));
    }
}
