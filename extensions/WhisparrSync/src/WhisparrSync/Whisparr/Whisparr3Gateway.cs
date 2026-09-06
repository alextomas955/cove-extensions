using System.Collections.Concurrent;
using System.Net.Http.Headers;
using Microsoft.Extensions.DependencyInjection;
using Whisparr3.Net;
using Whisparr3.Net.Client;

namespace WhisparrSync.Whisparr;

/// <summary>
/// The newer generation's request surface, obtained from the generated Whisparr 3 client.
/// </summary>
/// <remarks>
/// The generated client fixes its address and key at registration, and this extension's are settings
/// a person edits, so one registration is held per address-and-key pair rather than one per process.
/// A pair is reached again on the next call to the same instance, which is the normal case.
/// <para>
/// The transport settings this product requires are applied to every typed client the registration
/// creates: the redirect cap, the per-attempt timeout and the bound on how much of one answer is
/// read. None is the generated client's default, and each is a requirement stated in
/// <see cref="WhisparrClient"/>.
/// </para>
/// </remarks>
internal sealed class Whisparr3Gateway(
    Func<HttpMessageHandler>? primaryHandler = null,
    Action<HttpClient>? configure = null) : IDisposable
{
    /// <summary>How many address-and-key pairs are kept before the least recent is discarded.</summary>
    /// <remarks>
    /// A person testing a connection supplies a pair per attempt, so the set is not bounded by how
    /// many instances exist. The least recently reached entry is the one discarded, so a discarded
    /// registration is idle rather than one a request is running against.
    /// </remarks>
    internal const int MaxRegistrations = 8;

    private readonly ConcurrentDictionary<Whisparr3Target, Registration> _registrations = new();
    private long _reachCount;
    private bool _disposed;

    /// <summary>The typed APIs for the instance <paramref name="target"/> names.</summary>
    public Whisparr3Apis For(Whisparr3Target target) => new(Reach(target).Provider);

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

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var registration in _registrations.Values)
        {
            registration.Provider.Dispose();
        }

        _registrations.Clear();
    }

    private static string? ContentTypeOf(HttpContentHeaders? headers)
        => headers?.ContentType?.ToString();

    private Registration Reach(Whisparr3Target target)
    {
        var registration = _registrations.GetOrAdd(target, Register);
        registration.ReachedAt = Interlocked.Increment(ref _reachCount);
        DiscardBeyondCap();
        return registration;
    }

    private Registration Register(Whisparr3Target target)
    {
        var handler = primaryHandler ?? WhisparrClient.CreateHandler;
        var settings = configure ?? WhisparrClient.Configure;
        var services = new ServiceCollection();
        services.AddWhisparr3(new Whisparr3Options
        {
            BaseUrl = target.BaseAddress.ToString(),
            ApiKey = target.ApiKey,
            ConfigureHttpClient = builder => builder
                .ConfigurePrimaryHttpMessageHandler(handler)
                .AddHttpMessageHandler(static () =>
                    new BoundedResponseHandler(WhisparrClient.MaxResponseBytes))
                .ConfigureHttpClient(settings),
        });

        return new Registration(services.BuildServiceProvider());
    }

    // The least recently reached entry, which a running request is not holding: reaching one is what
    // marks it, and every call marks its own before this runs.
    private void DiscardBeyondCap()
    {
        while (_registrations.Count > MaxRegistrations)
        {
            var oldest = _registrations
                .OrderBy(entry => entry.Value.ReachedAt)
                .First();

            if (_registrations.TryRemove(oldest.Key, out var discarded))
            {
                discarded.Provider.Dispose();
            }
        }
    }

    private sealed class Registration(ServiceProvider provider)
    {
        public ServiceProvider Provider { get; } = provider;

        public long ReachedAt { get; set; }
    }
}

/// <summary>The instance one generated call is made against.</summary>
/// <remarks>
/// A record so the pair is the cache key by value. The key is the address and the key together,
/// because a key edited against the same address is a different registration.
/// </remarks>
internal readonly record struct Whisparr3Target(Uri BaseAddress, string ApiKey);

/// <summary>The generated client's typed APIs, already bound to one instance.</summary>
/// <remarks>
/// Bound rather than taking the instance per call, so a call site cannot name one instance for the
/// read and another for the write that follows it.
/// </remarks>
internal readonly struct Whisparr3Apis(IServiceProvider provider)
{
    public TApi Api<TApi>()
        where TApi : notnull
        => provider.GetRequiredService<TApi>();
}
