using CortexPlexus.Agent;

namespace CortexPlexus.Agent.Tests;

/// <summary>
/// Tests cho LocalIndexer pure helpers (file discovery, hash diff, Tree-sitter classification).
///
/// Phạm vi: TEST-PLAN.md #101, #102, #103, #104
///
/// Lưu ý: LocalIndexer constructor tạo DI container + Roslyn parser — expensive.
/// Test focus vào static helpers (internal) mà không cần instance.
/// </summary>
public class LocalIndexerTests
{
    // Helper: tạo temp dir với files, return (dir, action to cleanup).
    private static (string dir, Action cleanup) CreateTempDir(params (string relativePath, string content)[] files)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"cortex-local-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);

        foreach (var (rel, content) in files)
        {
            var full = Path.Combine(dir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }

        return (dir, () => Directory.Delete(dir, recursive: true));
    }

    // === #102 (reworked for DiffAgainstServer): hash diff + deletion detection ===
    //
    // Server hashes are stored project-RELATIVE (the agent uploads them relative);
    // local hashes are ABSOLUTE. The old FindChangedFiles compared the raw keys, which
    // never matched — every watch start re-parsed the whole repo and deletions were
    // undetectable (the stale-symbol bug). DiffAgainstServer normalizes both forms.

    [Fact]
    public void DiffAgainstServer_NewFileNotInServerHashes_Detected()
    {
        var localHashes = new Dictionary<string, string>
        {
            ["/repo/new.cs"] = "hash1",
            ["/repo/existing.cs"] = "hash2",
        };
        var serverHashes = new Dictionary<string, string>
        {
            ["existing.cs"] = "hash2", // relative form — new.cs chưa có
        };

        var (changed, deleted) = LocalIndexer.DiffAgainstServer(localHashes, serverHashes, "/repo");

        Assert.Contains("/repo/new.cs", changed);
        Assert.DoesNotContain("/repo/existing.cs", changed);
        Assert.Empty(deleted);
    }

    [Fact]
    public void DiffAgainstServer_HashChanged_Detected()
    {
        var localHashes = new Dictionary<string, string>
        {
            ["/repo/modified.cs"] = "new-hash",
        };
        var serverHashes = new Dictionary<string, string>
        {
            ["modified.cs"] = "old-hash",
        };

        var (changed, deleted) = LocalIndexer.DiffAgainstServer(localHashes, serverHashes, "/repo");

        Assert.Single(changed);
        Assert.Equal("/repo/modified.cs", changed[0]);
        Assert.Empty(deleted);
    }

    [Fact]
    public void DiffAgainstServer_RelativeServerKeys_MatchAbsoluteLocalKeys()
    {
        // The regression this method exists for: identical content must diff to
        // NOTHING even though local keys are absolute and server keys are relative.
        var localHashes = new Dictionary<string, string>
        {
            ["/repo/src/a.cs"] = "h1",
            ["/repo/src/b.cs"] = "h2",
        };
        var serverHashes = new Dictionary<string, string>
        {
            [$"src{Path.DirectorySeparatorChar}a.cs"] = "h1",
            [$"src{Path.DirectorySeparatorChar}b.cs"] = "h2",
        };

        var (changed, deleted) = LocalIndexer.DiffAgainstServer(localHashes, serverHashes, "/repo");

        Assert.Empty(changed);
        Assert.Empty(deleted);
    }

    [Fact]
    public void DiffAgainstServer_LegacyAbsoluteServerKeys_StillMatch()
    {
        // Legacy servers stored absolute paths — those rows must not re-trigger a
        // full parse nor be misreported as deletions.
        var localHashes = new Dictionary<string, string> { ["/repo/a.cs"] = "h1" };
        var serverHashes = new Dictionary<string, string> { ["/repo/a.cs"] = "h1" };

        var (changed, deleted) = LocalIndexer.DiffAgainstServer(localHashes, serverHashes, "/repo");

        Assert.Empty(changed);
        Assert.Empty(deleted);
    }

