using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SafeLocalHarness.Core;

public interface IProjectScaffolder
{
    Task<string> CreateAsync(StudioBlueprint blueprint, StudioProjectKind kind, CancellationToken cancellationToken = default, IProgress<ProjectCreationProgress>? progress = null, ProjectDerivation? derivation = null, string? sourceRequest = null, IGenerationSteering? steering = null);
}

public sealed record ProjectCreationProgress(string Stage, string Message, double? Fraction = null);
public sealed record ProjectDerivation(string Type, string SourceProjectId, string SourceName, string RequestSummary)
{
    public static ProjectDerivation Improvement(LocalProjectInfo source, string request) => new(
        "improved-version",
        Path.GetFileName(source.Path),
        source.Name[..Math.Min(source.Name.Length, 120)],
        request.Trim()[..Math.Min(request.Trim().Length, 500)]);
}

public sealed class ProjectScaffolder : IProjectScaffolder
{
    private readonly AppPaths _paths;
    private readonly IProjectTemplateGenerator _templates;
    private readonly IStaticWebAppVerifier _webVerifier;
    private readonly IWindowsGameSourceVerifier _windowsGameVerifier;
    private readonly WindowsAppSourceVerifier _windowsAppVerifier;
    private readonly IWindowsExecutableCompiler _windowsCompiler;
    private readonly IAgentScaffoldVerifier _agentVerifier;
    private readonly IGenerativeProjectGenerator? _generativeGenerator;
    private readonly IGenerativeProjectGenerator? _windowsGenerativeGenerator;
    private readonly IGenerativeProjectGenerator? _appsScriptGenerator;

    public ProjectScaffolder(
        AppPaths paths,
        IProjectTemplateGenerator? templates = null,
        IStaticWebAppVerifier? webVerifier = null,
        IWindowsGameSourceVerifier? windowsGameVerifier = null,
        IAgentScaffoldVerifier? agentVerifier = null,
        IGenerativeProjectGenerator? generativeGenerator = null,
        WindowsAppSourceVerifier? windowsAppVerifier = null,
        IWindowsExecutableCompiler? windowsCompiler = null,
        IGenerativeProjectGenerator? windowsGenerativeGenerator = null,
        IGenerativeProjectGenerator? appsScriptGenerator = null)
    {
        _paths = paths;
        _templates = templates ?? new ProjectTemplateGenerator();
        _webVerifier = webVerifier ?? new StaticWebAppVerifier();
        _windowsGameVerifier = windowsGameVerifier ?? new WindowsGameSourceVerifier();
        _windowsAppVerifier = windowsAppVerifier ?? new WindowsAppSourceVerifier();
        _windowsCompiler = windowsCompiler ?? new WindowsExecutableCompiler();
        _agentVerifier = agentVerifier ?? new AgentScaffoldVerifier();
        _generativeGenerator = generativeGenerator;
        _windowsGenerativeGenerator = windowsGenerativeGenerator;
        _appsScriptGenerator = appsScriptGenerator;
    }

