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
}
