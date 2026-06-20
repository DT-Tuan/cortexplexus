using CortexPlexus.Core.Models;

namespace CortexPlexus.Parsing.TreeSitter;

/// <summary>
/// Walks a tree-sitter AST to extract symbols and relationships for Java source files.
/// FQN format: com.example.ClassName.methodName
/// </summary>
internal sealed class JavaExtractor
{
    private readonly string _filePath;
    private readonly string _relativePath;
    private string _packageName;

    private readonly List<CodeSymbol> _symbols = [];
    private readonly List<Relationship> _relationships = [];
    private readonly HashSet<string> _testMethodFqns = [];

    public JavaExtractor(string sourceCode, string filePath, string relativePath)
    {
        _filePath = filePath;
        _relativePath = relativePath;
        _packageName = Path.GetFileNameWithoutExtension(relativePath);
    }

    public (List<CodeSymbol> Symbols, List<Relationship> Relationships) Extract(global::TreeSitter.Node root)
    {
        // First pass: find package declaration
        foreach (var child in root.Children)
        {
            if (child.Type == "package_declaration")
            {
                var nameNode = FindChildByType(child, "scoped_identifier") ?? FindChildByType(child, "identifier");
                if (nameNode is not null)
                    _packageName = NodeText(nameNode);
                break;
            }
        }

        WalkNode(root, containingClass: null);
        return (_symbols, _relationships);
    }

    private void WalkNode(global::TreeSitter.Node node, string? containingClass)
    {
        switch (node.Type)
        {
            case "class_declaration":
                ExtractClass(node, containingClass);
                return;
            case "interface_declaration":
                ExtractInterface(node, containingClass);
                return;
            case "enum_declaration":
                ExtractEnum(node, containingClass);
                return;
            case "method_declaration":
                ExtractMethod(node, containingClass);
                return;
            case "constructor_declaration":
                ExtractConstructor(node, containingClass);
                return;
            case "import_declaration":
                ExtractImport(node);
                break;
            case "method_invocation":
                ExtractCall(node, containingClass);
                break;
        }

        WalkChildren(node, containingClass);
    }

    private void WalkChildren(global::TreeSitter.Node node, string? containingClass)
    {
        foreach (var child in node.Children)
            WalkNode(child, containingClass);
    }

    private void ExtractClass(global::TreeSitter.Node node, string? outer)
    {
        var name = GetNameText(node);
        if (name is null) return;

        var fqn = outer is not null ? $"{outer}.{name}" : $"{_packageName}.{name}";
        var modifiers = GetModifiers(node);

        _symbols.Add(new ClassInfo
        {
            Fqn = fqn, Name = name, Kind = "class",
            FilePath = _filePath,
            StartLine = (int)node.StartPosition.Row + 1,
            EndLine = (int)node.EndPosition.Row + 1,
            Accessibility = modifiers.Accessibility,
            IsAbstract = modifiers.IsAbstract,
            IsStatic = modifiers.IsStatic,
            IsSealed = modifiers.IsFinal,
            Documentation = DocCommentHelper.GetPrecedingDocComment(node),
        });

        _relationships.Add(new Relationship(outer ?? _packageName, fqn, RelationshipType.Declares));

        // ADR-016 C3: a Spring stereotype annotation (@Component/@Service/...) makes this class a
        // self-registered bean → emit a di_registration node.
        _symbols.AddRange(DiDetector.DetectJavaClass(node, fqn, _filePath));

        // Inheritance: superclass
        var superclass = node.GetChildForField("superclass");
        if (superclass is not null)
        {
            var baseName = NodeText(superclass).Replace("extends ", "").Trim();
            if (!string.IsNullOrEmpty(baseName))
                _relationships.Add(new Relationship(fqn, baseName, RelationshipType.Inherits));
        }

        // Interfaces
        var interfaces = node.GetChildForField("interfaces");
        if (interfaces is not null)
        {
            foreach (var child in interfaces.Children)
            {
                if (child.IsNamed && child.Type is "type_identifier" or "generic_type")
                    _relationships.Add(new Relationship(fqn, ExtractTypeName(child), RelationshipType.Implements));
            }
        }

        var body = node.GetChildForField("body");
        if (body is not null)
            WalkChildren(body, fqn);
    }

