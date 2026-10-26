using System.Text;
using System.Text.Json;
using SafeLocalHarness.Core;

Console.OutputEncoding = new UTF8Encoding(false);
return await WorkbenchProgram.RunAsync(args);

internal sealed record DevelopmentJobDefinition(
    int Schema,
    string ProjectId,
    string ProjectName,
    string AppPurpose,
    string SourceRequest,
    IReadOnlyDictionary<string, string> RequirementTexts,
    DevelopmentUnitPlan? Plan,
    IReadOnlyList<string> TechStack,
    IReadOnlyList<string> BuildInstructions,
    IReadOnlyList<string> DesignDecisions);

internal static class WorkbenchProgram
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            if (UserSpaceGuard.IsElevated())
                throw new InvalidOperationException("LocalStudio Workbenchは管理者権限では実行しません。");
            if (args.Length == 0) return Usage("コマンドを指定してください。");
            if (args[0].Equals("rollback-drill", StringComparison.OrdinalIgnoreCase))
                return await RunRollbackDrillAsync(RequiredOption(args, "--root"));
            if (args[0].Equals("conversation-drill", StringComparison.OrdinalIgnoreCase))
                return await RunConversationDrillAsync(RequiredOption(args, "--root"));
            if (args[0].Equals("bench", StringComparison.OrdinalIgnoreCase))
                return await RunBenchmarkAsync(RequiredOption(args, "--root"), OptionalOption(args, "--cases"), HasFlag(args, "--spec"), OptionalOption(args, "--model"), HasFlag(args, "--gas"));
            if (args[0].Equals("pull-model", StringComparison.OrdinalIgnoreCase))
                return await RunFetchPinnedModelAsync(RequiredOption(args, "--root"), RequiredOption(args, "--model"));
            if (args[0].Equals("office-drill", StringComparison.OrdinalIgnoreCase))
                return await RunOfficeDrillAsync(RequiredOption(args, "--root"), RequiredOption(args, "--file"), RequiredOption(args, "--text"), HasFlag(args, "--apply"), OptionalOption(args, "--record"));
            if (args[0].Equals("pdf-letters", StringComparison.OrdinalIgnoreCase))
            {
                // Which fonts and sizes the text of a page is drawn in (to check drawn text; reads the PDF only).
                using var pdf = UglyToad.PdfPig.PdfDocument.Open(Path.GetFullPath(RequiredOption(args, "--file")));
                var page = pdf.GetPage(int.Parse(OptionalOption(args, "--page") ?? "1", System.Globalization.CultureInfo.InvariantCulture));
                Emit("pdf-letters", page.Letters.GroupBy(letter => (letter.FontName, Size: Math.Round(letter.PointSize, 1)))
                    .Select(group => new { group.Key.FontName, group.Key.Size, text = string.Concat(group.Select(letter => letter.Value)).Substring(0, Math.Min(40, group.Count())) }));
                return 0;
            }
            if (args[0].Equals("sample-docx", StringComparison.OrdinalIgnoreCase))
            {
                // A small document for drills: paragraphs, a 3x3 table and one tracked insertion and deletion (in a drill folder only).
                var path = Path.GetFullPath(RequiredOption(args, "--file"));
                if (!Path.GetFileName(Path.GetDirectoryName(path)!).StartsWith("ls-", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("演習用のフォルダー（ls- で始まる）に作ってください。");
                UserSpaceGuard.EnsureAllowed(path);
                using var document = DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Create(path, DocumentFormat.OpenXml.WordprocessingDocumentType.Document);
                var main = document.AddMainDocumentPart();
                DocumentFormat.OpenXml.Wordprocessing.Paragraph P(string text) => new(new DocumentFormat.OpenXml.Wordprocessing.Run(new DocumentFormat.OpenXml.Wordprocessing.Text(text) { Space = DocumentFormat.OpenXml.SpaceProcessingModeValues.Preserve }));
                DocumentFormat.OpenXml.Wordprocessing.TableRow R(params string[] cells) => new(cells.Select(cell => new DocumentFormat.OpenXml.Wordprocessing.TableCell(P(cell))));
                var tracked = new DocumentFormat.OpenXml.Wordprocessing.Paragraph(
                    new DocumentFormat.OpenXml.Wordprocessing.Run(new DocumentFormat.OpenXml.Wordprocessing.Text("提出先は") { Space = DocumentFormat.OpenXml.SpaceProcessingModeValues.Preserve }),
                    new DocumentFormat.OpenXml.Wordprocessing.DeletedRun(new DocumentFormat.OpenXml.Wordprocessing.Run(new DocumentFormat.OpenXml.Wordprocessing.DeletedText("総務課"))) { Id = "1", Author = "drill", Date = DateTime.Now },
                    new DocumentFormat.OpenXml.Wordprocessing.InsertedRun(new DocumentFormat.OpenXml.Wordprocessing.Run(new DocumentFormat.OpenXml.Wordprocessing.Text("選挙課"))) { Id = "2", Author = "drill", Date = DateTime.Now },
                    new DocumentFormat.OpenXml.Wordprocessing.Run(new DocumentFormat.OpenXml.Wordprocessing.Text("です。")));
                main.Document = new DocumentFormat.OpenXml.Wordprocessing.Document(new DocumentFormat.OpenXml.Wordprocessing.Body(
                    P("はじめに"), P("この文書は演習用です。重要な点は締め切りです。"), P("不要な段落です。"),
                    new DocumentFormat.OpenXml.Wordprocessing.Table(R("番号", "氏名", "状況"), R("1", "山田", "未"), R("2", "鈴木", "未"), R("3", "田中", "未")),
                    tracked, P("まとめ"), new DocumentFormat.OpenXml.Wordprocessing.SectionProperties()));
                Emit("sample-docx", new { path });
                return 0;
            }
            if (args[0].Equals("sample-pptx", StringComparison.OrdinalIgnoreCase))
            {
                // A small presentation for drills: five slides, one with speaker notes (in a drill folder only).
                var path = Path.GetFullPath(RequiredOption(args, "--file"));
                if (!Path.GetFileName(Path.GetDirectoryName(path)!).StartsWith("ls-", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("演習用のフォルダー（ls- で始まる）に作ってください。");
                UserSpaceGuard.EnsureAllowed(path);
                PowerPointOperations.Create(path, [
                    ("演習用の発表", ["2026年10月", "選挙課"], null),
                    ("目的", ["投票率を上げる", "若い世代に届ける"], "ここで目的を強調する"),
                    ("現状", ["前回の投票率は52%", "20代は31%"], null),
                    ("不要なスライド", ["削除の練習用"], null),
                    ("まとめ", ["次回までに案を作る"], null)]);
                Emit("sample-pptx", new { path });
                return 0;
            }
            if (args[0].Equals("sample-xlsx", StringComparison.OrdinalIgnoreCase))
                return SampleWorkbook(RequiredOption(args, "--file"), RequiredOption(args, "--rows"), OptionalOption(args, "--sheet") ?? "Sheet1");
            if (args[0].Equals("win-behavior", StringComparison.OrdinalIgnoreCase))
            {
                // A built Windows app started minimised and used through window messages (test-root projects only).
                var exe = Path.GetFullPath(RequiredOption(args, "--exe"));
                UserSpaceGuard.EnsureAllowed(exe);
                var report = await WindowsAppBehaviorChecker.CheckAsync(exe, OptionalOption(args, "--text") ?? "");
                Emit("win-behavior", report);
                return report.Problems.Count == 0 ? 0 : 1;
            }
            if (args[0].Equals("win-fix-compile", StringComparison.OrdinalIgnoreCase))
            {
                // A rejected Windows candidate (its Program.cs source) with the mechanical fixes applied and compiled in a drill folder.
                var folder = Path.GetFullPath(RequiredOption(args, "--out"));
                if (!Path.GetFileName(folder).StartsWith("ls-", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("演習用のフォルダー（ls- で始まる）を指定してください。");
                var problems = await LocalWindowsAppGenerationEngine.FixAndCompileAsync(await File.ReadAllTextAsync(Path.GetFullPath(RequiredOption(args, "--file"))), folder);
                Emit("win-fix-compile", new { folder, problems });
                return problems.Count == 0 ? 0 : 1;
            }
            if (args[0].Equals("chat-drill", StringComparison.OrdinalIgnoreCase))
                return await RunChatDrillAsync(RequiredOption(args, "--root"), RequiredOption(args, "--files"), RequiredOption(args, "--script"));
            if (args[0].Equals("pdf-drill", StringComparison.OrdinalIgnoreCase))
                return await RunPdfDrillAsync(RequiredOption(args, "--root"), RequiredOption(args, "--files"), RequiredOption(args, "--text"), HasFlag(args, "--apply"), OptionalOption(args, "--record"));
            if (args[0].Equals("library-info", StringComparison.OrdinalIgnoreCase))
                return await RunLibraryInfoAsync(RequiredOption(args, "--name"), OptionalOption(args, "--version"));
            if (args[0].Equals("library-fetch", StringComparison.OrdinalIgnoreCase))
                return await RunLibraryFetchAsync(RequiredOption(args, "--root"), RequiredOption(args, "--name"), RequiredOption(args, "--version"), RequiredOption(args, "--integrity"), OptionalOption(args, "--files"), OptionalOption(args, "--element"));
            if (args[0].Equals("library-app", StringComparison.OrdinalIgnoreCase))
                return await RunLibraryAppAsync(RequiredOption(args, "--root"), RequiredOption(args, "--text"));
            if (args[0].Equals("behavior-check", StringComparison.OrdinalIgnoreCase))
            {
                var report = await WebAppBehaviorChecker.CheckAsync(Path.GetFullPath(RequiredOption(args, "--project")));
                Emit("behavior-check", report);
                return report.Passed ? 0 : 1;
            }
            if (args[0].Equals("ocr-page", StringComparison.OrdinalIgnoreCase))
                return await RunOcrPageAsync(RequiredOption(args, "--file"), int.Parse(RequiredOption(args, "--page"), System.Globalization.CultureInfo.InvariantCulture), double.Parse(OptionalOption(args, "--scale") ?? "3", System.Globalization.CultureInfo.InvariantCulture));
            if (args[0].Equals("rename-drill", StringComparison.OrdinalIgnoreCase))
                return await RunRenameDrillAsync(RequiredOption(args, "--root"), RequiredOption(args, "--folder"), RequiredOption(args, "--text"), HasFlag(args, "--apply"), OptionalOption(args, "--record"));
            if (args[0].Equals("describe-image", StringComparison.OrdinalIgnoreCase))
                return await RunDescribeImageAsync(RequiredOption(args, "--root"), RequiredOption(args, "--image"), OptionalOption(args, "--text") ?? "この画面について");

            var paths = AppPaths.ForCurrentUser();
            paths.EnsureCreated();
            var settings = new SettingsStore(paths);
            var hardware = SystemHardwareProbe.Capture(paths);
            var resources = AdaptiveRuntimePolicy.Create(hardware, settings.Load().SelectedModel, settings.Load().ResourceMode, MemoryRelief.EstimateReclaimable());
            await SystemHardwareProbe.SaveAsync(paths, hardware, resources);
            using var pressureProbe = new WindowsMemoryPressureProbe();
            using var pressureMonitor = new MemoryPressureMonitor(pressureProbe, () => settings.Load().ResourceMode, AdaptiveRuntimePolicy.EffectiveInstalledBytes(hardware));

            var command = args[0].ToLowerInvariant();
            if (command == "probe")
            {
                Emit("hardware", new { hardware, resources });
                return 0;
            }
            if (command == "classify-kinds")
                return await RunKindClassificationAsync(paths, settings, resources, pressureMonitor, RequiredOption(args, "--requests"));

            var jobPath = RequiredOption(args, "--job");
            var job = await LoadJobAsync(jobPath);
            ValidateJob(job);
            var prepareModel = HasFlag(args, "--prepare-model");
            var jobRoot = Path.Combine(paths.StateRoot, "development-jobs", job.ProjectId);
            UserSpaceGuard.EnsureAllowed(jobRoot);
            Directory.CreateDirectory(jobRoot);

            await using var runtime = new OllamaManager(paths, resourcePlan: resources, pressureMonitor: pressureMonitor);
            if (prepareModel) await EnsureRecommendedModelAsync(runtime, resources);

            var plan = job.Plan;
            if (plan is null)
            {
                if (resources.Band == RuntimeResourceBand.DeterministicOnly)
                    throw new InvalidOperationException("現在の空き資源ではAIによる作業分解を開始できません。決定論的ツールは利用できます。");
                using var planner = new LocalDevelopmentPlanningEngine(runtime, settings, resources: resources);
                plan = await planner.PlanAsync(
                    job.ProjectName,
                    BuildPlanningRequest(job),
                    new Progress<PlanningProgress>(value => Emit("planning", value)));
            }
            var validation = DevelopmentUnitGraph.Validate(plan);
            if (!validation.Passed)
                throw new InvalidDataException("Task DAGが不正です: " + string.Join(", ", validation.Errors));
            ValidateRequirements(job, plan);
            var planPath = Path.Combine(jobRoot, "effective-plan.json");
            await File.WriteAllTextAsync(planPath, JsonSerializer.Serialize(plan, JsonOptions), new UTF8Encoding(false));
            Emit("plan-ready", new { path = planPath, units = plan.Units.Count });
            if (command == "plan") return 0;
            if (command != "run") return Usage("未対応のコマンドです: " + command);

            var toolchain = ResolveToolchain(args, paths, plan);
            var statePath = Path.Combine(jobRoot, "development-run.json");
            var store = new DevelopmentUnitCandidateStore(Path.Combine(jobRoot, "artifacts"));
            var state = File.Exists(statePath)
                ? await DevelopmentRunCoordinator.LoadAsync(statePath)
                : DevelopmentRunCoordinator.Create(plan);
            using var generator = new LocalDevelopmentUnitGenerationEngine(runtime, settings, resources: resources);
            var executor = new DevelopmentUnitExecutionEngine(
                generator,
                new DevelopmentUnitBuildVerifier(toolchain),
                store,
                runtime.UnloadModelsAsync);
            var maximumAttempts = OptionalPositiveInt(args, "--maximum-attempts") ?? int.MaxValue;
            var execution = await executor.RunAsync(
                plan,
                state,
                statePath,
                job.SourceRequest,
                new Progress<DevelopmentUnitExecutionProgress>(value => Emit("development", value)),
                maximumAttempts);
            if (!execution.Completed)
            {
                Emit("not-complete", new
                {
                    blocked = execution.BlockedUnitIds,
                    units = execution.State.Units.Select(item => new { item.UnitId, status = item.Status.ToString(), item.Attempt, item.LastError })
                });
                return execution.BlockedUnitIds.Count > 0 ? 3 : 2;
            }

            var destination = Path.Combine(paths.ProjectRoot, job.ProjectId);
            if (Directory.Exists(destination))
            {
                var inspection = await ProjectCartridgeStore.InspectAsync(destination);
                if (inspection.CanResume && inspection.Cartridge.ProjectId.Equals(job.ProjectId, StringComparison.Ordinal))
                {
                    Emit("already-stable", new { destination, inspection.Cartridge.VerificationStatus });
                    return 0;
                }
                throw new IOException("同名の未確認プロジェクトが既に存在します。上書きは行いません: " + destination);
            }

            var result = await new DevelopmentProjectAssembler(store).AssembleAsync(
                plan,
                execution.State,
                new DevelopmentProjectAssemblyRequest(
                    destination,
                    job.ProjectId,
                    job.ProjectName,
                    job.AppPurpose,
                    job.SourceRequest,
                    job.RequirementTexts,
                    job.TechStack,
                    job.BuildInstructions,
                    job.DesignDecisions));
            Emit("stable", result);
            return result.CompletionAllowed ? 0 : 4;
        }
        catch (Exception ex)
        {
            Emit("error", new { type = ex.GetType().Name, message = ex.Message });
            return 1;
        }
    }

    private static async Task<DevelopmentJobDefinition> LoadJobAsync(string path)
    {
        var fullPath = Path.GetFullPath(path);
        await using var stream = File.OpenRead(fullPath);
        return await JsonSerializer.DeserializeAsync<DevelopmentJobDefinition>(stream, JsonOptions)
            ?? throw new InvalidDataException("開発ジョブを読み取れませんでした。");
    }

    private static void ValidateJob(DevelopmentJobDefinition job)
    {
        if (job.Schema != 1) throw new InvalidDataException("開発ジョブのschemaは1である必要があります。");
        if (!System.Text.RegularExpressions.Regex.IsMatch(job.ProjectId ?? "", "^[a-z0-9][a-z0-9-]{1,63}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant))
            throw new InvalidDataException("安全なprojectIdではありません。");
        if (string.IsNullOrWhiteSpace(job.ProjectName) || string.IsNullOrWhiteSpace(job.AppPurpose) || string.IsNullOrWhiteSpace(job.SourceRequest))
            throw new InvalidDataException("projectName、appPurpose、sourceRequestは必須です。");
        if (job.SourceRequest.Length > 200_000) throw new InvalidDataException("sourceRequestが上限を超えています。");
        if (job.RequirementTexts.Count == 0 || job.RequirementTexts.Any(item => string.IsNullOrWhiteSpace(item.Key) || string.IsNullOrWhiteSpace(item.Value)))
            throw new InvalidDataException("Requirement LedgerのIDと本文は必須です。");
    }

    private static void ValidateRequirements(DevelopmentJobDefinition job, DevelopmentUnitPlan plan)
    {
        var referenced = plan.Units.SelectMany(unit => unit.RequirementIds).ToHashSet(StringComparer.Ordinal);
        var missing = job.RequirementTexts.Keys.Where(id => !referenced.Contains(id)).ToArray();
        var unknown = referenced.Where(id => !job.RequirementTexts.ContainsKey(id)).ToArray();
        if (missing.Length > 0 || unknown.Length > 0)
            throw new InvalidDataException($"Requirement LedgerとTask DAGが一致しません。未割当={string.Join(',', missing)} 不明={string.Join(',', unknown)}");
    }

    private static string BuildPlanningRequest(DevelopmentJobDefinition job)
    {
        var ledger = string.Join("\n", job.RequirementTexts.Select(item => $"{item.Key}: {item.Value}"));
        return $"""
            PURPOSE:
            {job.AppPurpose}

            REQUIREMENT LEDGER IDS (every ID must appear in at least one unit):
            {ledger}

            USER REQUEST:
            {job.SourceRequest}
            """;
    }

    private static async Task EnsureRecommendedModelAsync(ILocalModelRuntime runtime, RuntimeResourcePlan resources)
    {
        var recommended = resources.RecommendedModel
            ?? throw new InvalidOperationException("現在の資源状態ではモデルの準備を開始しません。");
        var installed = await runtime.GetInstalledModelsAsync();
        if (ModelSelectionPolicy.ResolveForPlanning(recommended, installed) is not null) return;
        // Downloads happen only through the user-approved Setup screen; the Workbench never starts one.
        throw new InvalidOperationException($"モデル {recommended} がまだ準備されていません。LocalStudioのセットアップ画面で内容を確認して準備してください。");
    }

    private static DevelopmentBuildToolchain ResolveToolchain(string[] args, AppPaths paths, DevelopmentUnitPlan plan)
    {
        var requiresDotnet = plan.Units.Any(unit => unit.BuildSystem == "dotnet");
        var dotnet = requiresDotnet ? ResolveDotnet(args, paths) : ResolveOptionalExecutable(args, "--dotnet", paths.RuntimeRoot, "dotnet.exe");
        var node = ResolveOptionalExecutable(args, "--node", paths.RuntimeRoot, "node.exe");
        var cargo = ResolveOptionalExecutable(args, "--cargo", paths.RuntimeRoot, "cargo.exe")
            ?? ResolveUserCargo();
        var npmCli = ResolveOptionalFile(args, "--npm-cli", paths.RuntimeRoot, "npm-cli.js")
            ?? ResolveNpmCliNearNode(node);
        return new(dotnet, node, cargo) { NpmCliPath = npmCli };
    }

    private static string ResolveDotnet(string[] args, AppPaths paths)
    {
        var explicitPath = OptionalOption(args, "--dotnet");
        if (explicitPath is not null) return Path.GetFullPath(explicitPath);
        var processPath = Environment.ProcessPath;
        if (processPath is not null && Path.GetFileName(processPath).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase))
            return processPath;
        if (Directory.Exists(paths.RuntimeRoot))
        {
            var bundled = Directory.EnumerateFiles(paths.RuntimeRoot, "dotnet.exe", SearchOption.AllDirectories).FirstOrDefault();
            if (bundled is not null) return bundled;
        }
        throw new FileNotFoundException("検証用.NET SDKが見つかりません。LocalStudioのセットアップを実行してください。");
    }

    private static string? ResolveOptionalExecutable(
        string[] args,
        string option,
        string runtimeRoot,
        string fileName)
    {
        var explicitPath = OptionalOption(args, option);
        if (explicitPath is not null) return Path.GetFullPath(explicitPath);
        if (Directory.Exists(runtimeRoot))
        {
            var bundled = Directory.EnumerateFiles(runtimeRoot, fileName, SearchOption.AllDirectories).FirstOrDefault();
            if (bundled is not null) return bundled;
        }
        var pathValue = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidate = Path.Combine(directory, fileName);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static string? ResolveUserCargo()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidate = Path.Combine(profile, ".cargo", "bin", "cargo.exe");
        return File.Exists(candidate) ? candidate : null;
    }

    private static string? ResolveOptionalFile(
        string[] args,
        string option,
        string runtimeRoot,
        string fileName)
    {
        var explicitPath = OptionalOption(args, option);
        if (explicitPath is not null) return Path.GetFullPath(explicitPath);
        return Directory.Exists(runtimeRoot)
            ? Directory.EnumerateFiles(runtimeRoot, fileName, SearchOption.AllDirectories).FirstOrDefault()
            : null;
    }

    private static string? ResolveNpmCliNearNode(string? node)
    {
        if (node is null) return null;
        var directory = Path.GetDirectoryName(node);
        if (directory is null) return null;
        var candidates = new[]
        {
            Path.Combine(directory, "node_modules", "npm", "bin", "npm-cli.js"),
            Path.Combine(directory, "..", "lib", "node_modules", "npm", "bin", "npm-cli.js")
        };
        return candidates.Select(Path.GetFullPath).FirstOrDefault(File.Exists);
    }

    private static bool HasFlag(string[] args, string name) => args.Contains(name, StringComparer.OrdinalIgnoreCase);

    private static string RequiredOption(string[] args, string name) =>
        OptionalOption(args, name) ?? throw new ArgumentException(name + " を指定してください。");

    private static string? OptionalOption(string[] args, string name)
    {
        for (var index = 1; index < args.Length; index++)
            if (args[index].Equals(name, StringComparison.OrdinalIgnoreCase))
                return index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal)
                    ? args[index + 1]
                    : throw new ArgumentException(name + " の値がありません。");
        return null;
    }

    private static int? OptionalPositiveInt(string[] args, string name)
    {
        var value = OptionalOption(args, name);
        if (value is null) return null;
        return int.TryParse(value, out var result) && result > 0
            ? result
            : throw new ArgumentException(name + " は1以上の整数で指定してください。");
    }

    private static int Usage(string message)
    {
        Emit("usage", new
        {
            message,
            commands = new[]
            {
                "probe",
                "plan --job <job.json> [--prepare-model]",
                "run --job <job.json> [--prepare-model] [--dotnet <dotnet.exe>] [--node <node.exe>] [--npm-cli <npm-cli.js>] [--cargo <cargo.exe>] [--maximum-attempts N]"
            }
        });
        return 64;
    }

    /// <summary>
    /// Developer-only rollback drill on real official artifacts, inside an isolated LocalStudio-shaped root (never the
    /// real data folder): install the reviewed previous Ollama release over the pinned one, confirm it runs, roll back,
    /// and confirm the pinned release is restored. The previous release is not part of the product catalog.
    /// </summary>
    /// <summary>
    /// Measures output-type detection on real requests with the owned local model. Input: UTF-8 lines
    /// "&lt;expected kind or ?&gt;\t&lt;request&gt;". Nothing is generated or saved; only the decision is reported.
    /// </summary>
    private static async Task<int> RunKindClassificationAsync(AppPaths paths, SettingsStore settings, RuntimeResourcePlan resources, MemoryPressureMonitor pressureMonitor, string requestsFile)
    {
        var lines = (await File.ReadAllLinesAsync(requestsFile, Encoding.UTF8))
            .Where(line => !string.IsNullOrWhiteSpace(line) && !line.StartsWith('#'))
            .Take(50)
            .ToArray();
        await using var runtime = new OllamaManager(paths, resourcePlan: resources, pressureMonitor: pressureMonitor);
        using var planner = new LocalPlanningEngine(runtime, resources: resources);
        var results = new List<object>();
        foreach (var line in lines)
        {
            var parts = line.Split('\t', 2);
            var expected = parts.Length == 2 ? parts[0].Trim() : "?";
            var request = parts.Length == 2 ? parts[1].Trim() : line.Trim();
            var keywords = OutputTargetAdvisor.Analyze(request);
            OutputKindJudgement? judgement = null;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            if (!keywords.IsConfident) judgement = await planner.ClassifyAsync(request, settings.Load().SelectedModel);
            var resolution = OutputKindResolver.Resolve(keywords, judgement);
            var decided = resolution.Kind?.ToString() ?? "ask";
            results.Add(new
            {
                request,
                expected,
                decided,
                correct = expected == "?" ? (bool?)null : string.Equals(expected, decided, StringComparison.OrdinalIgnoreCase),
                bySignal = keywords.IsConfident,
                model = judgement is null ? null : new { kind = judgement.Kind?.ToString(), judgement.Confidence },
                question = resolution.NeedsAnswer ? resolution.Message : null,
                seconds = Math.Round(clock.Elapsed.TotalSeconds, 1)
            });
        }
        await runtime.UnloadModelsAsync();
        Emit("kind-classification", new { count = results.Count, results });
        return 0;
    }

    /// <summary>
    /// Real-model check of the conversation flow: create a small web app, improve it by chat, then go back to v1.
    /// Projects, conversations, settings and logs live only under the test root; the managed runtime and model
    /// folders are used exactly as the app uses them. Nothing is written to the real data folder.
    /// </summary>
    private static async Task<int> RunConversationDrillAsync(string root)
    {
        await using var environment = TestRootEnvironment.Create(root);
        var runtime = environment.Runtime;
        var conversation = environment.NewConversation();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        conversation.MessageAdded += message => Emit("message", new { seconds = Math.Round(clock.Elapsed.TotalSeconds), role = message.Role.ToString(), message.Text, choices = message.Choices.Select(choice => choice.Id) });
        conversation.CreationProgress += progress => Emit("progress", new { seconds = Math.Round(clock.Elapsed.TotalSeconds), progress.Stage, progress.Message });

        async Task<bool> StartIfOfferedAsync(string step)
        {
            // The drill asks for a fully local app, so it accepts building without external access when offered.
            if (conversation.Messages[^1].Choices.Any(choice => choice.Id == ConversationOrchestrator.LocalOnlyChoice))
                await conversation.ChooseAsync(ConversationOrchestrator.LocalOnlyChoice);
            if (conversation.Messages[^1].Choices.All(choice => choice.Id != ConversationOrchestrator.StartChoice))
            {
                Emit("drill-step", new { step, passed = false, stage = conversation.Stage.ToString() });
                return false;
            }
            await conversation.ChooseAsync(ConversationOrchestrator.StartChoice);
            var passed = conversation.Stage == ConversationStage.Completed;
            Emit("drill-step", new { step, passed, stage = conversation.Stage.ToString() });
            return passed;
        }

        await conversation.SubmitAsync("ブラウザーで使うWebアプリで、メモを1行ずつ追加して一覧表示できるシンプルなメモ帳。保存はこのPCのブラウザー内だけ。");
        if (conversation.Stage == ConversationStage.Clarifying)
            await conversation.ChooseAsync("kind:" + StudioProjectKind.DesktopOrWebApp);
        var created = await StartIfOfferedAsync("create");
        var improved = false;
        var restored = false;
        if (created)
        {
            await conversation.SubmitAsync("追加ボタンをもっと大きくして、目立つ色にしてください");
            improved = await StartIfOfferedAsync("improve");
            await conversation.SubmitAsync("v1に戻して");
            var restoreChoice = conversation.Messages[^1].Choices.FirstOrDefault(choice => choice.Id.StartsWith(ConversationOrchestrator.RestoreChoicePrefix, StringComparison.Ordinal));
            if (restoreChoice is not null)
            {
                await conversation.ChooseAsync(restoreChoice.Id);
                restored = conversation.Messages[^1].Text.Contains("として復元しました", StringComparison.Ordinal);
            }
        }
        await runtime.UnloadModelsAsync();

        var project = conversation.ProjectPath;
        var versions = project is null ? null : await ProjectVersionStore.LoadAsync(project);
        var v2Css = project is null ? null : Path.Combine(ProjectVersionStore.VersionRoot(project, 2), AppStylesheetPolicy.FileName);
        Emit("drill-result", new
        {
            created,
            improved,
            restored,
            project,
            versions = versions?.Versions.Select(version => new { version.Number, version.Kind, version.Instruction, version.FilesChanged, version.LinesAdded, version.LinesRemoved }),
            v2HasAppCss = v2Css is not null && File.Exists(v2Css),
            v2AppCss = v2Css is not null && File.Exists(v2Css) ? await File.ReadAllTextAsync(v2Css) : null,
            currentHasAppCss = project is not null && File.Exists(Path.Combine(project, AppStylesheetPolicy.FileName)),
            totalSeconds = Math.Round(clock.Elapsed.TotalSeconds)
        });
        return created && improved && restored ? 0 : 1;
    }

    /// <summary>
    /// Local Studio benchmark: runs the golden requests through the real conversation flow under a test root and
    /// scores quality, time, memory headroom and repairs. Results go to the test root and to the console.
    /// </summary>
    /// <summary>
    /// Downloads one pinned model into LocalStudio's managed model store and verifies every blob against the reviewed
    /// manifest digest (only models pinned after user review; the download itself needs the user's approval).
    /// </summary>
    private static async Task<int> RunFetchPinnedModelAsync(string root, string model)
    {
        var candidate = ManagedModelManager.GetPinnedCandidate(model)
            ?? throw new InvalidOperationException("固定（ピン留め）されていないモデルはダウンロードしません: " + model);
        await using var environment = TestRootEnvironment.Create(root);
        long nextReport = 0;
        using var benchmark = new OllamaModelBenchmarkRunner(environment.Runtime);
        var verified = await new ManagedModelManager(environment.Paths, environment.Runtime, benchmark)
            .PullPinnedAsync(model, new Progress<SetupProgress>(value =>
            {
                var percent = (long)((value.Fraction ?? 0) * 100);
                if (percent < nextReport) return;
                nextReport = percent + 10;
                Emit("pull-progress", new { model, percent, value.Message });
            }));
        Emit("pull-result", new { model, expectedBytes = candidate.ApproximateDownloadBytes, verifiedBlobs = verified });
        return 0;
    }

    /// <summary>
    /// 4-1 drill in a test root: plan and preview a rename with the real model; with --apply also apply it, check the
    /// folder, undo it and check again. The folder must be inside the test root.
    /// </summary>
    private static async Task<int> RunRenameDrillAsync(string root, string folder, string text, bool apply, string? record = null)
    {
        await using var environment = TestRootEnvironment.Create(root);
        var full = Path.GetFullPath(folder);
        // Renaming refuses AppData (where test roots live), so drills use a sample folder whose name marks it as one.
        if (!Path.GetFileName(full.TrimEnd(Path.DirectorySeparatorChar)).StartsWith("ls-rename-drill", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("演習のフォルダー名は ls-rename-drill で始めてください（実際のファイルを誤って変えないため）。");
        using var planner = new LocalRenamePlanner(environment.Runtime, environment.Settings);
        // --record keeps every model exchange (JSONL) so a mistake can be turned into a replay fixture for Smoke.
        IRenamePlanner used = record is null ? planner : new RecordingRenamePlanner(planner, () => Path.GetFullPath(record));
        var assistant = new RenameAssistant(used, Path.Combine(environment.Paths.StateRoot, "work-journals"));
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var preview = await assistant.PreviewAsync(text, full);
        Emit("rename-preview", new { seconds = Math.Round(clock.Elapsed.TotalSeconds, 1), preview.Plan, preview.Changing, preview.Problems, items = preview.Items.Take(40) });
        if (!apply || !preview.CanApply) return 0;
        var applied = await assistant.ApplyAsync(preview.Id);
        Emit("rename-applied", new { applied.Renamed, applied.Succeeded, applied.Mismatches });
        var undone = await assistant.UndoAsync(applied.Journal.Id);
        Emit("rename-undone", new { undone.Renamed, undone.Succeeded, undone.Mismatches });
        return 0;
    }

    /// <summary>4-2 drill: answer or edit one Word / Excel file with the real model (folder name must start ls-office-drill).</summary>
    private static async Task<int> RunOfficeDrillAsync(string root, string file, string text, bool apply, string? record = null)
    {
        await using var environment = TestRootEnvironment.Create(root);
        var full = Path.GetFullPath(file);
        if (!Path.GetFileName(Path.GetDirectoryName(full)!).StartsWith("ls-office-drill", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("演習のファイルは ls-office-drill で始まるフォルダーに置いてください。");
        using var planner = new LocalOfficePlanner(environment.Runtime, environment.Settings);
        var assistant = new OfficeAssistant(record is null ? planner : new RecordingOfficePlanner(planner, () => Path.GetFullPath(record)));
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var reply = await assistant.HandleAsync(text, full);
        Emit("office-reply", new { seconds = Math.Round(clock.Elapsed.TotalSeconds, 1), reply.Text, edits = reply.Preview?.Edits, canApply = reply.Preview?.CanApply });
        if (apply && reply.Preview is { CanApply: true } preview)
        {
            var result = assistant.Apply(preview.Id);
            Emit("office-applied", new { result.OutputPath, result.Succeeded, result.Mismatches });
        }
        return 0;
    }

    /// <summary>A small workbook for drills ("a,b;c,d": rows split by ";", cells by ","; numbers stay numbers), in a drill folder only.</summary>
    private static int SampleWorkbook(string file, string rows, string sheetName)
    {
        var path = Path.GetFullPath(file);
        if (!Path.GetFileName(Path.GetDirectoryName(path)!).StartsWith("ls-", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("演習用のフォルダー（ls- で始まる）に作ってください。");
        UserSpaceGuard.EnsureAllowed(path);
        using var document = DocumentFormat.OpenXml.Packaging.SpreadsheetDocument.Create(path, DocumentFormat.OpenXml.SpreadsheetDocumentType.Workbook);
        var book = document.AddWorkbookPart();
        book.Workbook = new DocumentFormat.OpenXml.Spreadsheet.Workbook();
        var part = book.AddNewPart<DocumentFormat.OpenXml.Packaging.WorksheetPart>();
        var data = new DocumentFormat.OpenXml.Spreadsheet.SheetData();
        var lines = rows.Split(';');
        for (var row = 0; row < lines.Length; row++)
        {
            var element = new DocumentFormat.OpenXml.Spreadsheet.Row { RowIndex = (uint)(row + 1) };
            var cells = lines[row].Split(',');
            for (var column = 0; column < cells.Length; column++)
            {
                var cell = new DocumentFormat.OpenXml.Spreadsheet.Cell { CellReference = (char)('A' + Math.Min(column, 25)) + (row + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) };
                if (row > 0 && double.TryParse(cells[column], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _)) cell.CellValue = new DocumentFormat.OpenXml.Spreadsheet.CellValue(cells[column]);
                else { cell.DataType = DocumentFormat.OpenXml.Spreadsheet.CellValues.InlineString; cell.InlineString = new DocumentFormat.OpenXml.Spreadsheet.InlineString(new DocumentFormat.OpenXml.Spreadsheet.Text(cells[column])); }
                element.Append(cell);
            }
            data.Append(element);
        }
        part.Worksheet = new DocumentFormat.OpenXml.Spreadsheet.Worksheet(data);
        book.Workbook.AppendChild(new DocumentFormat.OpenXml.Spreadsheet.Sheets(new DocumentFormat.OpenXml.Spreadsheet.Sheet { Id = book.GetIdOfPart(part), SheetId = 1, Name = sheetName }));
        Emit("sample-xlsx", new { path });
        return 0;
    }

    /// <summary>
    /// The chat as the app runs it, without the window (hands-on checks done by LocalStudio itself): the real conversation,
    /// the real models and the working copies, on copies of files in a folder starting ls-chat-drill. The script has one
    /// line per step: a message, or "!save" / "!undo" to press that button of the last reply.
    /// </summary>
    private static async Task<int> RunChatDrillAsync(string root, string files, string script)
    {
        await using var environment = TestRootEnvironment.Create(root);
        // --files "-": no file; the lines are app requests and instructions (the real creation flow, no fixed templates).
        var paths = files == "-" ? [] : files.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Path.GetFullPath).ToArray();
        if (paths.Any(path => !Path.GetFileName(Path.GetDirectoryName(path)!).StartsWith("ls-chat-drill", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("演習のファイルは ls-chat-drill で始まるフォルダーに置いてください。");
        if (paths.Length == 0) return await RunAppDrillAsync(environment, script);
        using var officePlanner = new LocalOfficePlanner(environment.Runtime, environment.Settings);
        using var pdfPlanner = new LocalPdfPlanner(environment.Runtime, environment.Settings);
        var conversation = new ConversationOrchestrator(new HybridPlanningEngine(environment.Planner), environment.Planner,
            new ProjectScaffolder(environment.Paths, generativeGenerator: environment.Web), ConversationLog.CreateSession(environment.Paths), () => environment.Settings.Load().SelectedModel)
        {
            Office = new OfficeAssistant(officePlanner),
            Pdf = new PdfAssistant(pdfPlanner, new SafeLocalHarness.Windows.WindowsPdfImaging())
        };
        conversation.MessageAdded += message => Emit("chat", new { role = message.Role.ToString(), message.Text, choices = message.Choices.Select(choice => choice.Id) });
        var pdf = paths.All(path => path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase));
        foreach (var line in (await File.ReadAllLinesAsync(Path.GetFullPath(script))).Select(line => line.Trim()).Where(line => line.Length > 0 && !line.StartsWith('#')))
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            if (line.StartsWith('!'))
            {
                var prefix = line[1..] switch { "save" => ConversationOrchestrator.WorkSavePrefix, "undo" => ConversationOrchestrator.WorkUndoPrefix, var other => other };
                var choice = conversation.Messages.LastOrDefault(message => message.Choices.Any(item => item.Id.StartsWith(prefix, StringComparison.Ordinal)))?.Choices.First(item => item.Id.StartsWith(prefix, StringComparison.Ordinal));
                if (choice is null) { Emit("chat-step", new { line, error = "button not offered" }); continue; }
                await conversation.ChooseAsync(choice.Id);
            }
            else if (paths.All(path => PdfExport.ImageExtensions.Contains(Path.GetExtension(path).ToLowerInvariant()))) await conversation.SubmitImagesAsync(line, paths);
            else if (pdf) await conversation.SubmitPdfAsync(line, paths);
            else await conversation.SubmitDocumentAsync(line, paths[0]);
            Emit("chat-step", new { line, seconds = Math.Round(clock.Elapsed.TotalSeconds, 1) });
        }
        Emit("chat-log", new { conversation.Log.FilePath });
        return 0;
    }

    /// <summary>
    /// App creation as the chat does it: each line is a message, "!start" starts building (accepting a local-only build
    /// when offered), "!&lt;choice id prefix&gt;" presses another button. Progress, messages and the final project are emitted.
    /// </summary>
    private static async Task<int> RunAppDrillAsync(TestRootEnvironment environment, string script)
    {
        // LOCALSTUDIO_APPSPEC=1 compares the App Spec route (the model writes a spec, LocalStudio compiles it) on the same requests.
        environment.Web.PreferAppSpec = Environment.GetEnvironmentVariable("LOCALSTUDIO_APPSPEC") == "1";
        var conversation = environment.NewConversation();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        conversation.MessageAdded += message => Emit("chat", new { role = message.Role.ToString(), message.Text, choices = message.Choices.Select(choice => choice.Id) });
        conversation.CreationProgress += progress => Emit("progress", new { seconds = Math.Round(clock.Elapsed.TotalSeconds), progress.Stage, progress.Message });
        foreach (var line in (await File.ReadAllLinesAsync(Path.GetFullPath(script))).Select(line => line.Trim()).Where(line => line.Length > 0 && !line.StartsWith('#')))
        {
            var step = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                if (line == "!start")
                {
                    if (conversation.Messages[^1].Choices.Any(choice => choice.Id == ConversationOrchestrator.LocalOnlyChoice))
                        await conversation.ChooseAsync(ConversationOrchestrator.LocalOnlyChoice);
                    if (conversation.Messages[^1].Choices.All(choice => choice.Id != ConversationOrchestrator.StartChoice)) { Emit("chat-step", new { line, error = "start not offered", stage = conversation.Stage.ToString() }); continue; }
                    await conversation.ChooseAsync(ConversationOrchestrator.StartChoice);
                }
                else if (line.StartsWith('!'))
                {
                    var choice = conversation.Messages.LastOrDefault(message => message.Choices.Any(item => item.Id.StartsWith(line[1..], StringComparison.Ordinal)))?.Choices.First(item => item.Id.StartsWith(line[1..], StringComparison.Ordinal));
                    if (choice is null) { Emit("chat-step", new { line, error = "button not offered" }); continue; }
                    await conversation.ChooseAsync(choice.Id);
                }
                else await conversation.SubmitAsync(line);
            }
            catch (Exception ex)
            {
                Emit("chat-step", new { line, error = ex.GetType().Name + ": " + ex.Message });
                continue;
            }
            Emit("chat-step", new { line, seconds = Math.Round(step.Elapsed.TotalSeconds, 1), stage = conversation.Stage.ToString(), project = conversation.ProjectPath });
        }
        await environment.Runtime.UnloadModelsAsync();
        Emit("chat-log", new { conversation.Log.FilePath, project = conversation.ProjectPath });
        return 0;
    }
    /// <summary>4-3 drill: answer or edit PDFs with the real model; --files is "a.pdf|b.pdf" in a folder starting ls-pdf-drill.</summary>
    private static async Task<int> RunPdfDrillAsync(string root, string files, string text, bool apply, string? record = null)
    {
        await using var environment = TestRootEnvironment.Create(root);
        var paths = files.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Path.GetFullPath).ToArray();
        if (paths.Any(path => !Path.GetFileName(Path.GetDirectoryName(path)!).StartsWith("ls-pdf-drill", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("演習のPDFは ls-pdf-drill で始まるフォルダーに置いてください。");
        using var planner = new LocalPdfPlanner(environment.Runtime, environment.Settings);
        var assistant = new PdfAssistant(record is null ? planner : new RecordingPdfPlanner(planner, () => Path.GetFullPath(record)), new SafeLocalHarness.Windows.WindowsPdfImaging());
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var reply = await assistant.HandleAsync(text, paths);
        Emit("pdf-reply", new { seconds = Math.Round(clock.Elapsed.TotalSeconds, 1), reply.Text, ops = reply.Preview?.Ops, canApply = reply.Preview?.CanApply });
        if (apply && reply.Preview is { CanApply: true } preview)
        {
            var result = await assistant.ApplyAsync(preview.Id);
            Emit("pdf-applied", new { result.OutputPaths, result.Succeeded, result.Mismatches });
        }
        return 0;
    }

    /// <summary>4-5: what the npm registry says about a package (reads the registry entry only; nothing is downloaded).</summary>
    private static async Task<int> RunLibraryInfoAsync(string name, string? version)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        var info = await new NpmLibrarySource(http, Path.GetTempPath()).ReadAsync(name, version);
        Emit("library-info", new { info, metadataUrl = NpmLibrarySource.MetadataUrl(name) });
        return 0;
    }

    /// <summary>
    /// 4-5 drill: downloads one npm package version into a test root (the expected SHA-512 is given on the command line, as
    /// the approval), drafts its guide with the AI and puts the chosen files on the test root's shelf.
    /// </summary>
    private static async Task<int> RunLibraryFetchAsync(string root, string name, string version, string integrity, string? files, string? element = null)
    {
        await using var environment = TestRootEnvironment.Create(root);
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        var source = new NpmLibrarySource(http, Path.Combine(environment.Paths.Root, "library-downloads"));
        var info = await source.ReadAsync(name, version);
        if (info.Integrity != integrity) throw new InvalidOperationException($"承認された SHA-512 と登録情報が違うため、取得しません（登録情報: {info.Integrity}）。");
        var fetched = await source.FetchAsync(info);
        var chosen = string.IsNullOrWhiteSpace(files) ? fetched.Candidates.Take(1).ToArray()
            : files.Split('|').Select(file => fetched.Candidates.First(candidate => Path.GetFileName(candidate) == file)).ToArray();
        using var drafter = new LibraryGuideDrafter(environment.Runtime, environment.Settings);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var guide = await drafter.DraftAsync(name, version, fetched.DetectedGlobal, fetched.Readme);
        var entry = LibraryShelf.ForUser(environment.Paths).Add(new LibraryDraft(name, version, info.License, info.TarballUrl, fetched.DetectedGlobal, chosen, guide, element));
        Emit("library-fetch", new { ungrounded = LibraryGuideDrafter.UngroundedCode(guide, fetched.Readme), readmeCharacters = fetched.Readme?.Length, fetched.DownloadedBytes, candidates = fetched.Candidates.Select(Path.GetFileName), fetched.DetectedGlobal, chosen = chosen.Select(Path.GetFileName), guideSeconds = Math.Round(clock.Elapsed.TotalSeconds, 1), guide, entry.Key, entry.Folder });
        await environment.Runtime.UnloadModelsAsync();
        return 0;
    }

    /// <summary>4-5 drill: one web app made through the conversation with the test root's library shelf.</summary>
    private static async Task<int> RunLibraryAppAsync(string root, string text)
    {
        await using var environment = TestRootEnvironment.Create(root);
        var conversation = environment.NewConversation();
        conversation.CreationProgress += progress =>
        {
            if (progress.Stage is "libraries" or "candidate-rejected" or "auto-fix") Emit("library-app-progress", new { progress.Stage, progress.Message });
        };
        using var sampler = new ResourceSampler(environment.Runtime, environment.PressureProbe);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        await conversation.SubmitAsync(text);
        if (conversation.Stage == ConversationStage.Clarifying) await conversation.ChooseAsync("kind:" + StudioProjectKind.DesktopOrWebApp);
        if (conversation.Messages[^1].Choices.Any(choice => choice.Id == ConversationOrchestrator.LocalOnlyChoice)) await conversation.ChooseAsync(ConversationOrchestrator.LocalOnlyChoice);
        if (conversation.Messages[^1].Choices.Any(choice => choice.Id == ConversationOrchestrator.StartChoice)) await conversation.ChooseAsync(ConversationOrchestrator.StartChoice);
        var project = conversation.ProjectPath;
        Emit("library-app", new
        {
            stage = conversation.Stage.ToString(),
            seconds = Math.Round(clock.Elapsed.TotalSeconds, 1),
            sampler.MinAvailableBytes,
            project,
            libraries = project is null ? [] : LibraryShelf.ProjectLibraries(project),
            appJs = project is null || !File.Exists(Path.Combine(project, "app.js")) ? null : await File.ReadAllTextAsync(Path.Combine(project, "app.js")),
            last = conversation.Messages[^1].Text
        });
        await environment.Runtime.UnloadModelsAsync();
        return 0;
    }

    /// <summary>4-4b check: one PDF page rendered by Windows and read by Windows OCR (no AI, nothing written).</summary>
    private static async Task<int> RunOcrPageAsync(string file, int page, double scale)
    {
        var full = Path.GetFullPath(file);
        if (!Path.GetFileName(Path.GetDirectoryName(full)!).StartsWith("ls-pdf-drill", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("演習のPDFは ls-pdf-drill で始まるフォルダーに置いてください。");
        var imaging = new SafeLocalHarness.Windows.WindowsPdfImaging();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var png = await imaging.RenderPageAsync(full, page, scale);
        var rendered = clock.Elapsed.TotalSeconds;
        await File.WriteAllBytesAsync(Path.ChangeExtension(full, null) + $"_p{page}.png", png);
        var ocr = await imaging.RecognizeAsync(png);
        Emit("ocr-page", new { page, scale, words = ocr.Lines.SelectMany(line => line.Words).Select(word => word.Text).ToArray(), pngBytes = png.Length, ocr.Width, ocr.Height, renderSeconds = Math.Round(rendered, 2), totalSeconds = Math.Round(clock.Elapsed.TotalSeconds, 2), text = ocr.Text, lines = ocr.Lines.Count, japanese = SafeLocalHarness.Windows.WindowsPdfImaging.JapaneseOcrAvailable });
        return 0;
    }

    /// <summary>Reads one screenshot with the vision model in a test root and reports time and memory (3-5).</summary>
    private static async Task<int> RunDescribeImageAsync(string root, string image, string text)
    {
        await using var environment = TestRootEnvironment.Create(root);
        using var sampler = new ResourceSampler(environment.Runtime, environment.PressureProbe);
        var description = await environment.Describer.DescribeAsync([image], text,
            new Progress<ProjectCreationProgress>(value => Emit("describe-progress", new { value.Message })));
        Emit("describe-result", new { description.Model, description.Seconds, description.Text, sampler.PeakOwnedBytes, sampler.MinAvailableBytes });
        return 0;
    }

    private static async Task<int> RunBenchmarkAsync(string root, string? caseFilter, bool preferAppSpec, string? model, bool gas = false)
    {
        await using var environment = TestRootEnvironment.Create(root, model);
        environment.Web.PreferAppSpec = preferAppSpec;
        var cases = gas ? LocalStudioBenchmark.GasCases : LocalStudioBenchmark.WebCases;
        var selected = string.IsNullOrWhiteSpace(caseFilter)
            ? cases
            : cases.Where(item => caseFilter.Split(',').Contains(item.Id, StringComparer.OrdinalIgnoreCase)).ToArray();
        var runs = new List<BenchmarkRun>();
        foreach (var golden in selected)
        {
            var conversation = environment.NewConversation();
            var rejected = 0;
            var autoFixes = 0;
            conversation.CreationProgress += progress =>
            {
                if (progress.Stage == "candidate-rejected") Interlocked.Increment(ref rejected);
                if (progress.Stage == "auto-fix") Interlocked.Increment(ref autoFixes);
                // Why a route gave up is the most useful part of a benchmark run, so it is kept in the output.
                if (progress.Stage is "candidate-rejected" or "app-spec-rejected" or "app-spec-fallback" or "app-spec-ready" or "auto-fix" or "auto-fix-skipped")
                    Emit("bench-progress", new { golden.Id, progress.Stage, progress.Message });
            };
            using var sampler = new ResourceSampler(environment.Runtime, environment.PressureProbe);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            string? failure = null;
            try
            {
                await conversation.SubmitAsync(golden.Request);
                if (conversation.Stage == ConversationStage.Clarifying)
                    await conversation.ChooseAsync("kind:" + golden.Kind);
                if (conversation.Messages[^1].Choices.Any(choice => choice.Id == ConversationOrchestrator.LocalOnlyChoice))
                    await conversation.ChooseAsync(ConversationOrchestrator.LocalOnlyChoice);
                if (conversation.Messages[^1].Choices.Any(choice => choice.Id == ConversationOrchestrator.StartChoice))
                    await conversation.ChooseAsync(ConversationOrchestrator.StartChoice);
                if (conversation.Stage != ConversationStage.Completed) failure = conversation.Messages[^1].Text;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failure = ex.Message;
            }
            var createdSeconds = clock.Elapsed.TotalSeconds;
            var created = conversation.Stage == ConversationStage.Completed && conversation.ProjectPath is not null;
            var misses = created ? LocalStudioBenchmark.CheckGolden(golden, conversation.ProjectPath!) : ["not-created"];

            bool? improved = null;
            double? improvementSeconds = null;
            if (created && golden.Improvement is { } change)
            {
                var improveClock = System.Diagnostics.Stopwatch.StartNew();
                await conversation.SubmitAsync(change);
                if (conversation.Messages[^1].Choices.Any(choice => choice.Id == ConversationOrchestrator.StartChoice))
                    await conversation.ChooseAsync(ConversationOrchestrator.StartChoice);
                improved = conversation.Messages[^1].Text.Contains("を保存しました", StringComparison.Ordinal) &&
                           (!golden.ImprovementIsVisual || File.Exists(Path.Combine(conversation.ProjectPath!, AppStylesheetPolicy.FileName))) &&
                           // A GAS version is only useful once its difference zip is ready to paste.
                           (golden.Kind != StudioProjectKind.GoogleAppsScript || conversation.Messages[^1].Text.Contains("-diff.zip", StringComparison.Ordinal));
                improvementSeconds = Math.Round(improveClock.Elapsed.TotalSeconds, 1);
            }

            var run = new BenchmarkRun(golden.Id, created, created && misses.Count == 0, misses,
                rejected + 1, autoFixes, Math.Round(createdSeconds, 1),
                sampler.PeakOwnedBytes, sampler.MinAvailableBytes, failure is null ? null : failure[..Math.Min(failure.Length, 300)],
                improved, improvementSeconds,
                created && File.Exists(Path.Combine(conversation.ProjectPath!, LocalWebAppGenerationEngine.AppSpecFileName)));
            runs.Add(run);
            Emit("bench-run", run);
        }
        await environment.Runtime.UnloadModelsAsync();

        var score = LocalStudioBenchmark.Score(runs);
        var report = new
        {
            at = DateTimeOffset.Now,
            model = environment.Settings.Load().SelectedModel,
            kvCacheType = environment.Runtime.KvCacheType,
            route = gas ? "apps-script" : preferAppSpec ? "app-spec-first" : "free-generation",
            resourceMode = environment.Settings.Load().ResourceMode.ToString(),
            score,
            runs
        };
        var reportPath = Path.Combine(environment.Paths.Root, $"bench-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, JsonOptions), new UTF8Encoding(false));
        Emit("bench-result", new { score, reportPath });
        return 0;
    }

    private static async Task<int> RunRollbackDrillAsync(string root)
    {
        MemoryRelief.Enabled = true;
        var full = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        var real = AppPaths.ForCurrentUser().Root.TrimEnd(Path.DirectorySeparatorChar);
        if (full.Equals(real, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("ロールバック演習は実データのフォルダでは実行しません。");
        UserSpaceGuard.EnsureAllowed(full);
        var paths = new AppPaths(full,
            Path.Combine(full, "runtime"), Path.Combine(full, "models"), Path.Combine(full, "state"),
            Path.Combine(full, "logs"), Path.Combine(full, "downloads"), Path.Combine(full, "projects"));
        paths.EnsureCreated();

        using var catalog = new OfficialRuntimeCatalog();
        var pinned = catalog.GetOllamaStatus().Artifact
            ?? throw new InvalidOperationException("固定済みのOllamaがありません。");
        // Reviewed 2026-10-06: the release's sha256sum.txt and the GitHub API asset digest agree. Drill only.
        var previous = pinned with
        {
            Version = "0.35.0",
            OfficialSource = new Uri("https://github.com/ollama/ollama/releases/download/v0.35.0/ollama-windows-amd64.zip"),
            ExpectedSha256 = "d6f7d3dd4f5d013553a78c1e78b2521fcf41d43dd2863e4596cdc046fe6036db",
            ExpectedSizeBytes = 1_461_196_158,
            SignatureInformation = "sha256sum.txt of release v0.35.0 and GitHub API asset digest (reviewed 2026-10-06, drill only)"
        };

        var manager = new RuntimeArtifactManager(paths);
        var before = await StableOllamaVersionAsync(paths);
        Emit("drill-before", new { stableVersion = before });

        long nextReport = 0;
        var swap = await manager.ProvisionAsync(previous, OllamaManager.CreateIsolatedVersionVerifier(),
            new Progress<RuntimeDownloadProgress>(value =>
            {
                if (value.CompletedBytes < nextReport) return;
                nextReport = value.CompletedBytes + 100L * 1024 * 1024;
                Emit("drill-download", new { value.CompletedBytes, value.TotalBytes, value.Resumed });
            }));
        var afterSwap = await StableOllamaVersionAsync(paths);
        Emit("drill-swapped", new { swap.Passed, swap.FailureKind, swap.Summary, receiptVersion = swap.Receipt.Version, swap.Receipt.RollbackPath, stableVersion = afterSwap });
        if (!swap.Passed) return 1;

        var rolledBack = await manager.RollbackAsync("ollama");
        var afterRollback = await StableOllamaVersionAsync(paths);
        var receipt = (await new RuntimeArtifactStateStore(paths).LoadAsync()).FirstOrDefault(item => item.Product == "ollama");
        Emit("drill-rolled-back", new { rolledBack, stableVersion = afterRollback, receiptVersion = receipt?.Version, receiptStatus = receipt?.Status.ToString() });

        var passed = before == pinned.Version && afterSwap == previous.Version && rolledBack &&
                     afterRollback == pinned.Version && receipt?.Version == pinned.Version;
        Emit("drill-result", new { passed });
        return passed ? 0 : 1;
    }

    /// <summary>Runs the Stable ollama.exe with an isolated OLLAMA_HOST and returns the version it reports.</summary>
    private static async Task<string?> StableOllamaVersionAsync(AppPaths paths)
    {
        var executable = Path.Combine(paths.RuntimeRoot, "ollama", "stable", "content", "ollama.exe");
        if (!File.Exists(executable)) return null;
        var start = new System.Diagnostics.ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("--version");
        start.Environment["OLLAMA_HOST"] = OllamaManager.CreateIsolatedVersionVerifier().EnvironmentOverrides["OLLAMA_HOST"];
        using var process = System.Diagnostics.Process.Start(start)!;
        var output = await process.StandardOutput.ReadToEndAsync() + await process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await process.WaitForExitAsync(deadline.Token);
        var match = System.Text.RegularExpressions.Regex.Match(output, @"version is (\d+\.\d+\.\d+)");
        return match.Success ? match.Groups[1].Value : null;
    }

    private static void Emit(string eventName, object value) =>
        Console.WriteLine(JsonSerializer.Serialize(new { time = DateTimeOffset.UtcNow, @event = eventName, value }));
}

/// <summary>
/// The real AI stack rooted at a test folder: projects, conversations, settings and logs live only there, while the
/// managed runtime and model folders are used exactly as the app uses them. Never the real data folder itself.
/// </summary>
internal sealed class TestRootEnvironment : IAsyncDisposable
{
    private TestRootEnvironment(AppPaths paths, SettingsStore settings, WindowsMemoryPressureProbe pressureProbe,
        MemoryPressureMonitor pressureMonitor, OllamaManager runtime, LocalPlanningEngine planner, LocalWebAppGenerationEngine web,
        LocalAppsScriptGenerationEngine gas, LocalImageDescriber describer)
    {
        Gas = gas;
        Describer = describer;
        Paths = paths;
        Settings = settings;
        PressureProbe = pressureProbe;
        PressureMonitor = pressureMonitor;
        Runtime = runtime;
        Planner = planner;
        Web = web;
    }

    public AppPaths Paths { get; }
    public SettingsStore Settings { get; }
    public WindowsMemoryPressureProbe PressureProbe { get; }
    public MemoryPressureMonitor PressureMonitor { get; }
    public OllamaManager Runtime { get; }
    public LocalPlanningEngine Planner { get; }
    public LocalWebAppGenerationEngine Web { get; }
    public LocalAppsScriptGenerationEngine Gas { get; }
    public LocalImageDescriber Describer { get; }
    // Windows EXE generation, wired as the app wires it (app drills of EXE requests stopped without it, 2026-10-13).
    public LocalWindowsAppGenerationEngine? Windows { get; init; }

    public static TestRootEnvironment Create(string root, string? model = null)
    {
        MemoryRelief.Enabled = true;
        var full = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        var real = AppPaths.ForCurrentUser();
        var realRoot = real.Root.TrimEnd(Path.DirectorySeparatorChar);
        if (full.Equals(realRoot, StringComparison.OrdinalIgnoreCase) || full.StartsWith(realRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("演習と計測は実データのフォルダでは実行しません。");
        UserSpaceGuard.EnsureAllowed(full);
        var paths = new AppPaths(full, real.RuntimeRoot, real.ModelRoot, Path.Combine(full, "state"),
            Path.Combine(full, "logs"), Path.Combine(full, "downloads"), Path.Combine(full, "projects"));
        paths.EnsureCreated();
        var realSettings = Path.Combine(real.StateRoot, "settings.json");
        var testSettings = Path.Combine(paths.StateRoot, "settings.json");
        if (File.Exists(realSettings) && !File.Exists(testSettings)) File.Copy(realSettings, testSettings);

        var settings = new SettingsStore(paths);
        if (model is not null)
        {
            // The model under test is written to the test root's own settings only.
            if (!SettingsStore.IsAllowedModel(model)) throw new InvalidOperationException("許可されていないモデルです: " + model);
            settings.SaveAsync(settings.Load() with { SelectedModel = model }).GetAwaiter().GetResult();
        }
        var hardware = SystemHardwareProbe.Capture(paths);
        var resources = AdaptiveRuntimePolicy.Create(hardware, settings.Load().SelectedModel, settings.Load().ResourceMode, MemoryRelief.EstimateReclaimable());
        var pressureProbe = new WindowsMemoryPressureProbe();
        var pressureMonitor = new MemoryPressureMonitor(pressureProbe, () => settings.Load().ResourceMode, AdaptiveRuntimePolicy.EffectiveInstalledBytes(hardware));
        var runtime = new OllamaManager(paths, resourcePlan: resources, pressureMonitor: pressureMonitor);
        return new(paths, settings, pressureProbe, pressureMonitor, runtime,
            new LocalPlanningEngine(runtime, resources: resources),
            // 4-5: the test root has its own library shelf.
            new LocalWebAppGenerationEngine(runtime, settings, resources: resources) { Libraries = LibraryShelf.ForUser(paths) },
            new LocalAppsScriptGenerationEngine(runtime, settings, resources: resources),
            new LocalImageDescriber(runtime, resources: resources))
        {
            Windows = new LocalWindowsAppGenerationEngine(runtime, settings, paths, resources: resources) { BehaviorCheck = WindowsAppBehaviorChecker.CheckAsync }
        };
    }

    public ConversationOrchestrator NewConversation() => new(
        new HybridPlanningEngine(Planner), Planner, new ProjectScaffolder(Paths, generativeGenerator: Web, windowsGenerativeGenerator: Windows, appsScriptGenerator: Gas),
        ConversationLog.CreateSession(Paths), () => Settings.Load().SelectedModel,
        new KindRoutingImprover(new WebProjectImprover(Web), new GasProjectImprover(Gas)), Describer)
    {
        // Hand-over zips stay inside the test root, never in the user's Documents.
        GasHandoverRoot = Path.Combine(Paths.Root, "gas-handover")
    };

    public async ValueTask DisposeAsync()
    {
        Web.Dispose();
        Windows?.Dispose();
        Gas.Dispose();
        Describer.Dispose();
        Planner.Dispose();
        await Runtime.DisposeAsync();
        PressureMonitor.Dispose();
        PressureProbe.Dispose();
    }
}

/// <summary>Samples, every two seconds, the owned runtime's memory and the PC's free memory, keeping the extremes.</summary>
internal sealed class ResourceSampler : IDisposable
{
    private readonly Timer _timer;
    private long _peakOwned;
    private long _minAvailable = long.MaxValue;

    public ResourceSampler(ILocalModelRuntime runtime, IMemoryPressureProbe probe)
    {
        _timer = new Timer(_ =>
        {
            try
            {
                var owned = OwnedProcessMetrics.Measure(runtime.OwnedProcessIds).WorkingSetBytes;
                var available = probe.Sample().AvailableBytes;
                if (owned > Interlocked.Read(ref _peakOwned)) Interlocked.Exchange(ref _peakOwned, owned);
                if (available < Interlocked.Read(ref _minAvailable)) Interlocked.Exchange(ref _minAvailable, available);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // A process can exit between listing and measuring; the next sample covers it.
            }
        }, null, TimeSpan.Zero, TimeSpan.FromSeconds(2));
    }

    public long PeakOwnedBytes => Interlocked.Read(ref _peakOwned);
    public long MinAvailableBytes => Interlocked.Read(ref _minAvailable) == long.MaxValue ? 0 : Interlocked.Read(ref _minAvailable);

    public void Dispose() => _timer.Dispose();
}
