using System.Diagnostics;
using System.Text.Json;
using System.Xml.Linq;

namespace SafeLocalHarness.Core;

public enum DevelopmentPipelineStage
{
    Capability,
    Restore,
    Build,
    Test,
    DeterministicVerification,
    Package,
    FinalVerification
}

public enum DevelopmentFailureKind
{
    None,
    CompileFailure,
    RestoreFailure,
    ToolchainMissing,
    PackageResolutionFailure,
    EnvironmentRestriction,
    VerificationFailure,
    ToolchainFailure
}

public sealed record StackProfile(
    string Id,
    string DisplayName,
    IReadOnlyList<string> RequiredToolchains,
    bool SupportsPortableToolchain,
    bool SupportsOfflineRestore,
    bool SupportsDeterministicTests,
    bool QualifiedForStablePromotion);

public sealed record ToolchainCapability(
    string StackId,
    bool Available,
    IReadOnlyList<string> AvailableTools,
    IReadOnlyList<string> MissingTools,
    string Summary);

public static class DevelopmentStackCatalog
{
    private static readonly IReadOnlySet<string> Known = new HashSet<string>(StringComparer.Ordinal)
    {
        "dotnet", "web-static", "node", "cargo", "tauri"
    };

    public static IReadOnlySet<string> KnownIds => Known;
    public static bool IsKnown(string id) => Known.Contains(id);
}

public sealed record DevelopmentStageResult(
    bool Passed,
    DevelopmentPipelineStage Stage,
    DevelopmentFailureKind FailureKind,
    string Summary)
{
    public static DevelopmentStageResult Pass(DevelopmentPipelineStage stage, string summary) =>
        new(true, stage, DevelopmentFailureKind.None, summary);

    public static DevelopmentStageResult Fail(
        DevelopmentPipelineStage stage,
        DevelopmentFailureKind failureKind,
        string summary) =>
        new(false, stage, failureKind, summary);
}

public sealed record DevelopmentStackContext(
    DevelopmentUnit Unit,
    string CandidateRoot,
    DevelopmentBuildToolchain Toolchain);

public interface IRestoreAdapter
{
    Task<DevelopmentStageResult> RestoreAsync(DevelopmentStackContext context, CancellationToken cancellationToken);
}

public interface IBuildAdapter
{
    Task<DevelopmentStageResult> BuildAsync(DevelopmentStackContext context, CancellationToken cancellationToken);
}

public interface ITestAdapter
{
    Task<DevelopmentStageResult> TestAsync(DevelopmentStackContext context, CancellationToken cancellationToken);
}

public interface IVerificationAdapter
{
    Task<DevelopmentStageResult> VerifyAsync(DevelopmentStackContext context, CancellationToken cancellationToken);
}

public interface IArtifactPackager
{
    Task<DevelopmentStageResult> PackageAsync(DevelopmentStackContext context, CancellationToken cancellationToken);
}

public interface IDevelopmentStackAdapter
{
    StackProfile Profile { get; }
    ToolchainCapability InspectCapability(DevelopmentBuildToolchain toolchain);
    Task<DevelopmentUnitVerification> VerifyAsync(DevelopmentStackContext context, CancellationToken cancellationToken);
}

public sealed class DevelopmentStackRegistry
{
    private readonly IReadOnlyDictionary<string, IDevelopmentStackAdapter> _adapters;

    public DevelopmentStackRegistry(DevelopmentBuildToolchain toolchain, IEnumerable<IDevelopmentStackAdapter> adapters)
    {
        Toolchain = toolchain;
        _adapters = adapters.ToDictionary(adapter => adapter.Profile.Id, StringComparer.Ordinal);
    }

    public DevelopmentBuildToolchain Toolchain { get; }

    public static DevelopmentStackRegistry CreateDefault(DevelopmentBuildToolchain toolchain) =>
        new(toolchain,
        [
            StackAdapters.Dotnet(),
            StackAdapters.WebStatic(),
            StackAdapters.Node(),
            StackAdapters.Cargo(),
            StackAdapters.Tauri()
        ]);

    public IDevelopmentStackAdapter Resolve(string id) =>
        _adapters.TryGetValue(id, out var adapter)
            ? adapter
            : throw new InvalidDataException("Unknown development stack: " + id);

    public IReadOnlyList<ToolchainCapability> InspectCapabilities() =>
        _adapters.Values
            .OrderBy(adapter => adapter.Profile.Id, StringComparer.Ordinal)
            .Select(adapter => adapter.InspectCapability(Toolchain))
            .ToArray();
}

internal sealed class CompositeDevelopmentStackAdapter : IDevelopmentStackAdapter
{
    private readonly Func<DevelopmentBuildToolchain, ToolchainCapability> _capability;
    private readonly IRestoreAdapter _restore;
    private readonly IBuildAdapter _build;
    private readonly ITestAdapter _test;
    private readonly IVerificationAdapter _verification;
    private readonly IArtifactPackager _packager;

