using CortexPlexus.Core.Models;

namespace CortexPlexus.Parsing.TreeSitter;

/// <summary>
/// Detects environment variable and configuration access patterns across languages.
/// Returns ReadsConfig relationships from the caller FQN to the config key.
///
/// Supported patterns per language:
/// - Python:     os.environ["KEY"], os.environ.get("KEY"), os.getenv("KEY")
/// - JS/TS:      process.env.KEY, process.env["KEY"]
/// - Java:       System.getenv("KEY"), System.getProperty("KEY")
/// - Go:         os.Getenv("KEY")
/// - Rust:       env::var("KEY"), std::env::var("KEY")
/// - PHP:        $_ENV["KEY"], getenv("KEY"), $_SERVER["KEY"]
/// </summary>
internal static class ConfigAccessDetector
{
    /// <summary>
    /// Scans a tree-sitter AST node for config/env access patterns.
    /// Call this from within an extractor's WalkNode when visiting call or subscript nodes.
    /// </summary>
    public static List<Relationship> DetectPython(global::TreeSitter.Node root, string callerFqn,
        IReadOnlyDictionary<string, string>? constants = null)
    {
        var results = new List<Relationship>();
        var seen = new HashSet<string>();
        WalkForPython(root, callerFqn, results, seen, constants);
        return results;
    }

    public static List<Relationship> DetectTypeScript(global::TreeSitter.Node root, string callerFqn)
    {
        var results = new List<Relationship>();
        var seen = new HashSet<string>();
        WalkForTypeScript(root, callerFqn, results, seen);
        return results;
    }

    public static List<Relationship> DetectJava(global::TreeSitter.Node root, string callerFqn)
    {
        var results = new List<Relationship>();
        var seen = new HashSet<string>();
        WalkForJava(root, callerFqn, results, seen);
        return results;
    }

    public static List<Relationship> DetectGo(global::TreeSitter.Node root, string callerFqn)
    {
        var results = new List<Relationship>();
        var seen = new HashSet<string>();
        WalkForGo(root, callerFqn, results, seen);
        return results;
    }

    public static List<Relationship> DetectRust(global::TreeSitter.Node root, string callerFqn)
    {
        var results = new List<Relationship>();
        var seen = new HashSet<string>();
        WalkForRust(root, callerFqn, results, seen);
        return results;
    }

    public static List<Relationship> DetectPhp(global::TreeSitter.Node root, string callerFqn)
    {
        var results = new List<Relationship>();
        var seen = new HashSet<string>();
        WalkForPhp(root, callerFqn, results, seen);
        return results;
    }

    // --- Python: os.environ["KEY"], os.environ.get("KEY"), os.getenv("KEY") ---

    private static void WalkForPython(global::TreeSitter.Node node, string callerFqn,
        List<Relationship> results, HashSet<string> seen, IReadOnlyDictionary<string, string>? constants)
    {
        var text = node.Text ?? "";

        // os.environ["KEY"] — subscript expression
        if (node.Type == "subscript" && text.Contains("os.environ"))
        {
            var subscript = GetChildByFieldName(node, "subscript");
            if (subscript is not null && TryResolveKey(subscript, constants, out var key))
                AddEnvEdge(callerFqn, key, "os.environ", results, seen);
        }

        // os.environ.get("KEY") or os.getenv("KEY")
        if (node.Type == "call")
        {
            var funcNode = GetChildByFieldName(node, "function");
            var funcText = funcNode?.Text ?? "";

            if (funcText is "os.environ.get" or "os.getenv")
            {
                var args = GetChildByFieldName(node, "arguments");
                if (args is not null)
                {
                    var firstArg = GetFirstNamedChild(args);
                    if (firstArg is not null && TryResolveKey(firstArg, constants, out var key))
                        AddEnvEdge(callerFqn, key, "os.environ", results, seen);
                }
            }
        }

        foreach (var child in node.Children)
            WalkForPython(child, callerFqn, results, seen, constants);
    }

