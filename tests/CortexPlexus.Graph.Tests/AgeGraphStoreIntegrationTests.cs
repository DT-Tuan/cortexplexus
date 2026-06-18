using CortexPlexus.Core.Models;
using CortexPlexus.Graph;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace CortexPlexus.Graph.Tests;

/// <summary>
/// Integration tests cho AgeGraphStore với PostgreSQL + Apache AGE thật.
///
/// Phạm vi: TEST-PLAN.md #5, #6, #7, #8, #9, #11
/// (#10 schema initialization test tách file riêng; #12 performance test tách file riêng)
///
/// Lưu ý: mỗi test tự seed data + cleanup để tránh ảnh hưởng lẫn nhau.
/// </summary>
[Collection("Age")]
public class AgeGraphStoreIntegrationTests : IAsyncLifetime
{
    private readonly AgeFixture _fixture;
    private NpgsqlDataSource _dataSource = null!;
    private AgeGraphStore _store = null!;

    public AgeGraphStoreIntegrationTests(AgeFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        _dataSource = _fixture.CreateDataSource();
        _store = new AgeGraphStore(_dataSource, NullLogger<AgeGraphStore>.Instance);
        await _fixture.CleanAsync(_dataSource);
    }

    public async Task DisposeAsync()
    {
        await _dataSource.DisposeAsync();
    }

    // Helper: tạo method symbol cho test.
    private static MethodInfo MakeMethod(string fqn, string name, Guid repoId, string signature = "")
        => new()
        {
            Fqn = fqn,
            Name = name,
            Kind = "method",
            RepoId = repoId,
            FilePath = $"/repo/{name}.cs",
            StartLine = 10,
            EndLine = 20,
            Signature = string.IsNullOrEmpty(signature) ? $"{name}()" : signature
        };

    // Helper: seed một symbol vào bảng code_symbols (để tests dead code query, circular deps).
    private static async Task SeedCodeSymbolAsync(
        NpgsqlDataSource ds,
        string fqn,
        string name,
        string kind,
        Guid repoId,
        string? accessibility = "public",
        bool isTestMethod = false)
    {
        await using var conn = await ds.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO code_symbols (fqn, name, kind, repo_id, accessibility, file_path, start_line, end_line, signature, is_test_method)
            VALUES (@fqn, @name, @kind, @repo, @acc, @fp, 1, 10, @sig, @ist)
            ON CONFLICT (fqn) DO NOTHING
            """;
        cmd.Parameters.AddWithValue("@fqn", fqn);
        cmd.Parameters.AddWithValue("@name", name);
        cmd.Parameters.AddWithValue("@kind", kind);
        cmd.Parameters.AddWithValue("@repo", repoId);
        cmd.Parameters.AddWithValue("@acc", (object?)accessibility ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@fp", $"/repo/{name}.cs");
        cmd.Parameters.AddWithValue("@sig", $"{name}()");
        cmd.Parameters.AddWithValue("@ist", isTestMethod);
        await cmd.ExecuteNonQueryAsync();
    }

    // === #5: UpsertNodes_DuplicateFqn_IsIdempotent ===
    [Fact]
    public async Task UpsertNodes_DuplicateFqn_IsIdempotent()
    {
        // Mục đích: Insert cùng FQN 2 lần không tạo duplicate node trong graph.
        var repoId = await _fixture.SeedRepositoryAsync(_dataSource);
        var method = MakeMethod("App.Service.Process()", "Process", repoId);

        await _store.UpsertNodesAsync(new[] { method });
        await _store.UpsertNodesAsync(new[] { method }); // lần 2

        // Verify: chỉ có đúng 1 node với FQN đó trong graph.
        await using var conn = await _dataSource.OpenConnectionAsync();
        await using (var loadAge = conn.CreateCommand())
        {
            loadAge.CommandText = "LOAD 'age'; SET search_path = ag_catalog, \"$user\", public;";
            await loadAge.ExecuteNonQueryAsync();
        }

        // Dùng alias 'cnt' để tránh xung đột với PG keyword 'count'.
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT cnt::text FROM cypher('code_graph', $$
                MATCH (n) WHERE n.fqn = 'App.Service.Process()' RETURN count(n)
            $$) AS (cnt agtype);
            """;
        var result = (string?)await cmd.ExecuteScalarAsync();
        Assert.Equal("1", result?.Trim());
    }

