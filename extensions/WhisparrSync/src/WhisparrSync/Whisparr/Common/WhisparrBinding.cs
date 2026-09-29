using System.Text;
using WhisparrSync.Connection;
using WhisparrSync.Contracts;

namespace WhisparrSync.Whisparr;

/// <summary>One reachable Whisparr instance: which generation it is, where it is, and the key.</summary>
/// <remarks>
/// Validated where it is built, so a binding that exists is one a request can be sent on. The
/// address is rebuilt from scheme, authority and path the way the address reader rebuilds it, so
/// user-info a person embedded cannot travel on it. The key is kept out of what the record renders,
/// because a value carrying a secret discloses it wherever it is logged or recorded.
/// <para>
/// The three are held together rather than beside each other so a caller cannot take a generation
/// from one instance and an address from another.
/// </para>
/// <para>
/// Declared without positional parameters so the validation cannot be stepped around by a
/// <c>with</c> expression.
/// </para>
/// </remarks>
/// <exception cref="ArgumentException">
/// The address is relative or on a scheme other than http or https, or the key is blank.
/// </exception>
public sealed record WhisparrBinding
{
    public WhisparrBinding(WhisparrGeneration generation, Uri baseAddress, string apiKey)
    {
        ArgumentNullException.ThrowIfNull(baseAddress);

        if (!ConnectionTester.TryReadAddress(baseAddress.OriginalString, out var addressable))
        {
            throw new ArgumentException(
                "A Whisparr address must be an absolute http or https URL.", nameof(baseAddress));
        }

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new ArgumentException("A Whisparr binding carries a key.", nameof(apiKey));
        }

        Generation = generation;
        BaseAddress = addressable;
        ApiKey = apiKey;
    }

    public WhisparrGeneration Generation { get; }

    public Uri BaseAddress { get; }

    public string ApiKey { get; }

    public override string ToString()
        => new StringBuilder(nameof(WhisparrBinding))
            .Append(" { Generation = ").Append(Generation)
            .Append(", BaseAddress = ").Append(BaseAddress)
            .Append(" }")
            .ToString();
}
