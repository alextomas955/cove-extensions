namespace Renamer.Engine;

// FolderPath is relative and may be empty, which means no folder move; it keeps '/' only as the
// path separator. Filename excludes the extension. Ext carries its leading dot, or is empty. The
// executor applies the absolute-path confinement and filesystem checks.
public readonly record struct RenamerResult(string FolderPath, string Filename, string Ext);
