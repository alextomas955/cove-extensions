namespace WhisparrSync.Providers;

/// <summary>A scene's cover picture can be read from the provider by its stored identifier.</summary>
/// <remarks>
/// A provider holding no cover picture does not implement this, so a caller obtains nothing and
/// leaves the card as the instance composed it. Declared apart from
/// <see cref="IProviderCatalogue"/> for that reason.
/// </remarks>
public interface IReadsSceneCover
{
    Task<string?> ReadSceneCoverAsync(string providerSceneId, CancellationToken ct);
}
