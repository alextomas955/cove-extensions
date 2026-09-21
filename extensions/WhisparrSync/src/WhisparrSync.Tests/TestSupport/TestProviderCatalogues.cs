using WhisparrSync.Providers;

namespace WhisparrSync.Tests.TestSupport;

internal static class TestProviderCatalogues
{
    // Production reads the stored choice. A case that names its catalogue outright answers that one
    // whatever it is asked with, so what the case drives is the catalogue it supplied.
    internal static ProviderCatalogueSource Naming(IProviderCatalogue catalogue)
        => _ => Task.FromResult(catalogue);
}
