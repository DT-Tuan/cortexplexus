using System.Text.Json;
using System.Xml.Linq;
using CortexPlexus.Core.Models;

namespace CortexPlexus.Parsing;

/// <summary>
/// Multi-ecosystem dependency manifest parser. Sibling to <see cref="NuGetAuditAnalyzer"/>,
/// generalized to npm / pip / go / cargo / composer / maven (+ .csproj for one-stop audit).
/// <para>
/// Pure file parsing — no AST, no graph — so it is safe on any checkout path and fully
/// unit-testable offline. Version handling is best-effort: the declared constraint is captured
/// verbatim, never resolved. Parsing is heuristic per format (ADR-016 C1); malformed manifests
/// are skipped rather than thrown.
/// </para>
/// </summary>
public sealed class PackageManifestAnalyzer
{
    // Directories that never hold first-party manifests but can be huge / vendored.
    private static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", ".git", "bin", "obj", "vendor", "venv", ".venv", "env",
        "dist", "build", "target", ".tox", "__pycache__", ".idea", ".vs",
    };

    /// <summary>Walk a directory tree (pruning vendored dirs) and parse every recognized manifest.</summary>
    public IReadOnlyList<DependencyInfo> AnalyzeDirectory(string directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath))
            return [];

        var results = new List<DependencyInfo>();
        foreach (var file in EnumerateManifests(directoryPath))
        {
            try { results.AddRange(AnalyzeFile(file, directoryPath)); }
            catch { /* malformed manifest — skip, best-effort */ }
        }
        return results;
    }

    /// <summary>Parse a single manifest file, dispatching by file name.</summary>
    public IReadOnlyList<DependencyInfo> AnalyzeFile(string filePath, string? rootForRelative = null)
    {
        if (!File.Exists(filePath)) return [];

        var name = Path.GetFileName(filePath);
        var manifest = RelativeLabel(filePath, rootForRelative);
        var text = File.ReadAllText(filePath);

        if (name.Equals("package.json", StringComparison.OrdinalIgnoreCase))
            return ParsePackageJson(text, manifest);
        if (name.Equals("composer.json", StringComparison.OrdinalIgnoreCase))
            return ParseComposerJson(text, manifest);
        if (IsRequirementsTxt(name))
            return ParseRequirementsTxt(text, manifest, devManifest: NameSuggestsDev(name));
        if (name.Equals("pyproject.toml", StringComparison.OrdinalIgnoreCase))
            return ParsePyproject(text, manifest);
        if (name.Equals("Cargo.toml", StringComparison.OrdinalIgnoreCase))
            return ParseCargoToml(text, manifest);
        if (name.Equals("go.mod", StringComparison.OrdinalIgnoreCase))
            return ParseGoMod(text, manifest);
        if (name.Equals("pom.xml", StringComparison.OrdinalIgnoreCase))
            return ParsePomXml(text, manifest);
        if (name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            return ParseCsproj(text, manifest);

        return [];
    }

    // --- directory walk ----------------------------------------------------

    private static IEnumerable<string> EnumerateManifests(string root)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();

            string[] subdirs;
            try { subdirs = Directory.GetDirectories(dir); }
            catch { continue; }
            foreach (var sub in subdirs)
            {
                if (!SkipDirs.Contains(Path.GetFileName(sub)))
                    stack.Push(sub);
            }

            string[] files;
            try { files = Directory.GetFiles(dir); }
            catch { continue; }
            foreach (var f in files)
            {
                if (IsManifest(Path.GetFileName(f)))
                    yield return f;
            }
        }
    }

    private static bool IsManifest(string fileName) =>
        fileName.Equals("package.json", StringComparison.OrdinalIgnoreCase) ||
        fileName.Equals("composer.json", StringComparison.OrdinalIgnoreCase) ||
        fileName.Equals("pyproject.toml", StringComparison.OrdinalIgnoreCase) ||
        fileName.Equals("Cargo.toml", StringComparison.OrdinalIgnoreCase) ||
        fileName.Equals("go.mod", StringComparison.OrdinalIgnoreCase) ||
        fileName.Equals("pom.xml", StringComparison.OrdinalIgnoreCase) ||
        IsRequirementsTxt(fileName) ||
        fileName.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase);

    private static bool IsRequirementsTxt(string fileName) =>
        fileName.StartsWith("requirements", StringComparison.OrdinalIgnoreCase) &&
        fileName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase);

    private static bool NameSuggestsDev(string fileName) =>
        fileName.Contains("dev", StringComparison.OrdinalIgnoreCase) ||
        fileName.Contains("test", StringComparison.OrdinalIgnoreCase);

    private static string RelativeLabel(string filePath, string? root)
    {
        if (string.IsNullOrEmpty(root)) return Path.GetFileName(filePath);
        try { return Path.GetRelativePath(root, filePath).Replace('\\', '/'); }
        catch { return Path.GetFileName(filePath); }
    }

    // --- npm (package.json) ------------------------------------------------

    private static List<DependencyInfo> ParsePackageJson(string text, string manifest)
    {
        var deps = new List<DependencyInfo>();
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        AddJsonDepObject(root, "dependencies", "npm", manifest, isDev: false, deps);
        AddJsonDepObject(root, "devDependencies", "npm", manifest, isDev: true, deps);
        AddJsonDepObject(root, "peerDependencies", "npm", manifest, isDev: false, deps);
        return deps;
    }

    // --- php (composer.json) -----------------------------------------------

    private static List<DependencyInfo> ParseComposerJson(string text, string manifest)
    {
        var deps = new List<DependencyInfo>();
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        AddJsonDepObject(root, "require", "composer", manifest, isDev: false, deps);
        AddJsonDepObject(root, "require-dev", "composer", manifest, isDev: true, deps);
        return deps;
    }

    private static void AddJsonDepObject(
        JsonElement root, string prop, string eco, string manifest, bool isDev, List<DependencyInfo> into)
    {
        if (root.ValueKind != JsonValueKind.Object) return;
        if (!root.TryGetProperty(prop, out var obj) || obj.ValueKind != JsonValueKind.Object) return;
        foreach (var p in obj.EnumerateObject())
        {
            var version = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "*" : "*";
            into.Add(new DependencyInfo(p.Name, version, eco, manifest, isDev));
        }
    }

    // --- pip (requirements*.txt) -------------------------------------------

    private static List<DependencyInfo> ParseRequirementsTxt(string text, string manifest, bool devManifest)
    {
        var deps = new List<DependencyInfo>();
        foreach (var raw in SplitLines(text))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            if (line[0] == '-') continue;                       // -r, -e, --hash, options
            if (line.StartsWith("git+", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("http", StringComparison.OrdinalIgnoreCase)) continue;

            // Drop inline comment and environment markers.
            var hash = line.IndexOf(" #", StringComparison.Ordinal);
            if (hash >= 0) line = line[..hash].Trim();
            var marker = line.IndexOf(';');
            if (marker >= 0) line = line[..marker].Trim();
            if (line.Length == 0) continue;

            var (name, version) = SplitNameVersionPep508(line);
            if (name.Length == 0) continue;
            deps.Add(new DependencyInfo(name, version, "pip", manifest, devManifest));
        }
        return deps;
    }

    /// <summary>Split a PEP 508 requirement ("name[extra]==1.2 ; marker") into name + constraint.</summary>
    private static (string Name, string Version) SplitNameVersionPep508(string spec)
    {
        var idx = spec.IndexOfAny(['=', '>', '<', '~', '!', ' ', '\t', '@']);
        if (idx < 0)
            return (StripExtras(spec).Trim(), "*");
        var name = StripExtras(spec[..idx]).Trim();
        var version = spec[idx..].Trim();
        return (name, version.Length == 0 ? "*" : version);
    }

    private static string StripExtras(string name)
    {
        var bracket = name.IndexOf('[');
        return bracket >= 0 ? name[..bracket] : name;
    }

    // --- python (pyproject.toml) -------------------------------------------
    // Handles both PEP 621 ([project].dependencies array) and Poetry tables.

    private static List<DependencyInfo> ParsePyproject(string text, string manifest)
    {
        var deps = new List<DependencyInfo>();
        var lines = SplitLines(text);
        string section = "";

        for (var i = 0; i < lines.Count; i++)
        {
            var line = StripTomlComment(lines[i]).Trim();
            if (line.Length == 0) continue;

            if (IsTomlSectionHeader(line, out var header))
            {
                section = header;
                continue;
            }

            // PEP 621: [project] dependencies = [...] / optional-dependencies tables.
            if (section == "project" && StartsKey(line, "dependencies"))
            {
                CollectPep508Array(lines, ref i, deps, manifest, isDev: false);
                continue;
            }
            if (section.StartsWith("project.optional-dependencies", StringComparison.Ordinal))
            {
                // each key is a group: <group> = [ "pkg>=1", ... ]. The dev/test signal lives in
                // the group key (e.g. dev = [...]), not the section header.
                if (line.Contains('['))
                {
                    var eq = line.IndexOf('=');
                    var group = eq > 0 ? line[..eq].Trim().Trim('"', '\'') : "";
                    CollectPep508Array(lines, ref i, deps, manifest, isDev: NameSuggestsDev(group));
                }
                continue;
            }

            // Poetry: [tool.poetry.dependencies] / dev groups → key = "constraint".
            if (section is "tool.poetry.dependencies" or "tool.poetry.dev-dependencies" ||
                section.StartsWith("tool.poetry.group.", StringComparison.Ordinal))
            {
                var (name, version) = ParseTomlKeyDep(line);
                if (name.Length == 0 || name.Equals("python", StringComparison.OrdinalIgnoreCase)) continue;
                var dev = section.Contains("dev", StringComparison.OrdinalIgnoreCase) ||
                          section.Contains("test", StringComparison.OrdinalIgnoreCase) ||
                          section != "tool.poetry.dependencies";
                deps.Add(new DependencyInfo(name, version, "pip", manifest, dev));
            }
        }
        return deps;
    }

    // Collect a `dependencies = [ "a>=1", "b" ]` array starting at lines[i]; advances i to the ']' line.
    private static void CollectPep508Array(
        List<string> lines, ref int i, List<DependencyInfo> deps, string manifest, bool isDev)
    {
        var buffer = lines[i];
        while (!buffer.Contains(']') && i + 1 < lines.Count)
            buffer += " " + lines[++i];

        var open = buffer.IndexOf('[');
        var close = buffer.LastIndexOf(']');
        if (open < 0 || close <= open) return;
        var inner = buffer[(open + 1)..close];

        foreach (var token in inner.Split(','))
        {
            var item = token.Trim().Trim('"', '\'').Trim();
            if (item.Length == 0) continue;
            var (name, version) = SplitNameVersionPep508(item);
            if (name.Length == 0) continue;
            deps.Add(new DependencyInfo(name, version, "pip", manifest, isDev));
        }
    }

    // --- rust (Cargo.toml) -------------------------------------------------

    private static List<DependencyInfo> ParseCargoToml(string text, string manifest)
    {
        var deps = new List<DependencyInfo>();
        var section = "";
        foreach (var rawLine in SplitLines(text))
        {
            var line = StripTomlComment(rawLine).Trim();
            if (line.Length == 0) continue;
            if (IsTomlSectionHeader(line, out var header)) { section = header; continue; }

            var isDeps = section is "dependencies" or "dev-dependencies" or "build-dependencies" ||
                         section.EndsWith(".dependencies", StringComparison.Ordinal) ||
                         section.EndsWith(".dev-dependencies", StringComparison.Ordinal);
            if (!isDeps) continue;

            var (name, version) = ParseTomlKeyDep(line);
            if (name.Length == 0) continue;
            var dev = section.Contains("dev-dependencies", StringComparison.Ordinal);
            deps.Add(new DependencyInfo(name, version, "cargo", manifest, dev));
        }
        return deps;
    }

    // --- go (go.mod) -------------------------------------------------------

    private static List<DependencyInfo> ParseGoMod(string text, string manifest)
    {
        var deps = new List<DependencyInfo>();
        var inRequireBlock = false;
        foreach (var rawLine in SplitLines(text))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal)) continue;

            if (inRequireBlock)
            {
                if (line.StartsWith(')')) { inRequireBlock = false; continue; }
                AddGoRequire(line, manifest, deps);
                continue;
            }

            if (line.StartsWith("require (", StringComparison.Ordinal) || line == "require(")
            {
                inRequireBlock = true;
                continue;
            }
            if (line.StartsWith("require ", StringComparison.Ordinal))
                AddGoRequire(line["require ".Length..], manifest, deps);
        }
        return deps;
    }

    private static void AddGoRequire(string line, string manifest, List<DependencyInfo> deps)
    {
        // Skip transitive deps; an audit lists what the project directly declares.
        var indirect = line.Contains("// indirect", StringComparison.Ordinal);
        var comment = line.IndexOf("//", StringComparison.Ordinal);
        if (comment >= 0) line = line[..comment];
        var parts = line.Trim().Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || indirect) return;
        deps.Add(new DependencyInfo(parts[0], parts[1], "go", manifest, IsDev: false));
    }

    // --- maven (pom.xml) ---------------------------------------------------

    private static List<DependencyInfo> ParsePomXml(string text, string manifest)
    {
        var deps = new List<DependencyInfo>();
        var doc = XDocument.Parse(text);
        foreach (var dep in doc.Descendants().Where(e => e.Name.LocalName == "dependency"))
        {
            var groupId = LocalValue(dep, "groupId");
            var artifactId = LocalValue(dep, "artifactId");
            if (artifactId is null) continue;
            var version = LocalValue(dep, "version") ?? "*";
            var scope = LocalValue(dep, "scope");
            var name = groupId is null ? artifactId : $"{groupId}:{artifactId}";
            var dev = scope is "test" or "provided";
            deps.Add(new DependencyInfo(name, version.Trim(), "maven", manifest, dev));
        }
        return deps;
    }

    private static string? LocalValue(XElement parent, string localName) =>
        parent.Elements().FirstOrDefault(e => e.Name.LocalName == localName)?.Value?.Trim();

    // --- .NET (.csproj) ----------------------------------------------------

    private static List<DependencyInfo> ParseCsproj(string text, string manifest)
    {
        var deps = new List<DependencyInfo>();
        var doc = XDocument.Parse(text);
        foreach (var pr in doc.Descendants().Where(e => e.Name.LocalName == "PackageReference"))
        {
            var id = pr.Attribute("Include")?.Value ?? pr.Attribute("Update")?.Value;
            if (string.IsNullOrWhiteSpace(id)) continue;
            var version = pr.Attribute("Version")?.Value
                ?? pr.Elements().FirstOrDefault(e => e.Name.LocalName == "Version")?.Value
                ?? "*";
            deps.Add(new DependencyInfo(id, version, "nuget", manifest, IsDev: false));
        }
        return deps;
    }

    // --- TOML helpers (minimal, line-based) --------------------------------

    private static bool IsTomlSectionHeader(string trimmedLine, out string header)
    {
        header = "";
        if (trimmedLine.Length < 2 || trimmedLine[0] != '[') return false;
        var end = trimmedLine.IndexOf(']');
        if (end <= 1) return false;
        // [[array.of.tables]] → strip both brackets
        var inner = trimmedLine[1..end];
        if (inner.StartsWith('[')) inner = inner[1..];
        header = inner.Trim();
        return true;
    }

    private static bool StartsKey(string trimmedLine, string key) =>
        trimmedLine.StartsWith(key, StringComparison.Ordinal) &&
        trimmedLine[key.Length..].TrimStart().StartsWith('=');

    // Parse `name = "1.2"` or `name = { version = "1.2", features = [...] }`.
    private static (string Name, string Version) ParseTomlKeyDep(string line)
    {
        var eq = line.IndexOf('=');
        if (eq <= 0) return ("", "");
        var name = line[..eq].Trim().Trim('"', '\'');
        var value = line[(eq + 1)..].Trim();
        if (name.Length == 0) return ("", "");

        if (value.StartsWith('{'))
        {
            // inline table — pull the version = "..." field if present
            var v = ExtractInlineField(value, "version");
            return (name, v ?? "*");
        }
        var literal = value.Trim().Trim('"', '\'').Trim();
        return (name, literal.Length == 0 ? "*" : literal);
    }

    private static string? ExtractInlineField(string inlineTable, string field)
    {
        var key = inlineTable.IndexOf(field, StringComparison.Ordinal);
        if (key < 0) return null;
        var eq = inlineTable.IndexOf('=', key);
        if (eq < 0) return null;
        var rest = inlineTable[(eq + 1)..].TrimStart();
        if (rest.Length == 0 || (rest[0] != '"' && rest[0] != '\'')) return null;
        var quote = rest[0];
        var close = rest.IndexOf(quote, 1);
        return close < 0 ? null : rest[1..close];
    }

    private static string StripTomlComment(string line)
    {
        // Naive: a '#' not inside quotes starts a comment. Good enough for dep lines.
        var inSingle = false;
        var inDouble = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '\'' && !inDouble) inSingle = !inSingle;
            else if (c == '"' && !inSingle) inDouble = !inDouble;
            else if (c == '#' && !inSingle && !inDouble) return line[..i];
        }
        return line;
    }

    private static List<string> SplitLines(string text) =>
        [.. text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n')];
}
