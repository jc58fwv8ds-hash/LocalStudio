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
            // --no-launch: built EXEs are not started (outside the hours the user allows screen use).
            TestRootEnvironment.NoLaunch = HasFlag(args, "--no-launch");
            // --expert: Expert mode for this run only, as the app's session-only choice (never written to settings).
            TestRootEnvironment.Expert = HasFlag(args, "--expert");
            if (args[0].Equals("agent", StringComparison.OrdinalIgnoreCase))
                return await RunAgentAsync(RequiredOption(args, "--root"), RequiredOption(args, "--project"), RequiredOption(args, "--goal"),
                    int.TryParse(OptionalOption(args, "--steps"), out var agentSteps) ? agentSteps : 24, HasFlag(args, "--behavior"), HasFlag(args, "--keep"));
            // PDF editing phase 0: structure only (fonts, text commands, rules, widths); document text is never printed.
            if (args[0].Equals("pdf-survey", StringComparison.OrdinalIgnoreCase))
            {
                var file = RequiredOption(args, "--file");
                var split = (string? list) => (list ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                foreach (var page in PdfStructureSurvey.Survey(file, split(OptionalOption(args, "--query")))) Emit("pdf-page", page);
                Emit("pdf-replacements", PdfStructureSurvey.Replacements(file, split(OptionalOption(args, "--replace"))));
                return 0;
            }
            // PDF editing phase 0 trial: one in-place edit to a new file, then the saved file is read back and compared.
            // Path commands only (operator names and numbers), for studying how rules are drawn; text operands are left out.
            if (args[0].Equals("pdf-paths", StringComparison.OrdinalIgnoreCase))
            {
                using var document = PdfSharp.Pdf.IO.PdfReader.Open(RequiredOption(args, "--file"), PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import);
                var sequence = PdfSharp.Pdf.Content.ContentReader.ReadContent(document.Pages[int.Parse(RequiredOption(args, "--page"), System.Globalization.CultureInfo.InvariantCulture) - 1]);
                var line = new StringBuilder();
                var printed = 0;
                foreach (var item in sequence.OfType<PdfSharp.Pdf.Content.Objects.COperator>())
                {
                    var name = item.OpCode.OpCodeName.ToString();
                    if (name is "Tj" or "TJ" or "QuoteSingle" or "QuoteDouble") { line.Append("[text] "); continue; }
                    line.Append(string.Join(" ", item.Operands.Select(operand => operand is PdfSharp.Pdf.Content.Objects.CString ? "(s)" : operand.ToString()))).Append(' ').Append(name).Append(" | ");
                    if (name is "f" or "F" or "S" or "B" or "b" or "n" or "ET") { Console.WriteLine(line.ToString()); line.Clear(); if (++printed >= 60) break; }
                }
                return 0;
            }
            // One page as a PNG next to it (Windows renderer), to look at an edit; only inside ls-pdf-acceptance.
            if (args[0].Equals("pdf-render", StringComparison.OrdinalIgnoreCase))
            {
                var full = Path.GetFullPath(RequiredOption(args, "--file"));
                if (!Path.GetFileName(Path.GetDirectoryName(full)!).StartsWith("ls-pdf-", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("PDFは ls-pdf- で始まるフォルダーに置いてください。");
                var pageNumber = int.Parse(RequiredOption(args, "--page"), System.Globalization.CultureInfo.InvariantCulture);
                var png = await new SafeLocalHarness.Windows.WindowsPdfImaging().RenderPageAsync(full, pageNumber, double.Parse(OptionalOption(args, "--scale") ?? "2", System.Globalization.CultureInfo.InvariantCulture));
                var target = Path.ChangeExtension(full, null) + $"_p{pageNumber}.png";
                await File.WriteAllBytesAsync(target, png);
                // --around <letter>: where that letter is in the image, so only that spot is looked at (pages with personal data).
                object? around = null;
                if (OptionalOption(args, "--around") is { } letterWanted)
                {
                    using var pig = UglyToad.PdfPig.PdfDocument.Open(full);
                    var pigPage = pig.GetPage(pageNumber);
                    var hit = pigPage.Letters.FirstOrDefault(letter => letter.Value == letterWanted);
                    if (hit is not null)
                    {
                        using var image = new MemoryStream(png);
                        var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(image.AsRandomAccessStream());
                        var factor = decoder.PixelWidth / pigPage.Width;
                        around = new { x = (int)(hit.BoundingBox.Left * factor), y = (int)((pigPage.Height - hit.BoundingBox.Top) * factor), w = (int)(hit.BoundingBox.Width * factor), h = (int)(hit.BoundingBox.Height * factor) };
                    }
                }
                Emit("pdf-render", new { target, bytes = png.Length, around });
                return 0;
            }
            // The Excel helper's CombineSheets on real workbooks (each sheet named after its file), checked with the Open XML validator.
            if (args[0].Equals("xlsx-combine", StringComparison.OrdinalIgnoreCase))
            {
                var output = Path.GetFullPath(RequiredOption(args, "--out"));
                UserSpaceGuard.EnsureAllowed(output);
                var files = RequiredOption(args, "--files").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var sources = files.Select(file => new KeyValuePair<string, string>(System.Text.RegularExpressions.Regex.Match(Path.GetFileNameWithoutExtension(file), @"^\S+").Value, file)).ToList();
                var summary = new List<string[]> { new[] { "学校名", "ファイル" } };
                summary.AddRange(sources.Select(source => new[] { source.Key, Path.GetFileName(source.Value) }));
                var notes = XlsxLite.CombineSheets(output, sources, "一覧", summary);
                using var check = DocumentFormat.OpenXml.Packaging.SpreadsheetDocument.Open(output, false);
                var errors = new DocumentFormat.OpenXml.Validation.OpenXmlValidator().Validate(check).Take(5).Select(error => error.Description + " @" + error.Path?.XPath).ToList();
                var sheets = check.WorkbookPart!.WorksheetParts.Select(part => new
                {
                    cells = part.Worksheet!.Descendants<DocumentFormat.OpenXml.Spreadsheet.Cell>().Count(),
                    styled = part.Worksheet!.Descendants<DocumentFormat.OpenXml.Spreadsheet.Cell>().Count(cell => cell.StyleIndex is not null && cell.StyleIndex.Value > 0),
                    merges = part.Worksheet!.Descendants<DocumentFormat.OpenXml.Spreadsheet.MergeCell>().Count(),
                    columns = part.Worksheet!.Descendants<DocumentFormat.OpenXml.Spreadsheet.Column>().Count(),
                    drawings = part.DrawingsPart is null ? 0 : 1
                }).ToList();
                Emit("xlsx-combine", new { output, notes, errors, sheets });
                return errors.Count == 0 ? 0 : 1;
            }
            // The helper generated PDF editors use, on a real PDF: one edit, saved to a new file, then read back and compared.
            if (args[0].Equals("pdflite-trial", StringComparison.OrdinalIgnoreCase))
            {
                var file = RequiredOption(args, "--file");
                var output = RequiredOption(args, "--out");
                var pageNumber = int.Parse(RequiredOption(args, "--page"), System.Globalization.CultureInfo.InvariantCulture);
                var find = OptionalOption(args, "--find");
                var replacement = OptionalOption(args, "--replace") ?? "";
                var family = OptionalOption(args, "--font");
                var clock = System.Diagnostics.Stopwatch.StartNew();
                var doc = PdfLite.Open(file);
                var opened = clock.Elapsed.TotalSeconds;
                PdfLiteEdit edit;
                double? rule = null;
                if (find is not null)
                    edit = family is null ? doc.ReplaceText(pageNumber, find, replacement, 1) : doc.ReplaceTextWithFont(pageNumber, find, replacement, 1, family);
                else if (OptionalOption(args, "--move") is { } move)
                    edit = doc.MoveRule(pageNumber, int.Parse(RequiredOption(args, "--index"), System.Globalization.CultureInfo.InvariantCulture), double.Parse(move, System.Globalization.CultureInfo.InvariantCulture), 0);
                else
                {
                    rule = double.Parse(RequiredOption(args, "--rule"), System.Globalization.CultureInfo.InvariantCulture);
                    edit = doc.SetRuleThickness(pageNumber, -1, rule.Value);
                }
                Emit("pdflite-edit", new { edit.Done, edit.Message, edit.MissingLetters, edit.GrowthPoints, edit.UsedSubstituteFont, openSeconds = Math.Round(opened, 2), rules = doc.Rules(pageNumber).Count });
                if (!edit.Done) return 1;
                doc.Save(output);
                Emit("pdflite-saved", new { seconds = Math.Round(clock.Elapsed.TotalSeconds, 2), bytes = new FileInfo(output).Length, original = new FileInfo(file).Length,
                    prefixKept = File.ReadAllBytes(output).AsSpan(0, (int)new FileInfo(file).Length).SequenceEqual(File.ReadAllBytes(file)) });
                Emit("pdf-verify", PdfInPlaceEdit.Verify(file, output, pageNumber, expected: find is null ? null : replacement,
                    removed: find is not null && !replacement.Contains(find, StringComparison.Ordinal) ? find : null, find: find, ruleThickness: rule));
                return 0;
            }
            if (args[0].Equals("pdf-edit-trial", StringComparison.OrdinalIgnoreCase))
            {
                var file = RequiredOption(args, "--file");
                var output = RequiredOption(args, "--out");
                var pageNumber = int.Parse(RequiredOption(args, "--page"), System.Globalization.CultureInfo.InvariantCulture);
                var find = OptionalOption(args, "--find");
                var replacement = OptionalOption(args, "--replace");
                var edit = find is not null
                    ? PdfInPlaceEdit.ReplaceText(file, output, pageNumber, find, replacement ?? "")
                    : PdfInPlaceEdit.SetRuleThickness(file, output, pageNumber, double.Parse(RequiredOption(args, "--rule"), System.Globalization.CultureInfo.InvariantCulture));
                Emit("pdf-edit", edit);
                if (edit.Done)
                    Emit("pdf-verify", PdfInPlaceEdit.Verify(file, output, pageNumber, expected: replacement, removed: find is not null && replacement is not null && !replacement.Contains(find, StringComparison.Ordinal) ? find : null, find: find,
                        ruleThickness: find is null ? double.Parse(RequiredOption(args, "--rule"), System.Globalization.CultureInfo.InvariantCulture) : null));
                return edit.Done ? 0 : 1;
            }
            if (args[0].Equals("catalog-judge", StringComparison.OrdinalIgnoreCase))
                return await JudgeCatalogAsync(RequiredOption(args, "--runs"));
            if (args[0].Equals("catalog", StringComparison.OrdinalIgnoreCase))
                return HasFlag(args, "--list")
                    ? ListCatalog(OptionalOption(args, "--cases"))
                    : await RunCatalogAsync(RequiredOption(args, "--root"), OptionalOption(args, "--cases"));
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
                return await RunLibraryFetchAsync(RequiredOption(args, "--root"), RequiredOption(args, "--name"), RequiredOption(args, "--version"), RequiredOption(args, "--integrity"), OptionalOption(args, "--files"), OptionalOption(args, "--element"), OptionalOption(args, "--guide-file"), OptionalOption(args, "--global"));
            if (args[0].Equals("library-add-font", StringComparison.OrdinalIgnoreCase))
                return RunLibraryAddFont(RequiredOption(args, "--root"), RequiredOption(args, "--name"), RequiredOption(args, "--version"), RequiredOption(args, "--family"),
                    RequiredOption(args, "--font"), RequiredOption(args, "--license-file"), RequiredOption(args, "--source"), RequiredOption(args, "--guide-file"));
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
            var resources = AdaptiveRuntimePolicy.Create(hardware, settings.Load().SelectedModel, TestRootEnvironment.Expert ? ResourceMode.Expert : settings.Load().ResourceMode, MemoryRelief.EstimateReclaimable());
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

            using LocalDevelopmentPlanningEngine? planner = resources.Band == RuntimeResourceBand.DeterministicOnly
                ? null
                : new LocalDevelopmentPlanningEngine(runtime, settings, resources: resources);
            var destination = Path.Combine(paths.ProjectRoot, job.ProjectId);
            var sessionRequest = new DevelopmentSessionRequest(
                jobRoot,
                destination,
                job.ProjectId,
                job.ProjectName,
                job.AppPurpose,
                job.SourceRequest,
                job.RequirementTexts,
                job.TechStack,
                job.BuildInstructions,
                job.DesignDecisions,
                job.Plan);
            var prepared = await new DevelopmentSessionService(planner).PrepareAsync(sessionRequest);
            Emit("plan-ready", new { path = prepared.PlanPath, units = prepared.Plan.Units.Count });
            if (command == "plan") return 0;
            if (command != "run") return Usage("未対応のコマンドです: " + command);

            var toolchain = ResolveToolchain(args, paths, prepared.Plan);
            var store = new DevelopmentUnitCandidateStore(Path.Combine(jobRoot, "artifacts"));
            using var generator = new LocalDevelopmentUnitGenerationEngine(runtime, settings, resources: resources);
            var executor = new DevelopmentUnitExecutionEngine(
                generator,
                new DevelopmentUnitBuildVerifier(toolchain),
                store,
                runtime.UnloadModelsAsync);
            var maximumAttempts = OptionalPositiveInt(args, "--maximum-attempts") ?? int.MaxValue;
            var destinationExisted = Directory.Exists(destination);
            var session = await new DevelopmentSessionService(planner, executor, store).RunAsync(
                sessionRequest,
                progress: new Progress<DevelopmentUnitExecutionProgress>(value => Emit("development", value)),
                maximumUnitAttempts: maximumAttempts);
            if (session.Stage is DevelopmentSessionStage.Paused or DevelopmentSessionStage.Blocked)
            {
                Emit("not-complete", new
                {
                    blocked = session.BlockedUnitIds,
                    units = session.State.Units.Select(item => new { item.UnitId, status = item.Status.ToString(), item.Attempt, item.LastError })
                });
                return session.Stage == DevelopmentSessionStage.Blocked ? 3 : 2;
            }

            if (session.CompletionAllowed)
            {
                Emit(destinationExisted ? "already-stable" : "stable", new { destination = session.ProjectRoot, session.Stage, session.CompletionAllowed });
                return 0;
            }
            Emit("assembled-awaiting-evidence", new { destination = session.ProjectRoot, session.Stage, session.CompletionAllowed });
            return 4;
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
    private static async Task<int> RunLibraryFetchAsync(string root, string name, string version, string integrity, string? files, string? element = null, string? guideFile = null, string? global = null)
    {
        await using var environment = TestRootEnvironment.Create(root);
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        var source = new NpmLibrarySource(http, Path.Combine(environment.Paths.Root, "library-downloads"));
        var info = await source.ReadAsync(name, version);
        if (info.Integrity != integrity) throw new InvalidOperationException($"承認された SHA-512 と登録情報が違うため、取得しません（登録情報: {info.Integrity}）。");
        var fetched = await source.FetchAsync(info);
        var chosen = string.IsNullOrWhiteSpace(files) ? fetched.Candidates.Take(1).ToArray()
            : files.Split('|').Select(file => fetched.Candidates.First(candidate => Path.GetFileName(candidate) == file)).ToArray();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        // --guide-file: a guide written and checked by hand (the model's drafts named functions that do not exist, 2026-10-19).
        string guide;
        if (guideFile is not null) guide = await File.ReadAllTextAsync(guideFile);
        else
        {
            using var drafter = new LibraryGuideDrafter(environment.Runtime, environment.Settings);
            guide = await drafter.DraftAsync(name, version, fetched.DetectedGlobal, fetched.Readme);
        }
        var entry = LibraryShelf.ForUser(environment.Paths).Add(new LibraryDraft(NpmLibrarySource.ShelfName(name), version, info.License, info.TarballUrl, global ?? fetched.DetectedGlobal, chosen, guide, element));
        Emit("library-fetch", new { ungrounded = LibraryGuideDrafter.UngroundedCode(guide, fetched.Readme), readmeCharacters = fetched.Readme?.Length, fetched.DownloadedBytes, candidates = fetched.Candidates.Select(Path.GetFileName), fetched.DetectedGlobal, chosen = chosen.Select(Path.GetFileName), guideSeconds = Math.Round(clock.Elapsed.TotalSeconds, 1), guide, entry.Key, entry.Folder });
        await environment.Runtime.UnloadModelsAsync();
        return 0;
    }

    /// <summary>
    /// Puts a downloaded, verified font (TrueType/OpenType, OFL) on the test root's shelf as a *.font.js script with its
    /// licence, so web apps can embed Japanese text into PDFs. The guide is written by hand, not by the model.
    /// </summary>
    private static int RunLibraryAddFont(string root, string name, string version, string family, string font, string licenseFile, string source, string guideFile)
    {
        var paths = TestRootEnvironment.PathsFor(root);
        var staging = Path.Combine(paths.Root, "downloads", "font-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(staging);
        var script = Path.Combine(staging, Path.GetFileNameWithoutExtension(font) + ".font.js");
        File.WriteAllText(script, LibraryShelf.WrapFont(File.ReadAllBytes(font), family, File.ReadAllText(licenseFile)), new UTF8Encoding(false));
        var entry = LibraryShelf.ForUser(paths).Add(new LibraryDraft(name, version, "OFL-1.1", source, LibraryShelf.FontGlobal, [script], File.ReadAllText(guideFile)));
        Emit("library-add-font", new { entry.Manifest.Name, entry.Manifest.Version, bytes = new FileInfo(script).Length, entry.Folder });
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

    /// <summary>
    /// The agent loop on a project of the test root: the local model looks, edits and is checked until the goal is met.
    /// The kind follows the files (Program.cs: EXE, .gs: GAS, otherwise Web). --behavior starts built EXEs (screen use),
    /// --keep keeps the changes of a failed run (otherwise they are rolled back from the run snapshot).
    /// </summary>
    private static async Task<int> RunAgentAsync(string root, string project, string goal, int steps, bool behavior, bool keep)
    {
        await using var environment = TestRootEnvironment.Create(root);
        var full = Path.GetFullPath(project);
        var check = File.Exists(Path.Combine(full, "Program.cs"))
            ? AgentChecks.Windows(goal, behavior && !TestRootEnvironment.NoLaunch ? WindowsAppBehaviorChecker.CheckAsync : null)
            : Directory.EnumerateFiles(full, "*.gs").Any() ? AgentChecks.AppsScript(false) : AgentChecks.WebAsync;
        await environment.Runtime.EnsureServerAsync();
        var model = environment.Settings.Load().SelectedModel;
        await environment.Runtime.EnsureModelFitsAsync(model);
        var session = await new SafeWorkspaceEngine(environment.Paths).StartAsync(full);
        var loop = new AgentLoop(new OllamaAgentModel(environment.Runtime, model, environment.Resources), check, steps);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        loop.StepCompleted += step => Emit("agent-step", new { step, seconds = Math.Round(clock.Elapsed.TotalSeconds) });
        var outcome = await loop.RunAsync(session, full, goal);
        if (outcome.Completed) await session.CompleteAsync();
        else if (!keep) await session.RollbackAsync();
        await environment.Runtime.UnloadModelsAsync();
        Emit("agent-result", new { outcome.Completed, outcome.Reason, outcome.Checks, steps = outcome.Steps.Count, outcome.LastFindings, seconds = Math.Round(clock.Elapsed.TotalSeconds), runId = session.RunId });
        return outcome.Completed ? 0 : 1;
    }

    /// <summary>The catalogue without a model: each request with the capabilities the harness does not give yet.</summary>
    private static int ListCatalog(string? filter)
    {
        foreach (var item in RequirementsCatalog.Select(filter))
            Emit("catalog-case", new { item.Id, item.Category, kind = item.Kind.ToString(), size = item.Size.ToString(), item.Needs, gaps = RequirementsCatalog.Gaps(item) });
        return 0;
    }

    /// <summary>
    /// Runs catalogue requests through the real conversation (as the bench does) and reports pass rates per category.
    /// --cases takes ids, categories, kind:WindowsApp or size:Large. Windows cases launch the built EXE to check it,
    /// so they are run only while screen operation is allowed.
    /// </summary>
    private static async Task<int> RunCatalogAsync(string root, string? filter)
    {
        await using var environment = TestRootEnvironment.Create(root);
        environment.Web.PreferAppSpec = true;
        var runs = new List<CatalogRun>();
        foreach (var item in RequirementsCatalog.Select(filter))
        {
            var conversation = environment.NewConversation();
            var rejected = 0;
            conversation.CreationProgress += progress =>
            {
                if (progress.Stage == "candidate-rejected") Interlocked.Increment(ref rejected);
                if (progress.Stage is "candidate-rejected" or "app-spec-rejected" or "app-spec-fallback" or "auto-fix" or "agent-repair" or "agent-repair-step" or "agent-repair-ready" or "feature-growth" or "feature-growth-step" or "feature-growth-result")
                    Emit("catalog-progress", new { item.Id, progress.Stage, progress.Message });
            };
            var clock = System.Diagnostics.Stopwatch.StartNew();
            string? failure = null;
            try
            {
                await conversation.SubmitAsync(item.Request);
                if (conversation.Stage == ConversationStage.Clarifying)
                    await conversation.ChooseAsync("kind:" + item.Kind);
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
            var created = conversation.Stage == ConversationStage.Completed && conversation.ProjectPath is not null;
            var verdict = created ? await RequirementsCatalog.JudgeAsync(item, conversation.ProjectPath!) : new CatalogVerdict(CatalogVerdict.NotCreated, ["not-created"]);
            var run = new CatalogRun(item.Id, item.Category, item.Kind.ToString(), item.Size.ToString(), created, verdict.Verdict == CatalogVerdict.Verified, verdict.Misses,
                rejected + 1, Math.Round(clock.Elapsed.TotalSeconds, 1), failure is null ? null : failure[..Math.Min(failure.Length, 300)], verdict.Verdict);
            runs.Add(run);
            Emit("catalog-run", new { run, project = conversation.ProjectPath, madeKind = created ? RequirementsCatalog.MadeKind(conversation.ProjectPath!) : null });
        }
        await environment.Runtime.UnloadModelsAsync();
        var scores = RequirementsCatalog.Score(runs);
        var reportPath = Path.Combine(environment.Paths.Root, $"catalog-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(new { at = DateTimeOffset.Now, model = environment.Settings.Load().SelectedModel, scores, runs }, JsonOptions), new UTF8Encoding(false));
        Emit("catalog-result", new { scores, reportPath });
        return 0;
    }

    /// <summary>
    /// Judges projects an earlier catalogue run made (the catalog-run lines of its output, one or more files separated
    /// by ';'), without a model: the latest project of each case is played against the hand-written acceptance.
    /// </summary>
    private static async Task<int> JudgeCatalogAsync(string runFiles)
    {
        var latest = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in runFiles.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            foreach (var line in await File.ReadAllLinesAsync(file))
            {
                if (!line.Contains("\"catalog-run\"", StringComparison.Ordinal)) continue;
                try
                {
                    using var document = JsonDocument.Parse(line);
                    var data = document.RootElement.TryGetProperty("value", out var inner) ? inner : document.RootElement;
                    if (data.TryGetProperty("run", out var run) && (run.TryGetProperty("CaseId", out var id) || run.TryGetProperty("caseId", out id)) &&
                        data.TryGetProperty("project", out var project) && project.ValueKind == JsonValueKind.String)
                        latest[id.GetString()!] = project.GetString()!;
                }
                catch (JsonException) { }
            }
        var runs = new List<CatalogRun>();
        foreach (var item in RequirementsCatalog.Cases.Where(item => latest.ContainsKey(item.Id)))
        {
            var verdict = Directory.Exists(latest[item.Id]) ? await RequirementsCatalog.JudgeAsync(item, latest[item.Id]) : new CatalogVerdict(CatalogVerdict.NotCreated, ["project-missing"]);
            var run = new CatalogRun(item.Id, item.Category, item.Kind.ToString(), item.Size.ToString(), verdict.Verdict != CatalogVerdict.NotCreated,
                verdict.Verdict == CatalogVerdict.Verified, verdict.Misses, 0, 0, null, verdict.Verdict);
            runs.Add(run);
            Emit("catalog-judged", new { run, project = latest[item.Id], madeKind = RequirementsCatalog.MadeKind(latest[item.Id]) });
        }
        Emit("catalog-result", new { scores = RequirementsCatalog.Score(runs), verified = runs.Count(run => run.Passed), unverified = runs.Count(run => run.Verdict == CatalogVerdict.Unverified), wrong = runs.Count(run => run.Verdict == CatalogVerdict.Wrong), cases = runs.Count });
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
    public RuntimeResourcePlan? Resources { get; init; }

    /// <summary>The test root's folders (same rules as Create, without starting anything).</summary>
    public static AppPaths PathsFor(string root)
    {
        var full = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        var real = AppPaths.ForCurrentUser();
        var realRoot = real.Root.TrimEnd(Path.DirectorySeparatorChar);
        if (full.Equals(realRoot, StringComparison.OrdinalIgnoreCase) || full.StartsWith(realRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("演習と計測は実データのフォルダでは実行しません。");
        UserSpaceGuard.EnsureAllowed(full);
        var paths = new AppPaths(full, real.RuntimeRoot, real.ModelRoot, Path.Combine(full, "state"),
            Path.Combine(full, "logs"), Path.Combine(full, "downloads"), Path.Combine(full, "projects"));
        paths.EnsureCreated();
        return paths;
    }

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
        var resources = AdaptiveRuntimePolicy.Create(hardware, settings.Load().SelectedModel, Expert ? ResourceMode.Expert : settings.Load().ResourceMode, MemoryRelief.EstimateReclaimable());
        var pressureProbe = new WindowsMemoryPressureProbe();
        var pressureMonitor = new MemoryPressureMonitor(pressureProbe, () => Expert ? ResourceMode.Expert : settings.Load().ResourceMode, AdaptiveRuntimePolicy.EffectiveInstalledBytes(hardware));
        var runtime = new OllamaManager(paths, resourcePlan: resources, pressureMonitor: pressureMonitor);
        return new(paths, settings, pressureProbe, pressureMonitor, runtime,
            new LocalPlanningEngine(runtime, resources: resources),
            // 4-5: the test root has its own library shelf.
            new LocalWebAppGenerationEngine(runtime, settings, resources: resources) { Libraries = LibraryShelf.ForUser(paths), WriteAcceptance = true },
            new LocalAppsScriptGenerationEngine(runtime, settings, resources: resources),
            new LocalImageDescriber(runtime, resources: resources))
        {
            Windows = new LocalWindowsAppGenerationEngine(runtime, settings, paths, resources: resources) { BehaviorCheck = NoLaunch ? null : WindowsAppBehaviorChecker.CheckAsync },
            Resources = resources
        };
    }

    public static bool NoLaunch { get; set; }
    public static bool Expert { get; set; }

    public FeatureGrowth Growth() => new(Paths, Runtime, () => Settings.Load().SelectedModel, Resources, NoLaunch ? null : WindowsAppBehaviorChecker.CheckAsync);

    public ConversationOrchestrator NewConversation() => new(
        new HybridPlanningEngine(Planner), Planner, new ProjectScaffolder(Paths, generativeGenerator: Web, windowsGenerativeGenerator: Windows, appsScriptGenerator: Gas) { Repair = Growth().RepairAsync },
        ConversationLog.CreateSession(Paths), () => Settings.Load().SelectedModel,
        new KindRoutingImprover(new WebProjectImprover(Web), new GasProjectImprover(Gas)), Describer)
    {
        // Hand-over zips stay inside the test root, never in the user's Documents.
        GasHandoverRoot = Path.Combine(Paths.Root, "gas-handover"),
        Growth = Growth()
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