    /// <summary>
    /// Resolve a config-key argument to its string value: a string literal directly, or — for
    /// the constant-indirection case <c>KEY = "FOO"; os.environ.get(KEY)</c> (GH #6 case #2) — a
    /// bare identifier looked up in the module/class-level string-constant map.
    /// </summary>
    private static bool TryResolveKey(global::TreeSitter.Node node,
        IReadOnlyDictionary<string, string>? constants, out string key)
    {
        if (TryGetStringValue(node, out key)) return true;
        if (node.Type == "identifier" && constants is not null
            && constants.TryGetValue(node.Text ?? "", out var value) && !string.IsNullOrEmpty(value))
        {
            key = value;
            return true;
        }
        key = "";
        return false;
    }

    // --- TypeScript/JavaScript: process.env.KEY, process.env["KEY"] ---

    private static void WalkForTypeScript(global::TreeSitter.Node node, string callerFqn,
        List<Relationship> results, HashSet<string> seen)
    {
        var text = node.Text ?? "";

        // process.env.KEY — member_expression
        if (node.Type == "member_expression" && text.StartsWith("process.env."))
        {
            var property = GetChildByFieldName(node, "property");
            if (property is not null)
            {
                var key = property.Text ?? "";
                if (!string.IsNullOrEmpty(key) && key != "env")
                    AddEnvEdge(callerFqn, key, "process.env", results, seen);
            }
            return; // Don't recurse into children to avoid duplicate from parent
        }

        // process.env["KEY"] — subscript_expression
        if (node.Type == "subscript_expression" && text.StartsWith("process.env"))
        {
            var index = GetChildByFieldName(node, "index");
            if (index is not null && TryGetStringValue(index, out var key))
                AddEnvEdge(callerFqn, key, "process.env", results, seen);
            return;
        }

        foreach (var child in node.Children)
            WalkForTypeScript(child, callerFqn, results, seen);
    }

    // --- Java: System.getenv("KEY"), System.getProperty("KEY") ---

    private static void WalkForJava(global::TreeSitter.Node node, string callerFqn,
        List<Relationship> results, HashSet<string> seen)
    {
        if (node.Type == "method_invocation")
        {
            var objNode = GetChildByFieldName(node, "object");
            var nameNode = GetChildByFieldName(node, "name");
            var objText = objNode?.Text ?? "";
            var methodName = nameNode?.Text ?? "";

            if (objText == "System" && methodName is "getenv" or "getProperty")
            {
                var args = GetChildByFieldName(node, "arguments");
                if (args is not null)
                {
                    var firstArg = GetFirstNamedChild(args);
                    if (firstArg is not null && TryGetStringValue(firstArg, out var key))
                    {
                        var provider = methodName == "getenv" ? "System.getenv" : "System.getProperty";
                        AddEnvEdge(callerFqn, key, provider, results, seen);
                    }
                }
            }
        }

        foreach (var child in node.Children)
            WalkForJava(child, callerFqn, results, seen);
    }

    // --- Go: os.Getenv("KEY"), os.LookupEnv("KEY") ---

    private static void WalkForGo(global::TreeSitter.Node node, string callerFqn,
        List<Relationship> results, HashSet<string> seen)
    {
        if (node.Type == "call_expression")
        {
            var funcNode = GetChildByFieldName(node, "function");
            var funcText = funcNode?.Text ?? "";

            if (funcText is "os.Getenv" or "os.LookupEnv")
            {
                var args = GetChildByFieldName(node, "arguments");
                if (args is not null)
                {
                    var firstArg = GetFirstNamedChild(args);
                    if (firstArg is not null && TryGetStringValue(firstArg, out var key))
                        AddEnvEdge(callerFqn, key, "os.Getenv", results, seen);
                }
            }
        }

        foreach (var child in node.Children)
            WalkForGo(child, callerFqn, results, seen);
    }

    // --- Rust: env::var("KEY"), std::env::var("KEY"), env::var_os("KEY") ---

    private static void WalkForRust(global::TreeSitter.Node node, string callerFqn,
        List<Relationship> results, HashSet<string> seen)
    {
        if (node.Type == "call_expression")
        {
            var funcNode = GetChildByFieldName(node, "function");
            var funcText = funcNode?.Text ?? "";

            if (funcText is "env::var" or "std::env::var" or "env::var_os" or "std::env::var_os")
            {
                var args = GetChildByFieldName(node, "arguments");
                if (args is not null)
                {
                    var firstArg = GetFirstNamedChild(args);
                    if (firstArg is not null && TryGetStringValue(firstArg, out var key))
                        AddEnvEdge(callerFqn, key, "env::var", results, seen);
                }
            }
        }

        foreach (var child in node.Children)
            WalkForRust(child, callerFqn, results, seen);
    }

