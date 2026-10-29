using System.Text.RegularExpressions;

namespace SafeLocalHarness.Core;

/// <summary>How big a request is: small fits one generation, large needs feature-by-feature building.</summary>
public enum CatalogSize { Small, Medium, Large }

/// <summary>
/// One request from the requirements catalogue: what people actually ask for (office work and everything else),
/// with the capabilities it needs and a few mechanical expectations. It is not a template: nothing here is fed to the
/// generator, the request text is submitted as a user would type it.
/// </summary>
public sealed record CatalogCase(
    string Id,
    string Category,
    StudioProjectKind Kind,
    CatalogSize Size,
    string Request,
    IReadOnlyList<string> Needs,
    IReadOnlyList<string> SourcePatterns,
    IReadOnlyList<AcceptanceScenario>? Acceptance = null);

/// <summary>
/// Passed means the app was seen doing the job (hand-written acceptance played on it). An app that only compiled, or
/// whose job could not be played here (an EXE, a GAS script, a canvas game), is "unverified", never passed: the
/// catalogue once reported 18 of 22 passing when about 2 really worked (audit 2026-10-20).
/// </summary>
public sealed record CatalogRun(
    string CaseId,
    string Category,
    string Kind,
    string Size,
    bool Created,
    bool Passed,
    IReadOnlyList<string> Misses,
    int Attempts,
    double Seconds,
    string? Failure,
    string Verdict = "");

public sealed record CatalogCategoryScore(string Category, int Cases, int Created, int Passed, double PassRate, int Unverified = 0, int Wrong = 0);

/// <summary>What a catalogue check found: verified (seen working), wrong (seen failing), unverified (could not be played).</summary>
public sealed record CatalogVerdict(string Verdict, IReadOnlyList<string> Misses)
{
    public const string Verified = "verified";
    public const string Wrong = "wrong";
    public const string Unverified = "unverified";
    public const string NotCreated = "not-created";
}

/// <summary>
/// The requirements catalogue: the yardstick for "any app the user asks for". Pass rates are kept per category so a
/// weak area (for example reading Excel files or editing PDFs) shows up instead of hiding in an average.
/// Capability tags in Needs name what the generated app must be able to do beyond plain UI, so a gap in the harness
/// (not in the model) is visible before any run.
/// </summary>
public static class RequirementsCatalog
{
    private const StudioProjectKind Web = StudioProjectKind.DesktopOrWebApp;
    private const StudioProjectKind Exe = StudioProjectKind.WindowsApp;
    private const StudioProjectKind Gas = StudioProjectKind.GoogleAppsScript;

    // Expectations hold for whichever kind the conversation made (a flashcard app became an EXE and a shift table a GAS
    // project, catalogue 2026-10-19), so each names the Web, EXE and GAS ways of doing the same thing.
    private const string Persists = @"(localStorage|indexedDB|File\.(Write|Read|Append)All(Text|Lines)|StreamWriter|PropertiesService|setValues|appendRow)";
    private const string Reacts = @"(addEventListener|\+=\s*(new\s+\w*EventHandler|\w+_?Click|\w+Changed)|\.Click\s*\+=|onEdit|onOpen|createMenu)";
    private const string Checks = @"(checkbox|checked|CheckBox|insertCheckboxes|isChecked)";
    private const string Prints = @"(window\.print|print\(|PrintDocument|PrintDialog)";
    private const string Draws = @"(getContext|OnPaint|Graphics|Paint\s*\+=)";
    private const string Ticks = @"(setInterval|setTimeout|requestAnimationFrame|Timer|Date\.now|DateTime\.Now|Stopwatch)";
    private const string RandomOrder = @"(Math\.random|new\s+Random|sort|OrderBy)";
    private const string Sounds = @"(AudioContext|Audio\(|SoundPlayer|SystemSounds|Console\.Beep)";
    private const string ReadsFile = @"(FileReader|\.text\(\)|OpenFileDialog|File\.ReadAll|DriveApp|getDataAsString)";
    private const string SavesFile = @"(Blob|download|SaveFileDialog|File\.WriteAll|createFile)";

    // Hand-written acceptance (the yardstick, never given to the generator): what a user would do and see. Names list
    // the usual wordings, so an app is not failed for calling 数量 個数.
    private static AcceptanceStep Set(string name, string value, bool optional = false) => new(Set: name, Value: value, Optional: optional);
    private static AcceptanceStep Press(string name, bool optional = false) => new(Press: name, Optional: optional);
    private static readonly AcceptanceStep Reopen = new(Reload: true);
    private const string AddButton = "追加|登録|記録|保存|入力|追加する|登録する";

