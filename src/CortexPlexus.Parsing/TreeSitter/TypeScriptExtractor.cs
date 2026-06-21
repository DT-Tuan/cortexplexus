using CortexPlexus.Core.Models;

namespace CortexPlexus.Parsing.TreeSitter;

/// <summary>
/// Walks a tree-sitter AST to extract symbols and relationships for
/// TypeScript, JavaScript, and TSX source files.
/// </summary>
internal sealed class TypeScriptExtractor
{
    private readonly string _sourceCode;
    private readonly string _filePath;
    private readonly string _relativePath;

    private readonly List<CodeSymbol> _symbols = [];
    private readonly List<Relationship> _relationships = [];
    private readonly HashSet<string> _exportedNames = [];
    private readonly HashSet<string> _testMethodFqns = [];

    // classFqn → NestJS @Controller route prefix, set when the class is extracted so its methods'
    // @Get/@Post decorators can be combined into full routes (ADR-016 C2/2).
    private readonly Dictionary<string, string> _controllerPrefixes = new(StringComparer.Ordinal);
    private readonly bool _isTestFile;

    public TypeScriptExtractor(string sourceCode, string filePath, string relativePath)
    {
        _sourceCode = sourceCode;
        _filePath = filePath;
        _relativePath = relativePath;
        _isTestFile = _filePath.Contains(".test.") || _filePath.Contains(".spec.");
    }

    /// <summary>
    /// Extracts all symbols and relationships from the given AST root node.
    /// </summary>
    public (List<CodeSymbol> Symbols, List<Relationship> Relationships) Extract(global::TreeSitter.Node root)
    {
        CollectExports(root);
        var fileNamespace = _relativePath;
        EmitModuleSymbol(root, fileNamespace);
        DetectModuleScopeRelationships(root, fileNamespace);
        WalkNode(root, containingTypeFqn: null, fileNamespace: fileNamespace);
        return (_symbols, _relationships);
    }

    // Node types that own a named scoped symbol whose body is scanned elsewhere
    // (ExtractClass/ExtractFunction/etc.). Scanning them again at module scope
    // would double-count nested config/http/event reads.
    private static readonly HashSet<string> ScopedDeclarationTypes =
    [
        "class_declaration", "abstract_class_declaration", "interface_declaration",
        "function_declaration", "generator_function_declaration",
        "enum_declaration", "type_alias_declaration", "method_definition",
    ];

    /// <summary>
    /// Emit a vertex for the module/file itself so module-scope edges (imports,
    /// module-level <c>ReadsConfig</c>) have a real endpoint with a file location.
    /// "module" is not an embeddable kind (see <see cref="Core.EmbeddableKinds"/>).
    /// </summary>
    private void EmitModuleSymbol(global::TreeSitter.Node root, string fileNamespace)
    {
        _symbols.Add(new NamespaceInfo
        {
            Fqn = fileNamespace,
            Name = System.IO.Path.GetFileName(_relativePath),
            Kind = "module",
            FilePath = _filePath,
            StartLine = 1,
            EndLine = (int)root.EndPosition.Row + 1,
        });
    }

    /// <summary>
    /// Scan module-scope (top-level) statements for config/http/event access,
    /// attributed to the module FQN — e.g. <c>const port = process.env.PORT</c>
    /// at file top level. Scoped declarations are skipped (their bodies are scanned
    /// by their own extractors); <c>export</c> wrappers are unwrapped so
    /// <c>export const x = process.env.Y</c> is still caught (GH #6).
    /// </summary>
    private void DetectModuleScopeRelationships(global::TreeSitter.Node root, string fileNamespace)
    {
        foreach (var child in root.Children)
            ScanModuleScopeNode(child, fileNamespace);
    }

