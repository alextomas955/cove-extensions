using Cove.Core.Events;

namespace Renamer.Tests.TestSupport;

/// <summary>
/// A capturing <see cref="IEventBus"/> fake that records every published <see cref="CoveEvent"/>
/// so a test can assert the post-renamer event's ARGS (type + entity id), not merely that Publish
/// was called. Subscribe is a no-op (the executor only publishes).
/// </summary>
public sealed class CapturingEventBus : IEventBus
{
    /// <summary>Every published event, in publish order.</summary>
    public List<CoveEvent> Published { get; } = [];

    public void Publish(CoveEvent evt) => Published.Add(evt);

    public IDisposable Subscribe(Action<CoveEvent> handler) => new NoopDisposable();
    public IDisposable Subscribe(EventType type, Action<CoveEvent> handler) => new NoopDisposable();
    public IDisposable Subscribe<T>(Action<T> handler) where T : CoveEvent => new NoopDisposable();

    private sealed class NoopDisposable : IDisposable { public void Dispose() { } }
}
