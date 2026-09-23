namespace Renamer.Engine;

/// <summary>One performer of a media item in the renamer's own vocabulary.</summary>
/// <remarks>
/// The <c>$performers</c> token renders <see cref="Name"/>; <see cref="Id"/>, <see cref="Favorite"/>
/// and <see cref="Gender"/> drive the ordering and gender filtering applied before the max-count
/// limit. <see cref="Gender"/> is the Cove gender enum's string name, converted at the port
/// boundary, or <c>null</c> when unset.
/// </remarks>
public sealed record RenamerPerformer(int Id, string Name, bool Favorite, string? Gender);
