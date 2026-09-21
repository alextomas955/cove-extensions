using Renamer.Engine;

namespace Renamer.Api;

// Fixed token sets the live preview renders against, held server-side so the sample count is bounded
// and a preview request cannot amplify work, and so the samples use the engine's own token names.
//
// The shapes exercise the engine's empty-token and {}-drop behavior: the Image set carries no codecs
// or duration, and the Audio set carries no video tokens.
public static class SampleTokenSets
{
    public sealed record Sample(
        string Label,
        string OldName,
        IReadOnlyDictionary<string, string> Tokens,
        IReadOnlyDictionary<string, IReadOnlyList<string>> MultiValues);

    // In display order.
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
                [Tokens.Width] = "3840",
                [Tokens.Height] = "2160",
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
                [Tokens.Height] = "4000",
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
                [Tokens.AudioCodec] = "flac",
                [Tokens.Ext] = "flac",
            },
            MultiValues: new Dictionary<string, IReadOnlyList<string>>
            {
                [Tokens.Performers] = ["The Band"],
            }),
    ];
}
