using CortexPlexus.Core.Models;
using CortexPlexus.Parsing.TreeSitter;

namespace CortexPlexus.Parsing.Tests;

public sealed class TypeScriptExtractorTests
{
    [Fact]
    public void ExtractsClass()
    {
        var (symbols, _) = ParseTypeScript("""
            export class UserService {
                getUser(id: number) { return null; }
            }
            """);

        var cls = symbols.OfType<ClassInfo>().FirstOrDefault(s => s.Name == "UserService");
        Assert.NotNull(cls);
        Assert.Equal("class", cls.Kind);
    }

    [Fact]
    public void ExtractsInterface()
    {
        var (symbols, _) = ParseTypeScript("""
            export interface IUserService {
                getUser(id: number): Promise<User>;
            }
            """);

        var iface = symbols.OfType<InterfaceInfo>().FirstOrDefault(s => s.Name == "IUserService");
        Assert.NotNull(iface);
        Assert.Equal("interface", iface.Kind);
    }

    [Fact]
    public void ExtractsFunction()
    {
        var (symbols, _) = ParseTypeScript("""
            export function formatName(user: User): string {
                return user.name;
            }
            """);

        var func = symbols.OfType<MethodInfo>().FirstOrDefault(s => s.Name == "formatName");
        Assert.NotNull(func);
        Assert.Equal("function", func.Kind);
    }

    [Fact]
    public void ExtractsMethod()
    {
        var (symbols, _) = ParseTypeScript("""
            class OrderService {
                async processOrder(id: number): Promise<void> {
                    console.log(id);
                }
            }
            """);

        var method = symbols.OfType<MethodInfo>().FirstOrDefault(s => s.Name == "processOrder");
        Assert.NotNull(method);
        Assert.Equal("method", method.Kind);
    }

    [Fact]
    public void ExtractsEnum()
    {
        var (symbols, _) = ParseTypeScript("""
            enum Status { Active, Inactive, Deleted }
            """);

        var enm = symbols.OfType<ClassInfo>().FirstOrDefault(s => s.Name == "Status");
        Assert.NotNull(enm);
        Assert.Equal("enum", enm.Kind);
    }

    [Fact]
    public void ExtractsImportRelationship()
    {
        var (_, relationships) = ParseTypeScript("""
            import { HttpClient } from './http-client';
            """);

        var dep = relationships.FirstOrDefault(r => r.Type == RelationshipType.DependsOn);
        Assert.NotNull(dep);
    }

    [Fact]
    public void ExtractsCallRelationship()
    {
        var (_, relationships) = ParseTypeScript("""
            function main() {
                console.log("hello");
                processData();
            }
            function processData() {}
            """);

        var calls = relationships.Where(r => r.Type == RelationshipType.Calls).ToList();
        Assert.NotEmpty(calls);
    }

    [Fact]
    public void ExtractsModuleLevelConfigRead()
    {
        // GH #6 (TS side): top-level process.env reads were never scanned —
        // ConfigAccessDetector only ran on function/method/arrow bodies.
        var (_, relationships) = ParseTypeScript("""
            const port = process.env.PORT;
            """);

        Assert.Contains(relationships, r =>
            r.Type == RelationshipType.ReadsConfig
            && r.FromFqn == "test.ts"
            && r.ToFqn == "env:PORT");
    }

    [Fact]
    public void EmitsModuleSymbol()
    {
        var (symbols, _) = ParseTypeScript("const x = 1;");

        var module = symbols.FirstOrDefault(s => s.Kind == "module");
        Assert.NotNull(module);
        Assert.Equal("test.ts", module.Fqn);
    }

    private static (List<CodeSymbol>, List<Relationship>) ParseTypeScript(string code)
    {
        var lang = new global::TreeSitter.Language("typescript");
        using var parser = new global::TreeSitter.Parser(lang);
        using var tree = parser.Parse(code);
        var extractor = new TypeScriptExtractor(code, "test.ts", "test.ts");
        return extractor.Extract(tree.RootNode);
    }
}

public sealed class PythonExtractorTests
{
    [Fact]
    public void ExtractsClass()
    {
        var (symbols, _) = ParsePython("""
            class UserRepository:
                def __init__(self, db):
                    self.db = db
            """);

        var cls = symbols.OfType<ClassInfo>().FirstOrDefault(s => s.Name == "UserRepository");
        Assert.NotNull(cls);
        Assert.Equal("class", cls.Kind);
    }

