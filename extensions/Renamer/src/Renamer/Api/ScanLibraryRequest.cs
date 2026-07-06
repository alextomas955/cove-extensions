namespace Renamer.Api;

/// <summary>
/// Optional request body for the whole-library <c>/scan-library</c> dry-run enqueue. Carries the
/// caller's CURRENT (possibly unsaved) options as a raw JSON blob so a dry run can preview edits the
/// user has not saved yet — the whole point of a dry run.
/// </summary>
/// <remarks>
/// The options travel as a raw JSON string (<paramref name="Options"/>), NOT a bound
/// <c>RenamerOptions</c>: the host's minimal-API serializer is camelCase, while the options blob is
/// PascalCase + tolerant-read (<c>RenamerOptions.JsonOptions</c>) — the same contract
/// <see cref="global::Renamer.Options.OptionsStore"/> persists. Deserializing the string ourselves with that
/// options set keeps the scan's parsing byte-identical to the saved-options load path, so a dry run
/// on unsaved edits and a scan of saved settings interpret the blob the same way. A null or blank
/// blob means "use the saved options" — the original no-body behavior, preserved for back-compat.
/// </remarks>
/// <param name="Options">The current options as a PascalCase JSON blob, or null to scan saved options.</param>
public sealed record ScanLibraryRequest(string? Options);