    public CompositeDevelopmentStackAdapter(
        StackProfile profile,
        Func<DevelopmentBuildToolchain, ToolchainCapability> capability,
        IRestoreAdapter restore,
        IBuildAdapter build,
        ITestAdapter test,
        IVerificationAdapter verification,
        IArtifactPackager packager)
    {
        Profile = profile;
        _capability = capability;
        _restore = restore;
        _build = build;
        _test = test;
        _verification = verification;
        _packager = packager;
    }

    public StackProfile Profile { get; }

    public ToolchainCapability InspectCapability(DevelopmentBuildToolchain toolchain) => _capability(toolchain);

    public async Task<DevelopmentUnitVerification> VerifyAsync(
        DevelopmentStackContext context,
        CancellationToken cancellationToken)
    {
        var capability = InspectCapability(context.Toolchain);
        if (!capability.Available)
            return new(
                false,
                capability.Summary,
                DevelopmentFailureKind.ToolchainMissing,
                DevelopmentPipelineStage.Capability);

        foreach (var stage in new Func<Task<DevelopmentStageResult>>[]
        {
            () => _restore.RestoreAsync(context, cancellationToken),
            () => _build.BuildAsync(context, cancellationToken),
            () => _test.TestAsync(context, cancellationToken),
            () => _verification.VerifyAsync(context, cancellationToken),
            () => _packager.PackageAsync(context, cancellationToken)
        })
        {
            DevelopmentStageResult result;
            try
            {
                result = await stage();
            }
            catch (UnauthorizedAccessException ex)
            {
                return new(false, "environment.restricted: " + DevelopmentProcessRunner.Normalize(ex.Message), DevelopmentFailureKind.EnvironmentRestriction, DevelopmentPipelineStage.FinalVerification);
            }
            catch (IOException ex)
            {
                return new(false, "environment.io-restriction: " + DevelopmentProcessRunner.Normalize(ex.Message), DevelopmentFailureKind.EnvironmentRestriction, DevelopmentPipelineStage.FinalVerification);
            }
            catch (InvalidDataException ex)
            {
                return new(false, "verification.invalid-project: " + DevelopmentProcessRunner.Normalize(ex.Message), DevelopmentFailureKind.VerificationFailure, DevelopmentPipelineStage.DeterministicVerification);
            }
            if (!result.Passed)
                return new(false, result.Summary, result.FailureKind, result.Stage);
        }

        return new(true, "restore-build-test-verification-package-pass", DevelopmentFailureKind.None, DevelopmentPipelineStage.FinalVerification);
    }
}

internal static class StackAdapters
{
    public static IDevelopmentStackAdapter Dotnet() => new CompositeDevelopmentStackAdapter(
        new("dotnet", ".NET / WPF", ["dotnet-sdk"], true, true, true, true),
        toolchain => Capability("dotnet", [("dotnet-sdk", toolchain.DotnetExecutable)]),
        new DotnetRestoreAdapter(),
        new DotnetBuildAdapter(),
        new DotnetTestAdapter(),
        new DotnetVerificationAdapter(),
        new SafeInventoryPackager());

    public static IDevelopmentStackAdapter WebStatic() => new CompositeDevelopmentStackAdapter(
        new("web-static", "HTML / CSS / JavaScript", [], true, true, true, true),
        toolchain => Capability("web-static", []),
        new NoOpRestoreAdapter("web.restore-not-required"),
        new WebStaticBuildAdapter(),
        new JavaScriptSyntaxTestAdapter(),
        new WebStaticVerificationAdapter(),
        new SafeInventoryPackager());

    public static IDevelopmentStackAdapter Node() => new CompositeDevelopmentStackAdapter(
        new("node", "Node / React / TypeScript", ["node", "npm-for-dependencies"], true, true, true, true),
        toolchain => Capability("node", [("node", toolchain.NodeExecutable)]),
        new TauriRestoreAdapter(),
        new TauriBuildAdapter(),
        new TauriTestAdapter(),
        new NodeVerificationAdapter(),
        new SafeInventoryPackager());

    public static IDevelopmentStackAdapter Cargo() => new CompositeDevelopmentStackAdapter(
        new("cargo", "Rust / Cargo", ["cargo"], true, true, true, true),
        toolchain => Capability("cargo", [("cargo", toolchain.CargoExecutable)]),
        new CargoRestoreAdapter(),
        new CargoBuildAdapter(),
        new CargoTestAdapter(),
        new CargoVerificationAdapter(),
        new SafeInventoryPackager());

    public static IDevelopmentStackAdapter Tauri() => new CompositeDevelopmentStackAdapter(
        new("tauri", "Tauri / React / TypeScript / Rust", ["node", "npm", "cargo", "tauri-packager"], true, false, true, false),
        toolchain => Capability("tauri", [("node", toolchain.NodeExecutable), ("npm", toolchain.NpmCliPath), ("cargo", toolchain.CargoExecutable)]),
        new NodeRestoreAdapter(),
        new NodeBuildAdapter(),
        new NodeTestAdapter(),
        new TauriVerificationAdapter(),
        new UnqualifiedTauriPackager());

