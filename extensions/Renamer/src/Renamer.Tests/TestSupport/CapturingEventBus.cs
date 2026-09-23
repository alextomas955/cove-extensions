using Cove.Core.Events;

namespace Renamer.Tests.TestSupport;

// A capturing IEventBus fake that records every published CoveEvent so a test can assert the
// post-renamer event's args (type + entity id), not merely that Publish was called. Subscribe is a
// no-op (the executor only publishes).
public sealed class CapturingEventBus : IEventBus
{
    // Every published event, in publish order.
    public List<CoveEvent> Published { get; } = [];

    // When set, Publish throws this instead of recording the event.
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
