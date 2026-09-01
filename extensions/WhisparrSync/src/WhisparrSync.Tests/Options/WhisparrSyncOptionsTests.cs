using WhisparrSync.Connection;
using WhisparrSync.Contracts;
using WhisparrSync.Import;
using WhisparrSync.Options;

namespace WhisparrSync.Tests.Options;

/// <summary>
/// The options blob: what a default carries, that a round-trip through the store returns an equal
/// record, that a blob written before the record's current shape still loads, and that the two
/// generations' connections are independent of one another.
/// </summary>
/// <remarks>
/// Everything the settings page has no control for has its own file, which pins the defaults beside
/// what the shipped code does while they stay at them.
/// </remarks>
public sealed class WhisparrSyncOptionsTests
{
    /// <summary>Neither generation is configured until something configures it.</summary>
    [Fact]
    public void ADefaultRecordHasNoConnectionForEitherGeneration()
    {
        var options = new WhisparrSyncOptions();

        Assert.Null(options.V3);
        Assert.Null(options.V2);
    }

    /// <summary>
    /// A save followed by a load returns an equal record, including the collection member. Record
    /// equality compares a list by reference, so a round-trip's fresh list is exactly the case a
    /// default implementation would report as changed.
    /// </summary>
    [Fact]
    public async Task ARoundTripThroughTheStoreReturnsAnEqualRecord()
    {
        var store = new FakeStore();
        var saved = Populated();

        await new OptionsStore(store).SaveAsync(saved);
        var loaded = await new OptionsStore(store).LoadAsync();

        Assert.Equal(saved, loaded);
        Assert.Equal(saved.GetHashCode(), loaded.GetHashCode());
        Assert.Equal(2, loaded.ImportRefusals.Count);
        Assert.Equal(2, loaded.ImportRefusals[0].NewestPaths.Count);
    }

    /// <summary>
    /// Two records that differ only in how many entries a collection holds are not equal, at either
    /// level of the refusal aggregate.
    /// </summary>
    /// <remarks>
    /// The discriminating case for the count that precedes the elements in each component stream: a
    /// stream that yielded only the elements would let a shorter list line up against a longer one
    /// whose extra member happens to match the next component.
    /// </remarks>
    [Fact]
    public void RecordsDifferingOnlyInHowManyRefusalsTheyHoldAreNotEqual()
    {
        var saved = Populated();

        var oneRootFewer = saved with { ImportRefusals = [saved.ImportRefusals[0]] };
        var onePathFewer = saved with
        {
            ImportRefusals =
            [
                saved.ImportRefusals[0] with
                {
                    NewestPaths = [saved.ImportRefusals[0].NewestPaths[0]],
                },
                saved.ImportRefusals[1],
            ],
        };

        Assert.NotEqual(saved, oneRootFewer);
        Assert.NotEqual(saved, onePathFewer);
        Assert.NotEqual(saved.ImportRefusals[0], onePathFewer.ImportRefusals[0]);
    }

    /// <summary>
    /// The enums survive the round trip as their own spelling rather than as an ordinal, which is
    /// what keeps a stored blob readable after a member is inserted into any of them.
    /// </summary>
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

    /// <summary>
    /// Writing one generation's connection leaves the other's at the value it already had. Selecting
    /// the other generation and coming back has to return the first one unchanged.
    /// </summary>
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

    /// <summary>
    /// A generation nothing configured reads as absent. It must never read back the other
    /// generation's address, which would send a test at an instance the user did not name.
    /// </summary>
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

    /// <summary>
    /// The two recorded instants are stored apart, because they measure different things: when the
    /// version was read, and when the instance last answered anything at all.
    /// </summary>
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

    /// <summary>
    /// The watermark belongs to the generation it was read from, and each generation has its own.
    /// </summary>
    [Fact]
    public async Task EachGenerationCarriesItsOwnWatermark()
    {
        var store = new FakeStore();
        await new OptionsStore(store).SaveAsync(Populated());

        var loaded = await new OptionsStore(store).LoadAsync();

        Assert.Equal(V3Watermark, loaded.V3?.BackstopWatermarkUtc);
        Assert.Equal(V2Watermark, loaded.V2?.BackstopWatermarkUtc);
    }

