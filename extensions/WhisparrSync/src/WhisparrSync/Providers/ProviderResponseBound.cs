using System.Text;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Providers;

// A truncated body parses as a valid short page, so an answer past the bound is refused as null
// rather than returned as far as it was read.
internal static class ProviderResponseBound
{
    private const int ChunkBytes = 64 * 1024;

    internal static async Task<string?> ReadAsync(HttpContent content, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(content);

        var ceiling = WhisparrTransport.MaxResponseBytes + 1;
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
