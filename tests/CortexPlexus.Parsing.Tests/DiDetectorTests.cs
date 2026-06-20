using CortexPlexus.Core.Models;
using CortexPlexus.Parsing.TreeSitter;

namespace CortexPlexus.Parsing.Tests;

/// <summary>
/// ADR-016 C3: class-level DI registration detection → di_registration nodes.
/// Spring stereotypes (Java) and @Injectable providers (TypeScript), exercised end-to-end
/// through the extractors so the class FQN coupling is real.
/// </summary>
public sealed class DiDetectorTests
{
    private static (List<CodeSymbol> Symbols, List<Relationship> Rels) ParseJava(string code)
    {
        var lang = new global::TreeSitter.Language("java");
        using var parser = new global::TreeSitter.Parser(lang);
        using var tree = parser.Parse(code);
        var extractor = new JavaExtractor(code, "Test.java", "com/example/Test.java");
        return extractor.Extract(tree.RootNode);
    }

    private static (List<CodeSymbol> Symbols, List<Relationship> Rels) ParseTypeScript(string code)
    {
        var lang = new global::TreeSitter.Language("typescript");
        using var parser = new global::TreeSitter.Parser(lang);
        using var tree = parser.Parse(code);
        var extractor = new TypeScriptExtractor(code, "test.ts", "test.ts");
        return extractor.Extract(tree.RootNode);
    }

    private static List<DiRegistrationInfo> Registrations(List<CodeSymbol> symbols) =>
        symbols.OfType<DiRegistrationInfo>().ToList();

    // --- Java / Spring -----------------------------------------------------

    [Fact]
    public void Spring_ServiceAnnotation_EmitsSelfRegistration()
    {
        var (symbols, _) = ParseJava("""
        package com.example;
        @Service
        public class UserService {
        }
        """);

        var classFqn = symbols.Single(s => s.Kind == "class").Fqn;
        var reg = Assert.Single(Registrations(symbols));
        Assert.Equal("di_registration", reg.Kind);
        Assert.Equal($"DI:{classFqn}->{classFqn}", reg.Fqn);
        Assert.Equal(classFqn, reg.ServiceTypeFqn);
        Assert.Equal(classFqn, reg.ImplementationTypeFqn);
        Assert.Equal("Singleton", reg.Lifetime);
    }

    [Fact]
    public void Spring_RestController_IsDetected()
    {
        var (symbols, _) = ParseJava("""
        package com.example;
        @RestController
        public class ApiController {
        }
        """);

        Assert.Single(Registrations(symbols));
    }

    [Fact]
    public void Spring_FullyQualifiedStereotype_IsDetected()
    {
        var (symbols, _) = ParseJava("""
        package com.example;
        @org.springframework.stereotype.Repository
        public class UserRepo {
        }
        """);

        Assert.Single(Registrations(symbols));
    }

    [Fact]
    public void Java_PlainClass_NoRegistration()
    {
        var (symbols, _) = ParseJava("""
        package com.example;
        public class PlainOldObject {
        }
        """);

        Assert.Empty(Registrations(symbols));
    }

    [Fact]
    public void Java_NonStereotypeAnnotation_NoRegistration()
    {
        var (symbols, _) = ParseJava("""
        package com.example;
        @Deprecated
        public class LegacyThing {
        }
        """);

        Assert.Empty(Registrations(symbols));
    }

    // --- TypeScript / NestJS ----------------------------------------------

    [Fact]
    public void Nest_InjectableExportedClass_EmitsSelfRegistration()
    {
        var (symbols, _) = ParseTypeScript("""
        @Injectable()
        export class UserService {
          findAll() { return []; }
        }
        """);

        var classFqn = symbols.Single(s => s.Kind == "class").Fqn;
        var reg = Assert.Single(Registrations(symbols));
        Assert.Equal("di_registration", reg.Kind);
        Assert.Equal($"DI:{classFqn}->{classFqn}", reg.Fqn);
        Assert.Equal("Singleton", reg.Lifetime);
    }

    [Fact]
    public void Nest_InjectableNonExported_IsDetected()
    {
        var (symbols, _) = ParseTypeScript("""
        @Injectable()
        class InternalService {
        }
        """);

        Assert.Single(Registrations(symbols));
    }

    [Fact]
    public void Ts_PlainClass_NoRegistration()
    {
        var (symbols, _) = ParseTypeScript("""
        export class JustAModel {
          id: number = 0;
        }
        """);

        Assert.Empty(Registrations(symbols));
    }

    [Fact]
    public void Ts_NonProviderDecorator_NoRegistration()
    {
        // @Component is an Angular component, not a DI provider — must not be registered.
        var (symbols, _) = ParseTypeScript("""
        @Component({ selector: "app" })
        export class AppComponent {
        }
        """);

        Assert.Empty(Registrations(symbols));
    }
}
