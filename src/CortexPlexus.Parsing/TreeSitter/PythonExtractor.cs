using CortexPlexus.Core.Models;

namespace CortexPlexus.Parsing.TreeSitter;

/// <summary>
/// Walks a tree-sitter AST to extract symbols and relationships for Python source files.
/// </summary>
internal sealed class PythonExtractor
{
    private readonly string _filePath;
    private readonly string _modulePath;

    private readonly List<CodeSymbol> _symbols = [];
    private readonly List<Relationship> _relationships = [];
    private readonly HashSet<string> _testMethodFqns = [];
    private readonly bool _isTestFile;

    /// <summary>
    /// Best-effort name→FQN map for resolving call targets: import aliases
    /// (<c>from a.b import c as d</c> ⇒ <c>d</c>→<c>a.b.c</c>) and module-level
    /// definitions (<c>def helper</c> ⇒ <c>helper</c>→<c>&lt;module&gt;.helper</c>).
    /// Built in a pre-pass so resolution is independent of call/import ordering.
    /// Python is dynamically typed, so this is a heuristic — unresolved names are
    /// left verbatim rather than guessed (avoids false edges to builtins).
    /// </summary>
    private readonly Dictionary<string, string> _nameResolution = new(StringComparer.Ordinal);

    public PythonExtractor(string sourceCode, string filePath, string relativePath)
    {
        _filePath = filePath;

        // Convert relative path to Python module path: "src/services/auth.py" -> "src.services.auth"
        _modulePath = relativePath
            .Replace('\\', '/')
            .Replace('/', '.')
            .TrimEnd('.');

        if (_modulePath.EndsWith(".py"))
            _modulePath = _modulePath[..^3];

        var normalizedPath = filePath.Replace('\\', '/');
        _isTestFile = normalizedPath.Contains("test_") || normalizedPath.Contains("/tests/");
    }

    /// <summary>
    /// Extracts all symbols and relationships from the given AST root node.
    /// </summary>
    public (List<CodeSymbol> Symbols, List<Relationship> Relationships) Extract(global::TreeSitter.Node root)
    {
        BuildNameResolution(root);
        WalkNode(root, containingClass: null, callerScope: _modulePath);
        return (_symbols, _relationships);
    }

    /// <summary>
    /// Pre-pass: populate <see cref="_nameResolution"/> from top-level imports and
    /// module-level definitions so <see cref="ExtractCall"/> can resolve callee names
    /// to FQNs regardless of source ordering. Module-level defs override imports on
    /// name collision (matches Python's last-binding-wins for module scope).
    /// </summary>
    private void BuildNameResolution(global::TreeSitter.Node root)
    {
        foreach (var child in root.Children)
        {
            switch (child.Type)
            {
                case "import_statement":
                    CollectImportAliases(child);
                    break;
                case "import_from_statement":
                    CollectFromImportAliases(child);
                    break;
                case "class_definition":
                case "function_definition":
                    var name = GetNameText(child);
                    if (name is not null)
                        _nameResolution[name] = $"{_modulePath}.{name}";
                    break;
                case "decorated_definition":
                    var def = child.GetChildForField("definition");
                    var decoratedName = def is not null ? GetNameText(def) : null;
                    if (decoratedName is not null)
                        _nameResolution[decoratedName] = $"{_modulePath}.{decoratedName}";
                    break;
            }
        }
    }

    /// <summary><c>import a.b.c as x</c> ⇒ <c>x</c>→<c>a.b.c</c>. Plain
    /// <c>import a.b.c</c> needs no entry — its usage <c>a.b.c.f()</c> is already qualified.</summary>
    private void CollectImportAliases(global::TreeSitter.Node node)
    {
        foreach (var child in node.Children)
        {
            if (child.Type != "aliased_import") continue;
            var nameNode = child.GetChildForField("name");
            var aliasNode = child.GetChildForField("alias");
            if (nameNode is not null && aliasNode is not null)
                _nameResolution[NodeText(aliasNode)] = NodeText(nameNode);
        }
    }

    /// <summary><c>from a.b import c</c> ⇒ <c>c</c>→<c>a.b.c</c>;
    /// <c>from a.b import c as d</c> ⇒ <c>d</c>→<c>a.b.c</c>. Relative imports
    /// (<c>from . import x</c>) and wildcards are skipped (cannot resolve safely).</summary>
    private void CollectFromImportAliases(global::TreeSitter.Node node)
    {
        var moduleNode = node.GetChildForField("module_name");
        // Relative import (module_name is a relative_import node) or missing → skip.
        if (moduleNode is null || moduleNode.Type != "dotted_name") return;
        var module = NodeText(moduleNode);

        var sawImportKeyword = false;
        foreach (var child in node.Children)
        {
            if (child.Type == "import") { sawImportKeyword = true; continue; }
            if (!sawImportKeyword) continue;  // skip 'from' + module_name

            switch (child.Type)
            {
                case "dotted_name" or "identifier":
                    var imported = NodeText(child);
                    _nameResolution[LastSegment(imported)] = $"{module}.{imported}";
                    break;
                case "aliased_import":
                    var nameNode = child.GetChildForField("name");
                    var aliasNode = child.GetChildForField("alias");
                    if (nameNode is not null && aliasNode is not null)
                        _nameResolution[NodeText(aliasNode)] = $"{module}.{NodeText(nameNode)}";
                    break;
            }
        }
    }