    public async Task<string> CreateAsync(StudioBlueprint blueprint, StudioProjectKind kind, CancellationToken cancellationToken = default, IProgress<ProjectCreationProgress>? progress = null, ProjectDerivation? derivation = null, string? sourceRequest = null, IGenerationSteering? steering = null)
    {
        progress?.Report(new("prepare", "保存先と権限境界を確認しています", 0.08));
        _paths.EnsureCreated();
        _paths.QuarantineAbandonedProjectStaging();
        UserSpaceGuard.EnsureAllowed(_paths.ProjectRoot);
        var safe = ProjectKindPolicy.Apply(BlueprintValidator.Validate(blueprint), kind);
        var implementation = ImplementationSupportPolicy.Evaluate(safe, kind);
        if (!implementation.CanCreate) throw new InvalidDataException(implementation.Explanation);
        var slug = Regex.Replace(safe.Name, "[^a-zA-Z0-9_-]+", "-").Trim('-');
        if (string.IsNullOrWhiteSpace(slug)) slug = "project-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var destination = UniquePath(Path.Combine(_paths.ProjectRoot, slug));
        var staging = Path.Combine(_paths.ProjectRoot, ".staging-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(staging);
            IReadOnlyList<GeneratedTextFile> generatedFiles;
            var implementationMode = implementation.Route switch
            {
                ProjectImplementationRoute.VerifiedTemplate => "verified-template",
                ProjectImplementationRoute.VerifiedWindowsExecutable => "verified-native-windows-executable",
                ProjectImplementationRoute.DesignScaffold => "design-scaffold",
                ProjectImplementationRoute.GeneratedWindowsExecutable => "generated-local-windows-executable",
                ProjectImplementationRoute.GeneratedAppsScript => LocalAppsScriptGenerationEngine.ImplementationMode,
                _ => "generated-local-web-app"
            };
            if (implementation.Route is ProjectImplementationRoute.GeneratedLocalWebApp or ProjectImplementationRoute.GeneratedWindowsExecutable or ProjectImplementationRoute.GeneratedAppsScript)
            {
                var generator = implementation.Route switch
                {
                    ProjectImplementationRoute.GeneratedLocalWebApp => _generativeGenerator,
                    ProjectImplementationRoute.GeneratedAppsScript => _appsScriptGenerator,
                    _ => _windowsGenerativeGenerator
                };
                if (generator is null)
                    throw new InvalidOperationException("この環境では自律実装用のローカルAI生成器が準備されていません。");
                var generated = await generator.GenerateAsync(safe, kind, progress, cancellationToken, sourceRequest, steering);
                generatedFiles = generated.Files;
                implementationMode = generated.ImplementationMode;
            }
            else
            {
                progress?.Report(new("template", "検証済みの実装構造を準備しています", 0.26));
                generatedFiles = _templates.Generate(safe, kind);
            }

            progress?.Report(new("manifest", "プロジェクトの設計情報を保存しています", 0.58));
            var manifest = new
            {
                schema = 1,
                createdAt = DateTimeOffset.Now,
                projectKind = kind.ToString(),
                lifecycle = implementation.Route switch
                {
                    ProjectImplementationRoute.DesignScaffold => "design-scaffold",
                    ProjectImplementationRoute.GeneratedLocalWebApp => "structural-preview",
                    ProjectImplementationRoute.GeneratedWindowsExecutable => "structural-preview",
                    ProjectImplementationRoute.GeneratedAppsScript => "structural-preview",
                    _ => "verified-project"
                },
                implementationMode,
                implementationExplanation = implementation.Explanation,
                derivation,
                sourceRequest = LimitSourceRequest(sourceRequest),
                // Instructions received during generation and the attempt that applied them (travels with the cartridge).
                appliedInstructions = steering?.Applied.Select(item => new { item.Text, item.Attempt, item.AppliedAt }).ToArray(),
                blueprint = safe
            };
            await File.WriteAllTextAsync(Path.Combine(staging, "localstudio.json"), JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false), cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(staging, "README.md"), BuildReadme(safe, kind, implementation), new UTF8Encoding(false), cancellationToken);

            if (generatedFiles.Count == 0 || generatedFiles.Count > 32)
                throw new InvalidDataException("生成できるファイル数は1～32個です。");
            var generatedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            progress?.Report(new("files", $"必要なファイルを{generatedFiles.Count}件安全な領域へ保存しています", 0.66));
            foreach (var generated in generatedFiles)
            {
                var relative = ValidateGeneratedPath(generated.RelativePath);
                if (!generatedPaths.Add(relative)) throw new InvalidDataException("同じファイルパスが重複しています。");
                if (generated.Content.Length > 512 * 1024) throw new InvalidDataException("生成ファイルは512 KB以下に限定されています。");
                if (generated.Content.Contains('\uFFFD') || LocalWebAppGenerationEngine.HasForbiddenControlCharacters(generated.Content))
                    throw new InvalidDataException("生成内容に不正なUnicode文字または制御文字が含まれています。");
                var output = Path.Combine(staging, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                await File.WriteAllTextAsync(output, generated.Content, new UTF8Encoding(false, true), cancellationToken);
            }

            ProjectVerificationResult? verification = null;
            progress?.Report(new("verification", "生成結果を独立した検証器で確認しています", 0.78));
            if (kind == StudioProjectKind.DesktopOrWebApp)
            {
                verification = await _webVerifier.VerifyAsync(staging, cancellationToken);
            }
            else if (kind == StudioProjectKind.WindowsApp ||
                     kind == StudioProjectKind.WindowsGame && implementation.Route == ProjectImplementationRoute.GeneratedWindowsExecutable)
            {
                verification = await _windowsAppVerifier.VerifyAsync(staging, cancellationToken);
                if (verification.Passed)
                {
                    progress?.Report(new("compile", "Windowsアプリを管理者権限なしで作成しています", 0.84));
                    await _windowsCompiler.CompileAsync(staging, cancellationToken);
                    verification = verification with
                    {
                        PassedChecks = verification.PassedChecks.Append("build.windows-executable").ToArray(),
                        VerifiedAt = DateTimeOffset.Now
                    };
                }
            }
            else if (kind == StudioProjectKind.WindowsGame)
                verification = await _windowsGameVerifier.VerifyAsync(staging, cancellationToken);
            else if (kind == StudioProjectKind.Agent)
                verification = await _agentVerifier.VerifyAsync(staging, cancellationToken);
            else if (kind == StudioProjectKind.GoogleAppsScript)
                // Never run here; external access is never approved for generated GAS (the engine refuses UrlFetchApp).
                verification = await GasStaticVerifier.VerifyAsync(staging, externalAccessApproved: false, cancellationToken);
            if (verification is not null && !verification.Passed)
                throw new InvalidDataException("生成したプロジェクトが初期検証に合格しませんでした: " + string.Join(", ", verification.Errors));
            var isStructuralPreview = implementation.Route is ProjectImplementationRoute.GeneratedLocalWebApp or ProjectImplementationRoute.GeneratedWindowsExecutable or ProjectImplementationRoute.GeneratedAppsScript;
            if (verification is not null) await WriteVerificationEvidenceAsync(staging, verification, isStructuralPreview, cancellationToken);
            progress?.Report(new("development-records", "要件の証拠と持ち運べる開発状態を記録しています", 0.87));
            await WriteDevelopmentRecordsAsync(
                staging,
                Path.GetFileName(destination),
                safe,
                kind,
                implementation,
                implementationMode,
                verification,
                isStructuralPreview,
                sourceRequest,
                cancellationToken);
            progress?.Report(new("baseline", isStructuralPreview
                ? "構造確認済みプレビューと復元用スナップショットを保存しています"
                : "検証済み状態と復元用ベースラインを保存しています", 0.9));
            await CreateAcceptedBaselineAsync(staging, kind == StudioProjectKind.Agent ? "safe-scaffold" : isStructuralPreview ? "structural-preview" : "accepted", cancellationToken);
            Directory.Move(staging, destination);
            progress?.Report(new("complete", "プロジェクトを安全に保存しました", 1));
            return destination;
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
        }
    }

