using System.Text;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Providers;

/// <summary>Reads a provider's answer whole, or refuses one past the bound this product reads at.</summary>
/// <remarks>
/// A truncated body parses as a valid short page, so an answer past the bound is refused rather than
/// returned as far as it was held.
/// </remarks>
internal static class ProviderResponseBound
{
    private const int ChunkBytes = 64 * 1024;

    /// <summary>The whole of <paramref name="content"/>, or null where it passed the bound.</summary>
    internal static async Task<string?> ReadAsync(HttpContent content, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(content);

        var ceiling = WhisparrClient.MaxResponseBytes + 1;
        var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            using var buffered = new MemoryStream();
            var chunk = new byte[ChunkBytes];

            while (buffered.Length < ceiling)
            {
                var wanted = (int)Math.Min(chunk.Length, ceiling - buffered.Length);
                var read = await stream.ReadAsync(chunk.AsMemory(0, wanted), ct).ConfigureAwait(false);
                if (read == 0)
                {
                    return Encoding.UTF8.GetString(buffered.GetBuffer(), 0, (int)buffered.Length);
                }

                await buffered.WriteAsync(chunk.AsMemory(0, read), ct).ConfigureAwait(false);
            }

            return null;
        }
    }
}
