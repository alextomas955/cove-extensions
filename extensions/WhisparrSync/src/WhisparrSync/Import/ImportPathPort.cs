namespace WhisparrSync.Import;

internal sealed class ImportPathPort : IImportPathPort
{
    public ProbedPath Probe(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        try
        {
            var file = new FileInfo(path);
            return file.Exists ? new ProbedPath(true, file.Length) : new ProbedPath(false, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // A path that cannot be read is not a file this product can verify, and the caller
            // acts the same way on either.
            return new ProbedPath(false, null);
        }
    }
}
