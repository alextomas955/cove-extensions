using Renamer.Options;
using Renamer.Planner;

namespace Renamer.Tests.TestSupport;

public static class PlannerExtensions
{
    private static readonly RouteLookups NoRules = new(
        new Dictionary<int, Destination>(),
        new Dictionary<int, Destination>(),
        new Dictionary<string, Destination>(),
        []);

    // Plans with no routing rules, so every file takes the default destination.
    public static Task<RenamerPlan> PlanAsync(
        this RenamerPlanner planner, RenamerFileKind kind, int entityId, RenamerOptions options,
        CancellationToken ct)
        => planner.PlanAsync(kind, entityId, options, NoRules, ct);
}
