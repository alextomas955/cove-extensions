using WhisparrSync.Contracts;

namespace WhisparrSync.Whisparr;

/// <summary>The class of work a request does, which is what its retry behaviour is keyed on.</summary>
/// <remarks>
/// A class that must never be re-issued is a member with no entry in
/// <see cref="WhisparrRetryPolicy"/>'s table, which is a data change rather than a structural one.
/// </remarks>
public enum WhisparrVerbClass
{
    /// <summary>A request that only reads. Re-issuing one creates nothing and grabs nothing.</summary>
    Read,

    /// <summary>
    /// Changes the instance's own configuration. Never re-issued: a second attempt after an answer
    /// that did not arrive would act twice.
    /// </summary>
    Configure,

    /// <summary>
    /// Changes what an instance monitors. Never re-issued: a second attempt after an answer that did
    /// not arrive would act twice.
    /// </summary>
    Act,

    /// <summary>
    /// The one class that can make an instance download. Never re-issued, and reachable only through
    /// the role a caller obtains by name.
    /// </summary>
    Grab,
}

/// <summary>How many attempts a verb class is allowed.</summary>
/// <remarks>
/// Per verb class rather than uniform, because a uniform retry is what would silently re-issue a
/// request that acts. An unlisted class gets <see cref="NoRetry"/>, so the safe answer is the
/// default and a retrying class has to be written down.
/// </remarks>
public static class WhisparrRetryPolicy
{
    /// <summary>One attempt: the request is issued once and a failure is reported.</summary>
    public const int NoRetry = 1;

    private static readonly Dictionary<WhisparrVerbClass, int> AttemptsByVerbClass = new()
    {
        [WhisparrVerbClass.Read] = 2,
    };

    /// <summary>How many attempts <paramref name="verbClass"/> is allowed.</summary>
    public static int AttemptsFor(WhisparrVerbClass verbClass)
        => AttemptsByVerbClass.GetValueOrDefault(verbClass, NoRetry);
}

/// <summary>What one Whisparr request answered with.</summary>
/// <remarks>
/// The content type is the header as received, unparsed. A rejected key answers with none on both
/// generations, so an empty one is a real observation. The body is empty when there was none.
/// </remarks>
public sealed record WhisparrResponse(int StatusCode, string? ContentType, string Body)
{
    /// <summary>Why no entity was named, where a seam established that without an instance.</summary>
    /// <remarks>
    /// A v2 site is addressed by a number the metadata source issues, so a site nothing could be
    /// numbered for is refused before any request leaves and there is no status to classify. The
    /// reason is stated here instead of inventing a status.
    /// <para>
    /// <see cref="MonitorRefusalKind.None"/> on every answer that came from an instance, which is
    /// classified from its status.
    /// </para>
    /// </remarks>
    public MonitorRefusalKind Refusal { get; init; } = MonitorRefusalKind.None;
}
