using System.Text.Json;
using Renamer.Options;

namespace Renamer.Tests.Options;

/// <summary>
/// The frontend↔backend store contract: a panel-shaped JSON blob deserializes via
/// <see cref="RenamerOptions.JsonOptions"/> into the EXPECTED <see cref="RenamerOptions"/> (value
/// equality), AND a C#-serialized blob deserializes back equal — BOTH directions, so the panel can
/// read a backend-written blob and write one the backend reads losslessly. Property-name matching is
/// proven case-insensitive (lowerCamel and PascalCase mixed), and the three enums are matched as
/// stable strings.
/// </summary>
public sealed class OptionsRoundTripTests
{
    [Fact]
    public void Deserialize_RequiredFields_ReplacesDefault_DoesNotAppendToTitle()
    {
        // Reproduces the live gating bug: a stored blob sets RequiredFields to a single token.
        // System.Text.Json, by default, POPULATES a pre-initialized List<string> ("title") instead of
        // replacing it, yielding ["title","studioCode"] — so the user's chosen gate silently never
        // fires (title is always present). The deserialized list must be EXACTLY what the blob said.
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
            "whitelist": ["Alice", "Bob"],
            "blacklist": ["Carol"]
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
            Whitelist = ["Alice", "Bob"],
            Blacklist = ["Carol"],
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
        // Structural record equality — proves every panel field (mixed casing) bound correctly.
        Assert.Equal(ExpectedFromPanel(), loaded);
    }

    [Fact]
    public void Backend_Serialized_Blob_Deserializes_Back_Equal()
    {
        var original = ExpectedFromPanel();

        var json = JsonSerializer.Serialize(original, RenamerOptions.JsonOptions);
        var reloaded = JsonSerializer.Deserialize<RenamerOptions>(json, RenamerOptions.JsonOptions);

        Assert.Equal(original, reloaded); // the other direction: C#-written → read back equal
    }

    [Fact]
    public void PanelJson_To_Backend_To_PanelShape_Survives_BothDirections()
    {
        // Full loop: panel JSON → RenamerOptions → backend JSON → RenamerOptions, all equal.
        var fromPanel = JsonSerializer.Deserialize<RenamerOptions>(PanelJson, RenamerOptions.JsonOptions);
        var backendJson = JsonSerializer.Serialize(fromPanel, RenamerOptions.JsonOptions);
        var roundTripped = JsonSerializer.Deserialize<RenamerOptions>(backendJson, RenamerOptions.JsonOptions);

        Assert.Equal(ExpectedFromPanel(), roundTripped);
    }

    [Fact]
    public void Enums_Bind_From_String_Names_In_Either_Casing()
    {
        // lowerCamel property names + string enum values — the TS contract is case-insensitive on
        // property names while enum VALUES are the stable PascalCase strings.
        const string json = """{ "case": "Lower", "performers": { "onOverflow": "KeepFirst" } }""";

        var loaded = JsonSerializer.Deserialize<RenamerOptions>(json, RenamerOptions.JsonOptions);

        Assert.NotNull(loaded);
        Assert.Equal(CaseTransform.Lower, loaded!.Case);
        Assert.Equal(OverflowPolicy.KeepFirst, loaded.Performers.OnOverflow);
    }

    [Fact]
    public void AllowedRoots_RoundTrips_StructurallyEqual()
    {
        // A non-empty AllowedRoots must survive a C# serialize→deserialize loop value-equal —
        // proves the List<string> compares structurally (SequenceEqual), not by reference.
        var original = new RenamerOptions
        {
            AllowedRoots = ["D:/media", "E:/archive/movies"],
        };

        var json = JsonSerializer.Serialize(original, RenamerOptions.JsonOptions);
        var reloaded = JsonSerializer.Deserialize<RenamerOptions>(json, RenamerOptions.JsonOptions);

        Assert.Equal(original, reloaded);
        Assert.Equal(["D:/media", "E:/archive/movies"], reloaded!.AllowedRoots);
    }

    [Fact]
    public void AllowedRoots_MissingProperty_DefaultsToEmpty_NoThrow()
    {
        // Forward-compat: a blob written before AllowedRoots existed (omits the property) must
        // deserialize to an EMPTY list — the legacy source-confine behavior — without throwing.
        const string json = """{ "filenameTemplate": "$title" }""";

        var opts = JsonSerializer.Deserialize<RenamerOptions>(json, RenamerOptions.JsonOptions);

        Assert.NotNull(opts);
        Assert.Empty(opts!.AllowedRoots);
    }

    [Fact]
    public void AllowedRoots_Equality_Discriminates_On_Content_And_Order()
    {
        var baseline = new RenamerOptions { AllowedRoots = ["D:/media", "E:/archive"] };
        var same = new RenamerOptions { AllowedRoots = ["D:/media", "E:/archive"] };
        var reordered = new RenamerOptions { AllowedRoots = ["E:/archive", "D:/media"] };
        var different = new RenamerOptions { AllowedRoots = ["D:/media"] };

        Assert.Equal(baseline, same);
        Assert.Equal(baseline.GetHashCode(), same.GetHashCode());
        Assert.NotEqual(baseline, reordered);
        Assert.NotEqual(baseline, different);
    }
}
