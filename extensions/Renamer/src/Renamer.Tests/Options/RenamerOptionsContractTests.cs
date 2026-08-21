using System.Collections;
using System.Reflection;
using System.Text.Json;
using Renamer.Options;
using Renamer.Tests.TestSupport;
using Xunit.Abstractions;

namespace Renamer.Tests.Options;

/// <summary>
/// The <see cref="RenamerOptions"/> contract. What every option field must do — participate in
/// structural equality (the dirty-check the settings UI relies on) and survive a JSON round-trip
/// through the production serializer — is SWEPT by reflection, so a field added later is covered
/// without anyone writing a test for it. What reflection cannot derive is pinned by hand below: a
/// default VALUE is a decision, not a structure; so are a tolerance rule, a map's order-independence
/// and an enum's wire spelling.
/// </summary>
[Trait("Tier", "L0")]
public sealed class RenamerOptionsContractTests
{
    // 54 mutation paths are reachable at HEAD (38 direct properties + the 8 members of each of the two
    // nested MultiValueOptions). The floor is what makes an EMPTY violation list evidence instead of a
    // tautology: a sweep whose property filter matches nothing reports no violations while inspecting
    // nothing, which is the exact failure this file exists to end. It sits ~13 below the measured
    // number so a deliberate option removal does not trip it, and it is NOT a coverage target — an
    // accidental shrink shows up as a real measured count, not as this threshold.
    private const int MinimumExaminedPaths = 40;

    private readonly ITestOutputHelper _output;

    public RenamerOptionsContractTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void EveryPublicProperty_Participates_In_Equality()
    {
        Sweep sweep = ContractSweep.Build();
        ReportExamined(sweep);
        AssertSweepCanFail(sweep);

        var defaults = new RenamerOptions();
        var ignored = sweep.Mutations.Where(m => defaults == m.Mutated).Select(m => m.Label).ToList();

        Assert.True(
            ignored.Count == 0,
            "mutating these left the instance EQUAL to a default one, so they are missing from "
                + $"RenamerOptions.EqualityComponents(): {string.Join(", ", ignored)}");
    }

    [Fact]
    public void EveryPublicProperty_SurvivesAJsonRoundTrip()
    {
        Sweep sweep = ContractSweep.Build();
        AssertSweepCanFail(sweep);

        var broken = new List<string>();
        foreach (Mutation mutation in sweep.Mutations)
        {
            var json = JsonSerializer.Serialize(mutation.Mutated, RenamerOptions.JsonOptions);
            var reloaded = JsonSerializer.Deserialize<RenamerOptions>(json, RenamerOptions.JsonOptions);
            if (mutation.Mutated != reloaded)
            {
                broken.Add(mutation.Label);
            }
        }

        Assert.True(
            broken.Count == 0,
            "these did not come back value-equal through RenamerOptions.JsonOptions: "
                + string.Join(", ", broken));
    }

    [Fact]
    public void FullyPopulatedInstance_RoundTrips_Equal_AndIsNotTheDefault()
    {
        Sweep sweep = ContractSweep.Build();
        var defaults = new RenamerOptions();

        // A builder that silently populated nothing would make the round-trip below trivially true, so
        // the populated instance is first shown different from a default one property BY property —
        // an instance-level NotEqual would pass on a single populated field.
        var unpopulated = ContractSweep.UnchangedProperties(defaults, sweep.Populated);
        Assert.True(
            unpopulated.Count == 0,
            $"the populated builder left these at their default: {string.Join(", ", unpopulated)}");
        Assert.NotEqual(defaults, sweep.Populated);

        var json = JsonSerializer.Serialize(sweep.Populated, RenamerOptions.JsonOptions);
        var reloaded = JsonSerializer.Deserialize<RenamerOptions>(json, RenamerOptions.JsonOptions);

        Assert.Equal(sweep.Populated, reloaded);
    }

    // ---- Default VALUES. A default is a decision; reflection can only see that a property exists. ----

