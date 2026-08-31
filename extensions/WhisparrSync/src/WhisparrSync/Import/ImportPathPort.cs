namespace WhisparrSync.Import;

/// <inheritdoc cref="IImportPathPort"/>
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
            // A path the host cannot read is not a file this product can verify, and the caller's
            // next step is the same either way.
            return new ProbedPath(false, null);
        }
    }
}