    private void ExtractInterface(global::TreeSitter.Node node, string? outer)
    {
        var name = GetNameText(node);
        if (name is null) return;

        var fqn = outer is not null ? $"{outer}.{name}" : $"{_packageName}.{name}";

        _symbols.Add(new InterfaceInfo
        {
            Fqn = fqn, Name = name, Kind = "interface",
            FilePath = _filePath,
            StartLine = (int)node.StartPosition.Row + 1,
            EndLine = (int)node.EndPosition.Row + 1,
            Accessibility = GetModifiers(node).Accessibility,
            Documentation = DocCommentHelper.GetPrecedingDocComment(node),
        });

        _relationships.Add(new Relationship(outer ?? _packageName, fqn, RelationshipType.Declares));

        // Interface extends
        var extendsNode = node.GetChildForField("interfaces");
        if (extendsNode is not null)
        {
            foreach (var child in extendsNode.Children)
            {
                if (child.IsNamed && child.Type is "type_identifier" or "generic_type")
                    _relationships.Add(new Relationship(fqn, ExtractTypeName(child), RelationshipType.Inherits));
            }
        }

        var body = node.GetChildForField("body");
        if (body is not null)
            WalkChildren(body, fqn);
    }

    private void ExtractEnum(global::TreeSitter.Node node, string? outer)
    {
        var name = GetNameText(node);
        if (name is null) return;

        var fqn = outer is not null ? $"{outer}.{name}" : $"{_packageName}.{name}";

        _symbols.Add(new ClassInfo
        {
            Fqn = fqn, Name = name, Kind = "enum",
            FilePath = _filePath,
            StartLine = (int)node.StartPosition.Row + 1,
            EndLine = (int)node.EndPosition.Row + 1,
            Documentation = DocCommentHelper.GetPrecedingDocComment(node),
        });

        _relationships.Add(new Relationship(outer ?? _packageName, fqn, RelationshipType.Declares));

        var body = node.GetChildForField("body");
        if (body is not null)
            WalkChildren(body, fqn);
    }

    private void ExtractMethod(global::TreeSitter.Node node, string? containingClass)
    {
        var name = GetNameText(node);
        if (name is null) return;

        var fqn = containingClass is not null ? $"{containingClass}.{name}" : $"{_packageName}.{name}";
        var modifiers = GetModifiers(node);

        var returnType = node.GetChildForField("type");

        _symbols.Add(new MethodInfo
        {
            Fqn = fqn, Name = name, Kind = "method",
            FilePath = _filePath,
            StartLine = (int)node.StartPosition.Row + 1,
            EndLine = (int)node.EndPosition.Row + 1,
            Signature = BuildSignature(name, node, returnType),
            ReturnType = returnType is not null ? NodeText(returnType) : null,
            Accessibility = modifiers.Accessibility,
            IsStatic = modifiers.IsStatic,
            IsOverride = modifiers.IsOverride,
            IsTestMethod = modifiers.IsTest,
            ContainingTypeFqn = containingClass,
            Documentation = DocCommentHelper.GetPrecedingDocComment(node),
        });

        if (modifiers.IsTest) _testMethodFqns.Add(fqn);

        if (containingClass is not null)
            _relationships.Add(new Relationship(containingClass, fqn, RelationshipType.HasMethod));
        else
            _relationships.Add(new Relationship(_packageName, fqn, RelationshipType.Declares));

        var body = node.GetChildForField("body");
        if (body is not null)
        {
            _relationships.AddRange(ConfigAccessDetector.DetectJava(body, fqn));
            _relationships.AddRange(HttpCallDetector.DetectJava(body, fqn));
            WalkChildren(body, containingClass);
        }
    }

