using CortexPlexus.Core.Models;
using CortexPlexus.Parsing;

namespace CortexPlexus.Parsing.Tests;

/// <summary>
/// Per-ecosystem manifest parsing (ADR-016 C1). Each test writes a real manifest into a temp
/// directory and exercises <see cref="PackageManifestAnalyzer.AnalyzeDirectory"/> end-to-end.
/// </summary>
public sealed class PackageManifestAnalyzerTests : IDisposable
{
    private readonly string _dir;
    private readonly PackageManifestAnalyzer _analyzer = new();

    public PackageManifestAnalyzerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"cortex-manifest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private void Write(string relativePath, string content)
    {
        var full = Path.Combine(_dir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private IReadOnlyList<DependencyInfo> Analyze() => _analyzer.AnalyzeDirectory(_dir);

    // --- npm ---------------------------------------------------------------

    [Fact]
    public void PackageJson_SplitsProdAndDevDependencies()
    {
        Write("package.json", """
        {
          "name": "demo",
          "dependencies": { "express": "^4.18.2", "lodash": "4.17.21" },
          "devDependencies": { "jest": "^29.0.0" }
        }
        """);

        var deps = Analyze();

        Assert.Equal(3, deps.Count);
        Assert.All(deps, d => Assert.Equal("npm", d.Ecosystem));
        var express = deps.Single(d => d.Name == "express");
        Assert.Equal("^4.18.2", express.Version);
        Assert.False(express.IsDev);
        Assert.True(deps.Single(d => d.Name == "jest").IsDev);
    }

    // --- pip: requirements.txt --------------------------------------------

    [Fact]
    public void RequirementsTxt_ParsesOperatorsExtrasAndSkipsNoise()
    {
        Write("requirements.txt", """
        # a comment
        fastapi==0.110.0
        uvicorn[standard]>=0.27
        requests
        -r other.txt
        django>=4 ; python_version >= "3.10"
        """);

        var deps = Analyze();

        Assert.Equal(4, deps.Count);
        Assert.Equal("==0.110.0", deps.Single(d => d.Name == "fastapi").Version);
        Assert.Equal("uvicorn", deps.Single(d => d.Name == "uvicorn").Name);   // extras stripped
        Assert.Equal(">=0.27", deps.Single(d => d.Name == "uvicorn").Version);
        Assert.Equal("*", deps.Single(d => d.Name == "requests").Version);
        Assert.Equal(">=4", deps.Single(d => d.Name == "django").Version);     // marker stripped
        Assert.All(deps, d => Assert.Equal("pip", d.Ecosystem));
        Assert.DoesNotContain(deps, d => d.Name.StartsWith('-'));
    }

    [Fact]
    public void RequirementsDevTxt_MarksDependenciesAsDev()
    {
        Write("requirements-dev.txt", "pytest==8.0.0\n");

        var dep = Assert.Single(Analyze());

        Assert.True(dep.IsDev);
        Assert.Equal("pytest", dep.Name);
    }

    // --- pip: pyproject.toml ----------------------------------------------

    [Fact]
    public void Pyproject_Pep621_ParsesDependencyArray()
    {
        Write("pyproject.toml", """
        [project]
        name = "demo"
        dependencies = [
          "fastapi>=0.110",
          "pydantic==2.6.0",
        ]

        [project.optional-dependencies]
        dev = ["pytest>=8", "ruff"]
        """);

        var deps = Analyze();

        Assert.Equal(">=0.110", deps.Single(d => d.Name == "fastapi").Version);
        Assert.Equal("==2.6.0", deps.Single(d => d.Name == "pydantic").Version);
        Assert.True(deps.Single(d => d.Name == "pytest").IsDev);
        Assert.True(deps.Single(d => d.Name == "ruff").IsDev);
        Assert.All(deps, d => Assert.Equal("pip", d.Ecosystem));
    }

    [Fact]
    public void Pyproject_Poetry_SkipsPythonAndFlagsDevGroup()
    {
        Write("pyproject.toml", """
        [tool.poetry.dependencies]
        python = "^3.11"
        requests = "^2.31"
        httpx = { version = "^0.27", optional = true }

        [tool.poetry.group.dev.dependencies]
        black = "^24.0"
        """);

        var deps = Analyze();

        Assert.DoesNotContain(deps, d => d.Name == "python");
        Assert.Equal("^2.31", deps.Single(d => d.Name == "requests").Version);
        Assert.Equal("^0.27", deps.Single(d => d.Name == "httpx").Version);    // inline table version
        Assert.False(deps.Single(d => d.Name == "requests").IsDev);
        Assert.True(deps.Single(d => d.Name == "black").IsDev);
    }

    // --- rust --------------------------------------------------------------

    [Fact]
    public void CargoToml_ParsesDepsDevDepsAndInlineTable()
    {
        Write("Cargo.toml", """
        [package]
        name = "demo"

        [dependencies]
        serde = "1.0"
        tokio = { version = "1.36", features = ["full"] }

        [dev-dependencies]
        criterion = "0.5"
        """);

        var deps = Analyze();

        Assert.Equal("1.0", deps.Single(d => d.Name == "serde").Version);
        Assert.Equal("1.36", deps.Single(d => d.Name == "tokio").Version);
        Assert.False(deps.Single(d => d.Name == "serde").IsDev);
        Assert.True(deps.Single(d => d.Name == "criterion").IsDev);
        Assert.All(deps, d => Assert.Equal("cargo", d.Ecosystem));
    }

    // --- go ----------------------------------------------------------------

    [Fact]
    public void GoMod_ParsesBlockAndSingleAndSkipsIndirect()
    {
        Write("go.mod", """
        module example.com/demo

        go 1.22

        require (
            github.com/gin-gonic/gin v1.9.1
            github.com/stretchr/testify v1.9.0 // indirect
        )

        require github.com/spf13/cobra v1.8.0
        """);

        var deps = Analyze();

        Assert.Equal(2, deps.Count);
        Assert.Equal("v1.9.1", deps.Single(d => d.Name == "github.com/gin-gonic/gin").Version);
        Assert.Contains(deps, d => d.Name == "github.com/spf13/cobra");
        Assert.DoesNotContain(deps, d => d.Name == "github.com/stretchr/testify"); // indirect skipped
        Assert.All(deps, d => Assert.Equal("go", d.Ecosystem));
    }

    // --- php ---------------------------------------------------------------

    [Fact]
    public void ComposerJson_SplitsRequireAndRequireDev()
    {
        Write("composer.json", """
        {
          "require": { "php": ">=8.1", "laravel/framework": "^11.0" },
          "require-dev": { "phpunit/phpunit": "^11.0" }
        }
        """);

        var deps = Analyze();

        Assert.Equal("^11.0", deps.Single(d => d.Name == "laravel/framework").Version);
        Assert.False(deps.Single(d => d.Name == "laravel/framework").IsDev);
        Assert.True(deps.Single(d => d.Name == "phpunit/phpunit").IsDev);
        Assert.All(deps, d => Assert.Equal("composer", d.Ecosystem));
    }

    // --- maven -------------------------------------------------------------

    [Fact]
    public void PomXml_BuildsGroupArtifactNameAndFlagsTestScope()
    {
        Write("pom.xml", """
        <project xmlns="http://maven.apache.org/POM/4.0.0">
          <dependencies>
            <dependency>
              <groupId>org.springframework.boot</groupId>
              <artifactId>spring-boot-starter-web</artifactId>
              <version>3.2.0</version>
            </dependency>
            <dependency>
              <groupId>org.junit.jupiter</groupId>
              <artifactId>junit-jupiter</artifactId>
              <version>5.10.0</version>
              <scope>test</scope>
            </dependency>
          </dependencies>
        </project>
        """);

        var deps = Analyze();

        var web = deps.Single(d => d.Name == "org.springframework.boot:spring-boot-starter-web");
        Assert.Equal("3.2.0", web.Version);
        Assert.False(web.IsDev);
        Assert.True(deps.Single(d => d.Name == "org.junit.jupiter:junit-jupiter").IsDev);
        Assert.All(deps, d => Assert.Equal("maven", d.Ecosystem));
    }

    // --- .NET --------------------------------------------------------------

    [Fact]
    public void Csproj_ParsesAttributeAndElementVersions()
    {
        Write("App.csproj", """
        <Project Sdk="Microsoft.NET.Sdk">
          <ItemGroup>
            <PackageReference Include="Serilog" Version="3.1.1" />
            <PackageReference Include="Dapper">
              <Version>2.1.35</Version>
            </PackageReference>
          </ItemGroup>
        </Project>
        """);

        var deps = Analyze();

        Assert.Equal("3.1.1", deps.Single(d => d.Name == "Serilog").Version);
        Assert.Equal("2.1.35", deps.Single(d => d.Name == "Dapper").Version);   // element-form version
        Assert.All(deps, d => Assert.Equal("nuget", d.Ecosystem));
    }

    // --- cross-cutting -----------------------------------------------------

    [Fact]
    public void AnalyzeDirectory_PrunesVendoredDirsAndSpansEcosystems()
    {
        Write("package.json", """{ "dependencies": { "react": "^18.0.0" } }""");
        Write("backend/requirements.txt", "flask==3.0.0\n");
        // vendored manifest that must be ignored
        Write("node_modules/leftpad/package.json", """{ "dependencies": { "evil": "1.0.0" } }""");

        var deps = Analyze();

        Assert.Contains(deps, d => d.Name == "react" && d.Ecosystem == "npm");
        Assert.Contains(deps, d => d.Name == "flask" && d.Ecosystem == "pip");
        Assert.DoesNotContain(deps, d => d.Name == "evil");          // node_modules pruned
        // manifest labels are relative + distinguishable
        Assert.Equal("backend/requirements.txt", deps.Single(d => d.Name == "flask").Manifest);
    }

    [Fact]
    public void AnalyzeDirectory_MalformedManifest_IsSkippedNotThrown()
    {
        Write("package.json", "{ this is not valid json ");
        Write("requirements.txt", "valid-pkg==1.0\n");

        var deps = Analyze();   // must not throw

        Assert.Single(deps);
        Assert.Equal("valid-pkg", deps[0].Name);
    }

    [Fact]
    public void AnalyzeDirectory_MissingPath_ReturnsEmpty()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"cortex-nope-{Guid.NewGuid():N}");
        Assert.Empty(_analyzer.AnalyzeDirectory(missing));
    }
}
