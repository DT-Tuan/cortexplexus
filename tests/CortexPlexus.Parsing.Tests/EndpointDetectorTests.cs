using CortexPlexus.Core.Models;
using CortexPlexus.Parsing.TreeSitter;

namespace CortexPlexus.Parsing.Tests;

/// <summary>
/// ADR-016 C2: Python HTTP route detection (FastAPI / Flask) → api_endpoint nodes + HandledBy edges.
/// Exercised end-to-end through <see cref="PythonExtractor"/> so handler-FQN coupling is real.
/// </summary>
public sealed class EndpointDetectorTests
{
    private static (List<CodeSymbol> Symbols, List<Relationship> Relationships) ParsePython(string code)
    {
        var lang = new global::TreeSitter.Language("python");
        using var parser = new global::TreeSitter.Parser(lang);
        using var tree = parser.Parse(code);
        var extractor = new PythonExtractor(code, "test.py", "test");
        return extractor.Extract(tree.RootNode);
    }

    private static List<ApiEndpointInfo> Endpoints(List<CodeSymbol> symbols) =>
        symbols.OfType<ApiEndpointInfo>().ToList();

    [Fact]
    public void FastApi_GetDecorator_EmitsEndpointNodeAndHandledByEdge()
    {
        var (symbols, rels) = ParsePython("""
        @app.get("/users")
        async def list_users():
            return []
        """);

        var ep = Assert.Single(Endpoints(symbols));
        Assert.Equal("API:GET:/users", ep.Fqn);
        Assert.Equal("api_endpoint", ep.Kind);
        Assert.Equal("GET", ep.HttpMethod);
        Assert.Equal("/users", ep.RouteTemplate);
        Assert.Equal("test.list_users", ep.HandlerMethodFqn);

        var edge = Assert.Single(rels, r => r.Type == RelationshipType.HandledBy);
        Assert.Equal("API:GET:/users", edge.FromFqn);
        Assert.Equal("test.list_users", edge.ToFqn);
        // The handler the edge points at is a real symbol in the same parse.
        Assert.Contains(symbols, s => s.Fqn == "test.list_users" && s.Kind == "function");
    }

    [Fact]
    public void FastApi_RouterPostWithPathParam_PreservesRouteTemplate()
    {
        var (symbols, _) = ParsePython("""
        @router.post("/items/{item_id}")
        def create_item(item_id: int):
            ...
        """);

        var ep = Assert.Single(Endpoints(symbols));
        Assert.Equal("API:POST:/items/{item_id}", ep.Fqn);
        Assert.Equal("POST", ep.HttpMethod);
    }

    [Fact]
    public void Flask_RouteWithoutMethods_DefaultsToGet()
    {
        var (symbols, _) = ParsePython("""
        @app.route("/health")
        def health():
            return "ok"
        """);

        var ep = Assert.Single(Endpoints(symbols));
        Assert.Equal("API:GET:/health", ep.Fqn);
        Assert.Equal("GET", ep.HttpMethod);
    }

    [Fact]
    public void Flask_RouteWithMethodsList_EmitsOneEndpointPerMethod()
    {
        var (symbols, rels) = ParsePython("""
        @app.route("/submit", methods=["GET", "POST"])
        def submit():
            ...
        """);

        var eps = Endpoints(symbols);
        Assert.Equal(2, eps.Count);
        Assert.Contains(eps, e => e.Fqn == "API:GET:/submit");
        Assert.Contains(eps, e => e.Fqn == "API:POST:/submit");
        Assert.Equal(2, rels.Count(r => r.Type == RelationshipType.HandledBy));
    }

    [Fact]
    public void FastApi_WebSocketDecorator_UsesWsMethod()
    {
        var (symbols, _) = ParsePython("""
        @app.websocket("/ws")
        async def socket():
            ...
        """);

        var ep = Assert.Single(Endpoints(symbols));
        Assert.Equal("API:WS:/ws", ep.Fqn);
        Assert.Equal("WS", ep.HttpMethod);
    }

    [Fact]
    public void MultipleRouteDecorators_OnOneHandler_EmitDistinctEndpoints()
    {
        var (symbols, _) = ParsePython("""
        @app.get("/a")
        @app.get("/b")
        def multi():
            ...
        """);

        var eps = Endpoints(symbols);
        Assert.Equal(2, eps.Count);
        Assert.Contains(eps, e => e.Fqn == "API:GET:/a");
        Assert.Contains(eps, e => e.Fqn == "API:GET:/b");
        Assert.All(eps, e => Assert.Equal("test.multi", e.HandlerMethodFqn));
    }