    /// <summary>
    /// Replacing the stored API key keeps the watermark; moving the address starts it again.
    /// </summary>
    /// <remarks>
    /// A key belongs to a table this record knows nothing about, so a save that rotates one leaves
    /// the connection where it was. An address that moves is a different instance with its own
    /// history, and a mark that survived it would name a position in someone else's past.
    /// </remarks>
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

    /// <summary>A blob nothing ever wrote loads as the defaults rather than throwing.</summary>
    [Fact]
    public async Task AnEmptyStoreLoadsTheDefaults()
        => Assert.Equal(new WhisparrSyncOptions(), await new OptionsStore(new FakeStore()).LoadAsync());

    /// <summary>
    /// Every refusal-cause spelling an installed blob may carry still binds to its member.
    /// </summary>
    /// <remarks>
    /// A value the model cannot bind makes the WHOLE load answer with the defaults object, so a
    /// renamed member would discard the user's connection and watermarks with nothing observable
    /// happening. The blob is a literal and the spellings are transcribed by hand from the server's
    /// enum: a list computed from the enum would agree with it whatever it says.
    /// </remarks>
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
            Assert.Single(loaded.ImportRefusals).NewestPaths.Select(entry => entry.Cause));
    }

    /// <summary>
    /// A blob written before this record's current shape still loads: the member it carries that no
    /// longer exists is ignored, and the members it does not carry read as their defaults.
    /// </summary>
    /// <remarks>
    /// Written as a literal rather than produced by serializing anything, so it stays the blob an
    /// install actually holds rather than one this assembly can still describe.
    /// </remarks>
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
        Assert.Empty(loaded.ImportRefusals);
    }

    /// <summary>
    /// A stored interval below the floor is honoured as the floor, and the stored value is left as
    /// it was found.
    /// </summary>
    /// <remarks>
    /// Both cases arrive as a hand-written blob, because the point of flooring on the read is that a
    /// value that never passed through a save is still floored.
    /// </remarks>
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

    /// <summary>A stored interval at or above the floor is honoured as it stands.</summary>
    [Fact]
    public async Task AStoredIntervalAboveTheFloorIsHonoured()
    {
        var store = new FakeStore();
        await store.SetAsync(OptionsStore.Key, """{ "BackstopIntervalSeconds": 45 }""");

        Assert.Equal(
            TimeSpan.FromSeconds(45), (await new OptionsStore(store).LoadAsync()).BackstopInterval);
    }

    /// <summary>
    /// Two spellings of one Whisparr root differing only by a trailing separator are one entry.
    /// </summary>
    [Fact]
    public void ARootIsHeldUnderOneSpellingWhateverSeparatorItArrivesWith()
    {
        var bare = new ImportRootRefusals { Root = "/whisparr/media", CountSinceLastSuccess = 1 };
        var trailing = new ImportRootRefusals { Root = "/whisparr/media/", CountSinceLastSuccess = 1 };
        var backslash = new ImportRootRefusals { Root = @"C:\whisparr\media\", CountSinceLastSuccess = 1 };

        Assert.Equal(bare, trailing);
        Assert.Equal("/whisparr/media", trailing.Root);
        Assert.Equal(@"C:\whisparr\media", backslash.Root);
    }

    /// <summary>
    /// A root that is nothing but a separator keeps one, so the root of a filesystem stays
    /// addressable rather than folding into the blank key a delivery with no root uses.
    /// </summary>
    [Fact]
    public void ARootOfNothingButSeparatorsKeepsOne()
    {
        Assert.Equal("/", ImportRootRefusals.NormaliseRoot("/"));
        Assert.Equal("/", ImportRootRefusals.NormaliseRoot("///"));
        Assert.Equal("", ImportRootRefusals.NormaliseRoot(""));
        Assert.Equal("", ImportRootRefusals.NormaliseRoot(null));
    }

    /// <summary>
    /// A reported path is shortened where it is stored, so one delivery naming an arbitrarily long
    /// path cannot make the whole blob the host serves in one piece too large to serve.
    /// </summary>
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

    /// <summary>
    /// A stored path longer than the maximum loads shortened, so a blob an earlier build wrote
    /// cannot reintroduce a value with no ceiling.
    /// </summary>
    /// <remarks>
    /// Written as a literal rather than produced by serializing anything: the model can no longer
    /// hold the value this asks the load path to bind.
    /// </remarks>
    [Fact]
    public async Task AnOverLongStoredPathLoadsShortened()
    {
        var store = new FakeStore();
        var stored = new string('x', ImportRefusalEntry.PathMaxLength * 4);
        await store.SetAsync(
            OptionsStore.Key,
            $$"""
            {
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
            """);

        var loaded = await new OptionsStore(store).LoadAsync();

        var entry = Assert.Single(Assert.Single(loaded.ImportRefusals).NewestPaths);
        Assert.Equal(ImportRefusalEntry.PathMaxLength, entry.Path.Length);
        Assert.Equal(ImportRefusalCause.Unreadable, entry.Cause);
    }

    /// <summary>
    /// A stored refusal entry naming its path list as an explicit null loads with an empty list, and
    /// both the projector and the banner run over what loaded.
    /// </summary>
    /// <remarks>
    /// Written as a literal because the serializer never emits this shape, and nothing else on the
    /// load path reaches it: a property initialiser runs only for an ABSENT key, and the store's
    /// non-null restore does not descend into a collection's elements.
    /// </remarks>
    [Fact]
    public async Task AnExplicitlyNullNewestPathsLoadsAsAnEmptyList()
    {
        var store = new FakeStore();
        await store.SetAsync(
            OptionsStore.Key,
            """
            {
              "ImportRefusals": [
                {
                  "Root": "/whisparr/media",
                  "CountSinceLastSuccess": 2,
                  "NewestPaths": null
                }
              ]
            }
            """);

        var loaded = await new OptionsStore(store).LoadAsync();

        var entry = Assert.Single(loaded.ImportRefusals);
        Assert.NotNull(entry.NewestPaths);
        Assert.Empty(entry.NewestPaths);

        var refused = ImportRefusalProjector.Refuse(
            loaded.ImportRefusals,
            entry.Root,
            "/whisparr/media/scene-a/file.mp4",
            ImportRefusalCause.Unreadable);
        var counted = Assert.Single(refused);
        Assert.Equal(3, counted.CountSinceLastSuccess);
        Assert.Single(counted.NewestPaths);
        Assert.Empty(ImportRefusalProjector.Succeed(refused, entry.Root));

        var line = Assert.Single(
            ImportBannerView.From(loaded.ImportRefusals, loaded.ImportHealth).Roots);
        Assert.Equal("/whisparr/media", line.Root);
        Assert.Empty(line.NewestPaths);
    }

    /// <summary>
    /// A reported version is shortened where it is stored, so an instance at the configured address
    /// cannot make the whole blob the host serves in one piece too large to serve.
    /// </summary>
    /// <remarks>
    /// The ordinary reading is the control: a bound that blanked every value would satisfy the two
    /// length assertions on their own. Null is kept as null, which is what distinguishes a connection
    /// no test has read a version from.
    /// </remarks>
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

    /// <summary>
    /// A stored version longer than the maximum loads shortened, so a blob an earlier build wrote
    /// cannot reintroduce a value with no ceiling.
    /// </summary>
    /// <remarks>
    /// Written as a literal rather than produced by serializing anything: the model can no longer
    /// hold the value this asks the load path to bind.
    /// </remarks>
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

    /// <summary>
    /// A recorded failure text is shortened where it is stored, so one exception message cannot make
    /// the whole blob the host serves in one piece too large to serve.
    /// </summary>
    [Fact]
    public void ARecordedFailureTextIsShortenedWhereItIsStored()
    {
        var health = new ImportHealthAggregate
        {
            LastError = new string('x', ImportHealthAggregate.LastErrorMaxLength * 4),
        };

        Assert.Equal(ImportHealthAggregate.LastErrorMaxLength, health.LastError.Length);

        // Storing the already-shortened text again yields the same record, which is what keeps a
        // round-trip through the store equal to what went into it.
        Assert.Equal(health, new ImportHealthAggregate { LastError = health.LastError });
    }

    /// <summary>
    /// The API key has no home in this record. It is in a table this extension owns, so the host's
    /// bulk extension-data route has nothing of it to return.
    /// </summary>
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

    private static WhisparrSyncOptions Populated() => new()
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
        },
        ImportRefusals =
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
        ],
    };
}