    // Blank and duplicate rows go (the spaces around 佐藤 make the two rows the same once trimmed).
    private static readonly AcceptanceScenario[] CsvCleanAcceptance =
    [
        new("blank-and-duplicate-rows", "空白行と重複行が消えたCSVをダウンロードできる", [
            new(File: "CSV|ファイル|CSVファイル", Content: "名前,年齢\n 佐藤 ,30\n\n鈴木,25\n佐藤,30\n"),
            Press("整える|整形|実行|クリーン|処理|変換|クリーニング", optional: true), Press("ダウンロード|保存|書き出し|出力")],
            [], FileExpect: ["名前,年齢", "佐藤,30", "鈴木,25"], FileAbsent: ["line:佐藤,30", "line:"])
    ];

    private static readonly AcceptanceScenario[] InvoiceAcceptance =
    [
        new("subtotal-tax-total", "1行の請求書: 3個×200円", [Set("品名|商品|品目", "りんご"), Set("数量|個数", "3"), Set("単価|価格", "200"),
            Press("追加|行を追加|計算|反映", optional: true)], ["600", "660"])
    ];
    private static readonly AcceptanceScenario[] DailyReportAcceptance =
    [
        new("save-and-review", "日報を保存して開き直しても見返せる", [Set("日付", "2026-10-20"), Set("やったこと|今日やったこと|内容", "資料作成"),
            Set("明日やること|明日", "会議準備"), Set("気づき|メモ", "早めに着手", optional: true), Press("保存|登録|追加"), Reopen], ["資料作成"])
    ];
    private static readonly AcceptanceScenario[] FlashcardAcceptance =
    [
        new("add-and-keep", "カードを登録して開き直しても残る", [Set("表|おもて|単語|問題", "りんご"), Set("裏|うら|意味|答え", "apple"), Press(AddButton), Reopen], ["りんご|apple"])
    ];
    private static readonly AcceptanceScenario[] KakeiboAcceptance =
    [
        new("monthly-total", "同じ分類の2件が合計され、開き直しても残る", [
            Set("日付", "2026-10-05"), Set("項目|内容|品目|メモ", "昼食"), Set("金額", "800"), Set("分類|カテゴリ|カテゴリー|種類", "食費"), Press(AddButton),
            Set("日付", "2026-10-06"), Set("項目|内容|品目|メモ", "夕食"), Set("金額", "1200"), Set("分類|カテゴリ|カテゴリー|種類", "食費"), Press(AddButton)], ["2000"]),
        new("kept", "記録が開き直しても残る", [Set("日付", "2026-10-05"), Set("項目|内容|品目|メモ", "昼食"), Set("金額", "800"),
            Set("分類|カテゴリ|カテゴリー|種類", "食費"), Press(AddButton), Reopen], ["昼食|800"]),
        // "月ごと": a September and an October entry are not added together (one app summed all months, 2026-10-20).
        new("by-month", "別の月の記録は合算されない", [
            Set("日付", "2026-09-05"), Set("項目|内容|品目|メモ", "昼食"), Set("金額", "800"), Set("分類|カテゴリ|カテゴリー|種類", "食費"), Press(AddButton),
            Set("日付", "2026-10-06"), Set("項目|内容|品目|メモ", "夕食"), Set("金額", "1200"), Set("分類|カテゴリ|カテゴリー|種類", "食費"), Press(AddButton)], ["1200"], ["2000"])
    ];
    private static readonly AcceptanceScenario[] HabitAcceptance =
    [
        // Two days in a row make a streak of 2 (one app counted checked habits instead of days, 2026-10-20).
        new("two-day-streak", "2日続けてできたら連続2日", [Set("習慣|名前|内容", "読書"), Press(AddButton), Press("できた|完了|チェック|達成|読書"),
            new(NextDay: true), Press("できた|完了|チェック|達成|読書")], ["読書", "2日|連続2|連続日数:2|2回"])
    ];
    private static readonly AcceptanceScenario[] UnitAcceptance =
    [
        new("m-to-km", "1500 m は 1.5 km", [Set("値|数値|数|入力|変換する値|長さ", "1500"), Set("変換元|元の単位|元|から|from|単位", "m"), Set("変換先|先の単位|先|へ|to", "km"),
            Press("変換|計算", optional: true)], ["1.5"]),
        new("kg-to-g", "2 kg は 2000 g", [Set("値|数値|数|入力|変換する値|重さ", "2"), Set("変換元|元の単位|元|から|from|単位", "kg"), Set("変換先|先の単位|先|へ|to", "g"),
            Press("変換|計算", optional: true)], ["2000"])
    ];
    public static IReadOnlyList<CatalogCase> Cases { get; } =
    [
        // The user's own examples (2026-10-19).
        new("excel-merge-50", "事務・集計", Exe, CatalogSize.Large,
            "市内の学校50校程度に、イベントのアンケートを1校あたり1ブックのエクセルファイルで取りました。フォルダーを選ぶと、その中の50個のエクセルファイルから回答を読み取り、学校名の列を付けて1つのエクセルファイルにまとめるアプリ。",
            ["xlsx-read", "xlsx-write", "folder-pick"], [@"\.xlsx", @"(FolderBrowserDialog|Directory\.(Get|Enumerate)Files)"]),
        new("desktop-suggest", "監視・提案", Exe, CatalogSize.Large,
            "デスクトップ上を監視して、タスクやカレンダーの予定に追加すべき事柄（日付や締め切りが書かれた文書やウィンドウ）を抜き出し、ユーザーに候補として提案するアプリ。追加するかはユーザーが選ぶ。",
            ["screen-read", "text-extract", "date-parse", "background-timer"], [@"(Timer|FileSystemWatcher)", @"(DateTime|Regex)"]),
        new("pdf-editor", "文書編集", Exe, CatalogSize.Large,
            "PDFファイルをスライドのように編集するアプリ。文字の削除・書き換え、ページの回転、画像の削除と追加を自由にできて、別名で保存できる。",
            ["pdf-read", "pdf-write", "pdf-render", "image-insert", "pdf-japanese-text"], [@"(\.pdf|application/pdf|pdfjsLib|PDFLib)", @"(SaveFileDialog|save\(|download)"]),

        // Office work.
        new("csv-clean", "事務・集計", Web, CatalogSize.Medium,
            "CSVファイルを選ぶと、空白行と重複行を取り除き、前後の空白を消して、整えたCSVをダウンロードできるツール。",
            ["file-read", "file-download"], [ReadsFile, SavesFile], CsvCleanAcceptance),
        new("invoice", "事務・帳票", Web, CatalogSize.Medium,
            "品名・数量・単価を行ごとに入力すると、小計・消費税10%・合計を計算して、印刷できる請求書の形で表示するアプリ。",
            ["print"], [@"(\*|Math\.(round|floor)|Math\.Round)", Prints], InvoiceAcceptance),
        new("daily-report", "事務・帳票", Web, CatalogSize.Small,
            "日付・やったこと・明日やること・気づきを入力して保存し、過去の日報を一覧で見返せる日報アプリ。",
            ["persist"], [Persists], DailyReportAcceptance),
        new("shift-table", "事務・集計", Web, CatalogSize.Medium,
            "スタッフ名と1週間の出勤日をチェックで入力すると、曜日ごとの出勤人数と各自の出勤日数を集計して表示するシフト表。",
            [], [Checks]),
        new("rename-files", "ファイル操作", Exe, CatalogSize.Medium,
            "フォルダーを選び、中のファイル名の先頭に日付や連番を付けて一括で名前を変えるアプリ。変更前に新旧の名前を一覧で確認できる。",
            ["folder-pick", "file-rename"], [@"(File\.Move|FileInfo)", @"(ListView|DataGridView|ListBox)"]),
        new("folder-sort", "ファイル操作", Exe, CatalogSize.Medium,
            "ダウンロードフォルダーのファイルを、拡張子ごと（画像・文書・表計算・その他）のフォルダーに振り分けるアプリ。実行前に振り分け先を表示する。",
            ["folder-pick", "file-move"], [@"(File\.Move|Directory\.CreateDirectory)", @"Extension"]),
        new("gas-form-summary", "事務・集計", Gas, CatalogSize.Medium,
            "Googleフォームの回答が入ったスプレッドシートで、メニューから実行すると、質問ごとの回答の件数を新しいシートに集計するGAS。",
            ["sheets"], [@"SpreadsheetApp", @"(setValues|appendRow)"]),
        new("gas-reminder", "事務・連絡", Gas, CatalogSize.Small,
            "スプレッドシートのA列に締め切り日、B列に件名があるとき、締め切りが3日以内の行の一覧を自分にメールで送るGAS。",
            ["sheets", "mail"], [@"(MailApp|GmailApp)", @"new\s+Date"]),

        // Learning and everyday life.
        new("flashcards", "学習", Web, CatalogSize.Small,
            "表と裏に言葉を登録して、カードをめくって覚える単語帳。覚えたカードに印を付けられ、保存される。",
            ["persist"], [Persists], FlashcardAcceptance),
        new("kakeibo", "生活", Web, CatalogSize.Medium,
            "日付・項目・金額・分類を入れて記録し、月ごとの分類別の合計を表示する家計簿。データは保存される。",
            ["persist"], [Persists, @"(reduce|\+=|Sum\()"], KakeiboAcceptance),
        new("habit", "生活", Web, CatalogSize.Small,
            "毎日の習慣を登録して、その日にできたらチェックし、連続日数を表示する習慣トラッカー。",
            ["persist"], [Persists], HabitAcceptance),
        new("unit-convert", "ツール", Web, CatalogSize.Small,
            "長さ（mm・cm・m・km）と重さ（g・kg）を相互に変換する単位変換ツール。",
            [], [Reacts], UnitAcceptance),

        // Games, creative work, visualisation.
        new("memory-game", "ゲーム", Web, CatalogSize.Medium,
            "16枚のカードを裏返して同じ絵柄のペアを探す神経衰弱ゲーム。手数と経過時間を表示する。",
            [], [RandomOrder, Ticks]),
        new("breakout", "ゲーム", Web, CatalogSize.Medium,
            "キャンバスで動くブロック崩し。パドルはマウスか左右キーで動かし、ブロックを全部消すとクリア。",
            ["canvas"], [Draws, Ticks]),
        new("metronome", "音楽", Web, CatalogSize.Small,
            "テンポ（BPM）を指定してスタート・ストップできるメトロノーム。拍に合わせて音を鳴らし、表示も光らせる。",
            ["audio"], [Sounds, Ticks]),
        new("drawing", "創作", Web, CatalogSize.Small,
            "マウスでなぞって絵を描けるお絵かきアプリ。色と太さを選べて、全部消すボタンがある。",
            ["canvas"], [Draws, @"(lineTo|stroke|DrawLine|DrawLines|DrawCurve|FillEllipse|arc\()"]),
        new("bar-chart", "可視化", Web, CatalogSize.Small,
            "項目名と数値を入れると棒グラフで表示するアプリ。項目は追加・削除できる。",
            ["canvas-or-svg"], [@"(getContext|<svg|createElementNS|style\.(height|width)\s*=|OnPaint|FillRectangle|Chart|\.(Height|Width)\s*=|Size\s*=\s*new\s+Size\([^;]*\b(height|width|value)\b)"]),
        new("exe-timer", "ツール", Exe, CatalogSize.Small,
            "分と秒を指定してスタートすると残り時間を表示し、0になったら知らせるタイマー。",
            [], [@"Timer"]),
        new("exe-memo", "ツール", Exe, CatalogSize.Small,
            "メモを書いて保存できるメモ帳。閉じて開き直しても前の内容が残る。",
            ["persist"], [@"(File\.(Write|Read)AllText|StreamWriter)"])
    ];

