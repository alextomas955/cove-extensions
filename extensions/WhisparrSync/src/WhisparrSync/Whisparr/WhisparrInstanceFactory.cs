using Microsoft.Extensions.Logging;
using WhisparrSync.Contracts;

namespace WhisparrSync.Whisparr;

/// <summary>Answers the outbound seam bound to one instance.</summary>
/// <remarks>
/// Obtained rather than held: which generation is connected is a stored setting read per request, so
/// an instance resolved at load time would predate the connection it describes.
/// </remarks>
public interface IWhisparrInstanceFactory
{
    /// <summary>The seam bound to <paramref name="binding"/>.</summary>
    /// <remarks>
    /// The answer declares only the roles the binding's generation holds, so a role that generation
    /// does not hold is one the caller cannot obtain.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The binding names a generation this product has no instance for.
    /// </exception>
    IWhisparrClient Bound(WhisparrBinding binding);
}

// The one switch on the generation this product keeps. Every other place a generation used to be
// branched on now reaches the instance built here, which declares only the roles its generation
// holds.
//
// An instance is cheap, holding no connection and no buffer; the transport and the two gateways it
// sends through are the registered singletons.
internal sealed class WhisparrInstanceFactory(
    WhisparrTransport transport,
    Whisparr3Gateway v3Gateway,
    Whisparr2Gateway v2Gateway,
    ISiteNumberPort siteNumbers,
    ILogger log) : IWhisparrInstanceFactory
{
    public IWhisparrClient Bound(WhisparrBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);

        return binding.Generation switch
        {
            WhisparrGeneration.V3 => new WhisparrV3Instance(binding, transport, v3Gateway, log),
            WhisparrGeneration.V2 =>
                new WhisparrV2Instance(binding, transport, v2Gateway, siteNumbers, log),
            _ => throw new ArgumentOutOfRangeException(nameof(binding)),
        };
    }
}