    [Fact]
    public void Defaults_Match_ContextDecisions()
    {
        var o = new RenamerOptions();

        // The out-of-box template is the optional-grouped literal below: the date group drops its " - "
        // when $date resolves empty and the resolution group drops the whole " [...]" when $resolution
        // does, so a fresh install never leaves a leading separator or a dangling []. ($resolution — the
        // bucketed 4k/1080p label — is the default rather than $height's raw pixel count, so a library
        // already tagged [1080p] is not churned to [1080]; $height stays available for anyone who wants
        // the raw height.) The default lives in two hand-synced sources, this record and
        // src/Renamer.Ui/src/options.ts DEFAULT_OPTIONS, so this locks the C# side against a one-sided
        // edit; the TS side is covered by the live fresh-install verify.
        Assert.Equal("{$date - }$title{ [$resolution]}", o.FilenameTemplate);
        Assert.Equal("", o.FolderTemplate); // folder move stays opt-in

        Assert.Equal(255, o.FilenameMax);
        Assert.Equal(259, o.FullPathMax);
        Assert.Equal(CaseTransform.None, o.Case);
        Assert.False(o.AsciiTransliterate);

        Assert.Equal(" ", o.Performers.Separator);
        Assert.Equal(" ", o.Tags.Separator);
        Assert.Equal(0, o.Performers.MaxCount);
        Assert.Equal(OverflowPolicy.DropAll, o.Performers.OnOverflow);
        Assert.Equal(SortOrder.NameAsc, o.Performers.Sort);

        Assert.Equal(
            new List<string>
            {
                "videoCodec", "audioCodec", "frameRate", "resolution",
                "tags", "studioCode", "studio", "performers", "date",
            },
            o.DropOrder);

        // Opt-in behavior defaults OFF …
        Assert.False(o.AutoRenamerOnUpdate);
        Assert.False(o.SqueezeStudioNames);
        Assert.False(o.StripLeadingArticles);
        Assert.False(o.PreventTitlePerformer);
        Assert.Empty(o.FieldReplacers);
        Assert.Empty(o.ExcludeTagIds);    // EXCL-01 empty = no excludes (legacy behavior)
        Assert.Empty(o.ExcludeStudioIds); // EXCL-02
        Assert.Empty(o.ExcludePaths);     // EXCL-03

        // The basename fallback is opt-in with the rest, and for the same reason: it is the one option
        // that makes a rename WRITE metadata (the derived title is recorded on the item), so a fresh
        // install must not do it unasked.
        Assert.False(o.FilenameAsTitle);

        // … and the cosmetic behavior a fresh install is expected to want defaults ON.
        Assert.True(o.PreventConsecutiveSegments); // /Foo/Foo/Bar collapses (folder path only)
        Assert.True(o.NormalizePunctuation);       // smart quotes/dashes fold to ASCII
        Assert.Equal(",#", o.RemoveCharacters);    // strips comma + hash out of the box
        Assert.Equal(new List<string> { "The", "A", "An" }, o.Articles);
    }

    [Fact]
    public void GatingAndSuffix_Defaults_Match_ContextDecisions()
    {
        var o = new RenamerOptions();

        Assert.False(o.OnlyOrganized);                                // gate off by default
        Assert.Equal(new List<string> { "title" }, o.RequiredFields);  // Title required by default
        Assert.Contains("{n}", o.DuplicateSuffixFormat);               // counter placeholder present
    }

    // ---- Equality semantics the one-property-at-a-time sweep cannot state ----

