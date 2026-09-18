using Cove.Core.Events;

namespace Renamer.Tests.TestSupport;

/// <summary>
/// A capturing <see cref="IEventBus"/> fake that records every published <see cref="CoveEvent"/>
/// so a test can assert the post-renamer event's args (type + entity id), not merely that Publish
/// was called. Subscribe is a no-op (the executor only publishes).
/// </summary>
public sealed class CapturingEventBus : IEventBus
{
    /// <summary>Every published event, in publish order.</summary>
    public List<CoveEvent> Published { get; } = [];

    /// <summary>When set, <see cref="Publish"/> throws this instead of recording the event.</summary>
    public Exception? PublishThrow { get; set; }

    public void Publish(CoveEvent evt)
    {
        if (PublishThrow is not null)
        {
            throw PublishThrow;
        }

        Published.Add(evt);
    }

    public IDisposable Subscribe(Action<CoveEvent> handler) => new NoopDisposable();
    public IDisposable Subscribe(EventType type, Action<CoveEvent> handler) => new NoopDisposable();
    public IDisposable Subscribe<T>(Action<T> handler) where T : CoveEvent => new NoopDisposable();

    private sealed class NoopDisposable : IDisposable { public void Dispose() { } }
}
