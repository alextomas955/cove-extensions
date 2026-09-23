using System.Text.Json;
using Renamer.Options;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Options;

public sealed class OptionsRoundTripTests
{
    [Fact]
    public void Deserialize_RequiredFields_ReplacesDefault_DoesNotAppendToTitle()
    {
        // Reproduces the live gating bug: a stored blob sets RequiredFields to a single token.
        // System.Text.Json, by default, populates a pre-initialized List<string> ("title") instead of
        // replacing it, yielding ["title","studioCode"] - so the user's chosen gate silently never
        // fires (title is always present). The deserialized list must be exactly what the blob said.
        const string json = """{ "requiredFields": ["studioCode"] }""";

        var opts = JsonSerializer.Deserialize<RenamerOptions>(json, RenamerOptions.JsonOptions)!;

        Assert.Equal(["studioCode"], opts.RequiredFields);
    }

    [Fact]
    public void Deserialize_DropOrder_ReplacesDefault_DoesNotAppendToDefaults()
    {
        // Same STJ collection-populate hazard for the other defaulted List<string>.
        const string json = """{ "dropOrder": ["tags"] }""";

        var opts = JsonSerializer.Deserialize<RenamerOptions>(json, RenamerOptions.JsonOptions)!;

        Assert.Equal(["tags"], opts.DropOrder);
    }

    // A panel-shaped blob: mixed casing (lowerCamel + PascalCase), enums as strings, nested
    // Performers/Tags MultiValueOptions, and the DropOrder/RequiredFields/whitelist arrays.
    private const string PanelJson =
        """
        {
          "filenameTemplate": "$studio - $title [$resolution]",
          "FolderTemplate": "$studio/$year",
          "dateFormat": "yyyy-MM-dd",
          "Case": "Title",
          "asciiTransliterate": true,
          "filenameMax": 200,
          "FullPathMax": 240,
          "onlyOrganized": true,
          "autoRenamerOnUpdate": true,
          "duplicateSuffixFormat": " ({n})",
          "performers": {
            "separator": " & ",
            "maxCount": 3,
            "onOverflow": "KeepFirst",
            "sort": "None",
            "whitelistIds": [11, 12],
            "blacklistIds": [13]
          },
          "Tags": { "separator": "_", "sort": "NameAsc" },
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