    [Fact]
    public void NonRouteDecorators_ProduceNoEndpoints()
    {
        var (symbols, _) = ParsePython("""
        import functools

        @functools.lru_cache
        def cached():
            ...

        @pytest.fixture
        def client():
            ...
        """);

        Assert.Empty(Endpoints(symbols));
    }

    [Fact]
    public void DynamicRoute_NonStringLiteral_IsSkipped()
    {
        var (symbols, _) = ParsePython("""
        @app.get(ROUTE_CONST)
        def dynamic():
            ...
        """);

        Assert.Empty(Endpoints(symbols));
    }

    [Fact]
    public void PropertyDecorator_StillNotAnEndpoint_AndRemainsProperty()
    {
        var (symbols, _) = ParsePython("""
        class Service:
            @property
            def name(self):
                return self._name
        """);

        Assert.Empty(Endpoints(symbols));
        Assert.Contains(symbols, s => s.Kind == "property" && s.Fqn == "test.Service.name");
    }

    [Fact]
    public void ClassMethodRoute_HandlerFqnIncludesClass()
    {
        // FastAPI class-based / router-on-method style: handler FQN must carry the class.
        var (symbols, _) = ParsePython("""
        class Views:
            @router.get("/me")
            async def me(self):
                ...
        """);

        var ep = Assert.Single(Endpoints(symbols));
        Assert.Equal("API:GET:/me", ep.Fqn);
        Assert.Equal("test.Views.me", ep.HandlerMethodFqn);
    }

    // === TypeScript: NestJS controller decorators + Express route calls (C2/2) ===

    private static (List<CodeSymbol> Symbols, List<Relationship> Relationships) ParseTypeScript(string code)
    {
        var lang = new global::TreeSitter.Language("typescript");
        using var parser = new global::TreeSitter.Parser(lang);
        using var tree = parser.Parse(code);
        var extractor = new TypeScriptExtractor(code, "test.ts", "test.ts");
        return extractor.Extract(tree.RootNode);
    }

    [Fact]
    public void Nest_ControllerPrefixPlusGet_CombinesRouteAndLinksHandler()
    {
        var (symbols, rels) = ParseTypeScript("""
        @Controller("cats")
        export class CatsController {
          @Get(":id")
          findOne(id: string) { return id; }
        }
        """);

        var ep = Assert.Single(Endpoints(symbols));
        Assert.Equal("API:GET:/cats/:id", ep.Fqn);
        Assert.Equal("GET", ep.HttpMethod);
        Assert.Contains(rels, r => r.Type == RelationshipType.HandledBy
                                && r.FromFqn == "API:GET:/cats/:id"
                                && r.ToFqn.EndsWith(".findOne"));
    }

    [Fact]
    public void Nest_PostNoArg_UsesControllerRoot()
    {
        var (symbols, _) = ParseTypeScript("""
        @Controller("cats")
        export class CatsController {
          @Post()
          create() {}
        }
        """);

        var ep = Assert.Single(Endpoints(symbols));
        Assert.Equal("API:POST:/cats", ep.Fqn);
    }

    [Fact]
    public void Nest_EmptyControllerPlusRoutedGet_UsesMethodRoute()
    {
        var (symbols, _) = ParseTypeScript("""
        @Controller()
        export class HealthController {
          @Get("health")
          check() {}
        }
        """);

        var ep = Assert.Single(Endpoints(symbols));
        Assert.Equal("API:GET:/health", ep.Fqn);
    }

    [Fact]
    public void NonControllerClass_GetMethodName_IsNotAnEndpoint()
    {
        // A plain class with a method literally named "get" must not become a route.
        var (symbols, _) = ParseTypeScript("""
        export class Cache {
          get(key: string) { return key; }
        }
        """);

        Assert.Empty(Endpoints(symbols));
    }

    [Fact]
    public void Express_AppGetWithHandler_EmitsEndpoint()
    {
        var (symbols, _) = ParseTypeScript("""
        const app = express();
        app.get("/users", (req, res) => res.send([]));
        router.post("/users/:id", handler);
        """);

        var eps = Endpoints(symbols);
        Assert.Contains(eps, e => e.Fqn == "API:GET:/users");
        Assert.Contains(eps, e => e.Fqn == "API:POST:/users/:id");
    }

    [Fact]
    public void Express_MapGetWithSingleNonRouteArg_IsNotAnEndpoint()
    {
        // map.get("port") and app.get("setting") — getter calls, not routes.
        var (symbols, _) = ParseTypeScript("""
        const v = config.get("port");
        const s = app.get("trust proxy");
        """);

        Assert.Empty(Endpoints(symbols));
    }

    [Fact]
    public void Express_NonStringRoute_IsNotAnEndpoint()
    {
        var (symbols, _) = ParseTypeScript("""
        app.get(routePath, handler);
        """);

        Assert.Empty(Endpoints(symbols));
    }
}
