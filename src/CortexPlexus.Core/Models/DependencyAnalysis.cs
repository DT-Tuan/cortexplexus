namespace CortexPlexus.Core.Models;

/// <summary>
/// A single declared dependency from a package manifest, language-agnostic.
/// Covers npm/pip/go/cargo/composer/maven/nuget. Produced by PackageManifestAnalyzer.
/// </summary>
/// <param name="Name">Package name as declared (e.g. "fastapi", "express", "github.com/gin-gonic/gin",
/// "org.springframework:spring-core").</param>
/// <param name="Version">Declared version constraint, verbatim from the manifest
/// (e.g. "^1.2.3", ">=2.0", "1.5.0", "v1.9.1", "*"). Not resolved.</param>
/// <param name="Ecosystem">"nuget" | "npm" | "pip" | "go" | "cargo" | "composer" | "maven".</param>
/// <param name="Manifest">Manifest path relative to the audited root (e.g. "package.json",
/// "src/App.csproj"), so multiple manifests in a monorepo stay distinguishable.</param>
/// <param name="IsDev">True for dev/test-only dependencies (npm devDependencies, Cargo dev-dependencies,
/// poetry dev group, Maven test scope, requirements-dev.txt).</param>
public sealed record DependencyInfo(
    string Name,
    string Version,
    string Ecosystem,
    string Manifest,
    bool IsDev = false
);