    private void ScanModuleScopeNode(global::TreeSitter.Node node, string fileNamespace)
    {
        if (ScopedDeclarationTypes.Contains(node.Type))
            return;

        // Unwrap `export ...` so `export const x = process.env.Y` is scanned but
        // `export class Foo {...}` is still skipped.
        if (node.Type == "export_statement")
        {
            foreach (var inner in node.Children)
                if (inner.IsNamed)
                    ScanModuleScopeNode(inner, fileNamespace);
            return;
        }

        _relationships.AddRange(ConfigAccessDetector.DetectTypeScript(node, fileNamespace));
        _relationships.AddRange(HttpCallDetector.DetectTypeScript(node, fileNamespace));
        _relationships.AddRange(EventPatternDetector.DetectTypeScript(node, fileNamespace));
    }

    private void CollectExports(global::TreeSitter.Node node)
    {
        foreach (var child in node.Children)
        {
            if (child.Type == "export_statement")
            {
                foreach (var inner in child.Children)
                {
                    var nameNode = inner.GetChildForField("name");
                    if (nameNode is not null && nameNode.IsNamed)
                    {
                        _exportedNames.Add(NodeText(nameNode));
                    }
                }
            }
        }
    }

    private void WalkNode(global::TreeSitter.Node node, string? containingTypeFqn, string fileNamespace)
    {
        switch (node.Type)
        {
            case "class_declaration":
                ExtractClass(node, containingTypeFqn, fileNamespace);
                return;

            case "interface_declaration":
                ExtractInterface(node, containingTypeFqn, fileNamespace);
                return;

            case "type_alias_declaration":
                ExtractTypeAlias(node, fileNamespace);
                break;

            case "enum_declaration":
                ExtractEnum(node, fileNamespace);
                break;

            case "function_declaration":
                ExtractFunction(node, containingTypeFqn, fileNamespace);
                break;

            case "method_definition":
                ExtractMethod(node, containingTypeFqn, fileNamespace);
                break;

            case "lexical_declaration" or "variable_declaration":
                ExtractVariableArrowFunctions(node, containingTypeFqn, fileNamespace);
                break;

            case "import_statement":
                ExtractImport(node, fileNamespace);
                break;

            case "call_expression":
                ExtractCall(node, containingTypeFqn, fileNamespace);
                break;

            case "export_statement":
                WalkChildren(node, containingTypeFqn, fileNamespace);
                return;
        }

        if (node.Type is not ("class_declaration" or "interface_declaration"))
        {
            WalkChildren(node, containingTypeFqn, fileNamespace);
        }
    }

    private void WalkChildren(global::TreeSitter.Node node, string? containingTypeFqn, string fileNamespace)
    {
        foreach (var child in node.Children)
        {
            WalkNode(child, containingTypeFqn, fileNamespace);
        }
    }

    private void ExtractClass(global::TreeSitter.Node node, string? containingTypeFqn, string fileNamespace)
    {
        var name = GetNameText(node);
        if (name is null) return;

        var fqn = BuildFqn(name);

        _symbols.Add(new ClassInfo
        {
            Fqn = fqn,
            Name = name,
            Kind = "class",
            FilePath = _filePath,
            StartLine = (int)node.StartPosition.Row + 1,
            EndLine = (int)node.EndPosition.Row + 1,
            Accessibility = _exportedNames.Contains(name) ? "export" : null,
            Documentation = DocCommentHelper.GetPrecedingDocComment(node),
        });

        _relationships.Add(new Relationship(fileNamespace, fqn, RelationshipType.Declares));

        // ADR-016 C3: an @Injectable() decorator makes this class a self-registered DI provider
        // (NestJS / Angular) → emit a di_registration node.
        _symbols.AddRange(DiDetector.DetectTypeScriptClass(node, fqn, _filePath));

        // ADR-016 C2/2: a NestJS @Controller(...) sets the route prefix for this class's methods.
        // Record it BEFORE walking the body so ExtractMethod can combine it with @Get/@Post routes.
        var nestPrefix = EndpointDetector.TryGetNestControllerPrefix(node);
        if (nestPrefix is not null)
            _controllerPrefixes[fqn] = nestPrefix;

        ExtractClassHeritage(node, fqn);

        var body = FindChildByType(node, "class_body");
        if (body is not null)
        {
            WalkChildren(body, containingTypeFqn: fqn, fileNamespace);
        }
    }