    /// <summary>The capability tags the harness gives a generated app today (anything else is a known gap).</summary>
    public static IReadOnlySet<string> SupportedNeeds { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "persist", "print", "file-read", "file-download", "canvas", "canvas-or-svg", "audio",
        "folder-pick", "file-rename", "file-move", "background-timer", "date-parse", "text-extract",
        "sheets", "mail",
        // Excel workbooks in generated EXEs through the reviewed XlsxLite helper (WindowsToolbox, 2026-10-19).
        "xlsx-read", "xlsx-write",
        // PDFs in Web apps through the shelf libraries pdf.js (view) and pdf-lib (edit), approved 2026-10-19.
        "pdf-read", "pdf-write", "pdf-render", "image-insert",
        // Japanese text changed in place in EXEs through the reviewed PdfLite helper (letters the embedded font lacks are
        // written with MS Gothic / MS Mincho after the user agrees), 2026-10-20.
        "pdf-japanese-text"
    };

    public static IReadOnlyList<string> Gaps(CatalogCase item) => item.Needs.Where(need => !SupportedNeeds.Contains(need)).ToArray();

    public static IReadOnlyList<CatalogCase> Select(string? filter) =>
        string.IsNullOrWhiteSpace(filter)
            ? Cases
            : Cases.Where(item => filter.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Any(part => part.Equals(item.Id, StringComparison.OrdinalIgnoreCase) || part.Equals(item.Category, StringComparison.Ordinal) ||
                             part.Equals("kind:" + item.Kind, StringComparison.OrdinalIgnoreCase) || part.Equals("size:" + item.Size, StringComparison.OrdinalIgnoreCase))).ToArray();

