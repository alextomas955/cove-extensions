namespace Renamer.Contracts;

/// <summary>
/// The routing-rule keys whose entity Cove no longer holds.
/// </summary>
/// <remarks>
/// A rule keys on an entity's stable id so a rename cannot break it. The trade is that a MERGE or a
/// delete removes that id, and the rule then matches nothing — which is correct routing but reads as
/// nothing at all in the panel, because the host's reference control renders an id it cannot resolve as
/// a permanent "Loading…". This is what lets the panel say the entity is gone instead.
/// <para>
/// Answered from the database as System deliberately. The alternative — inferring absence from a failed
/// lookup in the browser — cannot tell "deleted" from "this viewer may not read it" or from a dropped
/// request, and acting on that confusion would mislabel a rule that is perfectly valid.
/// </para>
/// <para>
/// Both lists are bounded by how many rules the user wrote, never by library size: the query asks about
/// exactly the ids the rules name.
/// </para>
/// </remarks>
/// <param name="Studios">Ids in the per-studio destination map that no studio answers to.</param>
/// <param name="Tags">Ids in the per-tag destination map that no tag answers to.</param>
public sealed record OrphanedRulesView(
    IReadOnlyList<int> Studios,
    IReadOnlyList<int> Tags);