    private void ExtractConstructor(global::TreeSitter.Node node, string? containingClass)
    {
        var name = GetNameText(node);
        if (name is null) return;

        var fqn = containingClass is not null ? $"{containingClass}.ctor" : $"{_packageName}.{name}.ctor";

        _symbols.Add(new ConstructorInfo
        {
            Fqn = fqn, Name = name, Kind = "constructor",
            FilePath = _filePath,
            StartLine = (int)node.StartPosition.Row + 1,
            EndLine = (int)node.EndPosition.Row + 1,
            Signature = BuildSignature(name, node, returnType: null),
            ContainingTypeFqn = containingClass,
            Documentation = DocCommentHelper.GetPrecedingDocComment(node),
        });

        if (containingClass is not null)
            _relationships.Add(new Relationship(containingClass, fqn, RelationshipType.HasConstructor));

        var body = node.GetChildForField("body");
        if (body is not null)
        {
            _relationships.AddRange(ConfigAccessDetector.DetectJava(body, fqn));
            _relationships.AddRange(HttpCallDetector.DetectJava(body, fqn));
            WalkChildren(body, containingClass);
        }
    }

    private void ExtractImport(global::TreeSitter.Node node)
    {
        var nameNode = FindChildByType(node, "scoped_identifier") ?? FindChildByType(node, "identifier");
        if (nameNode is null) return;

        var importPath = NodeText(nameNode);
        _relationships.Add(new Relationship(
            _packageName, importPath, RelationshipType.DependsOn,
            new Dictionary<string, string> { ["importPath"] = importPath }));
    }

    private void ExtractCall(global::TreeSitter.Node node, string? containingClass)
    {
        var nameNode = node.GetChildForField("name");
        if (nameNode is null) return;

        var calleeName = NodeText(nameNode);
        var callerFqn = containingClass ?? _packageName;
        var relType = _testMethodFqns.Contains(callerFqn) ? RelationshipType.TestCovers : RelationshipType.Calls;
        _relationships.Add(new Relationship(callerFqn, calleeName, relType));
    }

    // --- Helpers ---

    private record Modifiers(string? Accessibility, bool IsStatic, bool IsAbstract, bool IsFinal, bool IsOverride, bool IsTest);

    private static Modifiers GetModifiers(global::TreeSitter.Node node)
    {
        string? accessibility = null;
        bool isStatic = false, isAbstract = false, isFinal = false, isOverride = false, isTest = false;

        var modifiersNode = node.GetChildForField("modifiers");
        if (modifiersNode is null)
        {
            foreach (var child in node.Children)
            {
                if (child.Type == "modifiers") { modifiersNode = child; break; }
            }
        }
        if (modifiersNode is null) return new(null, false, false, false, false, false);

        foreach (var mod in modifiersNode.Children)
        {
            var text = NodeText(mod);
            switch (text)
            {
                case "public": accessibility = "public"; break;
                case "private": accessibility = "private"; break;
                case "protected": accessibility = "protected"; break;
                case "static": isStatic = true; break;
                case "abstract": isAbstract = true; break;
                case "final": isFinal = true; break;
            }

            if (mod.Type is "marker_annotation" or "annotation" && NodeText(mod).Contains("Override"))
                isOverride = true;

            if (mod.Type is "marker_annotation" or "annotation" && NodeText(mod).Contains("Test"))
                isTest = true;
        }

        return new(accessibility, isStatic, isAbstract, isFinal, isOverride, isTest);
    }

    private static string ExtractTypeName(global::TreeSitter.Node node)
    {
        if (node.Type == "generic_type")
        {
            var typeId = FindChildByType(node, "type_identifier");
            return typeId is not null ? NodeText(typeId) : NodeText(node);
        }
        return NodeText(node);
    }

    private string? GetNameText(global::TreeSitter.Node node)
    {
        var nameNode = node.GetChildForField("name");
        return nameNode is not null && nameNode.IsNamed ? NodeText(nameNode) : null;
    }

    private static string NodeText(global::TreeSitter.Node node) => node.Text ?? string.Empty;

    private static global::TreeSitter.Node? FindChildByType(global::TreeSitter.Node node, string type)
    {
        foreach (var child in node.Children)
            if (child.Type == type) return child;
        return null;
    }

    private static bool HasChildWithType(global::TreeSitter.Node node, string type)
    {
        foreach (var child in node.Children)
            if (child.Type == type) return true;
        return false;
    }

    private static string BuildSignature(string name, global::TreeSitter.Node node, global::TreeSitter.Node? returnType)
    {
        var paramsNode = node.GetChildForField("parameters");
        var paramText = paramsNode is not null ? NodeText(paramsNode) : "()";
        var retText = returnType is not null ? $"{NodeText(returnType)} " : "";
        return $"{retText}{name}{paramText}";
    }
}