    private void ExtractClassHeritage(global::TreeSitter.Node node, string classFqn)
    {
        foreach (var child in node.Children)
        {
            if (child.Type == "class_heritage")
            {
                foreach (var clause in child.Children)
                {
                    if (clause.Type == "extends_clause")
                    {
                        var valueNode = clause.GetChildForField("value");
                        if (valueNode is not null)
                        {
                            _relationships.Add(new Relationship(classFqn, NodeText(valueNode), RelationshipType.Inherits));
                        }
                    }
                    else if (clause.Type == "implements_clause")
                    {
                        foreach (var typeNode in clause.Children)
                        {
                            if (typeNode.IsNamed && typeNode.Type != "implements")
                            {
                                _relationships.Add(new Relationship(classFqn, NodeText(typeNode), RelationshipType.Implements));
                            }
                        }
                    }
                }
            }
        }
    }

    private void ExtractInterface(global::TreeSitter.Node node, string? containingTypeFqn, string fileNamespace)
    {
        var name = GetNameText(node);
        if (name is null) return;

        var fqn = BuildFqn(name);
        var memberFqns = new List<string>();

        var body = FindChildByType(node, "interface_body") ?? FindChildByType(node, "object_type");
        if (body is not null)
        {
            foreach (var member in body.Children)
            {
                if (member.Type is "method_signature" or "property_signature")
                {
                    var memberName = GetNameText(member);
                    if (memberName is not null)
                    {
                        memberFqns.Add($"{fqn}.{memberName}");
                    }
                }
            }
        }

        _symbols.Add(new InterfaceInfo
        {
            Fqn = fqn,
            Name = name,
            Kind = "interface",
            FilePath = _filePath,
            StartLine = (int)node.StartPosition.Row + 1,
            EndLine = (int)node.EndPosition.Row + 1,
            Accessibility = _exportedNames.Contains(name) ? "export" : null,
            MemberFqns = memberFqns,
            Documentation = DocCommentHelper.GetPrecedingDocComment(node),
        });

        _relationships.Add(new Relationship(fileNamespace, fqn, RelationshipType.Declares));

        if (body is not null)
        {
            WalkChildren(body, containingTypeFqn: fqn, fileNamespace);
        }
    }

    private void ExtractTypeAlias(global::TreeSitter.Node node, string fileNamespace)
    {
        var name = GetNameText(node);
        if (name is null) return;

        var fqn = BuildFqn(name);
        _symbols.Add(new ClassInfo
        {
            Fqn = fqn,
            Name = name,
            Kind = "type",
            FilePath = _filePath,
            StartLine = (int)node.StartPosition.Row + 1,
            EndLine = (int)node.EndPosition.Row + 1,
            Accessibility = _exportedNames.Contains(name) ? "export" : null,
        });
        _relationships.Add(new Relationship(fileNamespace, fqn, RelationshipType.Declares));
    }

    private void ExtractEnum(global::TreeSitter.Node node, string fileNamespace)
    {
        var name = GetNameText(node);
        if (name is null) return;

        var fqn = BuildFqn(name);
        _symbols.Add(new ClassInfo
        {
            Fqn = fqn,
            Name = name,
            Kind = "enum",
            FilePath = _filePath,
            StartLine = (int)node.StartPosition.Row + 1,
            EndLine = (int)node.EndPosition.Row + 1,
            Accessibility = _exportedNames.Contains(name) ? "export" : null,
        });
        _relationships.Add(new Relationship(fileNamespace, fqn, RelationshipType.Declares));
    }

