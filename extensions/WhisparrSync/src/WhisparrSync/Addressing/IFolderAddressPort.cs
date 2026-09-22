using WhisparrSync.Contracts;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Addressing;

/// <summary>Which instance a folder is being addressed on, and how to ask it.</summary>
public sealed record FolderAddressTarget(
    WhisparrBinding Binding,
    IWhisparrInstanceFilesystemReading Filesystem);

/// <summary>How one folder is spelled on the connected instance, or why it is not.</summary>
/// <remarks>
/// Exactly one of InstancePath and Refusal is non-null. CoveRoot is blank where the folder sits
/// under no library root, and is carried either way so a run can report one line per root.
/// </remarks>
public sealed record AddressedFolder(
    string? InstancePath,
    FolderAgreementRefusal? Refusal,
    string CoveRoot,
    IReadOnlyList<string> Tried);

/// <summary>Turns a folder the library names into the path the instance can open.</summary>
/// <remarks>
/// One sample file and one set of probes per library root, reused by every folder under it, so a
/// run's cost grows with the root count and not with the folder count.
/// <para>
/// A folder that cannot be addressed is refused, never guessed at. An instance handed the library's
/// own spelling answers an empty listing, and the run then reports a clean zero.
/// </para>
/// </remarks>
public interface IFolderAddressPort
{
    /// <summary><paramref name="folder"/> as <paramref name="target"/> spells it.</summary>
    /// <exception cref="ArgumentException"><paramref name="folder"/> is blank.</exception>
    Task<AddressedFolder> AddressAsync(
        FolderAddressTarget target, string folder, CancellationToken ct);

    /// <summary>
    /// What <paramref name="coveRoot"/> itself comes to under <paramref name="supplied"/>, asking the
    /// instance rather than anything stored.
    /// </summary>
    /// <remarks>
    /// The same probe and verdict a run takes, so a path accepted here cannot be one a run then
    /// refuses. A reading that resolved is held for <paramref name="coveRoot"/> at once, so a saved
    /// mapping is in force on the next run rather than after the previous reading expires. One that
    /// did not resolve is not held.
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// <paramref name="coveRoot"/> or <paramref name="supplied"/> is blank.
    /// </exception>
    Task<AddressedFolder> AddressAsync(
        FolderAddressTarget target, string coveRoot, string supplied, CancellationToken ct);

    /// <summary>
    /// What instance root <paramref name="coveRoot"/> itself agrees with on
    /// <paramref name="target"/>.
    /// </summary>
    /// <remarks>
    /// Uses the same held reading every folder under that root is addressed through.
    /// <see cref="AddressedFolder.InstancePath"/> carries the instance's own root, not a folder
    /// beneath it.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="coveRoot"/> is blank.</exception>
    Task<AddressedFolder> AgreedRootAsync(
        FolderAddressTarget target, string coveRoot, CancellationToken ct);
}
