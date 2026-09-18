namespace Renamer.Api;

/// <summary>
/// Optional body for the whole-library <c>/scan-library</c> dry-run enqueue, carrying the caller's
/// current and possibly unsaved options.
/// </summary>
/// <remarks>
/// The options travel as a raw JSON string, not a bound <c>RenamerOptions</c>: the host's minimal-API
/// serializer is camelCase, while the options blob is PascalCase and tolerant-read under
/// <c>RenamerOptions.JsonOptions</c>, the same contract
/// <see cref="global::Renamer.Options.OptionsStore"/> persists. Deserializing the string here with
/// that options set makes a dry run on unsaved edits read the blob exactly as the saved-options load
/// path does. Null or blank means scan with the saved options.
/// </remarks>
public sealed record ScanLibraryRequest(string? Options);