    private static ToolchainCapability Capability(
        string stack,
        IReadOnlyList<(string Name, string? Path)> tools)
    {
        var available = tools.Where(tool => !string.IsNullOrWhiteSpace(tool.Path)).Select(tool => tool.Name).ToArray();
        var missing = tools.Where(tool => string.IsNullOrWhiteSpace(tool.Path)).Select(tool => tool.Name).ToArray();
        return new(stack, missing.Length == 0, available, missing,
            missing.Length == 0 ? "toolchain.available" : "toolchain.missing:" + string.Join(',', missing));
    }
}

internal sealed class NoOpRestoreAdapter(string summary) : IRestoreAdapter
{
    public Task<DevelopmentStageResult> RestoreAsync(DevelopmentStackContext context, CancellationToken cancellationToken) =>
        Task.FromResult(DevelopmentStageResult.Pass(DevelopmentPipelineStage.Restore, summary));
}

internal sealed class DotnetRestoreAdapter : IRestoreAdapter
{
    public async Task<DevelopmentStageResult> RestoreAsync(DevelopmentStackContext context, CancellationToken cancellationToken)
    {
        var projects = StackFiles.Find(context.CandidateRoot, "*.csproj");
        if (projects.Length == 0) return DevelopmentStageResult.Fail(DevelopmentPipelineStage.Restore, DevelopmentFailureKind.RestoreFailure, "restore.project-missing");
        foreach (var project in projects)
        {
            var policyErrors = SafeDevelopmentProjectPolicy.Validate(context.CandidateRoot, project);
            if (policyErrors.Count > 0)
                return DevelopmentStageResult.Fail(DevelopmentPipelineStage.Restore, DevelopmentFailureKind.VerificationFailure, string.Join(", ", policyErrors));
        }

        var cacheRoot = StackFiles.CacheRoot(context.CandidateRoot, "dotnet");
        var environment = EnvironmentFor(context, cacheRoot);
        foreach (var project in projects)
        {
            var result = await DevelopmentProcessRunner.RunAsync(
                context.Toolchain.DotnetExecutable!,
                context.CandidateRoot,
                ["restore", project, "--disable-parallel", "--ignore-failed-sources", "-p:RestoreIgnoreFailedSources=true", "-m:1"],
                environment,
                cancellationToken);
            if (result.ExitCode != 0)
                return DevelopmentProcessRunner.Failure(DevelopmentPipelineStage.Restore, DevelopmentFailureKind.RestoreFailure, "restore.failed", result);
        }
        return DevelopmentStageResult.Pass(DevelopmentPipelineStage.Restore, "dotnet.restore-pass");
    }