    // containingClass: nearest enclosing class FQN — drives method naming + self/cls
    //   resolution (null at module level).
    // callerScope: nearest enclosing function/method FQN, else the module — the node
    //   a 'call' is attributed to, so get_callers points at the method, not the class.
    private void WalkNode(global::TreeSitter.Node node, string? containingClass, string callerScope)
    {
        switch (node.Type)
        {
            case "class_definition":
                ExtractClass(node, callerScope);
                return;

            case "function_definition":
                ExtractFunction(node, containingClass, callerScope);
                return;

            case "decorated_definition":
                ExtractDecorated(node, containingClass, callerScope);
                return;

            case "import_statement":
                ExtractImport(node);
                break;

            case "import_from_statement":
                ExtractImportFrom(node);
                break;

            case "call":
                ExtractCall(node, containingClass, callerScope);
                break;
        }

        WalkChildren(node, containingClass, callerScope);
    }

    private void WalkChildren(global::TreeSitter.Node node, string? containingClass, string callerScope)
    {
        foreach (var child in node.Children)
        {
            WalkNode(child, containingClass, callerScope);
        }
    }

    private void ExtractClass(global::TreeSitter.Node node, string callerScope)
    {
        var name = GetNameText(node);
        if (name is null) return;

        var fqn = $"{_modulePath}.{name}";
        var body = node.GetChildForField("body");

        _symbols.Add(new ClassInfo
        {
            Fqn = fqn,
            Name = name,
            Kind = "class",
            FilePath = _filePath,
            StartLine = (int)node.StartPosition.Row + 1,
            EndLine = (int)node.EndPosition.Row + 1,
            Documentation = body is not null ? DocCommentHelper.GetPythonDocstring(body) : null,
        });

        _relationships.Add(new Relationship(_modulePath, fqn, RelationshipType.Declares));
        ExtractBaseClasses(node, fqn);

        if (body is not null)
        {
            _relationships.AddRange(ConfigAccessDetector.DetectPython(body, fqn));
            _relationships.AddRange(HttpCallDetector.DetectPython(body, fqn));
            _relationships.AddRange(EventPatternDetector.DetectPython(body, fqn));
            WalkChildren(body, containingClass: fqn, callerScope: callerScope);
        }
    }

    private void ExtractBaseClasses(global::TreeSitter.Node node, string classFqn)
    {
        var superclasses = node.GetChildForField("superclasses");
        if (superclasses is null) return;

        foreach (var child in superclasses.Children)
        {
            if (child.IsNamed)
            {
                _relationships.Add(new Relationship(classFqn, NodeText(child), RelationshipType.Inherits));
            }
        }
    }

    private void ExtractFunction(global::TreeSitter.Node node, string? containingClass, string callerScope)
    {
        var name = GetNameText(node);
        if (name is null) return;

        var isMethod = containingClass is not null;
        var fqn = containingClass is not null
            ? $"{containingClass}.{name}"
            : $"{_modulePath}.{name}";

        var isAsync = HasChildWithType(node, "async");
        var body = node.GetChildForField("body");
        var isTest = name.StartsWith("test_") || _isTestFile && name.StartsWith("test_");

        _symbols.Add(new MethodInfo
        {
            Fqn = fqn,
            Name = name,
            Kind = isMethod ? "method" : "function",
            FilePath = _filePath,
            StartLine = (int)node.StartPosition.Row + 1,
            EndLine = (int)node.EndPosition.Row + 1,
            Signature = BuildSignature(name, node),
            IsAsync = isAsync,
            IsTestMethod = isTest,
            ContainingTypeFqn = containingClass,
            Documentation = body is not null ? DocCommentHelper.GetPythonDocstring(body) : null,
        });

        if (isTest) _testMethodFqns.Add(fqn);

        if (containingClass is not null)
            _relationships.Add(new Relationship(containingClass, fqn, RelationshipType.HasMethod));
        else
            _relationships.Add(new Relationship(_modulePath, fqn, RelationshipType.Declares));

        if (body is not null)
        {
            _relationships.AddRange(ConfigAccessDetector.DetectPython(body, fqn));
            _relationships.AddRange(HttpCallDetector.DetectPython(body, fqn));
            _relationships.AddRange(EventPatternDetector.DetectPython(body, fqn));
            // containingClass kept (so self/cls inside this body still resolves to the
            // class); callerScope becomes this function so its calls attribute to it.
            WalkChildren(body, containingClass, callerScope: fqn);
        }
    }