    [Fact]
    public void ExtractsMethod()
    {
        var (symbols, _) = ParsePython("""
            class Service:
                def process(self, data):
                    return data
            """);

        var method = symbols.OfType<MethodInfo>().FirstOrDefault(s => s.Name == "process");
        Assert.NotNull(method);
        Assert.Equal("method", method.Kind);
    }

    [Fact]
    public void ExtractsTopLevelFunction()
    {
        var (symbols, _) = ParsePython("""
            def helper(x: int) -> int:
                return x * 2
            """);

        var func = symbols.OfType<MethodInfo>().FirstOrDefault(s => s.Name == "helper");
        Assert.NotNull(func);
        Assert.Equal("function", func.Kind);
    }

    [Fact]
    public void ExtractsImportRelationship()
    {
        var (_, relationships) = ParsePython("""
            from os.path import join
            import logging
            """);

        var deps = relationships.Where(r => r.Type == RelationshipType.DependsOn).ToList();
        Assert.NotEmpty(deps);
    }

    [Fact]
    public void ExtractsInheritance()
    {
        var (_, relationships) = ParsePython("""
            class Animal:
                pass

            class Dog(Animal):
                pass
            """);

        var inherits = relationships.FirstOrDefault(r => r.Type == RelationshipType.Inherits);
        Assert.NotNull(inherits);
    }

    [Fact]
    public void ExtractsDecoratedFunction()
    {
        var (symbols, _) = ParsePython("""
            class Api:
                @staticmethod
                def health_check():
                    return "ok"
            """);

        var method = symbols.OfType<MethodInfo>().FirstOrDefault(s => s.Name == "health_check");
        Assert.NotNull(method);
    }

    [Fact]
    public void ResolvesSelfCallToContainingClass()
    {
        var (_, relationships) = ParsePython("""
            class Service:
                def process(self):
                    self.helper()

                def helper(self):
                    pass
            """);

        Assert.Contains(relationships, r =>
            r.Type == RelationshipType.Calls
            && r.FromFqn == "test.Service.process"
            && r.ToFqn == "test.Service.helper");
    }

    [Fact]
    public void ResolvesFromImportToModuleQualifiedFqn()
    {
        var (_, relationships) = ParsePython("""
            from core.intel.scoring import score_item

            def run():
                score_item()
            """);

        Assert.Contains(relationships, r =>
            r.Type == RelationshipType.Calls
            && r.FromFqn == "test.run"
            && r.ToFqn == "core.intel.scoring.score_item");
    }

    [Fact]
    public void ResolvesAliasedImportCall()
    {
        var (_, relationships) = ParsePython("""
            import core.engine.tts as tts

            def run():
                tts.synthesize()
            """);

        Assert.Contains(relationships, r =>
            r.Type == RelationshipType.Calls
            && r.ToFqn == "core.engine.tts.synthesize");
    }

    [Fact]
    public void ResolvesModuleLevelDefCall()
    {
        var (_, relationships) = ParsePython("""
            def helper():
                pass

            def main():
                helper()
            """);

        Assert.Contains(relationships, r =>
            r.Type == RelationshipType.Calls
            && r.FromFqn == "test.main"
            && r.ToFqn == "test.helper");
    }

    [Fact]
    public void LeavesUnknownCalleeVerbatim()
    {
        // Builtins / dynamic dispatch must NOT be guessed into false module edges.
        var (_, relationships) = ParsePython("""
            def main():
                print("hi")
            """);

        Assert.Contains(relationships, r =>
            r.Type == RelationshipType.Calls && r.ToFqn == "print");
        Assert.DoesNotContain(relationships, r =>
            r.Type == RelationshipType.Calls && r.ToFqn == "test.print");
    }

    [Fact]
    public void ExtractsModuleLevelConfigRead()
    {
        // GH #6: module-level os.environ reads (e.g. Alembic env.py) were never
        // scanned — ConfigAccessDetector only ran on class/function bodies.
        var (_, relationships) = ParsePython("""
            import os
            db_url = os.environ.get("DATABASE_URL")
            """);

        Assert.Contains(relationships, r =>
            r.Type == RelationshipType.ReadsConfig
            && r.FromFqn == "test"
            && r.ToFqn == "env:DATABASE_URL");
    }

    [Fact]
    public void ExtractsModuleLevelConfigReadUnderTopLevelGuard()
    {
        // Mirrors Alembic env.py:20-24 — read at module scope guarded by a top-level if.
        var (_, relationships) = ParsePython("""
            import os
            url = os.environ.get("DATABASE_URL")
            if url:
                token = os.environ["API_TOKEN"]
            """);

        Assert.Contains(relationships, r =>
            r.Type == RelationshipType.ReadsConfig && r.ToFqn == "env:API_TOKEN" && r.FromFqn == "test");
    }

