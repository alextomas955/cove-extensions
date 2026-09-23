namespace Renamer.Api;

/// <summary>
/// Optional body for the whole-library <c>/scan-library</c> dry-run enqueue, carrying the caller's
/// current and possibly unsaved options.
/// </summary>
/// <remarks>
/// The options travel as a JSON string in the stored blob's spelling, parsed and repaired the way a
/// saved load is, so a dry run on unsaved edits reads them exactly as a rename would. Null or blank
/// means scan with the saved options.
/// </remarks>
public sealed record ScanLibraryRequest(string? Options);
