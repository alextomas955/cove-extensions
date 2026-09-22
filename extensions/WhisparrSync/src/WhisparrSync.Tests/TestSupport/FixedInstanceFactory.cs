using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.TestSupport;

// Answers the same seam whatever binding is asked for, so a case can drive the routes through one
// recorder and read the calls off it. The shipped factory answers a different type per generation,
// which a recorder standing in for both cannot.
internal sealed class FixedInstanceFactory(IWhisparrClient instance) : IWhisparrInstanceFactory
{
    public IWhisparrClient Bound(WhisparrBinding binding) => instance;
}