    [Fact]
    public void EmitsModuleSymbol()
    {
        var (symbols, _) = ParsePython("x = 1\n");

        var module = symbols.FirstOrDefault(s => s.Kind == "module");
        Assert.NotNull(module);
        Assert.Equal("test", module.Fqn);
    }

    [Fact]
    public void InFunctionConfigReadAttributedToFunctionNotModule()
    {
        // A read inside a function must attribute to the function, not double-count to the module.
        var (_, relationships) = ParsePython("""
            import os
            def load():
                return os.getenv("API_TOKEN")
            """);

        var reads = relationships
            .Where(r => r.Type == RelationshipType.ReadsConfig && r.ToFqn == "env:API_TOKEN")
            .ToList();
        Assert.Single(reads);
        Assert.Equal("test.load", reads[0].FromFqn);
    }

    [Fact]
    public void ResolvesConstantIndirectionConfigRead()
    {
        // GH #6 case #2: os.environ.get(CONST) where CONST is a module-level string constant.
        var (_, relationships) = ParsePython("""
            import os
            API_KEY_ENV = "HIVE_VBEE_API_KEY"

            class VbeeProvider:
                def __init__(self):
                    self._key = os.environ.get(API_KEY_ENV)
            """);

        Assert.Contains(relationships, r =>
            r.Type == RelationshipType.ReadsConfig && r.ToFqn == "env:HIVE_VBEE_API_KEY");
    }

    [Fact]
    public void PropertyDecoratedMethodsGetPropertyKind()
    {
        // @property / @cached_property are accessed as attributes, not called — must NOT be
        // emitted as "method" (else they're false dead-code candidates). GH #6 follow-up.
        var (symbols, _) = ParsePython("""
            class Cell:
                @property
                def priority(self):
                    return self._p

                @functools.cached_property
                def composite(self):
                    return 1

                def normal_method(self):
                    return 2
        """);

        Assert.Equal("property", symbols.First(s => s.Name == "priority").Kind);
        Assert.Equal("property", symbols.First(s => s.Name == "composite").Kind);
        Assert.Equal("method", symbols.First(s => s.Name == "normal_method").Kind);
    }

    private static (List<CodeSymbol>, List<Relationship>) ParsePython(string code)
    {
        var lang = new global::TreeSitter.Language("python");
        using var parser = new global::TreeSitter.Parser(lang);
        using var tree = parser.Parse(code);
        var extractor = new PythonExtractor(code, "test.py", "test");
        return extractor.Extract(tree.RootNode);
    }
}

// ======== Java ========

public sealed class JavaExtractorTests
{
    [Fact]
    public void ExtractsClass()
    {
        var (symbols, _) = ParseJava("""
            package com.example;
            public class UserService {
                public String getUser(int id) { return null; }
            }
            """);

        var cls = symbols.OfType<ClassInfo>().FirstOrDefault(s => s.Name == "UserService");
        Assert.NotNull(cls);
        Assert.Equal("class", cls.Kind);
        Assert.StartsWith("com.example", cls.Fqn);
    }

    [Fact]
    public void ExtractsInterface()
    {
        var (symbols, _) = ParseJava("""
            package com.example;
            public interface IUserService {
                String getUser(int id);
            }
            """);

        var iface = symbols.OfType<InterfaceInfo>().FirstOrDefault(s => s.Name == "IUserService");
        Assert.NotNull(iface);
    }

    [Fact]
    public void ExtractsMethod()
    {
        var (symbols, _) = ParseJava("""
            package com.example;
            public class Service {
                public void process(String input) {}
            }
            """);

        var method = symbols.OfType<MethodInfo>().FirstOrDefault(s => s.Name == "process");
        Assert.NotNull(method);
        Assert.Equal("method", method.Kind);
        Assert.Equal("com.example.Service", method.ContainingTypeFqn);
    }

    [Fact]
    public void ExtractsInheritance()
    {
        var (_, rels) = ParseJava("""
            package com.example;
            public class Dog extends Animal {
            }
            """);

        Assert.Contains(rels, r => r.Type == RelationshipType.Inherits && r.ToFqn == "Animal");
    }

    [Fact]
    public void ExtractsImport()
    {
        var (_, rels) = ParseJava("""
            package com.example;
            import java.util.List;
            public class Service {}
            """);

        Assert.Contains(rels, r => r.Type == RelationshipType.DependsOn);
    }

