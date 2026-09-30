using Microsoft.Extensions.Logging;
using WhisparrSync.Options;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Connection;

/// <summary>
/// What a verb needs before it can reach the connected instance: the stored settings, the credential
/// for the generation they name, and the factory that binds a client to the pair.
/// </summary>
/// <remarks>
/// The three are always resolved together, so they travel as one service. It holds nothing between
/// calls. The logger travels with them because a verb that cannot reach the instance reports it.
/// </remarks>
internal sealed record WhisparrAccess(
    OptionsStore Options,
    ICredentialPort Credentials,
    IWhisparrInstanceFactory Instances,
    ILogger Log);
