using CortexPlexus.App.Indexing;

namespace CortexPlexus.App.Tests;

/// <summary>
/// FindContainingProject must resolve a changed file to its PROJECT ROOT (not the file's immediate
/// directory) for every language — otherwise the file watcher fragments one project into one repo
/// per sub-directory (the repo-splitting bug: a Python project registered "app", "tests",
/// "schemas", … as separate repositories).
/// </summary>
public sealed class IndexingWorkerTests : IDisposable
{
    private readonly string _root;

    public IndexingWorkerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"cortex-worker-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private string Make(string relativeDir, string fileName, string markerRelativeDir, string markerName)
    {
        var projectDir = Path.Combine(_root, markerRelativeDir);
        Directory.CreateDirectory(projectDir);
        File.WriteAllText(Path.Combine(projectDir, markerName), "");

        var fileDir = Path.Combine(_root, relativeDir);
        Directory.CreateDirectory(fileDir);
        var filePath = Path.Combine(fileDir, fileName);
        File.WriteAllText(filePath, "x = 1\n");
        return filePath;
    }

    [Fact]
    public void Python_DeepFile_ResolvesToPyprojectRoot()
    {
        // proj/pyproject.toml ; proj/app/db/queries/x.py  → resolves to proj
        var file = Make("proj/app/db/queries", "x.py", "proj", "pyproject.toml");
        Assert.Equal(Path.Combine(_root, "proj"), IndexingWorker.FindContainingProject(file));
    }

    [Fact]
    public void Npm_ResolvesToPackageJsonRoot()
    {
        var file = Make("web/src/components", "Button.ts", "web", "package.json");
        Assert.Equal(Path.Combine(_root, "web"), IndexingWorker.FindContainingProject(file));
    }

    [Fact]
    public void GitRoot_ResolvesToVcsRoot()
    {
        var repo = Path.Combine(_root, "repo");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));   // .git is a directory
        var fileDir = Path.Combine(repo, "pkg", "sub");
        Directory.CreateDirectory(fileDir);
        var file = Path.Combine(fileDir, "y.py");
        File.WriteAllText(file, "y = 2\n");

        Assert.Equal(repo, IndexingWorker.FindContainingProject(file));
    }

    [Fact]
    public void Monorepo_NearestManifestWins_NotGitRoot()
    {
        // root/.git ; root/packages/a/package.json ; root/packages/a/src/i.ts → packages/a (nearest)
        var root = Path.Combine(_root, "mono");
        Directory.CreateDirectory(Path.Combine(root, ".git"));
        var pkg = Path.Combine(root, "packages", "a");
        Directory.CreateDirectory(pkg);
        File.WriteAllText(Path.Combine(pkg, "package.json"), "{}");
        var fileDir = Path.Combine(pkg, "src");
        Directory.CreateDirectory(fileDir);
        var file = Path.Combine(fileDir, "i.ts");
        File.WriteAllText(file, "export const i = 1;\n");

        Assert.Equal(pkg, IndexingWorker.FindContainingProject(file));
    }

    [Fact]
    public void CSharp_StillResolvesToCsprojRoot()
    {
        var proj = Path.Combine(_root, "Svc");
        Directory.CreateDirectory(proj);
        File.WriteAllText(Path.Combine(proj, "Svc.csproj"), "<Project/>");
        var fileDir = Path.Combine(proj, "Handlers");
        Directory.CreateDirectory(fileDir);
        var file = Path.Combine(fileDir, "H.cs");
        File.WriteAllText(file, "class H {}");

        Assert.Equal(proj, IndexingWorker.FindContainingProject(file));
    }

    [Fact]
    public void NoMarker_ReturnsNull_SoCallerFallsBackToFileDir()
    {
        var fileDir = Path.Combine(_root, "loose", "deeper");
        Directory.CreateDirectory(fileDir);
        var file = Path.Combine(fileDir, "z.py");
        File.WriteAllText(file, "z = 3\n");

        // No marker anywhere under the temp root → null (worker then uses the file's directory).
        Assert.Null(IndexingWorker.FindContainingProject(file));
    }
}
