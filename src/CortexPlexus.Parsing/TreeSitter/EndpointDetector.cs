using CortexPlexus.Core.Models;

namespace CortexPlexus.Parsing.TreeSitter;

/// <summary>
/// Detects HTTP API route declarations across languages and emits <see cref="ApiEndpointInfo"/>
/// nodes (FQN <c>API:&lt;METHOD&gt;:&lt;route&gt;</c>, matching the Roslyn ASP.NET emitter) plus a
/// <see cref="RelationshipType.HandledBy"/> edge to the handler symbol. Lighting up the existing
/// <c>get_api_endpoints</c> tool for non-.NET stacks (ADR-016 C2).
///
/// Python (this increment):
/// - FastAPI / APIRouter: <c>@app.get("/x")</c>, <c>@router.post("/x")</c>, <c>@app.websocket("/ws")</c>,
///   <c>@app.api_route("/x", methods=[...])</c>
/// - Flask / Blueprint:   <c>@app.route("/x")</c> (default GET), <c>@bp.route("/x", methods=["GET","POST"])</c>
/// </summary>
internal static class EndpointDetector
{
    /// <summary>Maps a decorator attribute name to its HTTP verb (FastAPI/router style).</summary>
    private static readonly Dictionary<string, string> VerbDecorators = new(StringComparer.OrdinalIgnoreCase)
    {
        ["get"] = "GET", ["post"] = "POST", ["put"] = "PUT", ["delete"] = "DELETE",
        ["patch"] = "PATCH", ["head"] = "HEAD", ["options"] = "OPTIONS", ["trace"] = "TRACE",
    };

    /// <summary>
    /// Inspect the decorators of a Python <c>decorated_definition</c> for route declarations.
    /// Called with the handler's already-resolved FQN so the HandledBy edge target is exact.
    /// </summary>
    public static (List<ApiEndpointInfo> Endpoints, List<Relationship> Relationships) DetectPythonRoutes(
        global::TreeSitter.Node decoratedDefinition, string handlerFqn, string? filePath)
    {
        var endpoints = new List<ApiEndpointInfo>();
        var edges = new List<Relationship>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var child in decoratedDefinition.Children)
        {
            if (child.Type != "decorator") continue;

            // A route decorator is always a call: @app.get("/x") / @app.route("/x", methods=[...]).
            var call = child.Children.FirstOrDefault(c => c.Type == "call");
            if (call is null) continue;

            var func = call.GetChildForField("function");
            if (func is null || func.Type != "attribute") continue;

            var attrName = func.GetChildForField("attribute")?.Text;
            if (string.IsNullOrEmpty(attrName)) continue;

            var args = call.GetChildForField("arguments");
            if (args is null) continue;

            var route = FirstStringArgument(args);
            if (route is null) continue;   // dynamic route → can't resolve, skip

            var methods = ResolveMethods(attrName, args);
            if (methods.Count == 0) continue;   // not an HTTP route decorator

            var startLine = (int)decoratedDefinition.StartPosition.Row + 1;
            var endLine = (int)decoratedDefinition.EndPosition.Row + 1;

            foreach (var method in methods)
            {
                var fqn = $"API:{method}:{route}";
                if (!seen.Add(fqn)) continue;

                endpoints.Add(new ApiEndpointInfo
                {
                    Fqn = fqn,
                    Name = $"{method} {route}",
                    Kind = "api_endpoint",
                    FilePath = filePath,
                    StartLine = startLine,
                    EndLine = endLine,
                    HttpMethod = method,
                    RouteTemplate = route,
                    HandlerMethodFqn = handlerFqn,
                });
                edges.Add(new Relationship(fqn, handlerFqn, RelationshipType.HandledBy));
            }
        }