    // === #6: UpsertEdges_InvalidFqn_WarnsAndContinues ===
    [Fact]
    public async Task UpsertEdges_InvalidFqn_StillProcessesOthers()
    {
        // Mục đích: Edge với FQN không tồn tại vẫn được xử lý (MERGE tạo node placeholder),
        // các edge khác không bị ảnh hưởng. AGE MERGE sẽ tạo node trống nếu chưa có —
        // nhưng quan trọng là không throw exception làm crash batch.
        var repoId = await _fixture.SeedRepositoryAsync(_dataSource);
        var a = MakeMethod("App.A.Method()", "Method", repoId);
        var b = MakeMethod("App.B.Method()", "Method", repoId);
        await _store.UpsertNodesAsync(new[] { a, b });

        // Mix: 1 edge giữa 2 node thật + 1 edge với FQN không tồn tại.
        var edges = new[]
        {
            new Relationship("App.A.Method()", "App.B.Method()", RelationshipType.Calls),
            new Relationship("Nonexistent.Ghost()", "App.B.Method()", RelationshipType.Calls),
        };

        // Không được throw exception.
        await _store.UpsertEdgesAsync(edges);

        // Verify: edge hợp lệ đã được tạo.
        var callers = await _store.QueryCallersAsync("App.B.Method()");
        Assert.Contains(callers, c => c.Fqn == "App.A.Method()");
    }

    // === #7: QueryCallers_Depth1_ReturnsDirectOnly ===
    [Fact]
    public async Task QueryCallers_Depth1_ReturnsDirectCallersOnly()
    {
        // Mục đích: depth=1 chỉ trả direct callers, không transitive.
        var repoId = await _fixture.SeedRepositoryAsync(_dataSource);
        var a = MakeMethod("App.A()", "A", repoId);
        var b = MakeMethod("App.B()", "B", repoId);
        var c = MakeMethod("App.C()", "C", repoId);
        await _store.UpsertNodesAsync(new[] { a, b, c });

        // A → B → C
        await _store.UpsertEdgesAsync(new[]
        {
            new Relationship("App.A()", "App.B()", RelationshipType.Calls),
            new Relationship("App.B()", "App.C()", RelationshipType.Calls),
        });

        var callers = await _store.QueryCallersAsync("App.C()", depth: 1);

        Assert.Contains(callers, r => r.Fqn == "App.B()");
        Assert.DoesNotContain(callers, r => r.Fqn == "App.A()"); // A là transitive, không phải direct
    }

    // === #8: QueryCallers_Depth3_ReturnsTransitive ===
    [Fact]
    public async Task QueryCallers_Depth3_ReturnsTransitiveCallers()
    {
        // Mục đích: depth=3 trả cả direct và transitive callers.
        var repoId = await _fixture.SeedRepositoryAsync(_dataSource);
        var a = MakeMethod("App.A()", "A", repoId);
        var b = MakeMethod("App.B()", "B", repoId);
        var c = MakeMethod("App.C()", "C", repoId);
        await _store.UpsertNodesAsync(new[] { a, b, c });

        // A → B → C
        await _store.UpsertEdgesAsync(new[]
        {
            new Relationship("App.A()", "App.B()", RelationshipType.Calls),
            new Relationship("App.B()", "App.C()", RelationshipType.Calls),
        });

        var callers = await _store.QueryCallersAsync("App.C()", depth: 3);

        Assert.Contains(callers, r => r.Fqn == "App.B()"); // direct
        Assert.Contains(callers, r => r.Fqn == "App.A()"); // transitive
    }