    private void ExtractDecorated(global::TreeSitter.Node node, string? containingClass, string callerScope)
    {
        var definition = node.GetChildForField("definition");
        if (definition is not null)
        {
            WalkNode(definition, containingClass, callerScope);
        }
        else
        {
            foreach (var child in node.Children)
            {
                if (child.Type is "function_definition" or "class_definition")
                {
                    WalkNode(child, containingClass, callerScope);
                    return;
                }
            }
        }
    }

    private void ExtractImport(global::TreeSitter.Node node)
    {
        foreach (var child in node.Children)
        {
            if (child.Type == "dotted_name")
            {
                var moduleName = NodeText(child);
                _relationships.Add(new Relationship(
                    _modulePath,
                    moduleName,
                    RelationshipType.DependsOn,
                    new Dictionary<string, string> { ["importPath"] = moduleName }));
            }
        }
    }

    private void ExtractImportFrom(global::TreeSitter.Node node)
    {
        var moduleNode = node.GetChildForField("module_name");
        if (moduleNode is null) return;

        var moduleName = NodeText(moduleNode);
        _relationships.Add(new Relationship(
            _modulePath,
            moduleName,
            RelationshipType.DependsOn,
            new Dictionary<string, string> { ["importPath"] = moduleName }));
    }

    private void ExtractCall(global::TreeSitter.Node node, string? containingClass, string callerScope)
    {
        var functionNode = node.GetChildForField("function");
        if (functionNode is null) return;

        var calleeName = functionNode.Type switch
        {
            "identifier" or "attribute" => NodeText(functionNode),
            _ => null,
        };

        if (calleeName is null) return;

        var resolved = ResolveCallee(calleeName, containingClass);
        var relType = _testMethodFqns.Contains(callerScope) ? RelationshipType.TestCovers : RelationshipType.Calls;
        _relationships.Add(new Relationship(callerScope, resolved, relType));
    }

    /// <summary>
    /// Best-effort resolution of a raw callee expression to an FQN:
    /// <list type="bullet">
    /// <item><c>self.m</c> / <c>cls.m</c> inside a class ⇒ <c>&lt;class&gt;.m</c></item>
    /// <item>first segment matching an import alias or module-level def ⇒ that FQN
    /// (e.g. <c>helper()</c>⇒<c>&lt;module&gt;.helper</c>, <c>svc.run()</c>⇒<c>a.b.svc.run</c>)</item>
    /// </list>
    /// Anything else (builtins, instance attributes, dynamic dispatch) is returned
    /// verbatim — we never guess, to avoid polluting the graph with false edges.
    /// </summary>
    private string ResolveCallee(string calleeName, string? containingClass)
    {
        if (containingClass is not null)
        {
            if (calleeName.StartsWith("self.", StringComparison.Ordinal))
                return $"{containingClass}.{calleeName["self.".Length..]}";
            if (calleeName.StartsWith("cls.", StringComparison.Ordinal))
                return $"{containingClass}.{calleeName["cls.".Length..]}";
        }

        var dot = calleeName.IndexOf('.');
        var head = dot < 0 ? calleeName : calleeName[..dot];
        if (_nameResolution.TryGetValue(head, out var resolved))
            return dot < 0 ? resolved : $"{resolved}{calleeName[dot..]}";

        return calleeName;
    }

    // --- Helpers ---

    private static string LastSegment(string dotted)
    {
        var idx = dotted.LastIndexOf('.');
        return idx < 0 ? dotted : dotted[(idx + 1)..];
    }

    private string? GetNameText(global::TreeSitter.Node node)
    {
        var nameNode = node.GetChildForField("name");
        return nameNode is not null && nameNode.IsNamed ? NodeText(nameNode) : null;
    }

    private static string NodeText(global::TreeSitter.Node node)
    {
        return node.Text ?? string.Empty;
    }

    private static bool HasChildWithType(global::TreeSitter.Node node, string type)
    {
        foreach (var child in node.Children)
        {
            if (child.Type == type) return true;
        }
        return false;
    }

    private string BuildSignature(string name, global::TreeSitter.Node node)
    {
        var paramsNode = node.GetChildForField("parameters");
        if (paramsNode is not null)
        {
            return $"{name}{NodeText(paramsNode)}";
        }
        return $"{name}()";
    }
}