    [Fact]
    public void DiffAgainstServer_ServerFileMissingLocally_ReportedDeleted()
    {
        // The stale-symbol case: DirectorService.cs deleted locally, server still has
        // its hash. Must surface as deleted (verbatim server key) so the server can
        // drop its rows — the old code silently ignored it forever.
        var localHashes = new Dictionary<string, string> { ["/repo/kept.cs"] = "h1" };
        var serverHashes = new Dictionary<string, string>
        {
            ["kept.cs"] = "h1",
            ["deleted/DirectorService.cs"] = "h-old",
        };

        var (changed, deleted) = LocalIndexer.DiffAgainstServer(localHashes, serverHashes, "/repo");

        Assert.Empty(changed);
        Assert.Single(deleted);
        Assert.Equal("deleted/DirectorService.cs", deleted[0]);
    }

    [Fact]
    public void DiffAgainstServer_EmptyLocalHashes_ReportsAllServerFilesDeleted()
    {
        var (changed, deleted) = LocalIndexer.DiffAgainstServer(
            [],
            new Dictionary<string, string> { ["deleted.cs"] = "old" },
            "/repo");

        Assert.Empty(changed);
        Assert.Single(deleted);
        Assert.Equal("deleted.cs", deleted[0]);
    }

    // === #101 partial: ComputeFileHash determinism ===
    [Fact]
    public void ComputeFileHash_SameContent_SameHash()
    {
        // Mục đích: Cùng content → cùng hash (SHA256 deterministic).
        var (dir, cleanup) = CreateTempDir(
            ("a.cs", "same content"),
            ("b.cs", "same content"));
        try
        {
            var hashA = LocalIndexer.ComputeFileHash(Path.Combine(dir, "a.cs"));
            var hashB = LocalIndexer.ComputeFileHash(Path.Combine(dir, "b.cs"));

            Assert.Equal(hashA, hashB);
            Assert.Equal(64, hashA.Length); // SHA256 hex = 64 chars
        }
        finally
        {
            cleanup();
        }
    }

    [Fact]
    public void ComputeFileHash_DifferentContent_DifferentHash()
    {
        var (dir, cleanup) = CreateTempDir(
            ("a.cs", "content A"),
            ("b.cs", "content B"));
        try
        {
            var hashA = LocalIndexer.ComputeFileHash(Path.Combine(dir, "a.cs"));
            var hashB = LocalIndexer.ComputeFileHash(Path.Combine(dir, "b.cs"));

            Assert.NotEqual(hashA, hashB);
        }
        finally
        {
            cleanup();
        }
    }

    // === FindSolutionsAndProjects ===
    [Fact]
    public void FindSolutionsAndProjects_SolutionWithMatchingCsproj_ReturnsOnlySln()
    {
        // When the .csproj is referenced by the .sln, we only need the .sln
        // (MSBuildWorkspace will pull the project in when it opens the solution).
        var slnContent = """
            Microsoft Visual Studio Solution File, Format Version 12.00
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "MyApp", "src\MyApp.csproj", "{00000000-0000-0000-0000-000000000000}"
            EndProject
            Global
            EndGlobal
            """;
        var (dir, cleanup) = CreateTempDir(
            ("MyApp.sln", slnContent),
            ("src/MyApp.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>"));
        try
        {
            var result = LocalIndexer.FindSolutionsAndProjects(dir);

            Assert.Single(result);
            Assert.EndsWith(".sln", result[0]);
        }
        finally
        {
            cleanup();
        }
    }

    // === R20 Issue #2: orphan .csproj (not referenced in any .sln) must be discovered ===

    /// <summary>
    /// CortexFlow smoke test reported get_test_coverage returning empty because
    /// CortexFlow.Tests.csproj is NOT referenced by CortexFlow.sln, so Roslyn
    /// MSBuildWorkspace never parsed it, so no test methods got is_test_method=true.
    /// After R20: agent returns sln + any orphan csproj, and both get parsed.
    /// </summary>
    [Fact]
    public void FindSolutionsAndProjects_SolutionWithOrphanCsproj_ReturnsBoth()
    {
        // Solution references src/MyApp.csproj but NOT tests/MyApp.Tests.csproj
        var slnContent = """
            Microsoft Visual Studio Solution File, Format Version 12.00
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "MyApp", "src\MyApp.csproj", "{00000000-0000-0000-0000-000000000000}"
            EndProject
            """;
        var (dir, cleanup) = CreateTempDir(
            ("MyApp.sln", slnContent),
            ("src/MyApp.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>"),
            ("tests/MyApp.Tests.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>"));
        try
        {
            var result = LocalIndexer.FindSolutionsAndProjects(dir);

            Assert.Equal(2, result.Count);
            Assert.Contains(result, p => p.EndsWith(".sln"));
            Assert.Contains(result, p => p.EndsWith("MyApp.Tests.csproj"));
            // MyApp.csproj itself should NOT appear — it's already in the sln
            Assert.DoesNotContain(result, p => p.EndsWith("MyApp.csproj") && !p.EndsWith("Tests.csproj"));
        }
        finally
        {
            cleanup();
        }
    }