    private void ExtractFunction(global::TreeSitter.Node node, string? containingTypeFqn, string fileNamespace)
    {
        var name = GetNameText(node);
        if (name is null) return;

        var fqn = containingTypeFqn is not null ? $"{containingTypeFqn}.{name}" : BuildFqn(name);
        var isAsync = HasChildWithType(node, "async");

        var isTest = _isTestFile;

        _symbols.Add(new MethodInfo
        {
            Fqn = fqn,
            Name = name,
            Kind = "function",
            FilePath = _filePath,
            StartLine = (int)node.StartPosition.Row + 1,
            EndLine = (int)node.EndPosition.Row + 1,
            Signature = BuildSignature(name, node),
            IsAsync = isAsync,
            IsTestMethod = isTest,
            ContainingTypeFqn = containingTypeFqn,
            Accessibility = _exportedNames.Contains(name) ? "export" : null,
            Documentation = DocCommentHelper.GetPrecedingDocComment(node),
        });

        if (isTest) _testMethodFqns.Add(fqn);

        if (containingTypeFqn is not null)
            _relationships.Add(new Relationship(containingTypeFqn, fqn, RelationshipType.HasMethod));
        else
            _relationships.Add(new Relationship(fileNamespace, fqn, RelationshipType.Declares));

        var body = node.GetChildForField("body");
        if (body is not null)
        {
            _relationships.AddRange(ConfigAccessDetector.DetectTypeScript(body, fqn));
            _relationships.AddRange(HttpCallDetector.DetectTypeScript(body, fqn));
            _relationships.AddRange(EventPatternDetector.DetectTypeScript(body, fqn));
            WalkChildren(body, containingTypeFqn, fileNamespace);
        }
    }

    private void ExtractMethod(global::TreeSitter.Node node, string? containingTypeFqn, string fileNamespace)
    {
        var name = GetNameText(node);
        if (name is null) return;

        var fqn = containingTypeFqn is not null ? $"{containingTypeFqn}.{name}" : BuildFqn(name);
        var isAsync = HasChildWithType(node, "async");

        var isTest = _isTestFile;

        _symbols.Add(new MethodInfo
        {
            Fqn = fqn,
            Name = name,
            Kind = "method",
            FilePath = _filePath,
            StartLine = (int)node.StartPosition.Row + 1,
            EndLine = (int)node.EndPosition.Row + 1,
            Signature = BuildSignature(name, node),
            IsAsync = isAsync,
            IsTestMethod = isTest,
            ContainingTypeFqn = containingTypeFqn,
            Documentation = DocCommentHelper.GetPrecedingDocComment(node),
        });

        if (isTest) _testMethodFqns.Add(fqn);

        if (containingTypeFqn is not null)
            _relationships.Add(new Relationship(containingTypeFqn, fqn, RelationshipType.HasMethod));
        else
            _relationships.Add(new Relationship(fileNamespace, fqn, RelationshipType.Declares));

        // ADR-016 C2/2: NestJS controller method routes (@Get/@Post/...). Only methods of a class
        // carrying @Controller get route detection — the prefix was recorded in ExtractClass.
        if (containingTypeFqn is not null && _controllerPrefixes.TryGetValue(containingTypeFqn, out var prefix))
        {
            var (eps, edges) = EndpointDetector.DetectTypeScriptRoutes(node, fqn, prefix, _filePath);
            _symbols.AddRange(eps);
            _relationships.AddRange(edges);
        }

        var body = node.GetChildForField("body");
        if (body is not null)
        {
            _relationships.AddRange(ConfigAccessDetector.DetectTypeScript(body, fqn));
            _relationships.AddRange(HttpCallDetector.DetectTypeScript(body, fqn));
            _relationships.AddRange(EventPatternDetector.DetectTypeScript(body, fqn));
            WalkChildren(body, containingTypeFqn, fileNamespace);
        }
    }