    [Fact]
    public void ExtractsEnum()
    {
        var (symbols, _) = ParseJava("""
            package com.example;
            public enum Status { ACTIVE, INACTIVE }
            """);

        var e = symbols.OfType<ClassInfo>().FirstOrDefault(s => s.Name == "Status");
        Assert.NotNull(e);
        Assert.Equal("enum", e.Kind);
    }

    private static (List<CodeSymbol>, List<Relationship>) ParseJava(string code)
    {
        var lang = new global::TreeSitter.Language("java");
        using var parser = new global::TreeSitter.Parser(lang);
        using var tree = parser.Parse(code);
        var extractor = new JavaExtractor(code, "Test.java", "com/example/Test.java");
        return extractor.Extract(tree.RootNode);
    }
}

// ======== PHP ========

public sealed class PhpExtractorTests
{
    [Fact]
    public void ExtractsClass()
    {
        var (symbols, _) = ParsePhp("""
            <?php
            namespace App\Services;
            class UserService {
                public function getUser(int $id) {}
            }
            """);

        var cls = symbols.OfType<ClassInfo>().FirstOrDefault(s => s.Name == "UserService");
        Assert.NotNull(cls);
        Assert.Equal("class", cls.Kind);
    }

    [Fact]
    public void ExtractsInterface()
    {
        var (symbols, _) = ParsePhp("""
            <?php
            namespace App\Contracts;
            interface UserRepositoryInterface {
                public function find(int $id);
            }
            """);

        var iface = symbols.OfType<InterfaceInfo>().FirstOrDefault(s => s.Name == "UserRepositoryInterface");
        Assert.NotNull(iface);
    }

    [Fact]
    public void ExtractsMethod()
    {
        var (symbols, _) = ParsePhp("""
            <?php
            namespace App\Services;
            class Service {
                public static function process(string $input): void {}
            }
            """);

        var method = symbols.OfType<MethodInfo>().FirstOrDefault(s => s.Name == "process");
        Assert.NotNull(method);
        Assert.Equal("method", method.Kind);
    }

    [Fact]
    public void ExtractsFunction()
    {
        var (symbols, _) = ParsePhp("""
            <?php
            function helper(string $name): string { return $name; }
            """);

        var func = symbols.OfType<MethodInfo>().FirstOrDefault(s => s.Name == "helper");
        Assert.NotNull(func);
        Assert.Equal("function", func.Kind);
    }

    private static (List<CodeSymbol>, List<Relationship>) ParsePhp(string code)
    {
        var lang = new global::TreeSitter.Language("php");
        using var parser = new global::TreeSitter.Parser(lang);
        using var tree = parser.Parse(code);
        var extractor = new PhpExtractor(code, "test.php", "app/Services/test.php");
        return extractor.Extract(tree.RootNode);
    }
}

// ======== Go ========

public sealed class GoExtractorTests
{
    [Fact]
    public void ExtractsFunction()
    {
        var (symbols, _) = ParseGo("""
            package main
            func Hello(name string) string {
                return "Hello " + name
            }
            """);

        var func = symbols.OfType<MethodInfo>().FirstOrDefault(s => s.Name == "Hello");
        Assert.NotNull(func);
        Assert.Equal("function", func.Kind);
        Assert.Equal("main.Hello", func.Fqn);
    }

    [Fact]
    public void ExtractsStruct()
    {
        var (symbols, _) = ParseGo("""
            package models
            type User struct {
                Name string
                Age  int
            }
            """);

        var s = symbols.OfType<ClassInfo>().FirstOrDefault(s => s.Name == "User");
        Assert.NotNull(s);
        Assert.Equal("struct", s.Kind);
    }

    [Fact]
    public void ExtractsInterface()
    {
        var (symbols, _) = ParseGo("""
            package service
            type Repository interface {
                Find(id int) error
            }
            """);

        var iface = symbols.OfType<InterfaceInfo>().FirstOrDefault(s => s.Name == "Repository");
        Assert.NotNull(iface);
    }

    [Fact]
    public void ExtractsMethodWithReceiver()
    {
        var (symbols, rels) = ParseGo("""
            package service
            type UserService struct{}
            func (s *UserService) GetUser(id int) error {
                return nil
            }
            """);

        var method = symbols.OfType<MethodInfo>().FirstOrDefault(s => s.Name == "GetUser");
        Assert.NotNull(method);
        Assert.Equal("method", method.Kind);
        Assert.Equal("service.UserService", method.ContainingTypeFqn);
        Assert.Contains(rels, r => r.Type == RelationshipType.HasMethod);
    }

