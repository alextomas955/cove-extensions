using WhisparrSync.Jobs;
using WhisparrSync.Monitoring;

namespace WhisparrSync.Missing;

// What the add behind the catalogue tab reaches past the connection itself. Bundled the way
// WhisparrAccess and BackgroundWork are, so the route lambda names one parameter for the three.
internal sealed record MissingEntryAccess(
    IEntityIdentityPort Identities, InstanceCatalogueCache Cache, BackgroundWork Work);