    internal static IReadOnlyDictionary<string, string?> EnvironmentFor(
        DevelopmentStackContext context,
        string? cacheRoot = null)
    {
        cacheRoot ??= StackFiles.CacheRoot(context.CandidateRoot, "dotnet");
        return new Dictionary<string, string?>
        {
            ["DOTNET_CLI_HOME"] = Path.Combine(cacheRoot, "home"),
            ["NUGET_PACKAGES"] = Path.Combine(cacheRoot, "packages"),
            ["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1",
            // Without this, the SDK's first run with each new DOTNET_CLI_HOME appends <home>\.dotnet\tools to the
            // user PATH in the registry (2026-10-07: 46 entries, 9,818 characters, cmd.exe could no longer resolve commands).
            ["DOTNET_ADD_GLOBAL_TOOLS_TO_PATH"] = "false",
            ["DOTNET_GENERATE_ASPNET_CERTIFICATE"] = "false",
            ["DOTNET_NOLOGO"] = "1",
            ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
            ["MSBUILDDISABLENODEREUSE"] = "1"
        };
    }
}

internal sealed class DotnetBuildAdapter : IBuildAdapter
{
    public async Task<DevelopmentStageResult> BuildAsync(DevelopmentStackContext context, CancellationToken cancellationToken)
    {
        var output = StackFiles.CacheRoot(context.CandidateRoot, "dotnet-output") + Path.DirectorySeparatorChar;
        foreach (var project in DotnetProjectOrder.DependencyFirst(
                     context.CandidateRoot,
                     StackFiles.Find(context.CandidateRoot, "*.csproj")))
        {
            var result = await DevelopmentProcessRunner.RunAsync(
                context.Toolchain.DotnetExecutable!,
                context.CandidateRoot,
                ["build", project, "--no-restore", "--no-dependencies", "--configuration", "Release", "-m:1", "-p:BaseOutputPath=" + output, "-p:UseSharedCompilation=false", "-p:RunAnalyzers=false"],
                DotnetRestoreAdapter.EnvironmentFor(context),
                cancellationToken);
            if (result.ExitCode != 0)
                return DevelopmentProcessRunner.Failure(DevelopmentPipelineStage.Build, DevelopmentFailureKind.CompileFailure, "compile.failed", result);
        }
        return DevelopmentStageResult.Pass(DevelopmentPipelineStage.Build, "dotnet.build-pass");
    }
}

internal sealed class DotnetTestAdapter : ITestAdapter
{
    public async Task<DevelopmentStageResult> TestAsync(DevelopmentStackContext context, CancellationToken cancellationToken)
    {
        var output = StackFiles.CacheRoot(context.CandidateRoot, "dotnet-output") + Path.DirectorySeparatorChar;
        var testProjects = StackFiles.Find(context.CandidateRoot, "*.csproj")
            .Where(path => Path.GetFileNameWithoutExtension(path).Contains("test", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        foreach (var project in testProjects)
        {
            var result = await DevelopmentProcessRunner.RunAsync(
                context.Toolchain.DotnetExecutable!,
                context.CandidateRoot,
                ["test", project, "--no-restore", "--no-build", "--configuration", "Release", "-m:1", "-p:BaseOutputPath=" + output],
                DotnetRestoreAdapter.EnvironmentFor(context),
                cancellationToken);
            if (result.ExitCode != 0)
                return DevelopmentProcessRunner.Failure(DevelopmentPipelineStage.Test, DevelopmentFailureKind.VerificationFailure, "test.failed", result);
        }
        return DevelopmentStageResult.Pass(DevelopmentPipelineStage.Test, testProjects.Length == 0 ? "dotnet.no-test-project" : "dotnet.test-pass");
    }
}

internal sealed class DotnetVerificationAdapter : IVerificationAdapter
{
    public Task<DevelopmentStageResult> VerifyAsync(DevelopmentStackContext context, CancellationToken cancellationToken) =>
        Task.FromResult(DevelopmentStageResult.Pass(DevelopmentPipelineStage.DeterministicVerification, "dotnet.policy-and-build-evidence-pass"));
}

internal sealed class WebStaticBuildAdapter : IBuildAdapter
{
    public Task<DevelopmentStageResult> BuildAsync(DevelopmentStackContext context, CancellationToken cancellationToken)
    {
        var errors = SafeWebAssetPolicy.Validate(context.CandidateRoot);
        return Task.FromResult(errors.Count == 0
            ? DevelopmentStageResult.Pass(DevelopmentPipelineStage.Build, "web.local-assets-pass")
            : DevelopmentStageResult.Fail(DevelopmentPipelineStage.Build, DevelopmentFailureKind.VerificationFailure, string.Join(", ", errors.Take(12))));
    }
}

internal sealed class JavaScriptSyntaxTestAdapter : ITestAdapter
{
    public async Task<DevelopmentStageResult> TestAsync(DevelopmentStackContext context, CancellationToken cancellationToken)
    {
        var scripts = StackFiles.JavaScript(context.CandidateRoot);
        if (scripts.Length == 0) return DevelopmentStageResult.Pass(DevelopmentPipelineStage.Test, "javascript.no-scripts");
        if (context.Toolchain.NodeExecutable is null)
            return DevelopmentStageResult.Fail(DevelopmentPipelineStage.Test, DevelopmentFailureKind.ToolchainMissing, "toolchain.node-missing");
        foreach (var script in scripts)
        {
            var result = await DevelopmentProcessRunner.RunAsync(context.Toolchain.NodeExecutable, context.CandidateRoot, ["--check", script], null, cancellationToken);
            if (result.ExitCode != 0)
                return DevelopmentProcessRunner.Failure(DevelopmentPipelineStage.Test, DevelopmentFailureKind.CompileFailure, "javascript.syntax-failed", result);
        }
        return DevelopmentStageResult.Pass(DevelopmentPipelineStage.Test, "javascript.syntax-pass");
    }
}

internal sealed class WebStaticVerificationAdapter : IVerificationAdapter
{
    public Task<DevelopmentStageResult> VerifyAsync(DevelopmentStackContext context, CancellationToken cancellationToken)
    {
        var hasEntry = Directory.EnumerateFiles(context.CandidateRoot, "index.html", SearchOption.AllDirectories).Any();
        return Task.FromResult(hasEntry
            ? DevelopmentStageResult.Pass(DevelopmentPipelineStage.DeterministicVerification, "web.entrypoint-pass")
            : DevelopmentStageResult.Fail(DevelopmentPipelineStage.DeterministicVerification, DevelopmentFailureKind.VerificationFailure, "web.entrypoint-missing"));
    }
}

internal sealed class NodeRestoreAdapter : IRestoreAdapter
{
    public async Task<DevelopmentStageResult> RestoreAsync(DevelopmentStackContext context, CancellationToken cancellationToken)
    {
        var packages = StackFiles.Find(context.CandidateRoot, "package.json");
        if (packages.Length == 0) return DevelopmentStageResult.Fail(DevelopmentPipelineStage.Restore, DevelopmentFailureKind.RestoreFailure, "node.package-missing");
        foreach (var package in packages)
        {
            var errors = SafeNodeProjectPolicy.Validate(context.CandidateRoot, package);
            if (errors.Count > 0)
                return DevelopmentStageResult.Fail(DevelopmentPipelineStage.Restore, DevelopmentFailureKind.VerificationFailure, string.Join(", ", errors.Take(12)));
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(package, cancellationToken));
            var hasDependencies = HasItems(document.RootElement, "dependencies") || HasItems(document.RootElement, "devDependencies");
            if (!hasDependencies) continue;
            var packageRoot = Path.GetDirectoryName(package)!;
            if (!File.Exists(Path.Combine(packageRoot, "package-lock.json")))
                return DevelopmentStageResult.Fail(DevelopmentPipelineStage.Restore, DevelopmentFailureKind.PackageResolutionFailure, "node.package-lock-missing");
            if (context.Toolchain.NpmCliPath is null)
                return DevelopmentStageResult.Fail(DevelopmentPipelineStage.Restore, DevelopmentFailureKind.ToolchainMissing, "toolchain.npm-missing");
            var result = await DevelopmentProcessRunner.RunAsync(
                context.Toolchain.NodeExecutable!,
                packageRoot,
                [context.Toolchain.NpmCliPath, "ci", "--ignore-scripts", "--offline", "--no-audit", "--no-fund", "--cache", StackFiles.CacheRoot(context.CandidateRoot, "npm")],
                null,
                cancellationToken);
            if (result.ExitCode != 0)
                return DevelopmentProcessRunner.Failure(DevelopmentPipelineStage.Restore, DevelopmentFailureKind.PackageResolutionFailure, "node.restore-failed", result);
        }
        return DevelopmentStageResult.Pass(DevelopmentPipelineStage.Restore, "node.restore-pass");
    }

    private static bool HasItems(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object && value.EnumerateObject().Any();
}

internal sealed class NodeBuildAdapter : IBuildAdapter
{
    public async Task<DevelopmentStageResult> BuildAsync(DevelopmentStackContext context, CancellationToken cancellationToken)
    {
        foreach (var package in StackFiles.Find(context.CandidateRoot, "package.json"))
        {
            if (!NodePackage.HasScript(package, "build")) continue;
            if (context.Toolchain.NpmCliPath is null)
                return DevelopmentStageResult.Fail(DevelopmentPipelineStage.Build, DevelopmentFailureKind.ToolchainMissing, "toolchain.npm-missing");
            var result = await DevelopmentProcessRunner.RunAsync(context.Toolchain.NodeExecutable!, Path.GetDirectoryName(package)!, [context.Toolchain.NpmCliPath, "run", "build", "--offline"], null, cancellationToken);
            if (result.ExitCode != 0)
                return DevelopmentProcessRunner.Failure(DevelopmentPipelineStage.Build, DevelopmentFailureKind.CompileFailure, "node.build-failed", result);
        }
        return DevelopmentStageResult.Pass(DevelopmentPipelineStage.Build, "node.build-pass");
    }
}

internal sealed class NodeTestAdapter : ITestAdapter
{
    public async Task<DevelopmentStageResult> TestAsync(DevelopmentStackContext context, CancellationToken cancellationToken)
    {
        foreach (var package in StackFiles.Find(context.CandidateRoot, "package.json"))
        {
            if (!NodePackage.HasScript(package, "test")) continue;
            if (context.Toolchain.NpmCliPath is null)
                return DevelopmentStageResult.Fail(DevelopmentPipelineStage.Test, DevelopmentFailureKind.ToolchainMissing, "toolchain.npm-missing");
            var result = await DevelopmentProcessRunner.RunAsync(context.Toolchain.NodeExecutable!, Path.GetDirectoryName(package)!, [context.Toolchain.NpmCliPath, "test", "--offline"], null, cancellationToken);
            if (result.ExitCode != 0)
                return DevelopmentProcessRunner.Failure(DevelopmentPipelineStage.Test, DevelopmentFailureKind.VerificationFailure, "node.test-failed", result);
        }
        return DevelopmentStageResult.Pass(DevelopmentPipelineStage.Test, "node.test-pass-or-not-declared");
    }
}

internal sealed class NodeVerificationAdapter : IVerificationAdapter
{
    public async Task<DevelopmentStageResult> VerifyAsync(DevelopmentStackContext context, CancellationToken cancellationToken)
    {
        var typescript = StackFiles.Find(context.CandidateRoot, "*.ts").Concat(StackFiles.Find(context.CandidateRoot, "*.tsx")).Any();
        var jsx = StackFiles.Find(context.CandidateRoot, "*.jsx").Any();
        if (jsx && !StackFiles.Find(context.CandidateRoot, "package.json").Any(package => NodePackage.HasScript(package, "build")))
            return DevelopmentStageResult.Fail(DevelopmentPipelineStage.DeterministicVerification, DevelopmentFailureKind.VerificationFailure, "node.jsx-build-script-missing");
        if (typescript)
        {
            var tsc = Directory.EnumerateFiles(context.CandidateRoot, "tsc.js", SearchOption.AllDirectories)
                .FirstOrDefault(path => path.Replace('\\', '/').Contains("node_modules/typescript/", StringComparison.OrdinalIgnoreCase));
            if (tsc is null)
                return DevelopmentStageResult.Fail(DevelopmentPipelineStage.DeterministicVerification, DevelopmentFailureKind.ToolchainMissing, "toolchain.typescript-isolated-verifier-missing");
            var result = await DevelopmentProcessRunner.RunAsync(context.Toolchain.NodeExecutable!, context.CandidateRoot, [tsc, "--noEmit"], null, cancellationToken);
            if (result.ExitCode != 0)
                return DevelopmentProcessRunner.Failure(DevelopmentPipelineStage.DeterministicVerification, DevelopmentFailureKind.CompileFailure, "typescript.check-failed", result);
        }
        var syntax = await new JavaScriptSyntaxTestAdapter().TestAsync(context, cancellationToken);
        return syntax.Passed
            ? DevelopmentStageResult.Pass(DevelopmentPipelineStage.DeterministicVerification, "node.deterministic-verification-pass")
            : syntax with { Stage = DevelopmentPipelineStage.DeterministicVerification };
    }
}

internal sealed class CargoRestoreAdapter : IRestoreAdapter
{
    public async Task<DevelopmentStageResult> RestoreAsync(DevelopmentStackContext context, CancellationToken cancellationToken)
    {
        var manifests = StackFiles.Find(context.CandidateRoot, "Cargo.toml");
        if (manifests.Length == 0) return DevelopmentStageResult.Fail(DevelopmentPipelineStage.Restore, DevelopmentFailureKind.RestoreFailure, "cargo.manifest-missing");
        foreach (var manifest in manifests)
        {
            var errors = SafeCargoProjectPolicy.Validate(context.CandidateRoot, manifest);
            if (errors.Count > 0)
                return DevelopmentStageResult.Fail(DevelopmentPipelineStage.Restore, DevelopmentFailureKind.VerificationFailure, string.Join(", ", errors.Take(12)));
            var result = await DevelopmentProcessRunner.RunAsync(context.Toolchain.CargoExecutable!, context.CandidateRoot, ["fetch", "--locked", "--offline", "--manifest-path", manifest], CargoEnvironment(context), cancellationToken);
            if (result.ExitCode != 0)
                return DevelopmentProcessRunner.Failure(DevelopmentPipelineStage.Restore, DevelopmentFailureKind.PackageResolutionFailure, "cargo.restore-failed", result);
        }
        return DevelopmentStageResult.Pass(DevelopmentPipelineStage.Restore, "cargo.restore-pass");
    }

    internal static IReadOnlyDictionary<string, string?> CargoEnvironment(DevelopmentStackContext context) =>
        new Dictionary<string, string?>
        {
            ["CARGO_NET_OFFLINE"] = "true",
            ["CARGO_TARGET_DIR"] = StackFiles.CacheRoot(context.CandidateRoot, "cargo-target")
        };
}

internal sealed class CargoBuildAdapter : IBuildAdapter
{
    public async Task<DevelopmentStageResult> BuildAsync(DevelopmentStackContext context, CancellationToken cancellationToken)
    {
        foreach (var manifest in StackFiles.Find(context.CandidateRoot, "Cargo.toml"))
        {
            var result = await DevelopmentProcessRunner.RunAsync(context.Toolchain.CargoExecutable!, context.CandidateRoot, ["build", "--locked", "--offline", "--manifest-path", manifest], CargoRestoreAdapter.CargoEnvironment(context), cancellationToken);
            if (result.ExitCode != 0)
                return DevelopmentProcessRunner.Failure(DevelopmentPipelineStage.Build, DevelopmentFailureKind.CompileFailure, "cargo.build-failed", result);
        }
        return DevelopmentStageResult.Pass(DevelopmentPipelineStage.Build, "cargo.build-pass");
    }
}

internal sealed class CargoTestAdapter : ITestAdapter
{
    public async Task<DevelopmentStageResult> TestAsync(DevelopmentStackContext context, CancellationToken cancellationToken)
    {
        foreach (var manifest in StackFiles.Find(context.CandidateRoot, "Cargo.toml"))
        {
            var result = await DevelopmentProcessRunner.RunAsync(context.Toolchain.CargoExecutable!, context.CandidateRoot, ["test", "--locked", "--offline", "--manifest-path", manifest], CargoRestoreAdapter.CargoEnvironment(context), cancellationToken);
            if (result.ExitCode != 0)
                return DevelopmentProcessRunner.Failure(DevelopmentPipelineStage.Test, DevelopmentFailureKind.VerificationFailure, "cargo.test-failed", result);
        }
        return DevelopmentStageResult.Pass(DevelopmentPipelineStage.Test, "cargo.test-pass");
    }
}

internal sealed class CargoVerificationAdapter : IVerificationAdapter
{
    public Task<DevelopmentStageResult> VerifyAsync(DevelopmentStackContext context, CancellationToken cancellationToken) =>
        Task.FromResult(DevelopmentStageResult.Pass(DevelopmentPipelineStage.DeterministicVerification, "cargo.policy-lock-and-offline-pass"));
}

internal sealed class TauriVerificationAdapter : IVerificationAdapter
{
    public Task<DevelopmentStageResult> VerifyAsync(DevelopmentStackContext context, CancellationToken cancellationToken)
    {
        var hasConfig = StackFiles.Find(context.CandidateRoot, "tauri.conf.json").Length > 0;
        return Task.FromResult(hasConfig
            ? DevelopmentStageResult.Pass(DevelopmentPipelineStage.DeterministicVerification, "tauri.config-present")
            : DevelopmentStageResult.Fail(DevelopmentPipelineStage.DeterministicVerification, DevelopmentFailureKind.VerificationFailure, "tauri.config-missing"));
    }
}

internal sealed class TauriRestoreAdapter : IRestoreAdapter
{
    public async Task<DevelopmentStageResult> RestoreAsync(DevelopmentStackContext context, CancellationToken cancellationToken)
    {
        var node = await new NodeRestoreAdapter().RestoreAsync(context, cancellationToken);
        return node.Passed ? await new CargoRestoreAdapter().RestoreAsync(context, cancellationToken) : node;
    }
}

internal sealed class TauriBuildAdapter : IBuildAdapter
{
    public async Task<DevelopmentStageResult> BuildAsync(DevelopmentStackContext context, CancellationToken cancellationToken)
    {
        var node = await new NodeBuildAdapter().BuildAsync(context, cancellationToken);
        return node.Passed ? await new CargoBuildAdapter().BuildAsync(context, cancellationToken) : node;
    }
}

internal sealed class TauriTestAdapter : ITestAdapter
{
    public async Task<DevelopmentStageResult> TestAsync(DevelopmentStackContext context, CancellationToken cancellationToken)
    {
        var node = await new NodeTestAdapter().TestAsync(context, cancellationToken);
        return node.Passed ? await new CargoTestAdapter().TestAsync(context, cancellationToken) : node;
    }
}

internal sealed class SafeInventoryPackager : IArtifactPackager
{
    public Task<DevelopmentStageResult> PackageAsync(DevelopmentStackContext context, CancellationToken cancellationToken)
    {
        var files = Directory.EnumerateFiles(context.CandidateRoot, "*", SearchOption.AllDirectories)
            .Where(path => !path.Contains(Path.DirectorySeparatorChar + ".localstudio-cache" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (files.Length == 0)
            return Task.FromResult(DevelopmentStageResult.Fail(DevelopmentPipelineStage.Package, DevelopmentFailureKind.VerificationFailure, "package.empty"));
        if (files.Any(path => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0))
            return Task.FromResult(DevelopmentStageResult.Fail(DevelopmentPipelineStage.Package, DevelopmentFailureKind.VerificationFailure, "package.reparse-point"));
        return Task.FromResult(DevelopmentStageResult.Pass(DevelopmentPipelineStage.Package, $"package.inventory-pass:{files.Length}"));
    }
}

internal sealed class UnqualifiedTauriPackager : IArtifactPackager
{
    public Task<DevelopmentStageResult> PackageAsync(DevelopmentStackContext context, CancellationToken cancellationToken) =>
        Task.FromResult(DevelopmentStageResult.Fail(
            DevelopmentPipelineStage.Package,
            DevelopmentFailureKind.VerificationFailure,
            "tauri.packaging-not-qualified; stack remains experimental and cannot be promoted"));
}

internal static class NodePackage
{
    public static bool HasScript(string packagePath, string name)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(packagePath));
        return document.RootElement.TryGetProperty("scripts", out var scripts) &&
               scripts.ValueKind == JsonValueKind.Object &&
               scripts.TryGetProperty(name, out var value) &&
               value.ValueKind == JsonValueKind.String;
    }
}

internal static class StackFiles
{
    public static string[] Find(string root, string pattern) =>
        Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories)
            .Where(path => !path.Contains(Path.DirectorySeparatorChar + ".localstudio-cache" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public static string[] JavaScript(string root) =>
        Find(root, "*").Where(path => Path.GetExtension(path) is ".js" or ".mjs" or ".cjs").ToArray();

    public static string CacheRoot(string candidateRoot, string name)
    {
        var root = Path.Combine(candidateRoot, ".localstudio-cache", name);
        Directory.CreateDirectory(root);
        return root;
    }
}

internal static class DotnetProjectOrder
{
    public static IReadOnlyList<string> DependencyFirst(string candidateRoot, IReadOnlyList<string> projects)
    {
        var root = Path.GetFullPath(candidateRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var known = projects.Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<string>();
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in projects.OrderBy(path => path, StringComparer.OrdinalIgnoreCase)) Visit(Path.GetFullPath(project));
        return ordered;

        void Visit(string project)
        {
            if (visited.Contains(project)) return;
            if (!visiting.Add(project)) throw new InvalidDataException("dotnet.project-reference-cycle");
            var document = XDocument.Load(project, LoadOptions.None);
            foreach (var reference in document.Descendants().Where(element => element.Name.LocalName == "ProjectReference"))
            {
                var include = reference.Attribute("Include")?.Value;
                if (string.IsNullOrWhiteSpace(include)) continue;
                var dependency = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(project)!, include));
                if (!dependency.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !known.Contains(dependency))
                    throw new InvalidDataException("dotnet.project-reference-outside-candidate");
                Visit(dependency);
            }
            visiting.Remove(project);
            visited.Add(project);
            ordered.Add(project);
        }
    }
}

internal sealed record DevelopmentProcessResult(int ExitCode, string Output, bool TimedOut);

internal static class DevelopmentProcessRunner
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(10);