    /// <summary>Expectations the generated sources miss (sources only: built EXEs are never read).</summary>
    public static IReadOnlyList<string> Check(CatalogCase item, string projectRoot)
    {
        // The conversation decides the output kind when the request does not name one (an invoice became an EXE,
        // 2026-10-19), so every kind of source is read and the kind made is reported by MadeKind.
        var extensions = new[] { ".gs", ".js", ".html", ".cs" };
        var source = string.Join("\n", Directory.EnumerateFiles(projectRoot, "*", SearchOption.AllDirectories)
            .Where(path => extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            .Select(File.ReadAllText));
        return item.SourcePatterns.Where(pattern => !Regex.IsMatch(source, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            .Select(pattern => "source:" + pattern).ToArray();
    }

    /// <summary>
    /// The honest verdict: source expectations, the false-success shapes of the kind made, and, for a web app, the
    /// hand-written acceptance played on it. Only an app seen doing the job is verified; one whose job could not be
    /// played here is unverified (with the reason), and one seen failing is wrong.
    /// </summary>
    public static async Task<CatalogVerdict> JudgeAsync(CatalogCase item, string projectRoot, CancellationToken cancellationToken = default)
    {
        var misses = Check(item, projectRoot).ToList();
        var made = MadeKind(projectRoot);
        var html = Path.Combine(projectRoot, "index.html");
        var js = Path.Combine(projectRoot, "app.js");
        if (made == Exe.ToString())
        {
            misses.AddRange(LocalWindowsAppGenerationEngine.FalseSuccessShapes(WindowsToolbox.AppSources(projectRoot)));
            return new(misses.Count > 0 ? CatalogVerdict.Wrong : CatalogVerdict.Unverified, misses.Count > 0 ? misses : ["unverified:exe-not-launched (compiled only; run it at the PC to confirm)"]);
        }
        if (made == Gas.ToString())
        {
            misses.AddRange((await GasStaticVerifier.VerifyAsync(projectRoot, false, cancellationToken)).Errors.Where(error => error != "gas.external-access-approved"));
            return new(misses.Count > 0 ? CatalogVerdict.Wrong : CatalogVerdict.Unverified, misses.Count > 0 ? misses : ["unverified:gas-not-run (checked statically; run it in Apps Script to confirm)"]);
        }
        if (made != Web.ToString() || !File.Exists(html) || !File.Exists(js)) return new(CatalogVerdict.Wrong, [.. misses, "made:" + made]);
        var page = await File.ReadAllTextAsync(html, cancellationToken);
        var script = await File.ReadAllTextAsync(js, cancellationToken);
        misses.AddRange(LocalWebAppGenerationEngine.FalseSuccessShapes(page, script));
        if (misses.Count > 0) return new(CatalogVerdict.Wrong, misses);
        if (item.Acceptance is not { Count: > 0 }) return new(CatalogVerdict.Unverified, ["unverified:no-playable-acceptance (canvas, sound, randomness or file picking cannot be played here)"]);
        var failures = await WebAppBehaviorChecker.CheckScenariosAsync(page, script, LibraryShelf.ProjectGlobals(projectRoot), item.Acceptance, cancellationToken, reportInconclusive: true);
        if (failures.Count == 0) return new(CatalogVerdict.Verified, []);
        // A control the yardstick could not find may be named differently, not missing: unverified, not wrong.
        return failures.All(failure => failure.StartsWith("acceptance.control-missing:", StringComparison.Ordinal) || failure.StartsWith("acceptance.inconclusive:", StringComparison.Ordinal))
            ? new(CatalogVerdict.Unverified, failures.Select(failure => "unverified:" + failure).ToArray())
            : new(CatalogVerdict.Wrong, failures);
    }

    /// <summary>Which kind the project turned out to be, from its files.</summary>
    public static string MadeKind(string projectRoot) =>
        File.Exists(Path.Combine(projectRoot, "Program.cs")) ? Exe.ToString()
        : Directory.EnumerateFiles(projectRoot, "*.gs").Any() ? Gas.ToString()
        : File.Exists(Path.Combine(projectRoot, "index.html")) ? Web.ToString() : "unknown";

    public static IReadOnlyList<CatalogCategoryScore> Score(IReadOnlyList<CatalogRun> runs) =>
        runs.GroupBy(run => run.Category)
            .Select(group => new CatalogCategoryScore(group.Key, group.Count(), group.Count(run => run.Created), group.Count(run => run.Passed),
                Math.Round(100.0 * group.Count(run => run.Passed) / group.Count(), 1),
                group.Count(run => run.Verdict == CatalogVerdict.Unverified), group.Count(run => run.Verdict == CatalogVerdict.Wrong)))
            .OrderBy(score => score.PassRate).ToArray();
}
