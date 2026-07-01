using Renamer.Options;

namespace Renamer.Api;

/// <summary>
/// The request body for the live-preview endpoint <c>POST /preview-sample</c>: the in-flight,
/// UNSAVED <see cref="RenamerOptions"/> from the settings panel. The endpoint runs the real
/// <see cref="Engine.TemplateEngine"/> over the built-in <see cref="SampleTokenSets"/> with these options
/// — selection-less and pure (no DB/disk), so the panel never re-implements naming logic.
/// </summary>
/// <param name="Options">
/// The panel's current options. <c>null</c> → safe defaults (<c>new RenamerOptions()</c>), matching
/// how <c>OptionsStore</c> falls back to defaults.
/// </param>
public sealed record PreviewSampleRequest(RenamerOptions? Options);
