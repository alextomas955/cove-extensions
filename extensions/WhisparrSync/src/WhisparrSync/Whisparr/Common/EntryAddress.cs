namespace WhisparrSync.Whisparr;

/// <summary>The rows one generation addresses an attaching entry by.</summary>
/// <remarks>
/// One generation names a scene by a row of its own and needs nothing beside it. The other holds a
/// scene as an episode under a site, which one row does not name: the site's row and the episode's
/// row are both required, and an address carrying only the first cannot be attached to.
/// <para>
/// A null <see cref="SiteRow"/> is an address composed where the site was never resolved. The
/// generation that needs it refuses such an address rather than attaching to a site nobody named.
/// </para>
/// </remarks>
internal readonly record struct EntryAddress(int Row, int? SiteRow = null);
