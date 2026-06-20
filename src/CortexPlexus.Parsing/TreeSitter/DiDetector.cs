using CortexPlexus.Core.Models;

namespace CortexPlexus.Parsing.TreeSitter;

/// <summary>
/// Detects dependency-injection registrations declared via class-level annotations/decorators and
/// emits <see cref="DiRegistrationInfo"/> nodes (FQN <c>DI:&lt;service&gt;-&gt;&lt;impl&gt;</c>, Kind
/// <c>di_registration</c>) matching the Roslyn DI emitter. Lights up <c>get_di_registrations</c>
/// for non-.NET stacks (ADR-016 C3).
///
/// - Java / Spring: <c>@Component @Service @Repository @Controller @RestController @Configuration</c>
///   on a class → a self-registered singleton bean.
/// - TypeScript / NestJS / Angular: <c>@Injectable()</c> on a class → a self-registered provider.
///
/// These are all self-registrations (service type == implementation type == the class), mirroring the
/// Roslyn single-type-argument case. Default lifetime is Singleton for both frameworks.
/// </summary>
internal static class DiDetector
{
    private static readonly HashSet<string> SpringStereotypes = new(StringComparer.Ordinal)
    {
        "Component", "Service", "Repository", "Controller", "RestController", "Configuration",
    };

    private static readonly HashSet<string> TsProviderDecorators = new(StringComparer.Ordinal)
    {
        "Injectable",   // NestJS providers + Angular services
    };

    public static List<DiRegistrationInfo> DetectJavaClass(
        global::TreeSitter.Node classNode, string classFqn, string? filePath)
    {
        var modifiers = FindChild(classNode, "modifiers");
        if (modifiers is null) return [];

        foreach (var mod in modifiers.Children)
        {
            if (mod.Type is not ("marker_annotation" or "annotation")) continue;
            var simpleName = LastSegment(mod.GetChildForField("name")?.Text);
            if (simpleName is not null && SpringStereotypes.Contains(simpleName))
                return [Build(classFqn, simpleName, classNode, filePath)];
        }
        return [];
    }

    public static List<DiRegistrationInfo> DetectTypeScriptClass(
        global::TreeSitter.Node classNode, string classFqn, string? filePath)
    {
        foreach (var dec in LeadingDecorators(classNode))
        {
            var name = LastSegment(DecoratorName(dec));
            if (name is not null && TsProviderDecorators.Contains(name))
                return [Build(classFqn, name, classNode, filePath)];
        }
        return [];
    }

    // --- shared ------------------------------------------------------------

    private static DiRegistrationInfo Build(
        string classFqn, string annotation, global::TreeSitter.Node classNode, string? filePath)
    {
        return new DiRegistrationInfo
        {
            Fqn = $"DI:{classFqn}->{classFqn}",
            Name = $"{annotation} {LastSegment(classFqn) ?? classFqn}",
            Kind = "di_registration",
            FilePath = filePath,
            StartLine = (int)classNode.StartPosition.Row + 1,
            EndLine = (int)classNode.EndPosition.Row + 1,
            ServiceTypeFqn = classFqn,
            ImplementationTypeFqn = classFqn,
            Lifetime = "Singleton",
            ModuleName = annotation,
        };
    }

    /// <summary>
    /// Class decorators sit either directly on the class node or on its wrapping
    /// <c>export_statement</c> parent (<c>@Injectable() export class X</c>).
    /// </summary>
    private static IEnumerable<global::TreeSitter.Node> LeadingDecorators(global::TreeSitter.Node classNode)
    {
        foreach (var child in classNode.Children)
            if (child.Type == "decorator") yield return child;

        var parent = classNode.Parent;
        if (parent is not null && parent.Type == "export_statement")
            foreach (var child in parent.Children)
                if (child.Type == "decorator") yield return child;
    }

    /// <summary>Name of a TS decorator: <c>@Injectable</c> or <c>@Injectable()</c> / <c>@core.Injectable()</c>.</summary>
    private static string? DecoratorName(global::TreeSitter.Node decorator)
    {
        foreach (var child in decorator.Children)
        {
            if (child.Type == "identifier") return child.Text;
            if (child.Type == "call_expression")
                return child.GetChildForField("function")?.Text;
            if (child.Type == "member_expression") return child.Text;
        }
        return null;
    }

    private static global::TreeSitter.Node? FindChild(global::TreeSitter.Node node, string type)
    {
        foreach (var child in node.Children)
            if (child.Type == type) return child;
        return null;
    }

    /// <summary>Last dotted segment of a possibly-qualified name (<c>a.b.Service</c> → <c>Service</c>).</summary>
    private static string? LastSegment(string? name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        var trimmed = name.Trim();
        var dot = trimmed.LastIndexOf('.');
        return dot >= 0 ? trimmed[(dot + 1)..] : trimmed;
    }
}