    public static async Task<DevelopmentProcessResult> RunAsync(
        string executable,
        string workingDirectory,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string?>? environment,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        if (UserSpaceGuard.IsElevated())
            throw new InvalidOperationException("Development verification must not run elevated.");
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        if (environment is not null)
            foreach (var item in environment) start.Environment[item.Key] = item.Value;
        using var process = Process.Start(start) ?? throw new InvalidOperationException("The development toolchain process could not be started.");

        // The job closes with this method, so toolchain descendants (MSBuild nodes, compiler servers)
        // cannot outlive the verification. Attachment is best-effort; Kill(true) below remains the fallback.
        WindowsProcessJob? job = null;
        try { job = WindowsProcessJob.Attach(process); }
        catch (System.ComponentModel.Win32Exception) { }
        using var ownedJob = job;

        var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout ?? DefaultTimeout);
        try
        {
            await process.WaitForExitAsync(deadline.Token);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(true);
            await process.WaitForExitAsync(CancellationToken.None);
            if (cancellationToken.IsCancellationRequested) throw;
            return new(process.ExitCode, (await stdout) + (await stderr), true);
        }
        return new(process.ExitCode, (await stdout) + (await stderr), false);
    }

    public static DevelopmentStageResult Failure(
        DevelopmentPipelineStage stage,
        DevelopmentFailureKind diagnosedKind,
        string label,
        DevelopmentProcessResult result)
    {
        var kind = Classify(result, diagnosedKind);
        var prefix = kind != DevelopmentFailureKind.ToolchainFailure
            ? label
            : result.TimedOut
                ? "toolchain.timeout"
                : $"toolchain.process-crashed(0x{unchecked((uint)result.ExitCode):X8})";
        return DevelopmentStageResult.Fail(stage, kind, prefix + ": " + Normalize(result.Output));
    }