        return (endpoints, edges);
    }

    /// <summary>
    /// Determine the HTTP method(s) for a decorator: a verb decorator yields one method; Flask's
    /// <c>route</c> / FastAPI's <c>api_route</c> read the <c>methods=[...]</c> kwarg (default GET);
    /// <c>websocket</c> yields WS. A non-route decorator yields an empty list.
    /// </summary>
    private static List<string> ResolveMethods(string attrName, global::TreeSitter.Node args)
    {
        if (VerbDecorators.TryGetValue(attrName, out var verb))
            return [verb];

        if (attrName.Equals("websocket", StringComparison.OrdinalIgnoreCase))
            return ["WS"];

        if (attrName.Equals("route", StringComparison.OrdinalIgnoreCase) ||
            attrName.Equals("api_route", StringComparison.OrdinalIgnoreCase))
        {
            var methods = ReadMethodsKwarg(args);
            return methods.Count > 0 ? methods : ["GET"];   // Flask default is GET
        }

        return [];
    }

    /// <summary>Reads <c>methods=["GET", "POST"]</c> from a call's argument list, uppercased.</summary>
    private static List<string> ReadMethodsKwarg(global::TreeSitter.Node args)
    {
        var result = new List<string>();
        foreach (var arg in args.Children)
        {
            if (arg.Type != "keyword_argument") continue;
            if (arg.GetChildForField("name")?.Text != "methods") continue;

            var value = arg.GetChildForField("value");
            if (value is null) continue;

            foreach (var item in value.Children)
            {
                var s = StringLiteralValue(item);
                if (s is not null) result.Add(s.ToUpperInvariant());
            }
        }
        return result;
    }

    /// <summary>First positional string argument of an argument list (the route template).</summary>
    private static string? FirstStringArgument(global::TreeSitter.Node args)
    {
        foreach (var child in args.Children)
        {
            if (!child.IsNamed) continue;
            if (child.Type == "keyword_argument") continue;   // skip methods=..., etc.
            var s = StringLiteralValue(child);
            if (s is not null) return s;
            // First positional arg isn't a string literal (e.g. a variable) → unresolvable.
            return null;
        }
        return null;
    }

    /// <summary>Extracts the text of a Python string literal node, stripping quotes/prefixes.</summary>
    private static string? StringLiteralValue(global::TreeSitter.Node node)
    {
        if (node.Type != "string") return null;

        // Python tree-sitter wraps the body as string > string_content.
        var content = node.Children.FirstOrDefault(c => c.Type == "string_content");
        if (content is not null) return content.Text;

        var text = node.Text ?? "";
        var start = text.IndexOf('"') >= 0 ? text.IndexOf('"') : text.IndexOf('\'');
        if (start < 0) return null;
        var quote = text[start];
        var end = text.LastIndexOf(quote);
        return end > start ? text[(start + 1)..end] : null;
    }

    // === TypeScript: NestJS method decorators + Express/router route calls (ADR-016 C2/2) ===

    private static readonly Dictionary<string, string> NestMethodDecorators = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Get"] = "GET", ["Post"] = "POST", ["Put"] = "PUT", ["Delete"] = "DELETE",
        ["Patch"] = "PATCH", ["Options"] = "OPTIONS", ["Head"] = "HEAD", ["All"] = "ALL",
    };

    private static readonly Dictionary<string, string> ExpressVerbs = new(StringComparer.Ordinal)
    {
        ["get"] = "GET", ["post"] = "POST", ["put"] = "PUT", ["delete"] = "DELETE",
        ["patch"] = "PATCH", ["options"] = "OPTIONS", ["head"] = "HEAD", ["all"] = "ALL",
    };

    /// <summary>
    /// If a TS class carries a NestJS <c>@Controller(...)</c> decorator, return its route prefix
    /// (empty string for <c>@Controller()</c>); null if it is not a controller. The prefix is then
    /// combined with each method's route. Decorators sit on the class node or its export parent.
    /// </summary>
    public static string? TryGetNestControllerPrefix(global::TreeSitter.Node classNode)
    {
        foreach (var dec in TsLeadingDecorators(classNode))
        {
            if (LastSegment(TsDecoratorName(dec)) == "Controller")
                return TsDecoratorFirstStringArg(dec) ?? "";
        }
        return null;
    }

    /// <summary>
    /// NestJS: read a method's <c>@Get/@Post/@Put/@Delete/@Patch/@All(...)</c> decorators and emit an
    /// api_endpoint per route (controller prefix + method route), with a HandledBy edge to the method.
    /// </summary>
    public static (List<ApiEndpointInfo> Endpoints, List<Relationship> Relationships) DetectTypeScriptRoutes(
        global::TreeSitter.Node methodNode, string handlerFqn, string controllerPrefix, string? filePath)
    {
        var endpoints = new List<ApiEndpointInfo>();
        var edges = new List<Relationship>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var dec in TsLeadingDecorators(methodNode))
        {
            var name = LastSegment(TsDecoratorName(dec));
            if (name is null || !NestMethodDecorators.TryGetValue(name, out var verb)) continue;

            var route = TsDecoratorFirstStringArg(dec) ?? "";
            var full = CombineNestRoute(controllerPrefix, route);
            var fqn = $"API:{verb}:{full}";
            if (!seen.Add(fqn)) continue;

            endpoints.Add(new ApiEndpointInfo
            {
                Fqn = fqn,
                Name = $"{verb} {full}",
                Kind = "api_endpoint",
                FilePath = filePath,
                StartLine = (int)methodNode.StartPosition.Row + 1,
                EndLine = (int)methodNode.EndPosition.Row + 1,
                HttpMethod = verb,
                RouteTemplate = full,
                HandlerMethodFqn = handlerFqn,
            });
            edges.Add(new Relationship(fqn, handlerFqn, RelationshipType.HandledBy));
        }
        return (endpoints, edges);
    }

    /// <summary>
    /// Express/router: <c>app.get("/x", handler)</c> / <c>router.post("/x", ...)</c>. Emits an
    /// api_endpoint (no HandledBy — the handler is usually inline/loose). Guards against false
    /// positives (e.g. <c>map.get("k")</c>) by requiring a route literal starting with "/" and at
    /// least two arguments (route + handler).
    /// </summary>
    public static ApiEndpointInfo? DetectExpressCall(global::TreeSitter.Node callNode, string? filePath)
    {
        var func = callNode.GetChildForField("function");
        if (func is null || func.Type != "member_expression") return null;

        var verb = func.GetChildForField("property")?.Text;
        if (verb is null || !ExpressVerbs.TryGetValue(verb, out var method)) return null;

        var args = callNode.GetChildForField("arguments");
        if (args is null) return null;

        var route = TsFirstStringArgument(args);
        if (route is null || !route.StartsWith('/')) return null;   // must look like a route
        if (CountNamedArgs(args) < 2) return null;                  // route + handler

        return new ApiEndpointInfo
        {
            Fqn = $"API:{method}:{route}",
            Name = $"{method} {route}",
            Kind = "api_endpoint",
            FilePath = filePath,
            StartLine = (int)callNode.StartPosition.Row + 1,
            EndLine = (int)callNode.EndPosition.Row + 1,
            HttpMethod = method,
            RouteTemplate = route,
        };
    }

    // --- TS helpers --------------------------------------------------------

    private static IEnumerable<global::TreeSitter.Node> TsLeadingDecorators(global::TreeSitter.Node node)
    {
        // Some grammars attach the decorator directly to the member node.
        foreach (var child in node.Children)
            if (child.Type == "decorator") yield return child;

        var parent = node.Parent;
        if (parent is null) yield break;

        // `@Controller() export class X`: decorator + `export` token + class_declaration are all
        // children of export_statement (the `export` token breaks contiguity), so take them all.
        if (parent.Type == "export_statement")
        {
            foreach (var child in parent.Children)
                if (child.Type == "decorator") yield return child;
            yield break;
        }

        // Otherwise (e.g. a method in class_body, or a non-exported decorated class): decorators are
        // the sibling `decorator` nodes IMMEDIATELY preceding this node.
        var pending = new List<global::TreeSitter.Node>();
        foreach (var child in parent.Children)
        {
            if (IsSamePosition(child, node))
            {
                foreach (var d in pending) yield return d;
                yield break;
            }
            if (child.Type == "decorator") pending.Add(child);
            else pending.Clear();
        }
    }

    private static bool IsSamePosition(global::TreeSitter.Node a, global::TreeSitter.Node b) =>
        a.StartPosition.Row == b.StartPosition.Row && a.StartPosition.Column == b.StartPosition.Column;

    private static string? TsDecoratorName(global::TreeSitter.Node decorator)
    {
        foreach (var child in decorator.Children)
        {
            if (child.Type == "identifier") return child.Text;
            if (child.Type == "call_expression") return child.GetChildForField("function")?.Text;
            if (child.Type == "member_expression") return child.Text;
        }
        return null;
    }

    private static string? TsDecoratorFirstStringArg(global::TreeSitter.Node decorator)
    {
        foreach (var child in decorator.Children)
        {
            if (child.Type == "call_expression")
            {
                var args = child.GetChildForField("arguments");
                return args is null ? null : TsFirstStringArgument(args);
            }
            if (child.Type == "string") return StringLiteralValue(child);
        }
        return null;
    }

    private static string? TsFirstStringArgument(global::TreeSitter.Node args)
    {
        foreach (var child in args.Children)
        {
            if (!child.IsNamed) continue;
            return child.Type == "string" ? StringLiteralValue(child) : null;
        }
        return null;
    }

    private static int CountNamedArgs(global::TreeSitter.Node args) =>
        args.Children.Count(c => c.IsNamed);

    /// <summary>Combine a NestJS controller prefix + method route into a single leading-slash path.</summary>
    private static string CombineNestRoute(string? prefix, string? route)
    {
        var parts = new List<string>(2);
        foreach (var seg in new[] { prefix, route })
        {
            var t = (seg ?? "").Trim().Trim('/').Trim();
            if (t.Length > 0) parts.Add(t);
        }
        return "/" + string.Join("/", parts);
    }

    private static string? LastSegment(string? name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        var trimmed = name.Trim();
        var dot = trimmed.LastIndexOf('.');
        return dot >= 0 ? trimmed[(dot + 1)..] : trimmed;
    }
}
