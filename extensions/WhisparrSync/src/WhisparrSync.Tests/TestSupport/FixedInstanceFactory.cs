using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.TestSupport;

// Answers the same seam whatever binding is asked for, so a case can drive the routes through one
// recorder and read the calls off it. The shipped factory answers a different type per generation,
// which a recorder standing in for both cannot.
internal sealed class FixedInstanceFactory(IWhisparrClient instance) : IWhisparrInstanceFactory
{
    // What each caller asked to be bound to, so a case can read the pair a resolution produced. The
    // seam answers one recorder whatever it is handed, and the address and key it was built with say
    // nothing about the ones that were resolved.
    public List<WhisparrBinding> Bindings { get; } = [];

    public IWhisparrClient Bound(WhisparrBinding binding)
    {
        Bindings.Add(binding);
        return instance;
    }
}
