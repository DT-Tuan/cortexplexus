using CortexPlexus.Core.Models;

namespace CortexPlexus.Core.Abstractions;

public interface IGraphStore
{
    Task InitializeSchemaAsync(CancellationToken ct = default);
    Task UpsertNodesAsync(IEnumerable<CodeSymbol> symbols, CancellationToken ct = default);
    Task UpsertEdgesAsync(IEnumerable<Relationship> relationships, CancellationToken ct = default);
    Task<IReadOnlyList<SearchResult>> QueryCallersAsync(string methodFqn, int depth = 1, CancellationToken ct = default);
    Task<IReadOnlyList<SearchResult>> QueryCalleesAsync(string methodFqn, int depth = 1, CancellationToken ct = default);
    Task<IReadOnlyList<SearchResult>> QueryDependenciesAsync(string fqn, int depth = 1, CancellationToken ct = default);
    Task<IReadOnlyList<SearchResult>> QueryImplementationsAsync(string interfaceFqn, CancellationToken ct = default);
    Task<IReadOnlyList<SearchResult>> QueryClassHierarchyAsync(string classFqn, CancellationToken ct = default);
    Task<IReadOnlyList<SearchResult>> QueryReferencedByAsync(string fqn, int depth = 1, CancellationToken ct = default);
    Task DeleteByRepoAsync(Guid repoId, CancellationToken ct = default);

    // Phase 3: .NET Deep Analysis queries
    Task<IReadOnlyList<SearchResult>> QueryDiRegistrationsAsync(string? serviceTypeFqn = null, Guid? repoId = null, CancellationToken ct = default);
    Task<IReadOnlyList<SearchResult>> QueryEntityMappingsAsync(string? entityName = null, Guid? repoId = null, CancellationToken ct = default);
    Task<IReadOnlyList<SearchResult>> QueryApiEndpointsAsync(string? moduleName = null, Guid? repoId = null, CancellationToken ct = default);
    Task<IReadOnlyList<SearchResult>> QueryDataFlowAsync(string endpointRoute, CancellationToken ct = default);

    // P1a: Test-to-Code mapping
    Task<IReadOnlyList<SearchResult>> QueryTestCoverageAsync(string methodFqn, CancellationToken ct = default);

    // P1b: Configuration Mapping
    Task<IReadOnlyList<SearchResult>> QueryConfigUsageAsync(string? configKey = null, Guid? repoId = null, CancellationToken ct = default);

    // P2d: Dead Code Detection
    Task<IReadOnlyList<SearchResult>> QueryDeadCodeAsync(Guid repoId, CancellationToken ct = default);

    // P4c: Circular Dependency Detection
    Task<IReadOnlyList<IReadOnlyList<string>>> QueryCircularDependenciesAsync(Guid repoId, CancellationToken ct = default);

    // Phase 6: Graph visualization
    Task<GraphOverview> GetGraphOverviewAsync(Guid repoId, int nodeLimit = 500, IReadOnlyList<string>? kindFilter = null, CancellationToken ct = default);
    Task<GraphOverview> GetNodeNeighborsAsync(string fqn, int depth = 1, CancellationToken ct = default);

    // R22 Fix #3: lookup methods belonging to a containing type for "did you mean" hints.
    // Returns up to <paramref name="limit"/> method symbols whose ContainingTypeFqn equals
    // the given class FQN, ordered by name. Used by GetCallers/GetCallees when the user
    // mistakenly passes a class FQN instead of a method FQN.
    Task<IReadOnlyList<SearchResult>> LookupMethodsByContainingTypeAsync(
        string classFqn, int limit = 10, CancellationToken ct = default);
}
