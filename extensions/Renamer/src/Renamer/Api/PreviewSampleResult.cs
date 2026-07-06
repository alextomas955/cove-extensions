namespace Renamer.Api;

/// <summary>
/// One built-in sample's live-preview result: the synthetic "before" filename, the engine-rendered
/// "after" name+ext and folder, and the advisory flags the UI surfaces. Computed by the real
/// <see cref="Engine.TemplateEngine"/> so the preview matches a real renamer exactly.
/// </summary>
/// <param name="SampleLabel">Human label of the sample shape: <c>"Video"</c> / <c>"Image"</c> / <c>"Audio"</c>.</param>
/// <param name="OldName">The synthetic original filename shown as the "before" (UI diff old side).</param>
/// <param name="NewName">The engine-rendered filename including its extension (the "after").</param>
/// <param name="Folder">The engine-rendered relative folder path (may be empty = no folder move).</param>
/// <param name="Flags">
/// Stable string codes the UI maps to copy: <c>"empty"</c>, <c>"sanitized"</c>, <c>"length-reduced"</c>,
/// <c>"gating-skip"</c>. Order is not significant.
/// </param>
/// <param name="DroppedFields">
/// When <see cref="Flags"/> contains <c>"length-reduced"</c>, the <see cref="Options.RenamerOptions.DropOrder"/>
/// fields actually dropped (reported by the engine), so the UI can show "dropped: {fields}". Empty otherwise.
/// </param>
public sealed record PreviewSampleResult(
    string SampleLabel,
    string OldName,
    string NewName,
    string Folder,
    string[] Flags,
    string[] DroppedFields);