    /// <summary>
    /// A non-zero exit is attributed to the product only when the toolchain produced diagnostics.
    /// Timeouts and Windows exception exits (0xC0000000 and above, e.g. 0xC0000005) before any
    /// compiler diagnostic are toolchain failures, not compile failures.
    /// </summary>
    public static DevelopmentFailureKind Classify(DevelopmentProcessResult result, DevelopmentFailureKind diagnosedKind)
    {
        if (result.TimedOut) return DevelopmentFailureKind.ToolchainFailure;
        if (LooksLikePackageResolutionFailure(result.Output)) return DevelopmentFailureKind.PackageResolutionFailure;
        if (result.Output.Contains("NETSDK1004", StringComparison.OrdinalIgnoreCase)) return DevelopmentFailureKind.RestoreFailure;
        if (HasCompilerDiagnostics(result.Output)) return diagnosedKind;
        if (unchecked((uint)result.ExitCode) >= 0xC0000000u) return DevelopmentFailureKind.ToolchainFailure;
        return diagnosedKind;
    }

    private static bool HasCompilerDiagnostics(string output) =>
        System.Text.RegularExpressions.Regex.IsMatch(output, @"\berror\s+(CS|MC|BC|FS|TS)\d{4}\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    public static bool LooksLikePackageResolutionFailure(string output) =>
        new[] { "NU1100", "NU1101", "NU1102", "NU1103", "NU1301", "unable to load the service index", "unable to resolve", "could not resolve", "not found in offline mode" }
            .Any(token => output.Contains(token, StringComparison.OrdinalIgnoreCase));

    public static string Normalize(string output)
    {
        var single = string.Join(' ', output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
        return single[..Math.Min(single.Length, 1_500)];
    }
}