    [Fact]
    public void ExtractCsprojPathsFromSln_ParsesProjectLines()
    {
        // Raw .sln project line format:
        // Project("{GUID}") = "ProjectName", "relative\path\Project.csproj", "{GUID}"
        var slnContent = """
            Microsoft Visual Studio Solution File, Format Version 12.00
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "A", "src\A.csproj", "{AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA}"
            EndProject
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "B", "libs\B\B.csproj", "{BBBBBBBB-BBBB-BBBB-BBBB-BBBBBBBBBBBB}"
            EndProject
            Project("{2150E333-8FDC-42A3-9474-1A3956D46DE8}") = "SolutionFolder", "SolutionFolder", "{CCCCCCCC-CCCC-CCCC-CCCC-CCCCCCCCCCCC}"
            EndProject
            Global
            EndGlobal
            """;
        var (dir, cleanup) = CreateTempDir(("App.sln", slnContent));
        try
        {
            var slnPath = Path.Combine(dir, "App.sln");
            var results = LocalIndexer.ExtractCsprojPathsFromSln(slnPath).ToList();

            Assert.Equal(2, results.Count);
            // Paths are returned as absolute + normalized
            Assert.Contains(results, p => p.EndsWith("A.csproj"));
            Assert.Contains(results, p => p.EndsWith("B.csproj"));
            // Solution folders (no .csproj suffix) must be filtered out
            Assert.DoesNotContain(results, p => p.Contains("SolutionFolder"));
        }
        finally
        {
            cleanup();
        }
    }

    [Fact]
    public void ExtractCsprojPathsFromSln_ParsesSlnxXmlFormat()
    {
        // Modern .slnx (XML) format — projects nested in <Folder>. Without this, every
        // project becomes an "orphan" standalone unit and gets re-indexed N× per full
        // index (the 21× CortexPlexus startup bug).
        var slnxContent = """
            <Solution>
              <Folder Name="/src/">
                <Project Path="src/A/A.csproj" />
                <Project Path="src/B/B.csproj" />
              </Folder>
              <Project Path="tests/C/C.csproj" />
            </Solution>
            """;
        var (dir, cleanup) = CreateTempDir(("App.slnx", slnxContent));
        try
        {
            var slnxPath = Path.Combine(dir, "App.slnx");
            var results = LocalIndexer.ExtractCsprojPathsFromSln(slnxPath).ToList();

            Assert.Equal(3, results.Count);
            Assert.Contains(results, p => p.EndsWith("A.csproj"));
            Assert.Contains(results, p => p.EndsWith("B.csproj"));
            Assert.Contains(results, p => p.EndsWith("C.csproj"));
            // Absolute + normalized
            Assert.All(results, p => Assert.True(Path.IsPathRooted(p)));
        }
        finally
        {
            cleanup();
        }
    }

    [Fact]
    public void FindSolutionsAndProjects_OnlyCsproj_ReturnsCsprojFiles()
    {
        var (dir, cleanup) = CreateTempDir(
            ("MyApp/MyApp.csproj", "// csproj"),
            ("Lib/Lib.csproj", "// csproj"));
        try
        {
            var result = LocalIndexer.FindSolutionsAndProjects(dir);

            Assert.Equal(2, result.Count);
            Assert.All(result, p => Assert.EndsWith(".csproj", p));
        }
        finally
        {
            cleanup();
        }
    }

