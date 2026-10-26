using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace SafeLocalHarness.Core;

public static class SafeDevelopmentProjectPolicy
{
    private static readonly IReadOnlySet<string> AllowedElements = new HashSet<string>(StringComparer.Ordinal)
    {
        "Project", "PropertyGroup", "ItemGroup", "TargetFramework", "Nullable", "ImplicitUsings",
        "LangVersion", "OutputType", "AssemblyName", "RootNamespace", "AllowUnsafeBlocks",
        "TreatWarningsAsErrors", "UseWPF", "UseWindowsForms", "EnableWindowsTargeting",
        "ProjectReference", "Compile", "None"
    };

    public static IReadOnlyList<string> Validate(string projectRoot, string projectPath)
    {
        var errors = new List<string>();
        var root = Path.GetFullPath(projectRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(projectPath);
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            return ["project.path"];
        var text = File.ReadAllText(path);
        if (text.Length > 20_000 || text.Contains("<!DOCTYPE", StringComparison.OrdinalIgnoreCase)) return ["project.xml-size-or-doctype"];
        XDocument document;
        try { document = XDocument.Parse(text, LoadOptions.None); }
        catch { return ["project.xml-invalid"]; }
        var project = document.Root;
        if (project is null || project.Name.LocalName != "Project") return ["project.root"];
        var sdk = project.Attribute("Sdk")?.Value;
        if (sdk is not ("Microsoft.NET.Sdk" or "Microsoft.NET.Sdk.WindowsDesktop")) errors.Add("project.sdk");
        foreach (var element in project.DescendantsAndSelf())
        {
            if (element.Name.NamespaceName.Length > 0 || !AllowedElements.Contains(element.Name.LocalName))
                errors.Add("project.element:" + element.Name.LocalName);
            if (element.Attributes().Any(attribute => attribute.Name.LocalName is "Condition" or "Sdk" && element != project))
                errors.Add("project.conditional-or-nested-sdk");
            if (element.Value.Contains("$(", StringComparison.Ordinal) || element.Value.Contains("@(", StringComparison.Ordinal))
                errors.Add("project.expression");
            if (element.Name.LocalName == "AllowUnsafeBlocks" && !element.Value.Trim().Equals("false", StringComparison.OrdinalIgnoreCase))
                errors.Add("project.unsafe");
            if (element.Name.LocalName == "ProjectReference") ValidateReference(root, path, element, errors);
            if (element.Name.LocalName is "Compile" or "None") ValidateInclude(root, path, element, errors);
        }
        if (!project.Descendants().Any(element => element.Name.LocalName == "TargetFramework")) errors.Add("project.target-framework");
        return errors.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static void ValidateReference(string root, string projectPath, XElement element, ICollection<string> errors)
    {
        var include = element.Attribute("Include")?.Value;
        if (!TryResolveInclude(root, projectPath, include, out var resolved) || !resolved.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            errors.Add("project.reference-path");
    }

    private static void ValidateInclude(string root, string projectPath, XElement element, ICollection<string> errors)
    {
        var include = element.Attribute("Include")?.Value;
        if (!TryResolveInclude(root, projectPath, include, out _)) errors.Add("project.include-path");
    }

    private static bool TryResolveInclude(string root, string projectPath, string? include, out string resolved)
    {
        resolved = "";
        if (string.IsNullOrWhiteSpace(include) || include.Contains('*') || include.Contains('?') || include.Contains("$(", StringComparison.Ordinal)) return false;
        resolved = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(projectPath)!, include));
        return resolved.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed record DevelopmentBuildToolchain(
    string? DotnetExecutable,
    string? NodeExecutable,
    string? CargoExecutable)
{
    public string? NpmCliPath { get; init; }
}

public static class SafeWebAssetPolicy
{
    private static readonly string[] RemoteTokens = ["http://", "https://", "src=\"//", "href=\"//", "url(//"];

    public static IReadOnlyList<string> Validate(string projectRoot)
    {
        var errors = new List<string>();
        foreach (var path in Directory.EnumerateFiles(projectRoot, "*", SearchOption.AllDirectories))
        {
            var extension = Path.GetExtension(path);
            if (extension is not (".html" or ".htm" or ".css" or ".js" or ".mjs" or ".cjs")) continue;
            var text = File.ReadAllText(path);
            foreach (var token in RemoteTokens)
                if (text.Contains(token, StringComparison.OrdinalIgnoreCase))
                    errors.Add("web.remote-reference:" + Path.GetRelativePath(projectRoot, path));
        }
        return errors.Distinct(StringComparer.Ordinal).ToArray();
    }
}

public static partial class SafeNodeProjectPolicy
{
    private static readonly IReadOnlyDictionary<string, string> AllowedScripts = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["build"] = "vite build",
        ["typecheck"] = "tsc --noEmit",
        ["test"] = "vitest run",
        ["lint"] = "eslint ."
    };

    [GeneratedRegex("^(?:@[a-z0-9][a-z0-9._-]*/)?[a-z0-9][a-z0-9._-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex PackageNamePattern();

    [GeneratedRegex("^[0-9]+\\.[0-9]+\\.[0-9]+(?:-[0-9A-Za-z.-]+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex ExactVersionPattern();

    public static IReadOnlyList<string> Validate(string projectRoot, string packagePath)
    {
        var errors = new List<string>();
        var root = Path.GetFullPath(projectRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(packagePath);
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(path).Equals("package.json", StringComparison.OrdinalIgnoreCase))
            return ["node.package-path"];
        var text = File.ReadAllText(path);
        if (text.Length > 50_000) return ["node.package-size"];
        JsonDocument document;
        try { document = JsonDocument.Parse(text); }
        catch { return ["node.package-json"]; }
        using (document)
        {
            var rootElement = document.RootElement;
            if (rootElement.ValueKind != JsonValueKind.Object) return ["node.package-root"];
            if (!rootElement.TryGetProperty("private", out var privateValue) || privateValue.ValueKind != JsonValueKind.True)
                errors.Add("node.package-private");
            if (rootElement.TryGetProperty("scripts", out var scripts))
            {
                if (scripts.ValueKind != JsonValueKind.Object) errors.Add("node.scripts-shape");
                else foreach (var script in scripts.EnumerateObject())
                {
                    if (script.Name is "preinstall" or "install" or "postinstall" or "prepare" or "prepack")
                        errors.Add("node.lifecycle-script:" + script.Name);
                    if (script.Value.ValueKind != JsonValueKind.String ||
                        !AllowedScripts.TryGetValue(script.Name, out var allowed) ||
                        !allowed.Equals(script.Value.GetString(), StringComparison.Ordinal))
                        errors.Add("node.script-not-approved:" + script.Name);
                }
            }
            ValidateDependencies(rootElement, "dependencies", errors);
            ValidateDependencies(rootElement, "devDependencies", errors);
        }
        return errors.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static void ValidateDependencies(JsonElement root, string propertyName, ICollection<string> errors)
    {
        if (!root.TryGetProperty(propertyName, out var dependencies)) return;
        if (dependencies.ValueKind != JsonValueKind.Object)
        {
            errors.Add("node.dependencies-shape:" + propertyName);
            return;
        }
        foreach (var dependency in dependencies.EnumerateObject())
        {
            var version = dependency.Value.ValueKind == JsonValueKind.String ? dependency.Value.GetString() : null;
            if (!PackageNamePattern().IsMatch(dependency.Name) || version is null || !ExactVersionPattern().IsMatch(version))
                errors.Add("node.dependency-not-pinned:" + dependency.Name);
        }
    }
}

public static class SafeCargoProjectPolicy
{
    private static readonly string[] ForbiddenTokens =
    [
        "build =", "path =", "git =", "registry =", "http://", "https://", "[patch.", "[replace]", "links ="
    ];

    public static IReadOnlyList<string> Validate(string projectRoot, string manifestPath)
    {
        var errors = new List<string>();
        var root = Path.GetFullPath(projectRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(manifestPath);
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(path).Equals("Cargo.toml", StringComparison.OrdinalIgnoreCase))
            return ["cargo.manifest-path"];
        var text = File.ReadAllText(path);
        if (text.Length > 50_000 || !text.Contains("[package]", StringComparison.Ordinal)) errors.Add("cargo.manifest-shape");
        foreach (var token in ForbiddenTokens)
            if (text.Contains(token, StringComparison.OrdinalIgnoreCase)) errors.Add("cargo.forbidden-token:" + token);
        if (File.Exists(Path.Combine(Path.GetDirectoryName(path)!, "build.rs"))) errors.Add("cargo.build-script");
        if (!File.Exists(Path.Combine(Path.GetDirectoryName(path)!, "Cargo.lock"))) errors.Add("cargo.lock-missing");
        if (HasDependencyEntries(text, "dependencies") || HasDependencyEntries(text, "dev-dependencies"))
            errors.Add("cargo.dependencies-require-vetting");
        return errors.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static bool HasDependencyEntries(string manifest, string section)
    {
        var match = Regex.Match(
            manifest,
            $@"(?ms)^\s*\[{Regex.Escape(section)}\]\s*$\s*(?<body>.*?)(?=^\s*\[|\z)",
            RegexOptions.CultureInvariant);
        if (!match.Success) return false;
        return match.Groups["body"].Value
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(line => !line.StartsWith('#'));
    }
}

public sealed class DevelopmentUnitBuildVerifier : IDevelopmentUnitVerifier
{
    private readonly DevelopmentBuildToolchain _toolchain;
    private readonly DevelopmentStackRegistry _stacks;
    private readonly DevelopmentUnitStaticVerifier _staticVerifier = new();

    public DevelopmentUnitBuildVerifier(string dotnetExecutable)
        : this(new DevelopmentBuildToolchain(dotnetExecutable, null, null)) { }

    public DevelopmentUnitBuildVerifier(DevelopmentBuildToolchain toolchain)
    {
        _toolchain = new(
            NormalizeExecutable(toolchain.DotnetExecutable, "dotnet.exe"),
            NormalizeExecutable(toolchain.NodeExecutable, "node.exe"),
            NormalizeExecutable(toolchain.CargoExecutable, "cargo.exe"))
        {
            NpmCliPath = NormalizeNpmCli(toolchain.NpmCliPath)
        };
        _stacks = DevelopmentStackRegistry.CreateDefault(_toolchain);
    }

    public async Task<DevelopmentUnitVerification> VerifyAsync(
        DevelopmentUnitPlan plan,
        DevelopmentUnit unit,
        string candidateRoot,
        CancellationToken cancellationToken = default)
    {
        var staticResult = await _staticVerifier.VerifyAsync(plan, unit, candidateRoot, cancellationToken);
        if (!staticResult.Passed) return staticResult with
        {
            FailureKind = DevelopmentFailureKind.VerificationFailure,
            Stage = DevelopmentPipelineStage.DeterministicVerification
        };
        return await _stacks.Resolve(unit.BuildSystem).VerifyAsync(
            new DevelopmentStackContext(unit, candidateRoot, _toolchain),
            cancellationToken);
    }

    private static string? NormalizeExecutable(string? path, string expectedName)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var full = Path.GetFullPath(path);
        if (!File.Exists(full) || !Path.GetFileName(full).Equals(expectedName, StringComparison.OrdinalIgnoreCase))
            throw new FileNotFoundException("Verified toolchain executable was not found.", full);
        return full;
    }

    private static string? NormalizeNpmCli(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var full = Path.GetFullPath(path);
        if (!File.Exists(full) || !Path.GetFileName(full).Equals("npm-cli.js", StringComparison.OrdinalIgnoreCase))
            throw new FileNotFoundException("npm-cli.js was not found.", full);
        return full;
    }
}
