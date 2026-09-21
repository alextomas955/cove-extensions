using System.Text.Json.Serialization;
using Cove.Extensions.Shared;
using WhisparrSync.Options;

namespace WhisparrSync.Contracts;

/// <summary>What the inbound callback did with one delivery, as the delivery is told.</summary>
/// <remarks>
/// Two values, and deliberately coarse. Whether a file was found, which candidate verified, and
/// whether anything reached the library are all withheld: the caller is anonymous, and an answer
/// that varied with what is on disk would turn this route into a filesystem probe.
/// </remarks>
[JsonConverter(typeof(CamelCaseStringEnumConverter))]
public enum ImportEventOutcome
{
    /// <summary>
    /// The delivery named an event this product acts on, and it was acted on. It does not say a file
    /// was imported.
    /// </summary>
    Accepted,

    /// <summary>The delivery named an event this product does not act on, and nothing was done.</summary>
    Ignored,
}

/// <summary>What the inbound callback answers a delivery it authenticated with.</summary>
/// <remarks>
/// Neither member varies with the contents of the filesystem.
/// </remarks>
public sealed record ImportAcknowledgement(
    CallbackSecretPosition SecretPosition,
    ImportEventOutcome Outcome);

/// <summary>Why a reported file was not imported.</summary>
/// <remarks>
/// The wire spelling is declared on the type. An equivalent converter in a serializer options
/// collection would outrank it rather than duplicate it.
/// </remarks>
[JsonConverter(typeof(CamelCaseStringEnumConverter))]
public enum ImportRefusalCause
{
    /// <summary>The reported file was found under no Cove library root.</summary>
    NotFoundUnderAnyRoot,

    /// <summary>Two or more candidates verified, so none was chosen.</summary>
    AmbiguousCandidates,

    /// <summary>The host was asked to take the verified file and would not.</summary>
    /// <remarks>
    /// This spelling is persisted in the stored options blob, so it must not change: a value a
    /// later model cannot bind makes the whole blob load as defaults, and the extension runs on
    /// them, refusing every write, until the stored blob is repaired.
    /// </remarks>
    Unreadable,
}

/// <summary>One offending path, and why it was not imported.</summary>
/// <remarks>
/// The cause is named per path rather than per root, so a misconfigured root and one unreadable
/// file do not read identically.
/// </remarks>
public sealed record ImportBannerPathLine(string Path, ImportRefusalCause Cause);

/// <summary>One Whisparr root's outstanding refusals, as the settings page reads them.</summary>
/// <remarks>
/// The root is spelled as the reporting instance spells it, and is blank where no reporting root
/// contained the path. The count is the stored one, since that root's last successful import. The
/// paths are newest first, at most <see cref="ImportRootRefusals.NewestPathsKept"/> of them.
/// </remarks>
public sealed record ImportBannerRootLine(
    string Root,
    int CountSinceLastSuccess,
    IReadOnlyList<ImportBannerPathLine> NewestPaths);

/// <summary>
/// What Whisparr reported and Cove's library does not hold: the refusals outstanding, one line per
/// Whisparr root that has any, and the records the backstop could not take at all.
/// </summary>
/// <remarks>
/// A projection of the stored aggregates, never a live options type. Its size is the Whisparr root
/// count times <see cref="ImportRootRefusals.NewestPathsKept"/> plus two scalars, which is a
/// property of what is stored rather than of a truncation applied here. Roots come in the order
/// they are stored.
/// <para>
/// The contained count is a running total over every pass: the mark moved past each of those
/// records, so this channel never offers them again and no later success clears the count. The
/// instant is null when no pass ever contained one.
/// </para>
/// </remarks>
public sealed record ImportBannerView(
    IReadOnlyList<ImportBannerRootLine> Roots,
    int RecordsContained,
    DateTimeOffset? LastContainedAtUtc)
{
    /// <summary>What the stored refusals and import health read as.</summary>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="refusals"/> or <paramref name="health"/> is null.
    /// </exception>
    public static ImportBannerView From(
        IReadOnlyList<ImportRootRefusals> refusals, ImportHealthAggregate health)
    {
        ArgumentNullException.ThrowIfNull(refusals);
        ArgumentNullException.ThrowIfNull(health);

        return new ImportBannerView(
            [
                .. refusals.Select(entry => new ImportBannerRootLine(
                    entry.Root,
                    entry.CountSinceLastSuccess,
                    [.. entry.NewestPaths.Select(path => new ImportBannerPathLine(path.Path, path.Cause))])),
            ],
            health.RecordsContained,
            health.LastContainedAtUtc);
    }
}