    [Fact]
    public void FindSolutionsAndProjects_ExcludesBinObj()
    {
        // Mục đích: .csproj trong bin/obj bị filter (dù rare case).
        var (dir, cleanup) = CreateTempDir(
            ("src/App.csproj", "// real"),
            ("bin/Debug/copy/App.csproj", "// copy in bin"));
        try
        {
            var result = LocalIndexer.FindSolutionsAndProjects(dir);

            Assert.Single(result);
            Assert.Contains("src", result[0]);
            Assert.DoesNotContain(result, p => p.Contains("bin"));
        }
        finally
        {
            cleanup();
        }
    }

    [Fact]
    public void FindSolutionsAndProjects_NonExistentPath_ReturnsEmpty()
    {
        var ghost = Path.Combine(Path.GetTempPath(), $"ghost-{Guid.NewGuid():N}");
        var result = LocalIndexer.FindSolutionsAndProjects(ghost);
        Assert.Empty(result);
    }

    [Fact]
    public void FindSolutionsAndProjects_DirectSolutionFile_Returns()
    {
        var (dir, cleanup) = CreateTempDir(("My.sln", "// sln"));
        try
        {
            var slnPath = Path.Combine(dir, "My.sln");

            // Truyền trực tiếp path file .sln → trả về [slnPath]
            var result = LocalIndexer.FindSolutionsAndProjects(slnPath);

            Assert.Single(result);
            Assert.Equal(slnPath, result[0]);
        }
        finally
        {
            cleanup();
        }
    }

    // === IsTreeSitterFile ===
    [Theory]
    [InlineData("file.ts", true)]
    [InlineData("file.tsx", true)]
    [InlineData("file.js", true)]
    [InlineData("file.jsx", true)]
    [InlineData("file.py", true)]
    [InlineData("file.cs", false)]
    [InlineData("file.md", false)]
    [InlineData("file.java", false)]
    [InlineData("file.go", false)]
    [InlineData("file.rs", false)]
    public void IsTreeSitterFile_ExtensionClassification(string filename, bool expected)
    {
        // Mục đích: Chỉ TS/TSX/JS/JSX/Python được parse bởi Tree-sitter trong LocalIndexer.
        // C# dùng Roslyn, Markdown dùng MarkdownParser, còn lại (Java/Go/Rust) không
        // được support bởi LocalIndexer (chỉ server-side).
        Assert.Equal(expected, LocalIndexer.IsTreeSitterFile(filename));
    }

    // === ComputeLocalHashes — end-to-end with temp files ===
    [Fact]
    public void ComputeLocalHashes_ScansAllSupportedExtensions()
    {
        // Mục đích: ComputeLocalHashes tìm đúng files đa ngôn ngữ, exclude build dirs.
        var (dir, cleanup) = CreateTempDir(
            ("src/App.cs", "class App {}"),
            ("src/index.ts", "export class Foo {}"),
            ("src/main.py", "def main(): pass"),
            ("README.md", "# Project"),
            ("src/image.png", "fake image"), // not supported
            ("bin/Debug/App.cs", "// excluded"),
            ("node_modules/lib/a.ts", "// excluded"));
        try
        {
            // LocalIndexer cần instance để gọi ComputeLocalHashes (non-static).
            // Dùng factory via ServiceProvider — nhưng constructor khởi tạo Roslyn services.
            // Workaround: tạo instance với empty config, không test server connectivity.
            var indexer = new LocalIndexer(
                "http://localhost:0",
                "test",
                Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

            var hashes = indexer.ComputeLocalHashes(dir);

            // Expected: App.cs, index.ts, main.py, README.md (4 files)
            // Excluded: image.png (extension), bin/App.cs, node_modules/a.ts
            Assert.Equal(4, hashes.Count);
            Assert.Contains(hashes.Keys, k => k.EndsWith("App.cs"));
            Assert.Contains(hashes.Keys, k => k.EndsWith("index.ts"));
            Assert.Contains(hashes.Keys, k => k.EndsWith("main.py"));
            Assert.Contains(hashes.Keys, k => k.EndsWith("README.md"));

            // Verify: không có file trong bin/ hay node_modules/
            Assert.DoesNotContain(hashes.Keys,
                k => k.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));
            Assert.DoesNotContain(hashes.Keys,
                k => k.Contains($"{Path.DirectorySeparatorChar}node_modules{Path.DirectorySeparatorChar}"));
        }
        finally
        {
            cleanup();
        }
    }
}