    // === #9: QueryCallees_CircularCall_NoCrash ===
    [Fact]
    public async Task QueryCallees_CircularCallGraph_DoesNotInfiniteLoop()
    {
        // Mục đích: A → B → A (circular) không gây infinite loop.
        var repoId = await _fixture.SeedRepositoryAsync(_dataSource);
        var a = MakeMethod("App.A()", "A", repoId);
        var b = MakeMethod("App.B()", "B", repoId);
        await _store.UpsertNodesAsync(new[] { a, b });

        await _store.UpsertEdgesAsync(new[]
        {
            new Relationship("App.A()", "App.B()", RelationshipType.Calls),
            new Relationship("App.B()", "App.A()", RelationshipType.Calls),
        });

        // Phải return trong thời gian hợp lý (AGE giới hạn depth bằng *1..N pattern).
        var task = _store.QueryCalleesAsync("App.A()", depth: 3);
        var completed = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(10)));

        Assert.Same(task, completed); // query xong trong 10s, không timeout
        var callees = await task;
        Assert.Contains(callees, r => r.Fqn == "App.B()");
    }

    // === #11: QueryDeadCode_ExcludesEntryPoints ===
    [Fact]
    public async Task QueryDeadCode_ExcludesConstructorsAndMain()
    {
        // Mục đích: QueryDeadCodeAsync không coi Main() và .ctor là dead code.
        // Dead code detection: public/internal method không có incoming Calls edge.
        var repoId = await _fixture.SeedRepositoryAsync(_dataSource);

        // Seed 3 methods: Main, .ctor, Unused — tất cả đều không có caller.
        await SeedCodeSymbolAsync(_dataSource, "App.Program.Main(string[])", "Main", "method", repoId);
        await SeedCodeSymbolAsync(_dataSource, "App.Service..ctor()", ".ctor", "method", repoId);
        await SeedCodeSymbolAsync(_dataSource, "App.Service.Unused()", "Unused", "method", repoId);

        var deadCode = await _store.QueryDeadCodeAsync(repoId);

        // Main và .ctor phải bị exclude theo spec của QueryDeadCode.
        Assert.DoesNotContain(deadCode, r => r.Name == "Main");
        Assert.DoesNotContain(deadCode, r => r.Name == ".ctor");
        // Unused phải bị detect.
        Assert.Contains(deadCode, r => r.Name == "Unused");
    }

    // === Multi-language: tree-sitter symbols (function kind, no access modifier) ===
    [Fact]
    public async Task QueryDeadCode_DetectsTreeSitterFunctionsWithoutAccessModifier()
    {
        // Python/TS functions have kind='function' and NULL accessibility (or 'export'
        // for TS). The original filter (kind='method' AND accessibility IN public/internal)
        // excluded ALL of them → dead-code was empty for non-.NET repos. Now they are
        // candidates, with leading-underscore as the "private" noise filter.
        var repoId = await _fixture.SeedRepositoryAsync(_dataSource);

        // Python: a used function (has a caller), an unused one, a private helper.
        await SeedCodeSymbolAsync(_dataSource, "app.svc.used_fn", "used_fn", "function", repoId, accessibility: null);
        await SeedCodeSymbolAsync(_dataSource, "app.svc.unused_fn", "unused_fn", "function", repoId, accessibility: null);
        await SeedCodeSymbolAsync(_dataSource, "app.svc._private_helper", "_private_helper", "function", repoId, accessibility: null);
        // TS: an exported but unused function.
        await SeedCodeSymbolAsync(_dataSource, "app/svc.ts::orphanExport", "orphanExport", "function", repoId, accessibility: "export");

        // Graph nodes + a single Calls edge into used_fn.
        await _store.UpsertNodesAsync(new CodeSymbol[]
        {
            new MethodInfo { Fqn = "app.svc.caller", Name = "caller", Kind = "function", RepoId = repoId, Signature = "caller()" },
            new MethodInfo { Fqn = "app.svc.used_fn", Name = "used_fn", Kind = "function", RepoId = repoId, Signature = "used_fn()" },
        });
        await _store.UpsertEdgesAsync(new[]
        {
            new Relationship("app.svc.caller", "app.svc.used_fn", RelationshipType.Calls),
        });

        var deadCode = await _store.QueryDeadCodeAsync(repoId);

        Assert.Contains(deadCode, r => r.Name == "unused_fn");      // function, no caller → dead
        Assert.Contains(deadCode, r => r.Name == "orphanExport");   // exported TS, no caller → dead
        Assert.DoesNotContain(deadCode, r => r.Name == "used_fn");  // has incoming Calls → not dead
        Assert.DoesNotContain(deadCode, r => r.Name == "_private_helper"); // underscore → filtered as private
    }

    // === R21 Fix #6: HTTP endpoint methods should be excluded from dead code ===
    [Fact]
    public async Task QueryDeadCode_ExcludesMethodsWithHandledByEdges()
    {
        // HTTP endpoint methods have no incoming Calls edges (invoked by the ASP.NET
        // runtime, not by other C# code) — before R21 they all showed up as dead code.
        // After R21 we exclude methods with any `HandledBy` incoming edge.
        var repoId = await _fixture.SeedRepositoryAsync(_dataSource);

        // Seed the production controller method + the endpoint node
        await SeedCodeSymbolAsync(_dataSource,
            "App.Controllers.ChatController.Completion()", "Completion", "method", repoId);
        await SeedCodeSymbolAsync(_dataSource,
            "API:POST:api/chat/completion", "api/chat/completion", "api_endpoint", repoId);
        await SeedCodeSymbolAsync(_dataSource,
            "App.Controllers.ChatController.Orphan()", "Orphan", "method", repoId);

        // Also upsert the nodes to the graph so edges work
        await _store.UpsertNodesAsync(new[]
        {
            MakeMethod("App.Controllers.ChatController.Completion()", "Completion", repoId),
            MakeMethod("App.Controllers.ChatController.Orphan()", "Orphan", repoId),
        });

        // Create a fake api_endpoint node too
        var endpoint = new ClassInfo
        {
            Fqn = "API:POST:api/chat/completion",
            Name = "api/chat/completion",
            Kind = "api_endpoint",
            RepoId = repoId,
            FilePath = "/repo/endpoint.cs",
            StartLine = 1,
            EndLine = 1
        };
        await _store.UpsertNodesAsync(new CodeSymbol[] { endpoint });

        // (api_endpoint)-[:HandledBy]->(method)
        await _store.UpsertEdgesAsync(new[]
        {
            new Relationship("API:POST:api/chat/completion",
                             "App.Controllers.ChatController.Completion()",
                             RelationshipType.HandledBy)
        });

        var deadCode = await _store.QueryDeadCodeAsync(repoId);

        // Completion has an incoming HandledBy edge → not dead
        Assert.DoesNotContain(deadCode, r => r.Name == "Completion");
        // Orphan has no incoming edges at all → still dead
        Assert.Contains(deadCode, r => r.Name == "Orphan");
    }

    // === R21 Fix #5: class hierarchy uses exact FQN match + graph traversal ===
    [Fact]
    public async Task QueryClassHierarchy_ExactFqnMatch_NoSiblingBleeding()
    {
        // Before R21, QueryClassHierarchyAsync used CONTAINS on the anchor FQN,
        // which matched every class containing the substring. Querying
        // "ChatController" would return every `*Controller` class because all of
        // them contain "Controller" in their FQN. After R21, use exact match.
        var repoId = await _fixture.SeedRepositoryAsync(_dataSource);

        var baseClass = MakeClass("App.Controllers.ControllerBase", "ControllerBase", repoId);
        var chat = MakeClass("App.Controllers.ChatController", "ChatController", repoId);
        var user = MakeClass("App.Controllers.UserController", "UserController", repoId);
        var admin = MakeClass("App.Controllers.AdminController", "AdminController", repoId);
        await _store.UpsertNodesAsync(new CodeSymbol[] { baseClass, chat, user, admin });

        // Only ChatController inherits from ControllerBase in this graph
        await _store.UpsertEdgesAsync(new[]
        {
            new Relationship("App.Controllers.ChatController",
                             "App.Controllers.ControllerBase",
                             RelationshipType.Inherits),
        });

        var hierarchy = await _store.QueryClassHierarchyAsync("App.Controllers.ChatController");

        // The hierarchy should contain ONLY ChatController and ControllerBase
        var fqns = hierarchy.Select(h => h.Fqn).ToList();
        Assert.Contains("App.Controllers.ChatController", fqns);
        Assert.Contains("App.Controllers.ControllerBase", fqns);
        // Must NOT contain sibling controllers
        Assert.DoesNotContain("App.Controllers.UserController", fqns);
        Assert.DoesNotContain("App.Controllers.AdminController", fqns);
    }

    // === R21 Fix #7: callers of a class should not include the class's own methods ===
    [Fact]
    public async Task QueryCallers_ClassFqn_DoesNotReturnOwnMethods()
    {
        // Before R21, QueryCallersAsync used CONTAINS on target.fqn, so querying
        // "App.Service" (class FQN) would match both the class node AND every
        // method whose FQN starts with "App.Service." — returning those methods
        // as "callers" of themselves. Fix: anchored match.
        var repoId = await _fixture.SeedRepositoryAsync(_dataSource);

        var service = MakeClass("App.Service", "Service", repoId);
        var method = MakeMethod("App.Service.Process()", "Process", repoId);
        var external = MakeMethod("App.Caller.Invoke()", "Invoke", repoId);
        await _store.UpsertNodesAsync(new CodeSymbol[] { service, method, external });

        // External caller → Service.Process
        await _store.UpsertEdgesAsync(new[]
        {
            new Relationship("App.Caller.Invoke()",
                             "App.Service.Process()",
                             RelationshipType.Calls),
        });

        // Query callers of the CLASS, not the method
        var callers = await _store.QueryCallersAsync("App.Service");

        // The exact-match fix means querying the class FQN returns no direct callers
        // (nothing calls "App.Service" — things call its METHODS).
        // Specifically, Service.Process should NOT appear as a caller.
        Assert.DoesNotContain(callers, c => c.Fqn == "App.Service.Process()");
    }

    // === R21 Fix #9/#10: kind is resolved from relational table, not FQN heuristic ===
    [Fact]
    public async Task QueryCallers_Result_Kind_IsStoredKind_NotHeuristic()
    {
        var repoId = await _fixture.SeedRepositoryAsync(_dataSource);

        // Seed the caller in BOTH relational + graph so kind lookup finds it
        await SeedCodeSymbolAsync(_dataSource, "App.Caller.Invoke()", "Invoke", "method", repoId);
        await SeedCodeSymbolAsync(_dataSource, "App.Target.Method()", "Method", "method", repoId);

        var caller = MakeMethod("App.Caller.Invoke()", "Invoke", repoId);
        var target = MakeMethod("App.Target.Method()", "Method", repoId);
        await _store.UpsertNodesAsync(new[] { caller, target });
        await _store.UpsertEdgesAsync(new[]
        {
            new Relationship("App.Caller.Invoke()",
                             "App.Target.Method()",
                             RelationshipType.Calls),
        });

        var callers = await _store.QueryCallersAsync("App.Target.Method()");

        var result = callers.FirstOrDefault(c => c.Fqn == "App.Caller.Invoke()");
        Assert.NotNull(result);
        // Before R21: InferKind saw "(" and returned "method" — still works in this
        // case but only by accident. The new code resolves from the stored table.
        Assert.Equal("method", result.Kind);
    }

    // === R23 Fix N1: dead code excludes test methods ===
    [Fact]
    public async Task QueryDeadCode_ExcludesTestMethods()
    {
        // Test methods (xUnit [Fact], NUnit [Test], etc.) are invoked by the test
        // runner via reflection — not by other C# code — so they have no incoming
        // Calls edges. Before R23 they all showed up as dead code on CortexFlow.
        // After R23 they should be filtered out.
        var repoId = await _fixture.SeedRepositoryAsync(_dataSource);

        // Seed three methods: a regular method, a test method, and another regular
        // method that has a caller (so it's not dead).
        await SeedCodeSymbolAsync(_dataSource, "App.Service.UnusedHelper()", "UnusedHelper", "method", repoId);
        await SeedCodeSymbolAsync(_dataSource, "App.Tests.MyTests.SimpleTest()", "SimpleTest", "method", repoId,
            isTestMethod: true);
        await SeedCodeSymbolAsync(_dataSource, "App.Service.CalledMethod()", "CalledMethod", "method", repoId);
        await SeedCodeSymbolAsync(_dataSource, "App.Service.Caller()", "Caller", "method", repoId);

        // Build graph nodes + Caller → CalledMethod edge so CalledMethod isn't dead
        await _store.UpsertNodesAsync(new[]
        {
            MakeMethod("App.Service.UnusedHelper()", "UnusedHelper", repoId),
            MakeMethod("App.Tests.MyTests.SimpleTest()", "SimpleTest", repoId),
            MakeMethod("App.Service.CalledMethod()", "CalledMethod", repoId),
            MakeMethod("App.Service.Caller()", "Caller", repoId),
        });
        await _store.UpsertEdgesAsync(new[]
        {
            new Relationship("App.Service.Caller()",
                             "App.Service.CalledMethod()",
                             RelationshipType.Calls),
        });

        var deadCode = await _store.QueryDeadCodeAsync(repoId);

        // The unused helper IS dead code
        Assert.Contains(deadCode, r => r.Name == "UnusedHelper");
        // The test method must NOT show up — even though no edge points to it
        Assert.DoesNotContain(deadCode, r => r.Name == "SimpleTest");
        // The called method has an incoming Calls edge → not dead
        Assert.DoesNotContain(deadCode, r => r.Name == "CalledMethod");
    }

    // === R23 Fix N2: get_api_endpoints moduleName uses path-segment match ===
    [Fact]
    public async Task QueryApiEndpoints_ModuleName_MatchesControllerFile_NotRouteSubstring()
    {
        // Before R23: filter `name ILIKE '%Chat%'` matched routes like `api/cea/chat`
        // (CeaController.cs) because the route name contained "chat". After R23: only
        // endpoints whose file path is `...\ChatController.cs` (or sits inside a
        // `Chat` folder) match.
        var repoId = await _fixture.SeedRepositoryAsync(_dataSource);

        // Seed endpoints from two different controllers with overlapping route names
        await SeedApiEndpointAsync(_dataSource,
            fqn: "API:POST:api/Chat/completion",
            name: "api/Chat/completion",
            filePath: "03.Backend\\App.API\\Controllers\\ChatController.cs",
            repoId: repoId);
        await SeedApiEndpointAsync(_dataSource,
            fqn: "API:POST:api/cea/chat",
            name: "api/cea/chat",
            filePath: "03.Backend\\App.API\\Controllers\\CeaController.cs",
            repoId: repoId);

        var results = await _store.QueryApiEndpointsAsync(moduleName: "Chat");

        // Should ONLY include the ChatController endpoint, not CeaController's
        Assert.Single(results);
        Assert.Equal("api/Chat/completion", results[0].Name);
        Assert.DoesNotContain(results, r => r.Name == "api/cea/chat");
    }

    [Fact]
    public async Task QueryApiEndpoints_ModuleName_MatchesFolderSegment()
    {
        // Some projects organize endpoints by folder (Auth/, Admin/) instead of
        // ControllerSuffix. The `\Module\` path-segment fallback handles that.
        var repoId = await _fixture.SeedRepositoryAsync(_dataSource);

        await SeedApiEndpointAsync(_dataSource,
            fqn: "API:POST:auth/login",
            name: "auth/login",
            filePath: "03.Backend\\App.API\\Auth\\LoginEndpoint.cs",
            repoId: repoId);
        await SeedApiEndpointAsync(_dataSource,
            fqn: "API:POST:users/create",
            name: "users/create",
            filePath: "03.Backend\\App.API\\Users\\UserEndpoint.cs",
            repoId: repoId);

        var results = await _store.QueryApiEndpointsAsync(moduleName: "Auth");

        Assert.Single(results);
        Assert.Equal("auth/login", results[0].Name);
    }

    // === R25 R24-1: ExecuteCypherQuery dedupes by FQN ===
    [Fact]
    public async Task QueryCallees_DiamondCallGraph_NoDuplicates()
    {
        // Diamond pattern: A → B → D and A → C → D. With *1..2 traversal, the
        // direct query returns D twice (once via B, once via C). Before R25 the
        // MCP layer showed both rows; R25 dedupes by FQN in ExecuteCypherQuery.
        var repoId = await _fixture.SeedRepositoryAsync(_dataSource);
        var a = MakeMethod("App.A()", "A", repoId);
        var b = MakeMethod("App.B()", "B", repoId);
        var c = MakeMethod("App.C()", "C", repoId);
        var d = MakeMethod("App.D()", "D", repoId);
        await _store.UpsertNodesAsync(new[] { a, b, c, d });

        // Diamond: A calls B and C, both call D
        await _store.UpsertEdgesAsync(new[]
        {
            new Relationship("App.A()", "App.B()", RelationshipType.Calls),
            new Relationship("App.A()", "App.C()", RelationshipType.Calls),
            new Relationship("App.B()", "App.D()", RelationshipType.Calls),
            new Relationship("App.C()", "App.D()", RelationshipType.Calls),
        });

        var callees = await _store.QueryCalleesAsync("App.A()", depth: 2);

        // Without dedup: B, C, D, D (D appears twice — once via A→B→D, once via A→C→D)
        // With R25 dedup: B, C, D (D only once)
        var dCount = callees.Count(c => c.Fqn == "App.D()");
        Assert.Equal(1, dCount);
    }

    private static async Task SeedApiEndpointAsync(
        NpgsqlDataSource ds, string fqn, string name, string filePath, Guid repoId)
    {
        await using var conn = await ds.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO code_symbols (fqn, name, kind, repo_id, accessibility, file_path, start_line, end_line, signature)
            VALUES (@fqn, @name, 'api_endpoint', @repo, 'public', @fp, 1, 10, @fqn)
            ON CONFLICT (fqn) DO NOTHING
            """;
        cmd.Parameters.AddWithValue("@fqn", fqn);
        cmd.Parameters.AddWithValue("@name", name);
        cmd.Parameters.AddWithValue("@repo", repoId);
        cmd.Parameters.AddWithValue("@fp", filePath);
        await cmd.ExecuteNonQueryAsync();
    }

    // Helper for class symbols (new for R21 hierarchy test)
    private static ClassInfo MakeClass(string fqn, string name, Guid repoId) => new()
    {
        Fqn = fqn,
        Name = name,
        Kind = "class",
        RepoId = repoId,
        FilePath = $"/repo/{name}.cs",
        StartLine = 1,
        EndLine = 100,
    };
}