    [Fact]
    public void ExtractsImport()
    {
        var (_, rels) = ParseGo("""
            package main
            import (
                "fmt"
                "os"
            )
            func main() {}
            """);

        Assert.Contains(rels, r => r.Type == RelationshipType.DependsOn && r.ToFqn == "fmt");
        Assert.Contains(rels, r => r.Type == RelationshipType.DependsOn && r.ToFqn == "os");
    }

    [Fact]
    public void ExtractsStructFields()
    {
        var (symbols, _) = ParseGo("""
            package models
            type Config struct {
                Host string
                Port int
            }
            """);

        var fields = symbols.OfType<PropertyInfo>().ToList();
        Assert.Contains(fields, f => f.Name == "Host");
        Assert.Contains(fields, f => f.Name == "Port");
    }

    private static (List<CodeSymbol>, List<Relationship>) ParseGo(string code)
    {
        var lang = new global::TreeSitter.Language("go");
        using var parser = new global::TreeSitter.Parser(lang);
        using var tree = parser.Parse(code);
        var extractor = new GoExtractor(code, "test.go", "test.go");
        return extractor.Extract(tree.RootNode);
    }
}

// ======== Rust ========

public sealed class RustExtractorTests
{
    [Fact]
    public void ExtractsStruct()
    {
        var (symbols, _) = ParseRust("""
            pub struct User {
                pub name: String,
                age: u32,
            }
            """);

        var s = symbols.OfType<ClassInfo>().FirstOrDefault(s => s.Name == "User");
        Assert.NotNull(s);
        Assert.Equal("struct", s.Kind);
        Assert.Equal("pub", s.Accessibility);
    }

    [Fact]
    public void ExtractsEnum()
    {
        var (symbols, _) = ParseRust("""
            pub enum Status {
                Active,
                Inactive,
            }
            """);

        var e = symbols.OfType<ClassInfo>().FirstOrDefault(s => s.Name == "Status");
        Assert.NotNull(e);
        Assert.Equal("enum", e.Kind);
    }

    [Fact]
    public void ExtractsTrait()
    {
        var (symbols, _) = ParseRust("""
            pub trait Repository {
                fn find(&self, id: u32) -> Option<User>;
            }
            """);

        var t = symbols.OfType<InterfaceInfo>().FirstOrDefault(s => s.Name == "Repository");
        Assert.NotNull(t);
    }

    [Fact]
    public void ExtractsFreeFunction()
    {
        var (symbols, _) = ParseRust("""
            pub fn process(input: &str) -> String {
                input.to_string()
            }
            """);

        var f = symbols.OfType<MethodInfo>().FirstOrDefault(s => s.Name == "process");
        Assert.NotNull(f);
        Assert.Equal("function", f.Kind);
    }

    [Fact]
    public void ExtractsImplMethods()
    {
        var (symbols, rels) = ParseRust("""
            struct Service {}
            impl Service {
                pub fn new() -> Self { Service {} }
                fn process(&self) {}
            }
            """);

        Assert.Contains(symbols.OfType<MethodInfo>(), m => m.Name == "new" && m.ContainingTypeFqn!.Contains("Service"));
        Assert.Contains(symbols.OfType<MethodInfo>(), m => m.Name == "process");
        Assert.Contains(rels, r => r.Type == RelationshipType.HasMethod);
    }

    [Fact]
    public void ExtractsTraitImpl()
    {
        var (_, rels) = ParseRust("""
            struct MyService {}
            trait Handler {
                fn handle(&self);
            }
            impl Handler for MyService {
                fn handle(&self) {}
            }
            """);

        Assert.Contains(rels, r => r.Type == RelationshipType.Implements && r.ToFqn == "Handler");
    }

    [Fact]
    public void ExtractsUseDeclaration()
    {
        var (_, rels) = ParseRust("""
            use std::io;
            use std::collections::HashMap;
            fn main() {}
            """);

        Assert.Contains(rels, r => r.Type == RelationshipType.DependsOn);
    }

    private static (List<CodeSymbol>, List<Relationship>) ParseRust(string code)
    {
        var lang = new global::TreeSitter.Language("rust");
        using var parser = new global::TreeSitter.Parser(lang);
        using var tree = parser.Parse(code);
        var extractor = new RustExtractor(code, "src/service.rs", "src/service.rs");
        return extractor.Extract(tree.RootNode);
    }
}
