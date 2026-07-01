using Renamer.Engine;

namespace Renamer.Api;

/// <summary>
/// Fixed, server-side representative token sets the live preview renders against. Defined here —
/// NOT supplied by the request — so the sample count is bounded (3) and a hostile preview template
/// cannot amplify work, and so the samples stay in sync with the real engine's token names
/// (<see cref="Tokens"/>) rather than a TS re-implementation.
///
/// The three shapes deliberately exercise the engine's empty-token / <c>{}</c>-drop behavior:
/// the Image set has NO codecs/duration (so <c>$videoCodec</c>/<c>$audioCodec</c>/<c>$duration</c>
/// resolve empty), and the Audio set has NO video tokens.
/// </summary>
public static class SampleTokenSets
{
    /// <summary>
    /// One representative sample: a human <see cref="Label"/>, the synthetic "before" filename
    /// (<see cref="OldName"/>), the scalar token dict (<see cref="Tokens"/>), and the multi-value
    /// dict (<see cref="MultiValues"/>) the engine consumes.
    /// </summary>
    public sealed record Sample(
        string Label,
        string OldName,
        IReadOnlyDictionary<string, string> Tokens,
        IReadOnlyDictionary<string, IReadOnlyList<string>> MultiValues);

    /// <summary>The three fixed samples (Video / Image / Audio), in display order.</summary>
    public static IReadOnlyList<Sample> All { get; } =
    [
        new Sample(
            Label: "Video",
            OldName: "the.example.2021.WEBRip.mp4",
            Tokens: new Dictionary<string, string>
            {
                [Tokens.Title] = "The Example",
                [Tokens.Studio] = "Acme Studios",
                [Tokens.StudioCode] = "ACM-042",
                [Tokens.Date] = "2021-03-14",
                [Tokens.Year] = "2021",
                [Tokens.Height] = "2160",        // → $resolution 2160p (derived by the engine)
                [Tokens.VideoCodec] = "h264",
                [Tokens.AudioCodec] = "aac",
                [Tokens.FrameRate] = "60",
                [Tokens.Duration] = "01-23-45",
                [Tokens.Ext] = "mp4",
            },
            MultiValues: new Dictionary<string, IReadOnlyList<string>>
            {
                [Tokens.Performers] = ["Jane Doe", "John Roe"],
                [Tokens.Tags] = ["4k", "demo"],
            }),

        new Sample(
            Label: "Image",
            OldName: "IMG_4821.jpg",
            Tokens: new Dictionary<string, string>
            {
                [Tokens.Title] = "Sunset",
                [Tokens.Studio] = "Acme Studios",
                [Tokens.Date] = "2022-07-01",
                [Tokens.Year] = "2022",
                [Tokens.Width] = "6000",
                [Tokens.Height] = "4000",        // → $resolution (derived); no codecs/duration
                [Tokens.Ext] = "jpg",
            },
            MultiValues: new Dictionary<string, IReadOnlyList<string>>
            {
                [Tokens.Tags] = ["landscape"],
            }),

        new Sample(
            Label: "Audio",
            OldName: "track01.flac",
            Tokens: new Dictionary<string, string>
            {
                [Tokens.Title] = "Track One",
                [Tokens.Studio] = "Acme Records",
                [Tokens.Date] = "2020-01-09",
                [Tokens.Year] = "2020",
                [Tokens.Duration] = "00-03-30",
                [Tokens.AudioCodec] = "flac",    // no video tokens (resolution/videoCodec/frameRate)
                [Tokens.Ext] = "flac",
            },
            MultiValues: new Dictionary<string, IReadOnlyList<string>>
            {
                [Tokens.Performers] = ["The Band"],
            }),
    ];
}
