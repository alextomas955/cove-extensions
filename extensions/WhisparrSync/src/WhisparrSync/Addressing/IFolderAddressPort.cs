using WhisparrSync.Contracts;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Addressing;

/// <summary>Which instance a folder is being addressed on, and how to ask it.</summary>
/// <param name="Generation">The lineage connected, which the held agreement is keyed by.</param>
/// <param name="BaseAddress">The instance.</param>
/// <param name="ApiKey">The key presented to it.</param>
/// <param name="Filesystem">The role the candidates are asked about through.</param>
public sealed record FolderAddressTarget(
    WhisparrGeneration Generation,
    Uri BaseAddress,
    string ApiKey,
    IWhisparrInstanceFilesystemReading Filesystem);

/// <summary>How one folder is spelled on the connected instance, or why it is not.</summary>
/// <param name="InstancePath">
/// The folder as the instance spells it, or null beside a non-null <paramref name="Refusal"/>.
/// </param>
/// <param name="Refusal">Why the folder has no instance spelling, or null when it has one.</param>
/// <param name="CoveRoot">
/// The library root the folder sits under, or blank where it sits under none. Carried either way, so
/// a run can report one line per root rather than one per folder.
/// </param>
/// <param name="Tried">Every candidate the instance was asked about while establishing the root.</param>
public sealed record AddressedFolder(
    string? InstancePath,
    FolderAgreementRefusal? Refusal,
    string CoveRoot,
    IReadOnlyList<string> Tried);

/// <summary>Turns a folder the library names into the path the instance can open.</summary>
/// <remarks>
/// One sample file and one set of probes per library root, held and reused by every folder under it,
/// so what a run costs grows with the root count rather than with the folder count.
/// <para>
/// A folder that cannot be addressed is answered rather than guessed at. Handing an instance the
/// library's own spelling produces a legitimately empty listing and a run reporting a clean zero,
/// which is the silent failure this seam exists to remove.
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
    /// The same probe and the same verdict a run takes, so a path accepted here cannot be one a run
    /// then refuses. A reading that resolved is held for <paramref name="coveRoot"/> on the spot, so
    /// a mapping saved is in force for the next run rather than after the previous reading expires;
    /// one that did not is held nowhere, because nothing about the root was settled by it.
    /// </remarks>
    /// <param name="target">Which instance to ask.</param>
    /// <param name="coveRoot">The library root being established.</param>
    /// <param name="supplied">Where the caller states the instance holds it.</param>
    /// <param name="ct">Cancels the reads.</param>
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
    /// The same held reading every folder under that root is addressed through, so asking once per
    /// entity still costs one establishment per root. <see cref="AddressedFolder.InstancePath"/>
    /// carries the instance's own root rather than a folder beneath it.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="coveRoot"/> is blank.</exception>
    Task<AddressedFolder> AgreedRootAsync(
        FolderAddressTarget target, string coveRoot, CancellationToken ct);
}
