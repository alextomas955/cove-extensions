using Renamer.Options;

namespace Renamer.Api;

/// <summary>
/// The body for <c>POST /preview-sample</c>: the unsaved <see cref="RenamerOptions"/> from the
/// settings panel.
/// </summary>
/// <remarks>
/// The endpoint runs the real <see cref="Engine.TemplateEngine"/> over the built-in
/// <see cref="SampleTokenSets"/>, so the panel never re-implements naming logic. Null options fall
/// back to the defaults, as <c>OptionsStore</c> does.
/// </remarks>
public sealed record PreviewSampleRequest(RenamerOptions? Options);