    private void ExtractVariableArrowFunctions(global::TreeSitter.Node node, string? containingTypeFqn, string fileNamespace)
    {
        foreach (var child in node.Children)
        {
            if (child.Type != "variable_declarator") continue;

            var nameNode = child.GetChildForField("name");
            var valueNode = child.GetChildForField("value");
            if (nameNode is null || valueNode is null) continue;
            if (valueNode.Type != "arrow_function") continue;

            var name = NodeText(nameNode);
            var fqn = containingTypeFqn is not null ? $"{containingTypeFqn}.{name}" : BuildFqn(name);
            var isAsync = HasChildWithType(valueNode, "async");

            var isTest = _isTestFile;

            _symbols.Add(new MethodInfo
            {
                Fqn = fqn,
                Name = name,
                Kind = "function",
                FilePath = _filePath,
                StartLine = (int)child.StartPosition.Row + 1,
                EndLine = (int)child.EndPosition.Row + 1,
                Signature = $"{name}()",
                IsAsync = isAsync,
                IsTestMethod = isTest,
                ContainingTypeFqn = containingTypeFqn,
                Accessibility = _exportedNames.Contains(name) ? "export" : null,
                Documentation = DocCommentHelper.GetPrecedingDocComment(node),
            });

            if (isTest) _testMethodFqns.Add(fqn);

            if (containingTypeFqn is not null)
                _relationships.Add(new Relationship(containingTypeFqn, fqn, RelationshipType.HasMethod));
            else
                _relationships.Add(new Relationship(fileNamespace, fqn, RelationshipType.Declares));

            var body = valueNode.GetChildForField("body");
            if (body is not null)
            {
                _relationships.AddRange(ConfigAccessDetector.DetectTypeScript(body, fqn));
            _relationships.AddRange(HttpCallDetector.DetectTypeScript(body, fqn));
            _relationships.AddRange(EventPatternDetector.DetectTypeScript(body, fqn));
                WalkChildren(body, containingTypeFqn, fileNamespace);
            }
        }
    }

    private void ExtractImport(global::TreeSitter.Node node, string fileNamespace)
    {
        var sourceNode = node.GetChildForField("source");
        if (sourceNode is null) return;

        var raw = NodeText(sourceNode);
        var importPath = raw.Trim('\'', '"', '`');

        _relationships.Add(new Relationship(
            fileNamespace,
            importPath,
            RelationshipType.DependsOn,
            new Dictionary<string, string> { ["importPath"] = importPath }));
    }

    private void ExtractCall(global::TreeSitter.Node node, string? containingTypeFqn, string fileNamespace)
    {
        var functionNode = node.GetChildForField("function");
        if (functionNode is null) return;

        var calleeName = functionNode.Type switch
        {
            "identifier" or "member_expression" => NodeText(functionNode),
            _ => null,
        };

        if (calleeName is null) return;

        var callerFqn = containingTypeFqn ?? fileNamespace;
        var relType = _testMethodFqns.Contains(callerFqn) ? RelationshipType.TestCovers : RelationshipType.Calls;
        _relationships.Add(new Relationship(callerFqn, calleeName, relType));

        // ADR-016 C2/2: Express/router route call — app.get("/x", h) / router.post("/x", h).
        var endpoint = EndpointDetector.DetectExpressCall(node, _filePath);
        if (endpoint is not null)
            _symbols.Add(endpoint);
    }

    // --- Helpers ---

    private string BuildFqn(string symbolName) => $"{_relativePath}:{symbolName}";

    private string? GetNameText(global::TreeSitter.Node node)
    {
        var nameNode = node.GetChildForField("name");
        return nameNode is not null && nameNode.IsNamed ? NodeText(nameNode) : null;
    }

    private static string NodeText(global::TreeSitter.Node node)
    {
        return node.Text ?? string.Empty;
    }

    private static global::TreeSitter.Node? FindChildByType(global::TreeSitter.Node node, string type)
    {
        foreach (var child in node.Children)
        {
            if (child.Type == type) return child;
        }
        return null;
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