    // --- PHP: $_ENV["KEY"], getenv("KEY"), $_SERVER["KEY"] ---
    // PHP tree-sitter uses: function_call_expression (not function_call),
    // variable_name for $_ENV/$_SERVER, encapsed_string > string_content for strings.

    private static void WalkForPhp(global::TreeSitter.Node node, string callerFqn,
        List<Relationship> results, HashSet<string> seen)
    {
        // $_ENV["KEY"] or $_SERVER["KEY"] — subscript_expression
        if (node.Type == "subscript_expression")
        {
            // First named child is variable_name (e.g., "$_ENV")
            var firstChild = GetFirstNamedChild(node);
            var objText = firstChild?.Text ?? "";

            if (objText is "$_ENV" or "$_SERVER")
            {
                // Find the string content inside the subscript
                var key = FindStringContentInChildren(node);
                if (key is not null)
                {
                    var provider = objText == "$_ENV" ? "$_ENV" : "$_SERVER";
                    AddEnvEdge(callerFqn, key, provider, results, seen);
                }
            }
        }

        // getenv("KEY") — function_call_expression
        if (node.Type == "function_call_expression")
        {
            var funcNode = GetChildByFieldName(node, "name") ?? GetFirstNamedChild(node);
            if (funcNode?.Text == "getenv")
            {
                var args = GetChildByFieldName(node, "arguments");
                if (args is not null)
                {
                    // Find string_content inside arguments > argument > encapsed_string > string_content
                    var key = FindStringContentInChildren(args);
                    if (key is not null)
                        AddEnvEdge(callerFqn, key, "getenv", results, seen);
                }
            }
        }

        foreach (var child in node.Children)
            WalkForPhp(child, callerFqn, results, seen);
    }

    /// <summary>
    /// Recursively finds the first string_content node value in a subtree.
    /// PHP tree-sitter wraps strings as: encapsed_string > string_content.
    /// </summary>
    private static string? FindStringContentInChildren(global::TreeSitter.Node node)
    {
        if (node.Type == "string_content")
        {
            var text = node.Text ?? "";
            return string.IsNullOrEmpty(text) ? null : text;
        }

        foreach (var child in node.Children)
        {
            var result = FindStringContentInChildren(child);
            if (result is not null) return result;
        }

        return null;
    }

    // --- Helpers ---

    private static void AddEnvEdge(string callerFqn, string key, string provider,
        List<Relationship> results, HashSet<string> seen)
    {
        if (string.IsNullOrWhiteSpace(key)) return;
        var configFqn = $"env:{key}";
        var edgeKey = $"{callerFqn}->{configFqn}";
        if (!seen.Add(edgeKey)) return;

        results.Add(new Relationship(
            callerFqn, configFqn, RelationshipType.ReadsConfig,
            new Dictionary<string, string> { ["provider"] = provider }));
    }

    private static global::TreeSitter.Node? GetChildByFieldName(global::TreeSitter.Node node, string fieldName)
    {
        return node.GetChildForField(fieldName);
    }

    private static global::TreeSitter.Node? GetFirstNamedChild(global::TreeSitter.Node node)
    {
        foreach (var child in node.Children)
        {
            if (child.IsNamed) return child;
        }
        return null;
    }

    private static bool TryGetStringValue(global::TreeSitter.Node node, out string value)
    {
        var text = node.Text ?? "";

        // String literal: "value", 'value'
        if (node.Type is "string" or "string_literal" or "interpreted_string_literal"
            or "string_value" or "encapsed_string" or "string_content")
        {
            value = StripQuotes(text);
            return !string.IsNullOrEmpty(value);
        }

        // Some languages wrap the literal in quotes at the node level
        if (text.Length >= 2 &&
            ((text[0] == '"' && text[^1] == '"') || (text[0] == '\'' && text[^1] == '\'')))
        {
            value = text[1..^1];
            return !string.IsNullOrEmpty(value);
        }

        value = "";
        return false;
    }

    private static string StripQuotes(string text)
    {
        if (text.Length < 2) return text;
        if ((text[0] == '"' && text[^1] == '"') || (text[0] == '\'' && text[^1] == '\''))
            return text[1..^1];
        return text;
    }
}
