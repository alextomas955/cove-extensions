using WhisparrSync.Linking;

namespace WhisparrSync.Import;

/// <summary>The two seams an ingest reaches a reader's disk through.</summary>
/// <remarks>
/// One delivery asks both about one file: whether a candidate path is that file, and whether it can
/// be given a second name where the library keeps it. Neither seam can express a move, a rename or
/// a write over what is there.
/// </remarks>
internal sealed record ImportFilesystem(IImportPathPort Paths, ITreeLinkPort Links);
