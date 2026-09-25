using WhisparrSync.Connection;
using WhisparrSync.Contracts;
using WhisparrSync.Import;
using WhisparrSync.Options;

namespace WhisparrSync.Tests.Options;

public sealed class WhisparrSyncOptionsTests
{
    [Fact]
    public void ADefaultRecordHasNoConnectionForEitherGeneration()
    {
        var options = new WhisparrSyncOptions();

        Assert.Null(options.V3);
        Assert.Null(options.V2);
    }

    // Record equality compares a list by reference, so a round-trip's fresh list is the case a
    // default implementation would report as changed.
    [Fact]
    public async Task ARoundTripThroughTheStoreReturnsAnEqualRecord()
    {
        var store = new FakeStore();
        var saved = Populated();

        await new OptionsStore(store).SaveAsync(saved);
        var loaded = await new OptionsStore(store).LoadAsync();

        Assert.Equal(WhisparrSyncOptions.Persisted(saved), WhisparrSyncOptions.Persisted(loaded));
        Assert.Equal(2, loaded.Instance().ImportRefusals.Count);
        Assert.Equal(2, loaded.Instance().ImportRefusals[0].NewestPaths.Count);
    }

    // Storing a spelling rather than an ordinal is what keeps a stored blob readable after a member
    // is inserted into any of these enums.
    [Fact]
    public async Task TheEnumsRoundTripAsStrings()
    {
        var store = new FakeStore();

        await new OptionsStore(store).SaveAsync(Populated() with
        {
            SelectedGeneration = WhisparrGeneration.V2,
            DefaultMonitorScope = MonitorScope.AllScenes,
            UpgradeBehavior = UpgradeBehavior.Replace,
        });

        var blob = await store.GetAsync(OptionsStore.Key);
        Assert.Contains("\"v2\"", blob, StringComparison.Ordinal);
        Assert.Contains("\"allScenes\"", blob, StringComparison.Ordinal);
        Assert.Contains("\"replace\"", blob, StringComparison.Ordinal);
        Assert.Contains("\"notFoundUnderAnyRoot\"", blob, StringComparison.Ordinal);
        Assert.Contains("\"ambiguousCandidates\"", blob, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WritingOneGenerationLeavesTheOtherUntouched()
    {
        var store = new FakeStore();
        var options = new OptionsStore(store);
        await options.SaveAsync(Populated());

        var reloaded = await options.LoadAsync();
        await options.SaveAsync(reloaded with
        {
            V3 = reloaded.V3! with { Address = "http://moved:6969/", RecordedVersion = "3.3.9.1" },
        });

        var after = await options.LoadAsync();
        Assert.Equal("http://v2-host:6969/", after.V2?.Address);
        Assert.Equal("2.2.0.231", after.V2?.RecordedVersion);
        Assert.Equal("http://moved:6969/", after.V3?.Address);
    }

    // Reading back the other generation's address would send a test at an instance the user did
    // not name.
    [Fact]
    public async Task AGenerationNeverConfiguredReadsAsAbsent()
    {
        var store = new FakeStore();
        var options = new OptionsStore(store);

        await options.SaveAsync(new WhisparrSyncOptions
        {
            V3 = new WhisparrSyncGenerationConnection { Address = "http://v3-host:6969/" },
        });

        var loaded = await options.LoadAsync();
        Assert.Null(loaded.V2);
        Assert.Equal("http://v3-host:6969/", loaded.V3?.Address);
    }

    // The two instants measure different things: when the version was read, and when the instance
    // last answered anything at all.
    [Fact]
    public async Task TheTwoRecordedInstantsSurviveIndependently()
    {
        var store = new FakeStore();
        var options = new OptionsStore(store);
        await options.SaveAsync(Populated());

        var loaded = await options.LoadAsync();

        Assert.Equal(VerifiedAt, loaded.V3?.VersionVerifiedAtUtc);
        Assert.Equal(ReachableAt, loaded.V3?.LastReachableAtUtc);
    }

    [Fact]
    public async Task EachGenerationCarriesItsOwnWatermark()
    {
        var store = new FakeStore();
        await new OptionsStore(store).SaveAsync(Populated());

        var loaded = await new OptionsStore(store).LoadAsync();

        Assert.Equal(V3Watermark, loaded.V3?.BackstopWatermarkUtc);
        Assert.Equal(V2Watermark, loaded.V2?.BackstopWatermarkUtc);
    }

    // A key belongs to a table this record knows nothing about, so a save that rotates one leaves
    // the connection where it was. An address that moves is a different instance, and a mark that
    // survived it would name a position in someone else's past.
    [Fact]
    public void AKeyRotationKeepsTheWatermarkAndAnAddressChangeDoesNot()
    {
        var stored = Populated();

        var rotated = SettingsProjector.Apply(
            stored,
            new WhisparrSyncSettingsSaveRequest(
                WhisparrGeneration.V3,
                new WhisparrSyncGenerationSaveRequest(
                    "http://v3-host:6969", KeyWriteSignal.Replace, "a-new-value"),
                null));

        var moved = SettingsProjector.Apply(
            stored,
            new WhisparrSyncSettingsSaveRequest(
                WhisparrGeneration.V3,
                new WhisparrSyncGenerationSaveRequest(
                    "http://somewhere-else:6969", KeyWriteSignal.Keep, null),
                null));

        Assert.Equal(V3Watermark, rotated.V3?.BackstopWatermarkUtc);
        Assert.Null(moved.V3?.BackstopWatermarkUtc);
        Assert.Equal(V2Watermark, moved.V2?.BackstopWatermarkUtc);
    }

    [Fact]
    public async Task AnEmptyStoreLoadsTheDefaults()
        => Assert.Equal(new WhisparrSyncOptions(), await new OptionsStore(new FakeStore()).LoadAsync());

    // A value the model cannot bind makes the whole load answer with the defaults object, so a
    // renamed member would discard the user's connection and watermarks with nothing observable
    // happening. The spellings are transcribed by hand from the server's enum, because a list
    // computed from the enum would agree with it whatever it says.
    [Fact]
    public async Task ABlobCarryingEachStoredRefusalCauseBindsRatherThanLoadingTheDefaults()
    {
        var store = new FakeStore();
        await store.SetAsync(
            OptionsStore.Key,
            """
            {
              "SelectedGeneration": "v3",
              "V3": { "Address": "http://v3-host:6969/" },
              "InstanceSettingsV3": {
                "ImportRefusals": [
                  {
                    "Root": "/whisparr-media",
                    "CountSinceLastSuccess": 3,
                    "NewestPaths": [
                      { "Path": "/whisparr-media/a.mp4", "Cause": "notFoundUnderAnyRoot" },
                      { "Path": "/whisparr-media/b.mp4", "Cause": "ambiguousCandidates" },
                      { "Path": "/whisparr-media/c.mp4", "Cause": "unreadable" }
                    ]
                  }
                ]
              }
            }
            """);

        var loaded = await new OptionsStore(store).LoadAsync();

        Assert.NotEqual(new WhisparrSyncOptions(), loaded);
        Assert.Equal("http://v3-host:6969/", loaded.V3?.Address);
        Assert.Equal(
            [
                ImportRefusalCause.NotFoundUnderAnyRoot,
                ImportRefusalCause.AmbiguousCandidates,
                ImportRefusalCause.Unreadable,
            ],
            Assert.Single(loaded.Instance().ImportRefusals).NewestPaths.Select(entry => entry.Cause));
    }

    // A literal rather than something serialized here, so it stays the blob an install holds rather
    // than one this assembly can still describe.
    [Fact]
    public async Task ABlobFromBeforeThisShapeLoadsWithTheNewMembersAtTheirDefaults()
    {
        var store = new FakeStore();
        await store.SetAsync(
            OptionsStore.Key,
            """
            {
              "SelectedGeneration": "v3",
              "V3": {
                "Address": "http://v3-host:6969/",
                "RecordedVersion": "3.3.8.1097"
              },
              "PathTranslation": [
                { "CovePrefix": "/media", "WhisparrPrefix": "/data" }
              ],
              "DefaultMonitorScope": "allScenes",
              "CallbackHost": "https://media.example.com/cove"
            }
            """);

        var loaded = await new OptionsStore(store).LoadAsync();

        Assert.Equal("http://v3-host:6969/", loaded.V3?.Address);
        Assert.Equal(MonitorScope.AllScenes, loaded.DefaultMonitorScope);
        Assert.Equal("https://media.example.com/cove", loaded.CallbackHost);

        Assert.Null(loaded.V3?.BackstopWatermarkUtc);
        Assert.Equal(UpgradeBehavior.Add, loaded.UpgradeBehavior);
        Assert.Equal(
            WhisparrSyncOptions.DefaultBackstopIntervalSeconds, loaded.BackstopIntervalSeconds);
        Assert.Equal(new ImportHealthAggregate(), loaded.ImportHealth);
        Assert.Empty(loaded.Instance().ImportRefusals);
    }

    // Two types of this name, held apart by a file-scoped using alias, is a defect a legal edit
    // triggers in silence. Dropping or reordering that one line compiles and changes which enum an
    // outbound body is composed from.
    [Fact]
    public void ExactlyOneExportedTypeIsNamedMonitorScope()
    {
        var named = typeof(WhisparrSyncOptions).Assembly.GetExportedTypes()
            .Where(type => type.Name == nameof(MonitorScope))
            .Select(type => type.FullName)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["WhisparrSync.Contracts.MonitorScope"], named);
    }

    [Fact]
    public void TheOneScopeEnumSpellsBothScopesAndNothingElse()
    {
        Assert.Equal(
            ["AllScenes", "FutureScenes"],
            Enum.GetNames<MonitorScope>().Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void TheStoredDefaultIsTheActingEnumAtTheNarrowerScope()
    {
        Assert.Equal(
            typeof(MonitorScope),
            typeof(WhisparrSyncOptions).GetProperty(nameof(WhisparrSyncOptions.DefaultMonitorScope))
                ?.PropertyType);
        Assert.Equal(MonitorScope.FutureScenes, new WhisparrSyncOptions().DefaultMonitorScope);
    }

    [Fact]
    public async Task ABlobNamingTheWiderScopeLoadsAsTheWiderScope()
    {
        var store = new FakeStore();
        await store.SetAsync(OptionsStore.Key, """{"DefaultMonitorScope": "allScenes"}""");

        var load = await new OptionsStore(store).LoadBoundAsync();

        Assert.True(load.Bound);
        Assert.Equal(MonitorScope.AllScenes, load.Options.DefaultMonitorScope);
    }

    // Choosing the narrow scope wrongly costs one more gesture. Choosing the wide one wrongly marks
    // a whole back catalogue wanted, which spends indexer traffic and disk, and on v3 narrowing
    // again does not undo it. So a word the enum does not declare fails the bind, which the store
    // reports so that nothing saves over the stored configuration. The default is the narrower
    // scope. Spellings only: the shared enum converter accepts a JSON number and admits an
    // undefined value, which is true of every enum this product stores.
    [Theory]
    [InlineData("\"somethingElse\"")]
    [InlineData("\"\"")]
    [InlineData("null")]
    public async Task AnUnrecognisedScopeSpellingLoadsAsTheNarrowerScope(string stored)
    {
        var store = new FakeStore();
        await store.SetAsync(
            OptionsStore.Key,
            $$"""{"CallbackHost": "https://media.example.com/cove", "DefaultMonitorScope": {{stored}} }""");

        var load = await new OptionsStore(store).LoadBoundAsync();

        Assert.Equal(MonitorScope.FutureScenes, load.Options.DefaultMonitorScope);
        Assert.NotEqual(MonitorScope.AllScenes, load.Options.DefaultMonitorScope);
    }

    // The blob is hand-written, because the point of flooring on the read is that a value which
    // never passed through a save is still floored.
    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(29)]
    public async Task AStoredIntervalBelowTheFloorReadsBackAsTheFloor(int stored)
    {
        var store = new FakeStore();
        await store.SetAsync(OptionsStore.Key, $$"""{ "BackstopIntervalSeconds": {{stored}} }""");

        var loaded = await new OptionsStore(store).LoadAsync();

        Assert.Equal(stored, loaded.BackstopIntervalSeconds);
        Assert.Equal(
            TimeSpan.FromSeconds(WhisparrSyncOptions.BackstopIntervalFloorSeconds),
            loaded.BackstopInterval);
    }

    [Theory]
    [InlineData("1", 1)]
    [InlineData("2", 2)]
    [InlineData("29", 29)]
    public void AVariableNamingAShorterFloorShortensIt(string named, int expected)
        => Assert.Equal(expected, WhisparrSyncOptions.ShortenedFloorSeconds(named));

    // Only downward, so nothing an operator sets can make a deployment sweep less often than it
    // does with the variable absent.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("30")]
    [InlineData("900")]
    [InlineData("twenty")]
    [InlineData("2.5")]
    [InlineData("0x2")]
    public void AVariableThatNamesNoShorterFloorLeavesTheStandardOneStanding(string? named)
        => Assert.Equal(
            WhisparrSyncOptions.StandardBackstopIntervalFloorSeconds,
            WhisparrSyncOptions.ShortenedFloorSeconds(named));

    [Fact]
    public async Task AStoredIntervalAboveTheFloorIsHonoured()
    {
        var store = new FakeStore();
        await store.SetAsync(OptionsStore.Key, """{ "BackstopIntervalSeconds": 45 }""");

        Assert.Equal(
            TimeSpan.FromSeconds(45), (await new OptionsStore(store).LoadAsync()).BackstopInterval);
    }

    [Fact]
    public void ARootIsHeldUnderOneSpellingWhateverSeparatorItArrivesWith()
    {
        var bare = new ImportRootRefusals { Root = "/whisparr/media", CountSinceLastSuccess = 1 };
        var trailing = new ImportRootRefusals { Root = "/whisparr/media/", CountSinceLastSuccess = 1 };
        var backslash = new ImportRootRefusals { Root = @"C:\whisparr\media\", CountSinceLastSuccess = 1 };

        Assert.Equal(bare.Root, trailing.Root);
        Assert.Equal("/whisparr/media", trailing.Root);
        Assert.Equal(@"C:\whisparr\media", backslash.Root);
    }

    // The root of a filesystem stays addressable rather than folding into the blank key a delivery
    // with no root uses.
    [Fact]
    public void ARootOfNothingButSeparatorsKeepsOne()
    {
        Assert.Equal("/", ImportRootRefusals.NormaliseRoot("/"));
        Assert.Equal("/", ImportRootRefusals.NormaliseRoot("///"));
        Assert.Equal("", ImportRootRefusals.NormaliseRoot(""));
        Assert.Equal("", ImportRootRefusals.NormaliseRoot(null));
    }

    // A path is shortened where it is stored, so one delivery naming an arbitrarily long path
    // cannot make the whole blob the host serves in one piece too large to serve.
    [Fact]
    public void AReportedPathIsShortenedWhereItIsStored()
    {
        var overLong = new ImportRefusalEntry
        {
            Path = new string('x', ImportRefusalEntry.PathMaxLength * 4),
        };
        var atTheMaximum = new ImportRefusalEntry
        {
            Path = new string('y', ImportRefusalEntry.PathMaxLength),
        };
        var underIt = new ImportRefusalEntry { Path = "/whisparr/media/scene-a/file.mp4" };

        Assert.Equal(ImportRefusalEntry.PathMaxLength, overLong.Path.Length);
        Assert.Equal(ImportRefusalEntry.PathMaxLength, atTheMaximum.Path.Length);
        Assert.Equal("/whisparr/media/scene-a/file.mp4", underIt.Path);
    }

    // The ceiling is applied on the read, so a value that never passed through a save is still
    // shortened. Cove's bulk data route writes this blob whole, so an over-long value can arrive
    // without this product having written it. Written as a literal, because the model cannot hold
    // the value the load path is asked to bind.
    [Fact]
    public async Task AnOverLongStoredPathLoadsShortened()
    {
        var store = new FakeStore();
        var stored = new string('x', ImportRefusalEntry.PathMaxLength * 4);
        await store.SetAsync(
            OptionsStore.Key,
            $$"""
            {
              "InstanceSettingsV3": {
                "ImportRefusals": [
                  {
                    "Root": "/whisparr/media",
                    "CountSinceLastSuccess": 1,
                    "NewestPaths": [
                      { "Path": "{{stored}}", "Cause": "unreadable" }
                    ]
                  }
                ]
              }
            }
            """);

        var loaded = await new OptionsStore(store).LoadAsync();

        var entry = Assert.Single(Assert.Single(loaded.Instance().ImportRefusals).NewestPaths);
        Assert.Equal(ImportRefusalEntry.PathMaxLength, entry.Path.Length);
        Assert.Equal(ImportRefusalCause.Unreadable, entry.Cause);
    }

    // Written as a literal because the serializer never emits this shape, and nothing else on the
    // load path reaches it. A property initialiser runs only for an absent key, and the store's
    // non-null restore does not descend into a collection's elements.
    [Fact]
    public async Task AnExplicitlyNullNewestPathsLoadsAsAnEmptyList()
    {
        var store = new FakeStore();
        await store.SetAsync(
            OptionsStore.Key,
            """
            {
              "InstanceSettingsV3": {
                "ImportRefusals": [
                  {
                    "Root": "/whisparr/media",
                    "CountSinceLastSuccess": 2,
                    "NewestPaths": null
                  }
                ]
              }
            }
            """);

        var loaded = await new OptionsStore(store).LoadAsync();

        var entry = Assert.Single(loaded.Instance().ImportRefusals);
        Assert.NotNull(entry.NewestPaths);
        Assert.Empty(entry.NewestPaths);

        var refused = ImportRefusalProjector.Refuse(
            loaded.Instance().ImportRefusals,
            entry.Root,
            "/whisparr/media/scene-a/file.mp4",
            ImportRefusalCause.Unreadable);
        var counted = Assert.Single(refused);
        Assert.Equal(3, counted.CountSinceLastSuccess);
        Assert.Single(counted.NewestPaths);
        Assert.Empty(ImportRefusalProjector.Succeed(refused, entry.Root));

        var line = Assert.Single(
            ImportBannerView.From(loaded.Instance().ImportRefusals, loaded.ImportHealth).Roots);
        Assert.Equal("/whisparr/media", line.Root);
        Assert.Empty(line.NewestPaths);
    }

    // A version is shortened where it is stored, so an instance at the configured address cannot
    // make the whole blob the host serves in one piece too large to serve. The ordinary reading is
    // the control: a bound that blanked every value would satisfy the length assertions alone.
    [Fact]
    public void AReportedVersionIsShortenedWhereItIsStored()
    {
        var maximum = WhisparrSyncGenerationConnection.RecordedVersionMaxLength;
        var overLong = new WhisparrSyncGenerationConnection
        {
            RecordedVersion = new string('x', maximum * 4),
        };
        var atTheMaximum = new WhisparrSyncGenerationConnection
        {
            RecordedVersion = new string('y', maximum),
        };
        var ordinary = new WhisparrSyncGenerationConnection { RecordedVersion = "3.3.8.1097" };
        var neverRead = new WhisparrSyncGenerationConnection();

        Assert.Equal(maximum, overLong.RecordedVersion?.Length);
        Assert.Equal(maximum, atTheMaximum.RecordedVersion?.Length);
        Assert.Equal("3.3.8.1097", ordinary.RecordedVersion);
        Assert.Null(neverRead.RecordedVersion);
    }

    // The ceiling is applied on the read, as the earlier case records. Written as a literal,
    // because the model cannot hold the value the load path is asked to bind.
    [Fact]
    public async Task AnOverLongStoredVersionLoadsShortened()
    {
        var store = new FakeStore();
        var stored = new string('x', WhisparrSyncGenerationConnection.RecordedVersionMaxLength * 4);
        await store.SetAsync(
            OptionsStore.Key,
            $$"""
            {
              "SelectedGeneration": "v3",
              "V3": {
                "Address": "http://v3-host:6969/",
                "RecordedVersion": "{{stored}}"
              }
            }
            """);

        var loaded = await new OptionsStore(store).LoadAsync();

        var connection = loaded.ConnectionFor(WhisparrGeneration.V3);
        Assert.NotNull(connection);
        Assert.Equal(
            WhisparrSyncGenerationConnection.RecordedVersionMaxLength,
            connection.RecordedVersion?.Length);
    }

    // A failure text is shortened where it is stored, so one exception message cannot make the
    // whole blob the host serves in one piece too large to serve.
    [Fact]
    public void ARecordedFailureTextIsShortenedWhereItIsStored()
    {
        var health = new ImportHealthAggregate
        {
            LastError = new string('x', ImportHealthAggregate.LastErrorMaxLength * 4),
        };

        Assert.Equal(ImportHealthAggregate.LastErrorMaxLength, health.LastError.Length);

        // Shortening the already-shortened text yields the same record, which is what keeps a
        // round-trip through the store equal to what went into it.
        Assert.Equal(health, new ImportHealthAggregate { LastError = health.LastError });
    }

    // The API key has no home in this record. It is in a table this extension owns, so the host's
    // bulk extension-data route has nothing of it to return.
    [Fact]
    public async Task TheStoredBlobCarriesNothingNamedLikeAKey()
    {
        var store = new FakeStore();
        await new OptionsStore(store).SaveAsync(Populated());

        var blob = await store.GetAsync(OptionsStore.Key);
        Assert.DoesNotContain("key", blob, StringComparison.OrdinalIgnoreCase);
    }

    private static readonly DateTimeOffset VerifiedAt = new(2026, 8, 30, 11, 22, 33, TimeSpan.Zero);
    private static readonly DateTimeOffset ReachableAt = new(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset V3Watermark = new(2026, 8, 30, 10, 15, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset V2Watermark = new(2026, 8, 29, 22, 5, 0, TimeSpan.Zero);

    private static WhisparrSyncOptions Populated() => new WhisparrSyncOptions
    {
        SelectedGeneration = WhisparrGeneration.V3,
        V3 = new WhisparrSyncGenerationConnection
        {
            Address = "http://v3-host:6969/",
            RecordedVersion = "3.3.8.1097",
            VersionVerifiedAtUtc = VerifiedAt,
            LastReachableAtUtc = ReachableAt,
            BackstopWatermarkUtc = V3Watermark,
        },
        V2 = new WhisparrSyncGenerationConnection
        {
            Address = "http://v2-host:6969/",
            RecordedVersion = "2.2.0.231",
            VersionVerifiedAtUtc = VerifiedAt,
            LastReachableAtUtc = ReachableAt,
            BackstopWatermarkUtc = V2Watermark,
        },
        MetadataProviderEndpoints = new MetadataProviderEndpoints { V3 = "http://provider.invalid/v3" },
        CallbackHost = "https://media.example.com/cove",
        UpgradeBehavior = UpgradeBehavior.Add,
        BackstopIntervalSeconds = 600,
        ImportHealth = new ImportHealthAggregate
        {
            LastWorkedAtUtc = ReachableAt,
            LastFailedAtUtc = VerifiedAt,
            LastError = "the path named in the delivery is under no library root",
            ConsecutiveFailures = 3,
            BackstopPositionLost = true,
        }
    }.WithInstance(
        importRefusals:
        [
            new ImportRootRefusals
            {
                Root = "/whisparr/media",
                CountSinceLastSuccess = 3,
                NewestPaths =
                [
                    new ImportRefusalEntry
                    {
                        Path = "/whisparr/media/scene-b/file.mp4",
                        Cause = ImportRefusalCause.NotFoundUnderAnyRoot,
                    },
                    new ImportRefusalEntry
                    {
                        Path = "/whisparr/media/scene-a/file.mp4",
                        Cause = ImportRefusalCause.AmbiguousCandidates,
                    },
                ],
            },
            new ImportRootRefusals
            {
                Root = "/whisparr/archive",
                CountSinceLastSuccess = 1,
                NewestPaths =
                [
                    new ImportRefusalEntry
                    {
                        Path = "/whisparr/archive/scene-c/file.mp4",
                        Cause = ImportRefusalCause.Unreadable,
                    },
                ],
            },
        ]);
}