    private static string ValidateGeneratedPath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
            throw new InvalidDataException("テンプレートのファイルパスが不正です。");
        var normalized = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "LocalStudioTemplateRoot", relativePath));
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "LocalStudioTemplateRoot")).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!normalized.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("テンプレートがプロジェクト外を参照しました。");
        var safe = Path.GetRelativePath(root, normalized);
        if (safe.Equals(".localstudio", StringComparison.OrdinalIgnoreCase) || safe.StartsWith(".localstudio" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("テンプレートはLocalStudio制御領域を生成できません。");
        return safe;
    }

    private static string? LimitSourceRequest(string? value) => string.IsNullOrWhiteSpace(value)
        ? null
        : value.Trim()[..Math.Min(value.Trim().Length, 24_000)];

    internal static async Task WriteVerificationEvidenceAsync(string projectRoot, ProjectVerificationResult verification, bool structuralPreview, CancellationToken cancellationToken)
    {
        var verificationRoot = Path.Combine(projectRoot, ".localstudio", "verification");
        Directory.CreateDirectory(verificationRoot);
        var json = JsonSerializer.Serialize(new
        {
            schema = 1,
            verifier = "LocalStudio.ProjectVerifier",
            result = verification.Passed ? structuralPreview ? "STRUCTURAL_PASS" : "PASS" : "FAIL",
            scope = structuralPreview ? "structure-security-and-build; runtime-behavior-pending" : "template-contract",
            verification.VerifiedAt,
            passedChecks = verification.PassedChecks,
            errors = verification.Errors
        }, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(Path.Combine(verificationRoot, "initial.json"), json, new UTF8Encoding(false), cancellationToken);
    }

    internal static async Task WriteDevelopmentRecordsAsync(
        string projectRoot,
        string projectId,
        StudioBlueprint blueprint,
        StudioProjectKind kind,
        ProjectImplementationDecision implementation,
        string implementationMode,
        ProjectVerificationResult? verification,
        bool structuralPreview,
        string? sourceRequest,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var evidencePath = ".localstudio/verification/initial.json";
        RequirementEvidence? evidence = null;
        var evidenceFullPath = Path.Combine(projectRoot, PortableProjectPath.ToPlatformPath(evidencePath));
        if (verification is not null && File.Exists(evidenceFullPath))
        {
            await using var stream = File.OpenRead(evidenceFullPath);
            evidence = new(
                "initial-verification",
                "deterministic-verification",
                evidencePath,
                Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant(),
                now,
                structuralPreview
                    ? "構造・安全境界・ビルドのみ確認。実操作は未確認です。"
                    : "LocalStudioの独立した決定論的検証に合格しました。");
        }

        var completionEligible = verification is not null && verification.Passed && !structuralPreview &&
                                 implementation.Route != ProjectImplementationRoute.DesignScaffold;
        var requirementItems = blueprint.Features.Select((feature, index) => new RequirementLedgerItem(
            $"LS-REQ-{index + 1:000}",
            feature,
            true,
            completionEligible ? RequirementState.Passed : RequirementState.NotTested,
            completionEligible && evidence is not null ? [evidence] : [],
            completionEligible ? null : "実際の操作または対象固有の決定論的テストが必要です。")).ToArray();
        var ledger = new RequirementLedgerDocument(1, projectId, blueprint.Name, now, requirementItems);
        await RequirementLedgerStore.SaveAtomicAsync(projectRoot, ledger, cancellationToken);

        var requirementIds = requirementItems.Select(item => item.Id).ToArray();
        var taskDag = new DevelopmentUnitPlan(1, blueprint.Name, [
            new DevelopmentUnit("requirement-ledger", "要件確定", "要求を追跡可能な完成条件へ変換", "Domain", [], requirementIds, "contract"),
            new DevelopmentUnit("target-implementation", "対象実装", "要求を満たす最小の変更を作成", "Application", ["requirement-ledger"], requirementIds, "build"),
            new DevelopmentUnit("deterministic-verification", "決定論的検証", "安い検証から順に証拠を記録", "Infrastructure", ["target-implementation"], requirementIds, "integration-test")
        ]);
        var sources = await ProjectCartridgeStore.FingerprintSourcesAsync(projectRoot, cancellationToken);
        var sourceFingerprint = ComputeSourceSetFingerprint(sources);
        var pendingFeatures = requirementItems.Where(item => item.State != RequirementState.Passed).Select(item => item.Text).ToArray();
        var tests = requirementItems.Select(item => new ProjectTestDefinition(
            "TEST-" + item.Id,
            item.Id,
            "affected-deterministic",
            "この要件を対象に、ビルド・静的検査・必要な動作検証を安い順に行う。")).ToArray();
        var testResults = completionEligible && evidence is not null
            ? tests.Select(test => new ProjectTestResult(test.Id, "PASS", evidencePath, now)).ToArray()
            : [];
        var cartridge = new ProjectCartridge(
            1,
            projectId,
            blueprint.Name,
            "target-project",
            blueprint.Summary,
            LimitSourceRequest(sourceRequest) ?? blueprint.Summary,
            blueprint.Features,
            ledger,
            "LocalStudio Coreと対象プロジェクトを分離し、対象側はRequirement Ledger、Task DAG、決定論的検証で管理する。",
            TechnologyStack(kind),
            [],
            sources.Select(source => Path.GetDirectoryName(PortableProjectPath.ToPlatformPath(source.RelativePath)) ?? ".")
                .Select(path => string.IsNullOrWhiteSpace(path) ? "." : path.Replace('\\', '/'))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            BuildInstructions(kind),
            requirementItems.Where(item => item.State == RequirementState.Passed).Select(item => item.Text).ToArray(),
            pendingFeatures,
            pendingFeatures.Length == 0 ? [] : ["対象固有の動作確認が未完了です。"],
            [
                "通常利用・更新に管理者権限を要求しない。",
                "有料APIを必須依存にしない。",
                "完成判定は必須要件のPASSと再現可能なEvidenceを必要とする。",
                "実装方式: " + implementationMode
            ],
            [],
            tests,
            testResults,
            completionEligible ? "PASS" : structuralPreview ? "STRUCTURAL_PASS_RUNTIME_PENDING" : "NOT_TESTED",
            pendingFeatures,
            [],
            new ProjectBuildState(completionEligible ? "verified" : "candidate", now, sourceFingerprint),
            taskDag,
            completionEligible ? "verified" : "verification-pending",
            sources,
            now);
        await ProjectCartridgeStore.SaveAtomicAsync(projectRoot, cartridge, cancellationToken);
    }

    private static string ComputeSourceSetFingerprint(IReadOnlyList<ProjectSourceFingerprint> sources)
    {
        var canonical = string.Join("\n", sources.Select(source => $"{source.RelativePath}\t{source.Size}\t{source.Sha256}"));
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static IReadOnlyList<string> TechnologyStack(StudioProjectKind kind) => kind switch
    {
        StudioProjectKind.WindowsApp or StudioProjectKind.WindowsGame => [".NET", "Windows desktop", "local user-space storage"],
        StudioProjectKind.GoogleAppsScript => ["Google Apps Script"],
        StudioProjectKind.Agent => ["structured agent scaffold", "local model runtime abstraction"],
        _ => ["HTML", "CSS", "JavaScript", "local browser storage"]
    };

    private static IReadOnlyList<string> BuildInstructions(StudioProjectKind kind) => kind switch
    {
        StudioProjectKind.WindowsApp or StudioProjectKind.WindowsGame => ["通常ユーザー権限でローカルビルドする。", "生成したEXEの形式と起動条件を決定論的に検証する。"],
        StudioProjectKind.GoogleAppsScript => ["appsscript.json の権限がコードの使うサービスと一致することを静的に検証する。", "Apps Scriptエディタでの実行はユーザーが行う。"],
        StudioProjectKind.Agent => ["権限未付与の状態で構造と安全境界を検証する。"],
        _ => ["外部通信なしで静的ファイルを検証する。", "ローカルブラウザーでAffected Testを実行する。"]
    };

    private static string UniquePath(string desired)
    {
        if (!Directory.Exists(desired)) return desired;
        for (var i = 2; i < 1000; i++) if (!Directory.Exists(desired + "-" + i)) return desired + "-" + i;
        throw new IOException("新しいプロジェクト用フォルダーを作成できませんでした。");
    }

    private static string BuildReadme(StudioBlueprint blueprint, StudioProjectKind kind, ProjectImplementationDecision implementation)
    {
        var disclosure = kind == StudioProjectKind.Agent
            ? "> これは安全性を確認した設計スキャフォールドです。実働アプリではなく、権限は未付与、自動実行は無効です。"
            : implementation.Route == ProjectImplementationRoute.GeneratedAppsScript
                ? "> LocalStudioが静的に検査したGoogle Apps Scriptです。LocalStudioはGoogleにログインせず、実行も公開もしていません。\n> 使い方：書き出したzipの中のファイルをApps Scriptエディタに作成（貼り付け）し、appsscript.json の権限を確認してから実行してください。"
            : implementation.Route is ProjectImplementationRoute.GeneratedLocalWebApp or ProjectImplementationRoute.GeneratedWindowsExecutable
                ? "> これは構造・安全境界を確認した新規実装プレビューです。同一AIのレビューだけで完全動作を保証せず、実際の操作確認後に正式版として扱います。"
            : kind == StudioProjectKind.WindowsApp
                ? "> LocalStudioがソース、安全境界、EXE形式を確認したWindowsアプリです。管理者権限とネットワーク通信を要求しません。"
                : "> LocalStudioの確認済み計画から生成されたローカルプロジェクトです。外部公開はまだ行われていません。";
        return $"# {blueprint.Name}\n\n{blueprint.Summary}\n\n## 実装方式\n\n{implementation.Label} — {implementation.Explanation}\n\n## 機能\n\n{string.Join("\n", blueprint.Features.Select(x => "- " + x))}\n\n{disclosure}\n";
    }

    internal static async Task CreateAcceptedBaselineAsync(string projectRoot, string status, CancellationToken cancellationToken)
    {
        var controlRoot = Path.Combine(projectRoot, ".localstudio");
        var filesRoot = Path.Combine(controlRoot, "baseline", "files");
        Directory.CreateDirectory(filesRoot);
        Directory.CreateDirectory(Path.Combine(controlRoot, "runs"));
        var entries = new List<object>();
        // The baseline keeps sources only; build outputs (e.g. the generated EXE) are regenerated and never opened.
        var sourceFiles = Directory.EnumerateFiles(projectRoot, "*", SearchOption.AllDirectories)
            .Where(path => !Path.GetRelativePath(projectRoot, path).StartsWith(".localstudio" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            .Where(path => !ProjectFilePolicy.IsBuildOutput(Path.GetRelativePath(projectRoot, path)))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        foreach (var source in sourceFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(projectRoot, source);
            var copy = Path.Combine(filesRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
            File.Copy(source, copy, true);
            await using var stream = File.OpenRead(source);
            entries.Add(new { path = relative.Replace('\\', '/'), sha256 = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(stream, cancellationToken)) });
        }
        var manifest = new { schema = 1, acceptedAt = DateTimeOffset.Now, status, files = entries };
        await File.WriteAllTextAsync(Path.Combine(controlRoot, "baseline", "manifest.json"), JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false), cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(controlRoot, "ledger.json"), "[]\n", new UTF8Encoding(false), cancellationToken);
    }
}
