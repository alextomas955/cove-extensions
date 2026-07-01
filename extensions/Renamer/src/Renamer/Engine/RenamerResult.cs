namespace Renamer.Engine;

/// <summary>
/// The pure output of <see cref="TemplateEngine.Render"/> — a sanitized, length-safe
/// relative folder path, filename (without extension), and extension. It is a plain value:
/// NO disk write, NO DB. The executor maps this onto a real <c>VideoFile</c>/parent folder and
/// performs the absolute-path confinement + filesystem checks the engine deliberately omits.
/// </summary>
/// <param name="FolderPath">
/// Relative, per-segment-sanitized folder path (may be empty = no folder move). Keeps
/// <c>/</c> only as a path separator.
/// </param>
/// <param name="Filename">The sanitized filename component WITHOUT the extension.</param>
/// <param name="Ext">The extension including its leading dot (e.g. <c>.mkv</c>), or empty.</param>
public readonly record struct RenamerResult(string FolderPath, string Filename, string Ext);
