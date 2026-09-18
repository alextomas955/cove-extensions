namespace Renamer.Contracts;

/// <summary>The routing-rule keys whose entity Cove no longer holds.</summary>
/// <remarks>
/// A rule keys on an entity's stable id, so a merge or a delete removes that id and the rule then
/// matches nothing. The host's reference control renders an id it cannot resolve as a permanent
/// "Loading", so this view lets the panel say the entity is gone. It is answered from the database as
/// System, because a failed lookup in the browser cannot tell a deletion from a permission the viewer
/// lacks or from a dropped request. Both lists are bounded by how many rules the user wrote: the query
/// asks about exactly the ids the rules name.
/// </remarks>
public sealed record OrphanedRulesView(
    IReadOnlyList<int> Studios,
    IReadOnlyList<int> Tags);
