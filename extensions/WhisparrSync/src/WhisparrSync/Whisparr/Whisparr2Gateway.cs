using System.Net.Http.Headers;
using Microsoft.Extensions.DependencyInjection;
using Whisparr2.Net;
using Whisparr2.Net.Client;

namespace WhisparrSync.Whisparr;

/// <summary>
/// Whisparr v2's request surface, obtained from the generated Whisparr 2 client.
/// </summary>
/// <remarks>
/// The two generations' packages declare unrelated <c>IApiResponse</c> interfaces with no common base
/// and unrelated registration option types, so one type cannot serve both and this is a twin of
/// <see cref="Whisparr3Gateway"/>.
/// <para>
/// The transport settings this product requires are applied to every typed client the registration
/// creates: the redirect cap, the bound on how much of one answer is read, and the budget the target
/// names. None is the generated client's default, and each is a requirement stated in
/// <see cref="WhisparrClient"/>.
/// </para>
/// </remarks>
internal sealed class Whisparr2Gateway : IDisposable
{
    private readonly GeneratedClientRegistry<Whisparr2Target> _registry;

    public Whisparr2Gateway(
        Func<HttpMessageHandler>? primaryHandler = null,
        Action<HttpClient>? configure = null)
    {
        var handler = primaryHandler ?? WhisparrClient.CreateHandler;
        // No default settings of its own: the budget below is where a gateway client's timeout comes
        // from, and a supplied configure still runs after it so a caller can override.
        var settings = configure ?? (static _ => { });
        _registry = new GeneratedClientRegistry<Whisparr2Target>(
            target => Register(target, handler, settings));
    }

    /// <summary>The typed APIs for the instance <paramref name="target"/> names.</summary>
    public Whisparr2Apis For(Whisparr2Target target) => new(_registry.Reach(target));

    /// <summary>What one generated call answered, in the spelling every seam member returns.</summary>
    /// <remarks>
    /// The status and the body are taken as received. The generated accessor deserialises on exactly
    /// one status and answers null on every other, so reading through it would lose the difference
    /// between a refusal and an empty answer that this product's own classification rests on.
    /// </remarks>
    public static WhisparrResponse Answered(IApiResponse answered)
    {
        ArgumentNullException.ThrowIfNull(answered);

        return new WhisparrResponse(
            (int)answered.StatusCode,
            ContentTypeOf(answered.ContentHeaders),
            answered.RawContent ?? string.Empty);
    }

    public void Dispose() => _registry.Dispose();

    private static string? ContentTypeOf(HttpContentHeaders? headers)
        => headers?.ContentType?.ToString();

    private static ServiceProvider Register(
        Whisparr2Target target, Func<HttpMessageHandler> handler, Action<HttpClient> settings)
    {
        var services = new ServiceCollection();
        services.AddWhisparr2(new Whisparr2Options
        {
            BaseUrl = target.BaseAddress.ToString(),
            ApiKey = target.ApiKey,
            ConfigureHttpClient = builder => builder
                .ConfigurePrimaryHttpMessageHandler(handler)
                .AddHttpMessageHandler(static () =>
                    new BoundedResponseHandler(WhisparrClient.MaxResponseBytes))
                // The registry keys by value, so a second budget against one instance is a second
                // client rather than a setting changed on a client already in use.
                .ConfigureHttpClient(client => client.Timeout = target.Budget)
                .ConfigureHttpClient(settings),
        });

        return services.BuildServiceProvider();
    }
}

/// <summary>The instance one generated call is made against, and how long it may take.</summary>
/// <remarks>
/// A record so the whole of it is the cache key by value. The address and the key are both in it
/// because a key edited against the same address is a different registration. The budget is in it
/// because <see cref="HttpClient.Timeout"/> cannot be changed once a client has been used, so a
/// second budget against the same instance is a second client rather than a setting.
/// </remarks>
internal readonly record struct Whisparr2Target(Uri BaseAddress, string ApiKey, TimeSpan Budget)
{
    /// <summary>The instance at <paramref name="baseAddress"/>, for a per-item call.</summary>
    public Whisparr2Target(Uri baseAddress, string apiKey)
        : this(baseAddress, apiKey, WhisparrClient.RequestTimeout)
    {
    }
}

/// <summary>The generated client's typed APIs, already bound to one instance.</summary>
/// <remarks>
/// Bound rather than taking the instance per call, so a call site cannot name one instance for the
/// read and another for the write that follows it.
/// </remarks>
internal readonly struct Whisparr2Apis(IServiceProvider provider)
{
    public TApi Api<TApi>()
        where TApi : notnull
        => provider.GetRequiredService<TApi>();
}