    [Fact]
    public void FreshInstances_SameValues_AreEqual_WithEqualHash()
    {
        var a = new RenamerOptions();
        var b = new RenamerOptions();

        // The sweep asserts only INEQUALITY, and GetHashCode is outside it entirely. Distinct
        // list/dictionary instances with identical contents (the defaults include DropOrder,
        // RequiredFields, Articles and the two MultiValueOptions) must still be value-equal, with an
        // equal hash — the half of the contract a mutation-based sweep can never buy.
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void PopulatedCollections_FreshInstances_SameContents_AreEqual_WithEqualHash()
    {
        RenamerOptions Make() => new()
        {
            DropOrder = ["studio", "date"],
            AllowedRoots = ["/media/a", "/media/b"],
            StudioDestinations = new() { [1] = Dests.At("/x"), [2] = Dests.At("/y") },
            TagDestinations = new() { [11] = Dests.At("/anime") },
            PathDestinations = [new PathDestinationRule { Pattern = "p", Dest = Dests.At("/d") }],
            Performers = new() { WhitelistIds = [3, 4] },
        };

        Assert.Equal(Make(), Make());
        Assert.Equal(Make().GetHashCode(), Make().GetHashCode());
    }

    [Fact]
    public void TagDestinations_OrderIndependent_ValueSensitive()
    {
        // A Dictionary has no guaranteed order and a round-trip may reorder its keys, so the maps
        // compare order-INDEPENDENTLY while the lists do not. The sweep mutates one property at a time
        // and would pass just as happily on an implementation that made key order significant.
        var a = new RenamerOptions { TagDestinations = new() { [11] = Dests.At("/x"), [12] = Dests.At("/y") } };
        var reordered = new RenamerOptions { TagDestinations = new() { [12] = Dests.At("/y"), [11] = Dests.At("/x") } };

        Assert.Equal(a, reordered);
        Assert.Equal(a.GetHashCode(), reordered.GetHashCode());

        Assert.NotEqual(a, new RenamerOptions { TagDestinations = new() { [11] = Dests.At("/x"), [12] = Dests.At("/DIFFERENT") } });
    }

    [Fact]
    public void StudioDestinations_OrderIndependent_ValueSensitive()
    {
        var a = new RenamerOptions { StudioDestinations = new() { [1] = Dests.At("/x"), [2] = Dests.At("/y") } };
        var reordered = new RenamerOptions { StudioDestinations = new() { [2] = Dests.At("/y"), [1] = Dests.At("/x") } };

        Assert.Equal(a, reordered);
        Assert.Equal(a.GetHashCode(), reordered.GetHashCode());

        Assert.NotEqual(a, new RenamerOptions { StudioDestinations = new() { [1] = Dests.At("/x"), [2] = Dests.At("/DIFFERENT") } });
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

    // ---- Load tolerance: what an ABSENT, an UNKNOWN and an already-STORED property mean ----

    [Fact]
    public void StoredOldDefaultBlob_RoundTripsUnchanged_NotOverwrittenByNewDefaults()
    {
        // A blob saved before the default flips carries the OLD template and the flags off. Loading it
        // must return those stored values verbatim — a new default applies only to an ABSENT field,
        // never to a present one, so an existing user's saved options never silently change.
        const string json =
            """
            {"FilenameTemplate":"$title{ [$resolution]}","PreventConsecutiveSegments":false,
             "FilenameAsTitle":false,"NormalizePunctuation":false}
            """;

        var loaded = JsonSerializer.Deserialize<RenamerOptions>(json, RenamerOptions.JsonOptions);

        Assert.NotNull(loaded);
        Assert.Equal("$title{ [$resolution]}", loaded.FilenameTemplate);
        Assert.False(loaded.PreventConsecutiveSegments);
        Assert.False(loaded.FilenameAsTitle);
        Assert.False(loaded.NormalizePunctuation);
    }

    [Fact]
    public void UnknownProperty_IsIgnored_OnLoad()
    {
        // forward-compat: a future field that this version does not know about.
        const string json =
            """{"FilenameTemplate":"$studio - $title","Case":"Title","UnknownFutureField":42}""";

        var loaded = JsonSerializer.Deserialize<RenamerOptions>(json, RenamerOptions.JsonOptions);

        Assert.NotNull(loaded);
        Assert.Equal("$studio - $title", loaded.FilenameTemplate);
        Assert.Equal(CaseTransform.Title, loaded.Case);
    }

    [Fact]
    public void MissingProperty_Defaults_OnLoad()
    {
        // JSON that omits FilenameMax / FullPathMax / Case — they must default.
        const string json = """{"FilenameTemplate":"$title"}""";

        var loaded = JsonSerializer.Deserialize<RenamerOptions>(json, RenamerOptions.JsonOptions);

        Assert.NotNull(loaded);
        Assert.Equal(255, loaded.FilenameMax);
        Assert.Equal(259, loaded.FullPathMax);
        Assert.Equal(CaseTransform.None, loaded.Case);
    }

    [Fact]
    public void NewFields_OmittedFromJson_LoadWithDefaults()
    {
        // forward-compat: a blob that predates every field below still loads, with the absent fields
        // taking their CURRENT defaults rather than their type's zero value — which for the four
        // default-ON flags is the difference between a fresh-install behavior and a silent opt-out.
        const string json = """{"FilenameTemplate":"$title"}""";

        var loaded = JsonSerializer.Deserialize<RenamerOptions>(json, RenamerOptions.JsonOptions);

        Assert.NotNull(loaded);
        Assert.Empty(loaded.FieldReplacers);
        Assert.False(loaded.StripLeadingArticles);
        Assert.Equal(new List<string> { "The", "A", "An" }, loaded.Articles);
        Assert.False(loaded.PreventTitlePerformer);
        Assert.Empty(loaded.ExcludeTagIds);
        Assert.Empty(loaded.ExcludeStudioIds);
        Assert.Empty(loaded.ExcludePaths);
        Assert.Empty(loaded.AllowedRoots); // legacy source-confine behavior, and it must not throw
        Assert.True(loaded.PreventConsecutiveSegments);
        Assert.True(loaded.NormalizePunctuation);
        Assert.False(loaded.FilenameAsTitle);
        Assert.Equal(",#", loaded.RemoveCharacters);
    }

    [Fact]
    public void AllowedRoots_MissingProperty_DefaultsToEmpty_NoThrow()
    {
        // Kept apart from the blob above because THIS is the security-relevant one: an empty AllowedRoots
        // is what confines a renamer to the file's own source folder, so a blob written before the
        // property existed must land on empty rather than on anything that widens where files may move.
        const string json = """{ "filenameTemplate": "$title" }""";

        var opts = JsonSerializer.Deserialize<RenamerOptions>(json, RenamerOptions.JsonOptions);

        Assert.NotNull(opts);
        Assert.Empty(opts.AllowedRoots);
    }

    [Fact]
    public void Deserialize_RequiredFields_ReplacesDefault_DoesNotAppendToTitle()
    {
        // Reproduces the live gating bug: a stored blob sets RequiredFields to a single token.
        // System.Text.Json, in its populate mode, ADDS to a pre-initialized List<string> ("title")
        // instead of replacing it, yielding ["title","studioCode"] — so the user's chosen gate silently
        // never fires, because title is always present. The deserialized list must be EXACTLY what the
        // blob said.
        const string json = """{ "requiredFields": ["studioCode"] }""";

        var opts = JsonSerializer.Deserialize<RenamerOptions>(json, RenamerOptions.JsonOptions)!;

        Assert.Equal(["studioCode"], opts.RequiredFields);
    }

    [Fact]
    public void Deserialize_DropOrder_ReplacesDefault_DoesNotAppendToDefaults()
    {
        // The same populate hazard on the other defaulted List<string>.
        const string json = """{ "dropOrder": ["tags"] }""";

        var opts = JsonSerializer.Deserialize<RenamerOptions>(json, RenamerOptions.JsonOptions)!;

        Assert.Equal(["tags"], opts.DropOrder);
    }

    // ---- The wire: enum spelling, and blobs whose validity a value can break ----

    [Fact]
    public void Enum_Serializes_AsStableString_NotInteger()
    {
        var opts = new RenamerOptions { Case = CaseTransform.Title };

        var json = JsonSerializer.Serialize(opts, RenamerOptions.JsonOptions);

        Assert.Contains("\"Title\"", json);
        Assert.DoesNotContain("\"Case\":2", json); // not the numeric ordinal
    }

    [Fact]
    public void DurationFormat_Default_SerializesWithEscapedBackslashes_AndIsValidJson()
    {
        // The default DurationFormat is a TimeSpan format whose literal value contains backslashes
        // (hh\-mm\-ss). The serializer must escape each one so the stored blob is valid JSON a strict
        // reader (the settings panel) can parse back. A lone backslash here is what made the panel fail
        // with "Bad escaped character in JSON".
        var json = JsonSerializer.Serialize(new RenamerOptions(), RenamerOptions.JsonOptions);

        Assert.Contains(@"""DurationFormat"":""hh\\-mm\\-ss""", json); // escaped, not a lone backslash

        using var parsed = JsonDocument.Parse(json); // throws if the blob is not valid JSON
        var reloaded = JsonSerializer.Deserialize<RenamerOptions>(json, RenamerOptions.JsonOptions);
        Assert.Equal(@"hh\-mm\-ss", reloaded!.DurationFormat);
    }

    [Fact]
    public void PathDestinationValue_WithWindowsPath_RoundTripsAsValidJson()
    {
        // A routing destination's ROOT is one of Cove's library paths, which on Windows carries
        // backslashes that likewise must be escaped so the stored blob stays valid JSON across a
        // save → load round-trip.
        var original = new RenamerOptions
        {
            PathDestinations =
            [
                new PathDestinationRule { Pattern = @"C:\In", Dest = Dests.At(@"G:\Media", "Sorted") },
            ],
        };

        var json = JsonSerializer.Serialize(original, RenamerOptions.JsonOptions);
        using var parsed = JsonDocument.Parse(json); // valid JSON, no lone backslash
        var reloaded = JsonSerializer.Deserialize<RenamerOptions>(json, RenamerOptions.JsonOptions);

        Assert.Equal(Dests.At(@"G:\Media", "Sorted"), Assert.Single(reloaded!.PathDestinations).Dest);
    }

    // ---- The settings panel ↔ backend binding ----

    // A panel-shaped blob: mixed casing (lowerCamel + PascalCase), enums as strings, nested
    // Performers/Tags MultiValueOptions, and the DropOrder/RequiredFields/whitelist-id arrays.
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
        // Structural record equality — proves every panel field (mixed casing) bound correctly.
        Assert.Equal(ExpectedFromPanel(), loaded);
    }

    [Fact]
    public void PanelJson_To_Backend_To_PanelShape_Survives_BothDirections()
    {
        // Full loop: panel JSON → RenamerOptions → backend JSON → RenamerOptions, all equal. The sweep
        // proves the backend half alone; this is the direction the panel actually uses, so the panel can
        // read a backend-written blob and write one the backend reads losslessly.
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
        Assert.Equal(CaseTransform.Lower, loaded.Case);
        Assert.Equal(OverflowPolicy.KeepFirst, loaded.Performers.OnOverflow);
    }

    private void ReportExamined(Sweep sweep)
    {
        _output.WriteLine($"examined {sweep.Paths.Count} property paths, {sweep.Mutations.Count} mutations:");
        foreach (var path in sweep.Paths)
        {
            _output.WriteLine($"  {path}");
        }
    }

    /// <summary>
    /// The two assertions that make an empty violation list mean something: nothing was skipped, and
    /// discovery found a plausible number of properties.
    /// </summary>
    private static void AssertSweepCanFail(Sweep sweep)
    {
        // A property the generator cannot produce a DISTINCT value for is never skipped: a skipped
        // property is an unpinned property, which is the state this sweep exists to end. It fails here,
        // by name and type, so adding an option of an unfamiliar shape forces a decision.
        Assert.True(
            sweep.Unsupported.Count == 0,
            "no distinct value could be generated for these, so they would be swept vacuously: "
                + string.Join(", ", sweep.Unsupported));

        Assert.Contains(nameof(RenamerOptions.FilenameTemplate), sweep.Paths);
        Assert.True(
            sweep.Paths.Count >= MinimumExaminedPaths,
            $"the sweep examined {sweep.Paths.Count} property paths, under the floor of "
                + $"{MinimumExaminedPaths} — discovery is broken, so an empty violation list is not a pass");
    }

    private sealed record Mutation(string Label, RenamerOptions Mutated);

    private sealed record Sweep(
        IReadOnlyList<string> Paths,
        IReadOnlyList<Mutation> Mutations,
        IReadOnlyList<string> Unsupported,
        RenamerOptions Populated);

    private static class ContractSweep
    {
        public static Sweep Build()
        {
            var defaults = new RenamerOptions();
            var paths = new List<string>();
            var mutations = new List<Mutation>();
            var unsupported = new List<string>();

            foreach (PropertyInfo prop in SettableProperties(typeof(RenamerOptions)))
            {
                var current = prop.GetValue(defaults);

                // A nested record whose default is NULL has no instance to walk into, and its own
                // members are not the decision anyway: "unset" versus "set to something" is. It falls
                // through to the leaf path below, which mutates it as one value.
                if (IsNestedRecord(prop.PropertyType) && current is not null)
                {
                    foreach (PropertyInfo nested in SettableProperties(prop.PropertyType))
                    {
                        var path = $"{prop.Name}.{nested.Name}";
                        paths.Add(path);

                        var candidates = DistinctCandidates(nested.PropertyType, nested.GetValue(current));
                        if (candidates.Count == 0)
                        {
                            unsupported.Add($"{path} : {nested.PropertyType.Name}");
                            continue;
                        }

                        foreach (var (suffix, value) in candidates)
                        {
                            var mutatedNested = With(prop.PropertyType, current, nested, value);
                            mutations.Add(new Mutation(
                                path + suffix,
                                (RenamerOptions)With(typeof(RenamerOptions), defaults, prop, mutatedNested)));
                        }
                    }

                    continue;
                }

                paths.Add(prop.Name);
                var direct = DistinctCandidates(prop.PropertyType, current);
                if (direct.Count == 0)
                {
                    unsupported.Add($"{prop.Name} : {prop.PropertyType.Name}");
                    continue;
                }

                foreach (var (suffix, value) in direct)
                {
                    mutations.Add(new Mutation(
                        prop.Name + suffix,
                        (RenamerOptions)With(typeof(RenamerOptions), defaults, prop, value)));
                }
            }

            return new Sweep(paths, mutations, unsupported, (RenamerOptions)PopulateAll(typeof(RenamerOptions), defaults));
        }

        /// <summary>Names the properties of <paramref name="candidate"/> still equal to those of <paramref name="baseline"/>.</summary>
        public static List<string> UnchangedProperties(object baseline, object candidate)
            => SettableProperties(baseline.GetType())
                .Where(p => LeafEquals(p.GetValue(baseline), p.GetValue(candidate)))
                .Select(p => p.Name)
                .ToList();

        private static List<PropertyInfo> SettableProperties(Type type)
            => type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanWrite && p.GetIndexParameters().Length == 0)
                .OrderBy(p => p.Name, StringComparer.Ordinal)
                .ToList();

        private static bool IsNestedRecord(Type type)
            => !type.IsPrimitive
                && !type.IsEnum
                && type != typeof(string)
                && !typeof(IEnumerable).IsAssignableFrom(type)
                && type.GetConstructor(Type.EmptyTypes) is not null
                && SettableProperties(type).Count > 0;

        private static object PopulateAll(Type type, object source)
        {
            var clone = Activator.CreateInstance(type)!;
            foreach (PropertyInfo prop in SettableProperties(type))
            {
                var current = prop.GetValue(source);
                if (IsNestedRecord(prop.PropertyType) && current is not null)
                {
                    prop.SetValue(clone, PopulateAll(prop.PropertyType, current));
                    continue;
                }

                var candidates = DistinctCandidates(prop.PropertyType, current);
                prop.SetValue(clone, candidates.Count > 0 ? candidates[0].Value : current);
            }

            return clone;
        }

        private static object With(Type type, object source, PropertyInfo target, object? value)
        {
            var clone = Activator.CreateInstance(type)!;
            foreach (PropertyInfo prop in SettableProperties(type))
            {
                prop.SetValue(clone, prop.Name == target.Name ? value : prop.GetValue(source));
            }

            return clone;
        }

        /// <summary>
        /// Values distinct from <paramref name="current"/> for a leaf of <paramref name="type"/>, or an
        /// EMPTY list when none can be produced — which the caller reports as a failure rather than a
        /// skip. Enums expand to every other member (so a member added to an enum is round-tripped by
        /// name without a hand-written case), and a list of two or more expands to a reordered copy
        /// (lists compare order-SENSITIVELY, unlike the destination maps).
        /// </summary>
        private static List<(string Suffix, object? Value)> DistinctCandidates(Type type, object? current)
        {
            var result = new List<(string Suffix, object? Value)>();

            if (type == typeof(string))
            {
                result.Add((string.Empty, (string?)current + " ~sweep"));
            }
            else if (type == typeof(bool))
            {
                result.Add((string.Empty, !(bool)current!));
            }
            else if (type == typeof(int))
            {
                result.Add((string.Empty, (int)current! + 1));
            }
            else if (type == typeof(long))
            {
                result.Add((string.Empty, (long)current! + 1L));
            }
            else if (type.IsEnum)
            {
                foreach (var member in Enum.GetValues(type))
                {
                    if (!Equals(member, current))
                    {
                        result.Add(($"={member}", member));
                    }
                }
            }
            else if (typeof(IDictionary).IsAssignableFrom(type) && type.IsGenericType)
            {
                AddDictionaryCandidate(type, (IDictionary)current!, result);
            }
            else if (typeof(IList).IsAssignableFrom(type) && type.IsGenericType)
            {
                AddListCandidates(type, (IList)current!, result);
            }
            else if (IsNestedRecord(type))
            {
                // Reached only for a nested record left NULL by default, where the mutation that means
                // anything is supplying the whole object — its members are populated so the round-trip
                // has to carry each of them, not merely a non-null marker.
                result.Add((string.Empty, PopulateAll(type, Activator.CreateInstance(type)!)));
            }

            // A generator that quietly returned the current value would make that property's assertion
            // vacuously true forever, so a candidate that is not actually different is dropped here —
            // which leaves the list empty and turns the property into a reported failure.
            return result.Where(c => !LeafEquals(c.Value, current)).ToList();
        }

        private static void AddListCandidates(Type type, IList current, List<(string Suffix, object? Value)> result)
        {
            var sample = TrySample(type.GetGenericArguments()[0]);
            if (sample is null)
            {
                return;
            }

            var appended = (IList)Activator.CreateInstance(type)!;
            foreach (var item in current)
            {
                appended.Add(item);
            }

            appended.Add(sample);
            result.Add((string.Empty, appended));

            if (current.Count >= 2)
            {
                var reversed = (IList)Activator.CreateInstance(type)!;
                for (var i = current.Count - 1; i >= 0; i--)
                {
                    reversed.Add(current[i]);
                }

                result.Add(("~reordered", reversed));
            }
        }

        private static void AddDictionaryCandidate(Type type, IDictionary current, List<(string Suffix, object? Value)> result)
        {
            var args = type.GetGenericArguments();
            var key = TrySample(args[0]);
            var value = TrySample(args[1]);
            if (key is null || value is null)
            {
                return;
            }

            var extended = (IDictionary)Activator.CreateInstance(type)!;
            foreach (DictionaryEntry entry in current)
            {
                extended.Add(entry.Key, entry.Value);
            }

            if (!extended.Contains(key))
            {
                extended.Add(key, value);
            }

            result.Add((string.Empty, extended));
        }

        private static object? TrySample(Type type)
        {
            if (type == typeof(string))
            {
                return "sweep-probe";
            }

            if (type == typeof(int))
            {
                return 987654;
            }

            if (type == typeof(long))
            {
                return 987654L;
            }

            if (type.IsEnum)
            {
                return Enum.GetValues(type).GetValue(0);
            }

            return type.GetConstructor(Type.EmptyTypes) is not null ? Activator.CreateInstance(type) : null;
        }

        /// <summary>
        /// Value comparison for a swept leaf. Collections are compared ELEMENT-wise: reference equality
        /// would call every freshly built collection "different" and so could never catch a generator
        /// that produced an identical one.
        /// </summary>
        private static bool LeafEquals(object? a, object? b)
        {
            if (a is IEnumerable left and not string && b is IEnumerable right and not string)
            {
                return left.Cast<object>().SequenceEqual(right.Cast<object>());
            }

            return Equals(a, b);
        }
    }
}
