using Renamer.Options;

namespace Renamer.Contracts;

/// <summary>The saved rename settings, with what a caller must know before offering a Save.</summary>
/// <remarks>
/// The options travel as the model rather than as a projection of it: this is the one response whose
/// purpose is to hand back exactly what a save takes, and a projection would be a second declaration of
/// the same field set. <c>Options</c> is the defaults when nothing is saved yet, and also when the
/// stored blob could not be parsed, which <c>Unreadable</c> is what tells apart. Each pending flag marks
/// a stored shape the one-time conversion has not resolved, which the current model reads as an empty
/// or blank rule; the save endpoint refuses while either is true, because nothing else holds a copy of
/// what a save would overwrite.
/// </remarks>
public sealed record OptionsView(
    RenamerOptions Options,
    bool PendingNameMigration,
    bool PendingDestinationMigration,
    bool Unreadable);
