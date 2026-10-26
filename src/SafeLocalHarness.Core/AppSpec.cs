using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SafeLocalHarness.Core;

// LocalStudio App Spec: the model describes WHAT the app is (state, screen elements, actions) in a small fixed
// vocabulary, and LocalStudio translates it into HTML/JS deterministically. After Alan Kay's STEPS (problem-oriented
// languages shrink code by orders of magnitude) and the Elm architecture (state / update / view). Forbidden browser
// APIs cannot appear because the compiler never writes them, and requirement evidence is checked mechanically.

public sealed record AppSpec(
    [property: JsonPropertyName("state")] IReadOnlyList<SpecState> State,
    [property: JsonPropertyName("ui")] IReadOnlyList<SpecElement> Ui,
    [property: JsonPropertyName("actions")] IReadOnlyList<SpecAction> Actions,
    [property: JsonPropertyName("requirements")] IReadOnlyList<SpecRequirement> Requirements,
    [property: JsonPropertyName("style")] IReadOnlyList<SpecStyle>? Style = null);

/// <summary>
/// type: text | number | list. persist: kept in this browser's localStorage. formula: a number worked out from other
/// states ("total / people") every time anything changes, like a spreadsheet cell.
/// </summary>
public sealed record SpecState(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("persist")] bool Persist = false,
    [property: JsonPropertyName("formula")] string? Formula = null);

/// <summary>type: text-input | number-input | textarea | button | list | text.</summary>
public sealed record SpecElement(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("label")] string? Label = null,
    [property: JsonPropertyName("bind")] string? Bind = null,
    [property: JsonPropertyName("action")] string? Action = null,
    [property: JsonPropertyName("source")] string? Source = null,
    [property: JsonPropertyName("template")] string? Template = null,
    [property: JsonPropertyName("itemAction")] SpecItemAction? ItemAction = null);

public sealed record SpecItemAction(
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("action")] string Action);

public sealed record SpecAction(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("steps")] IReadOnlyList<SpecStep> Steps);

/// <summary>
/// op: set | add | append | removeAt | clear | random | compute | startTimer | stopTimer.
/// value: a number, plain text, "$state" or "$index" (the list item an item action was pressed on).
/// </summary>
public sealed record SpecStep(
    [property: JsonPropertyName("op")] string Op,
    [property: JsonPropertyName("target")] string Target,
    [property: JsonPropertyName("value")] string? Value = null,
    [property: JsonPropertyName("min")] int? Min = null,
    [property: JsonPropertyName("max")] int? Max = null,
    [property: JsonPropertyName("expression")] string? Expression = null);

/// <summary>Which element and action implement a requirement (the mechanical evidence).</summary>
public sealed record SpecRequirement(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("ui")] string? Ui,
    [property: JsonPropertyName("action")] string? Action);

/// <summary>size: small | large; color: accent | danger | muted. Compiled to app.css, never free CSS.</summary>
public sealed record SpecStyle(
    [property: JsonPropertyName("target")] string Target,
    [property: JsonPropertyName("size")] string? Size = null,
    [property: JsonPropertyName("color")] string? Color = null);

public sealed record CompiledApp(GeneratedWebAppCandidate Candidate, IReadOnlyList<GeneratedRequirementReview> Evidence);

public static class AppSpecCompiler
{
    private static readonly Regex Name = new("^[a-z][A-Za-z0-9]{0,30}$", RegexOptions.CultureInvariant);
    private static readonly Regex ElementId = new("^[a-z][a-z0-9-]{0,30}$", RegexOptions.CultureInvariant);
    private static readonly Regex TemplateHole = new(@"\{(?<name>[a-z][A-Za-z0-9]{0,30})(?:\.(?<member>count|length|lines))?\}", RegexOptions.CultureInvariant);
    private static readonly Regex ExpressionToken = new(@"\s*(?:(?<number>\d+(?:\.\d+)?)|(?<name>[a-z][A-Za-z0-9]{0,30})(?:\.(?<member>length|count))?|(?<symbol>[-+*/(),]))", RegexOptions.CultureInvariant);
    private static readonly HashSet<string> StateTypes = ["text", "number", "list"];
    private static readonly HashSet<string> ElementTypes = ["text-input", "number-input", "textarea", "button", "list", "text"];
    private static readonly HashSet<string> Ops = ["set", "add", "append", "removeAt", "clear", "random", "compute", "startTimer", "stopTimer"];
    // Compiler helpers and browser globals that a generated name must never shadow.
    private static readonly HashSet<string> Reserved =
    [
        "state", "load", "save", "render", "round", "timers", "statusLine", "index", "event", "value", "item", "remove",
        "open", "close", "print", "stop", "alert", "confirm", "prompt", "fetch", "eval", "name", "status", "length",
        "document", "window", "location", "top", "parent", "self", "frames", "history", "navigator", "screen", "focus", "blur", "scroll",
        // JavaScript keywords: a function named "delete" stops the compiled script (trial 2026-10-13).
        "delete", "new", "function", "class", "return", "var", "let", "const", "if", "else", "for", "while", "switch", "case", "default", "this",
        "typeof", "void", "with", "yield", "in", "of", "do", "try", "catch", "finally", "throw", "import", "export", "super", "extends", "enum",
        "await", "null", "true", "false", "instanceof", "debugger", "continue", "break"
    ];

    // What the spec language has no words for: such requests are built by free generation instead.
    private static readonly Regex Unexpressible = new(
        "ゲーム|game|キャンバス|canvas|描画|お絵かき|絵を描|図形|グラフ|チャート|chart|アニメ|動き回|ドラッグ|drag|画像|写真|音|サウンド|音楽|地図|カレンダー|" +
        "盤|マス目|オセロ|将棋|囲碁|テトリス|パズル|迷路|当たり判定|スコア|クイズ|問題を出|選択肢|診断|占い|単語帳|フラッシュカード|しりとり|キーボードで操作|矢印キー|スワイプ|色を選|カラーピッカー|ファイルを読み込|CSV|印刷|QR",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>The first thing the request asks for that the spec language cannot express; null when it can.</summary>
    public static string? BeyondVocabulary(string request) => Unexpressible.Match(request ?? "") is { Success: true } match ? match.Value : null;

    /// <summary>Returns everything wrong with a spec; empty means it can be compiled.</summary>
    public static IReadOnlyList<string> Validate(AppSpec spec, IReadOnlyList<GeneratedRequirement> requirements)
    {
        var errors = new List<string>();
        if (spec.State is null || spec.Ui is null || spec.Actions is null || spec.Requirements is null) return ["spec.missing-section"];
        if (spec.State.Count > 12 || spec.Ui.Count > 14 || spec.Actions.Count > 12) errors.Add("spec.too-large");
        var states = spec.State.GroupBy(item => item.Name ?? "", StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var actions = spec.Actions.Select(item => item.Name ?? "").ToHashSet(StringComparer.Ordinal);
        var ids = spec.Ui.Select(item => item.Id ?? "").ToHashSet(StringComparer.Ordinal);
        if (states.Count != spec.State.Count || actions.Count != spec.Actions.Count || ids.Count != spec.Ui.Count) errors.Add("spec.duplicate-name");
        foreach (var state in spec.State)
        {
            if (!Name.IsMatch(state.Name ?? "") || Reserved.Contains(state.Name!) || !StateTypes.Contains(state.Type ?? "")) errors.Add("spec.state:" + state.Name);
            if (state.Formula is { Length: > 0 } formula && (state.Type != "number" || CompileExpression(formula, states) is null ||
                Regex.IsMatch(formula, $@"\b{Regex.Escape(state.Name ?? "")}\b"))) errors.Add("spec.formula:" + state.Name);
        }
        // A value the screen shows must come from somewhere: typed in, set by an action, or worked out by a formula
        // (trial 2026-10-13: a bill splitter showed "1人あたり: 0" whatever was typed).
        var runnable = spec.Ui.Select(element => element.Action).Concat(spec.Ui.Select(element => element.ItemAction?.Action)).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var written = spec.Actions.Where(action => runnable.Contains(action.Name ?? "")).SelectMany(action => action.Steps ?? []).Select(step => step.Target)
            .Concat(spec.Ui.Select(element => element.Bind)).OfType<string>().ToHashSet(StringComparer.Ordinal);
        foreach (var shown in spec.Ui.SelectMany(element => element.Template is null ? [] : TemplateHole.Matches(element.Template).Select(hole => hole.Groups["name"].Value)).Distinct())
            if (states.TryGetValue(shown, out var shownState) && shownState.Type != "list" && string.IsNullOrWhiteSpace(shownState.Formula) && !shownState.Persist && !written.Contains(shown))
                errors.Add("spec.never-set:" + shown);
        foreach (var action in spec.Actions)
        {
            if (!Name.IsMatch(action.Name ?? "") || Reserved.Contains(action.Name!) || states.ContainsKey(action.Name!) || action.Steps is not { Count: > 0 and <= 8 })
            {
                errors.Add("spec.action:" + action.Name);
                continue;
            }
            foreach (var step in action.Steps)
            {
                if (!Ops.Contains(step.Op ?? "") || !states.TryGetValue(step.Target ?? "", out var target)) { errors.Add($"spec.step:{action.Name}"); continue; }
                var needs = step.Op switch
                {
                    "append" or "removeAt" => "list",
                    "add" or "random" or "compute" or "startTimer" or "stopTimer" => "number",
                    _ => null
                };
                if (needs is not null && target.Type != needs) errors.Add($"spec.step-type:{action.Name}.{step.Op}");
                if (step.Value is { } value && !IsValue(value, states)) errors.Add($"spec.value:{action.Name}");
                if (step.Op == "random" && (step.Min is null || step.Max is null || step.Min > step.Max)) errors.Add($"spec.random:{action.Name}");
                if (step.Op == "compute" && CompileExpression(step.Expression ?? "", states) is null)
                    errors.Add($"spec.expression:{action.Name}({(step.Expression ?? "").Replace(",", " ")[..Math.Min((step.Expression ?? "").Length, 60)]})");
            }
        }
        foreach (var element in spec.Ui)
        {
            if (!ElementId.IsMatch(element.Id ?? "") || element.Id == "ls-status" || !ElementTypes.Contains(element.Type ?? "")) { errors.Add("spec.ui:" + element.Id); continue; }
            if (element.Type is "text-input" or "number-input" or "textarea" && (element.Bind is null || !states.ContainsKey(element.Bind))) errors.Add("spec.bind:" + element.Id);
            if (element.Type == "button" && (element.Action is null || !actions.Contains(element.Action))) errors.Add("spec.button:" + element.Id);
            if (element.Type == "list" && (element.Source is null || !states.TryGetValue(element.Source, out var source) || source.Type != "list")) errors.Add("spec.list:" + element.Id);
            if (element.ItemAction is { } itemAction && !actions.Contains(itemAction.Action ?? "")) errors.Add("spec.item-action:" + element.Id);
            if (element.Type == "text" && (element.Template is null || TemplateHole.Matches(element.Template).Any(hole => !states.ContainsKey(hole.Groups["name"].Value)))) errors.Add("spec.template:" + element.Id);
            if ((element.Label ?? element.Template ?? "").Length > 120) errors.Add("spec.text-too-long:" + element.Id);
        }
        if (!spec.Ui.Any(element => element.Type is "button" or "text-input" or "number-input" or "textarea")) errors.Add("spec.no-button");
        foreach (var appended in spec.Actions.SelectMany(action => action.Steps ?? []).Where(step => step.Op == "append").Select(step => step.Target).Distinct())
            if (!spec.Ui.Any(element => element.Type == "list" && element.Source == appended) && !spec.Ui.Any(element => (element.Template ?? "").Contains("{" + appended, StringComparison.Ordinal)))
                errors.Add("spec.list-not-shown:" + appended);
        foreach (var style in spec.Style ?? [])
            if (!ids.Contains(style.Target ?? "") || style.Size is not (null or "small" or "large") || style.Color is not (null or "accent" or "danger" or "muted")) errors.Add("spec.style:" + style.Target);
        foreach (var requirement in requirements)
        {
            var mapping = spec.Requirements.FirstOrDefault(item => item.Id == requirement.Id);
            // "ブラウザを閉じても残る" is met by saving, which the compiler does for persisted state; no button is needed.
            if (Regex.IsMatch(requirement.Text, "残る|残す|保存|閉じても|再読み込み|次回", RegexOptions.CultureInvariant) &&
                !Regex.IsMatch(requirement.Text, "ボタン|押") && spec.State.Any(state => state.Persist)) continue;
            if (mapping is null) { errors.Add("spec.requirement-missing:" + requirement.Id); continue; }
            if (RequirementEvidencePolicy.NeedsUiEvidence(requirement.Text) && (mapping.Ui is null || !ids.Contains(mapping.Ui))) errors.Add("spec.requirement-ui:" + requirement.Id);
            if (VisualRequirementPolicy.IsVisual(requirement.Text))
            {
                if (!(spec.Style ?? []).Any(style => style.Target == mapping.Ui)) errors.Add("spec.requirement-style:" + requirement.Id);
            }
            else if (mapping.Action is null || !(actions.Contains(mapping.Action) || mapping.Action == "render"))
            {
                // Background behaviour (saving) is implemented by the compiler itself for persisted state.
                var coveredByPersistence = !RequirementEvidencePolicy.NeedsUiEvidence(requirement.Text) && spec.State.Any(state => state.Persist);
                if (!coveredByPersistence) errors.Add("spec.requirement-action:" + requirement.Id);
            }
        }
        return errors;
    }

    /// <summary>
    /// Mechanical clean-up of the small slips a 7B model makes in a spec, before validation (the same idea as the
    /// script auto-fix): operation synonyms, JavaScript-style expressions, and requirement mappings that point at a
    /// button instead of naming its action. Only rewrites with one obvious meaning are made; anything else is left for
    /// validation to reject.
    /// </summary>
    public static AppSpec Normalize(AppSpec spec, IReadOnlyList<GeneratedRequirement>? requirements = null, string? request = null)
    {
        // The user's own words count too: the planner may have rephrased "+1ボタン" away.
        RequestedText = string.Join(" ", (requirements ?? []).Select(requirement => requirement.Text).Append(request ?? ""));
        if (spec.State is null || spec.Ui is null || spec.Actions is null || spec.Requirements is null) return spec;
        // Entries without a name, and missing lists, would stop the clean-up below; they carry nothing usable.
        spec = spec with
        {
            State = spec.State.Where(state => state is { Name: not null, Type: not null }).ToArray(),
            Actions = spec.Actions.Where(action => action?.Name is not null).Select(action => action with { Steps = (action.Steps ?? []).Where(step => step is { Op: not null, Target: not null }).ToArray() }).ToArray(),
            Ui = spec.Ui.Where(element => element is { Id: not null, Type: not null }).ToArray(),
            Requirements = spec.Requirements.Where(mapping => mapping?.Id is not null).ToArray()
        };
        spec = AsciiNames(spec);
        spec = NormalizeNames(spec);
        var actionNames = spec.Actions.Select(action => action.Name).ToHashSet(StringComparer.Ordinal);
        var actions = spec.Actions.Select(action => action with
        {
            Steps = (action.Steps ?? []).Select(NormalizeStep).ToArray()
        }).ToArray();
        var requirementMappings = spec.Requirements.Select(mapping =>
        {
            var element = spec.Ui.FirstOrDefault(item => item.Id == mapping.Ui);
            var action = mapping.Action is { } named && actionNames.Contains(named) ? named
                : element?.Action is { } buttonAction && actionNames.Contains(buttonAction) ? buttonAction
                : element?.ItemAction?.Action is { } itemAction && actionNames.Contains(itemAction) ? itemAction
                // A requirement met by what a text or list shows ("1人あたりの金額が表示される") is met by drawing it.
                : element?.Type is "text" or "list" ? "render"
                : mapping.Action;
            var ui = mapping.Ui is { } id && spec.Ui.Any(item => item.Id == id) ? id
                : spec.Ui.FirstOrDefault(item => item.Action == action)?.Id ?? mapping.Ui;
            return mapping with { Ui = ui, Action = action };
        }).ToList();
        // A requirement left without a (valid) element is matched to the element whose label or text shares its words
        // ("身長(cm)と体重(kg)の入力" → the 身長 and 体重 inputs; trial 2026-10-13). Typing into an input or showing a text
        // is carried out by drawing ("render").
        static string[] Words(string? text) => Regex.Matches(text ?? "", @"[ァ-ヺー]{2,}|[\p{IsCJKUnifiedIdeographs}々]{2,}|[A-Za-z]{3,}", RegexOptions.CultureInvariant).Select(match => match.Value).ToArray();
        foreach (var requirement in requirements ?? [])
        {
            var existing = requirementMappings.FirstOrDefault(mapping => mapping.Id == requirement.Id);
            if (existing?.Ui is { } valid && spec.Ui.Any(element => element.Id == valid)) continue;
            var words = Words(requirement.Text);
            var best = spec.Ui.Select(element => (Element: element, Score: Words((element.Label ?? "") + " " + (element.Template ?? "")).Count(word => words.Any(other => other.Contains(word, StringComparison.Ordinal) || word.Contains(other, StringComparison.Ordinal)))))
                .Where(item => item.Score > 0).OrderByDescending(item => item.Score).Select(item => item.Element).FirstOrDefault();
            // "ボタンを押すと…" with a single button: that button.
            if (best is null && Regex.IsMatch(requirement.Text, "ボタン|押|クリック|タップ") && spec.Ui.Count(element => element.Type == "button") == 1)
                best = spec.Ui.Single(element => element.Type == "button");
            if (best is null) continue;
            var bestAction = best.Action is { } bestNamed && actionNames.Contains(bestNamed) ? bestNamed
                : existing?.Action is { } kept && actionNames.Contains(kept) ? kept : "render";
            var fixedMapping = new SpecRequirement(requirement.Id, best.Id, bestAction);
            if (existing is null) requirementMappings.Add(fixedMapping); else requirementMappings[requirementMappings.IndexOf(existing)] = fixedMapping;
        }
        return NormalizeIntent(spec with { Actions = actions, Requirements = requirementMappings.ToArray() });
    }

    /// <summary>
    /// Names written in Japanese ("商品名", "add商品") are replaced everywhere they are used by plain generated names
    /// (value1, action1, part-1): names are never shown to the user, labels and texts keep the Japanese (trial 2026-10-13).
    /// </summary>
    private static AppSpec AsciiNames(AppSpec spec)
    {
        static bool Plain(string? name) => name is not null && Regex.IsMatch(name, "^[A-Za-z0-9_-]+$");
        var stateMap = new Dictionary<string, string>(StringComparer.Ordinal);
        var actionMap = new Dictionary<string, string>(StringComparer.Ordinal);
        var idMap = new Dictionary<string, string>(StringComparer.Ordinal);
        static string Camel(string name) => Regex.Replace(name, "[-_]+([a-zA-Z0-9])", match => match.Groups[1].Value.ToUpperInvariant());
        // "lap-count" → lapCount, "BMI" → bmi, "Total" → total: the same name in the allowed form.
        static string Lower(string name)
        {
            var camel = Camel(name);
            return camel.All(character => !char.IsLower(character)) ? camel.ToLowerInvariant() : char.ToLowerInvariant(camel[0]) + camel[1..];
        }
        foreach (var name in spec.State.Select(state => state.Name).OfType<string>().Where(name => !Plain(name) || Reserved.Contains(name) || !Name.IsMatch(name)).Distinct())
            stateMap[name] = Plain(name) && Name.IsMatch(Lower(name)) && !Reserved.Contains(Lower(name)) && !spec.State.Any(state => state.Name == Lower(name)) ? Lower(name)
                : Plain(name) && Name.IsMatch(Lower(name) + "Value") && !spec.State.Any(state => state.Name == Lower(name) + "Value") ? Lower(name) + "Value" : "value" + (stateMap.Count + 1);
        foreach (var name in spec.Actions.Select(action => action.Name).OfType<string>().Where(name => !Plain(name) || !Name.IsMatch(name)).Distinct())
            actionMap[name] = Plain(name) && Name.IsMatch(Lower(name)) && !spec.Actions.Any(action => action.Name == Lower(name)) ? Lower(name) : "action" + (actionMap.Count + 1);
        foreach (var id in spec.Ui.Select(element => element.Id).OfType<string>().Where(id => !Plain(id)).Distinct()) idMap[id] = "part-" + (idMap.Count + 1);
        if (stateMap.Count + actionMap.Count + idMap.Count == 0) return spec;
        string? S(string? name) => name is not null && stateMap.TryGetValue(name, out var mapped) ? mapped : name;
        string? A(string? name) => name is not null && actionMap.TryGetValue(name, out var mapped) ? mapped : name;
        string? I(string? id) => id is not null && idMap.TryGetValue(id, out var mapped) ? mapped : id;
        string? V(string? value) => value is not null && value.StartsWith('$') && stateMap.TryGetValue(value[1..], out var mapped) ? "$" + mapped : value;
        string? T(string? text) => text is null ? null : stateMap.Aggregate(text, (current, pair) => Regex.Replace(current, @"\{" + Regex.Escape(pair.Key) + @"(?=[.}])", "{" + pair.Value));
        string? E(string? expression) => expression is null ? null : stateMap.OrderByDescending(pair => pair.Key.Length)
            .Aggregate(expression, (current, pair) => Plain(pair.Key) ? Regex.Replace(current, $@"(?<![\w$]){Regex.Escape(pair.Key)}(?![\w])", pair.Value) : current.Replace(pair.Key, pair.Value, StringComparison.Ordinal));
        return spec with
        {
            State = spec.State.Select(state => state with { Name = S(state.Name)!, Formula = E(state.Formula) }).ToArray(),
            Actions = spec.Actions.Select(action => action with
            {
                Name = A(action.Name)!,
                Steps = (action.Steps ?? []).Select(step => step with { Target = S(step.Target)!, Value = V(step.Value), Expression = E(step.Expression) }).ToArray()
            }).ToArray(),
            Ui = spec.Ui.Select(element => element with
            {
                Id = I(element.Id)!, Bind = S(element.Bind), Source = S(element.Source), Action = A(element.Action), Template = T(element.Template),
                // A Japanese id or action name ("商品追加") is the label the model meant when it gave none.
                Label = !string.IsNullOrWhiteSpace(element.Label) || element.Type is "text" or "list" ? element.Label
                    : new[] { element.Id, element.Action, element.Bind }.FirstOrDefault(name => name is not null && !Plain(name) && name.Any(character => character > 0x3000)) is { } japanese
                        ? Regex.Replace(Regex.Replace(japanese, "[A-Za-z0-9_-]+", ""), "(ボタン|欄|フィールド)$", "") is { Length: > 0 } cleaned ? cleaned : element.Label
                        : element.Label,
                ItemAction = element.ItemAction is { } item ? item with { Action = A(item.Action)! } : null
            }).ToArray(),
            Requirements = spec.Requirements.Select(mapping => mapping with { Ui = I(mapping.Ui), Action = A(mapping.Action) }).ToArray(),
            Style = spec.Style?.Select(style => style with { Target = I(style.Target)! }).ToArray()
        };
    }

    // The requirement texts of the spec being normalized (per thread, as Normalize is static).
    [ThreadStatic] private static string? requestedText;
    private static string RequestedText { get => requestedText ?? ""; set => requestedText = value; }

    /// <summary>
    /// The name the request gives this button ("+1ボタン" for an increment, "リセットボタン" for a reset), matched by what
    /// the button's action or id says it does. Null when the request names no button for it.
    /// </summary>
    private static string? RequestedButtonLabel(SpecElement button)
    {
        var said = (button.Action ?? "") + " " + button.Id;
        foreach (Match phrase in Regex.Matches(RequestedText, @"([^\s、。・「」,]{1,8}?)ボタン", RegexOptions.CultureInvariant))
        {
            var name = phrase.Groups[1].Value.Trim('の', 'と', 'が', 'を');
            if (name.Length == 0) continue;
            var fits = Regex.IsMatch(name, @"^\+|増|足|プラス") ? Regex.IsMatch(said, "plus|inc|add|up", RegexOptions.IgnoreCase)
                : Regex.IsMatch(name, @"^[-−ー]|減|引|マイナス") ? Regex.IsMatch(said, "minus|dec|sub|down", RegexOptions.IgnoreCase)
                : ButtonLabels.Any(item => item.Label == name && item.Action.IsMatch(button.Action ?? ""));
            if (fits) return name;
        }
        return null;
    }

    private static readonly (Regex Action, string Label)[] ButtonLabels =
    [
        (new("^(start|begin|run|play)", RegexOptions.IgnoreCase), "スタート"), (new("^(stop|pause|halt)", RegexOptions.IgnoreCase), "ストップ"),
        (new("^(reset|clear)", RegexOptions.IgnoreCase), "リセット"), (new("lap", RegexOptions.IgnoreCase), "ラップ"),
        (new("^(add|append|create|insert)", RegexOptions.IgnoreCase), "追加"), (new("^(delete|remove)", RegexOptions.IgnoreCase), "削除"),
        (new("^(save|store)", RegexOptions.IgnoreCase), "保存"), (new("^(calc|compute)", RegexOptions.IgnoreCase), "計算")
    ];

    /// <summary>
    /// Slips with one obvious meaning seen in a stopwatch spec (trial 2026-10-13): a list used but never declared, a
    /// state name written without "$", actions literally named startTimer / stopTimer that set a made-up "$now"
    /// instead of using the timer steps, a measured time shown nowhere, buttons without labels, and actions nothing
    /// uses. Anything less certain is left for validation to reject.
    /// </summary>
    private static AppSpec NormalizeIntent(AppSpec spec)
    {
        // Formulas written the JavaScript way ("state.total / state.people", Math.round) mean the same arithmetic.
        var states = spec.State.Select(state => state.Formula is { Length: > 0 } formula
            ? state with { Formula = Regex.Replace(formula, @"\bstate\.", "", RegexOptions.CultureInvariant).Replace("Math.round(", "round(", StringComparison.Ordinal).Trim().TrimEnd(';') }
            : state).ToList();
        bool Declared(string? name) => name is not null && states.Any(state => state.Name == name);
        void Declare(string? name, string type)
        {
            if (type != "-" && name is not null && Name.IsMatch(name) && !Reserved.Contains(name) && !Declared(name) && states.Count < 12) states.Add(new SpecState(name, type));
        }
        foreach (var element in spec.Ui.Where(element => element.Type == "list")) Declare(element.Source, "list");
        foreach (var step in spec.Actions.SelectMany(action => action.Steps ?? []))
            Declare(step.Target, step.Op switch { "append" or "removeAt" => "list", "add" or "startTimer" or "stopTimer" or "random" or "compute" => "number", _ => "-" });
        foreach (var element in spec.Ui.Where(element => element.Type is "text-input" or "textarea")) Declare(element.Bind, "text");
        foreach (var element in spec.Ui.Where(element => element.Type == "number-input")) Declare(element.Bind, "number");
        // A value a calculation writes is a number even when declared as text ("bmi: text", trial 2026-10-13), unless text is typed into it.
        var textTyped = spec.Ui.Where(element => element.Type is "text-input" or "textarea").Select(element => element.Bind).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var counted = spec.Actions.SelectMany(action => action.Steps ?? []).Where(step => step.Op is "compute" or "add" or "random" or "startTimer" or "stopTimer").Select(step => step.Target).ToHashSet(StringComparer.Ordinal);
        states = states.Select(state => state.Type == "text" && counted.Contains(state.Name) && !textTyped.Contains(state.Name) ? state with { Type = "number" } : state).ToList();

        // The same action or element written twice: the first one is kept.
        var actions = spec.Actions.Where(action => action.Name is not null).GroupBy(action => action.Name, StringComparer.Ordinal).Select(group => group.First()).ToList();
        // "set <list> $typed" adds to the list (setting a list to one text broke its delete button, trial 2026-10-13);
        // "set <list> ''" empties it; removeAt always removes the item it is pressed on, so a value given to it means nothing.
        var listNames = states.Where(state => state.Type == "list").Select(state => state.Name).ToHashSet(StringComparer.Ordinal);
        // With one list, removing an item can only mean removing it from that list.
        if (listNames.Count == 1)
            actions = actions.Select(action => action with
            {
                Steps = (action.Steps ?? []).Select(step => step.Op == "removeAt" && !listNames.Contains(step.Target) ? step with { Target = listNames.First() } : step).ToArray()
            }).ToList();
        actions = actions.Select(action => action with
        {
            Steps = (action.Steps ?? []).Select(step =>
                step.Op == "set" && listNames.Contains(step.Target) ? (string.IsNullOrEmpty(step.Value) || step.Value == "[]" ? step with { Op = "clear", Value = null } : step with { Op = "append" })
                : step.Op == "removeAt" ? step with { Value = null }
                : step).ToArray()
        }).ToList();
        var hasTimer = actions.SelectMany(action => action.Steps ?? []).Any(step => step.Op is "startTimer" or "stopTimer");
        string? timerState = null;
        // A start action and a stop action that only "set" something (a made-up clock value) mean a timer.
        static bool Starts(string name) => Regex.IsMatch(name, "^(start|begin)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        static bool Stops(string name) => Regex.IsMatch(name, "^(stop|pause|end)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        static bool OnlySets(SpecAction action) => (action.Steps ?? []).All(step => step.Op == "set");
        if (!hasTimer && actions.Any(action => Starts(action.Name) && OnlySets(action)) && actions.Any(action => Stops(action.Name) && OnlySets(action)))
        {
            timerState = actions.Where(action => Starts(action.Name) || Stops(action.Name)).SelectMany(action => action.Steps ?? [])
                .Select(step => step.Target).FirstOrDefault(target => states.Any(state => state.Name == target && state.Type == "number"))
                ?? states.FirstOrDefault(state => state.Type == "number")?.Name;
            if (timerState is null && states.Count < 12) { timerState = "elapsed"; states.Add(new SpecState(timerState, "number")); }
            if (timerState is not null)
                actions = actions.Select(action =>
                    Starts(action.Name) && OnlySets(action) ? action with { Steps = [new SpecStep("startTimer", timerState)] }
                    : Stops(action.Name) && OnlySets(action) ? action with { Steps = [new SpecStep("stopTimer", timerState)] }
                    // "reset" setting the time to something that is not a number sets it to 0.
                    : Regex.IsMatch(action.Name, "^(reset|clear)", RegexOptions.IgnoreCase) ? action with
                    {
                        Steps = (action.Steps ?? []).Select(step => step.Op == "set" && step.Target == timerState && !double.TryParse(step.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out _)
                            ? step with { Value = "0" } : step).ToArray()
                    }
                    : action).ToList();
        }
        timerState ??= actions.SelectMany(action => action.Steps ?? []).FirstOrDefault(step => step.Op == "startTimer")?.Target;
        var names = states.Select(state => state.Name).ToHashSet(StringComparer.Ordinal);
        actions = actions.Select(action => action with
        {
            Steps = (action.Steps ?? []).Select(step => step.Value switch
            {
                { } bare when step.Op is "append" or "set" && names.Contains(bare) => step with { Value = "$" + bare },
                "$now" or "$time" or "$date" when timerState is not null && !names.Contains(step.Value![1..]) => step with { Value = "$" + timerState },
                _ => step
            }).ToArray()
        }).ToList();

        // An input bound to the list it fills (trial 2026-10-13, shopping list): it gets its own value, which "append"
        // adds to the list and then empties.
        var inputs = spec.Ui.ToList();
        foreach (var input in spec.Ui.Where(element => element.Type is "text-input" or "number-input" or "textarea" &&
                                                         states.Any(state => state.Name == element.Bind && state.Type == "list")).ToArray())
        {
            var list = input.Bind!;
            var typed = list + "Input";
            if (!Name.IsMatch(typed) || names.Contains(typed) || states.Count >= 12) continue;
            // What is typed into a list is text (item names), unless the request is about amounts or scores.
            var numeric = input.Type == "number-input" && Regex.IsMatch(RequestedText, "金額|数値|点数|円|数量|体重|距離|回数", RegexOptions.CultureInvariant);
            states.Add(new SpecState(typed, numeric ? "number" : "text"));
            names.Add(typed);
            inputs[inputs.IndexOf(input)] = input with { Bind = typed, Type = numeric ? "number-input" : input.Type == "number-input" ? "text-input" : input.Type };
            actions = actions.Select(action => action.Steps.Any(step => step.Op == "append" && step.Target == list)
                ? action with
                {
                    Steps = action.Steps.Select(step => step.Op == "append" && step.Target == list && (step.Value is null || step.Value == "$" + list || !names.Contains((step.Value ?? "").TrimStart('$')))
                            ? step with { Value = "$" + typed } : step)
                        .Append(new SpecStep("set", typed, numeric ? "0" : "")).ToArray()
                }
                : action).ToList();
        }
        // A number field whose value is only ever added to a list holds item names, unless the request is about amounts.
        if (!Regex.IsMatch(RequestedText, "金額|数値|点数|円|数量|体重|距離|回数", RegexOptions.CultureInvariant))
            foreach (var input in inputs.Where(element => element.Type == "number-input" && element.Bind is not null).ToArray())
            {
                var bound = input.Bind!;
                var uses = actions.SelectMany(action => action.Steps).Where(step => step.Value == "$" + bound || (step.Expression ?? "").Contains(bound, StringComparison.Ordinal)).ToArray();
                if (uses.Length == 0 || !uses.All(step => step.Op == "append")) continue;
                inputs[inputs.IndexOf(input)] = input with { Type = "text-input" };
                var at = states.FindIndex(state => state.Name == bound);
                if (at >= 0) states[at] = states[at] with { Type = "text" };
                actions = actions.Select(action => action with { Steps = action.Steps.Select(step => step.Op == "set" && step.Target == bound && step.Value == "0" ? step with { Value = "" } : step).ToArray() }).ToList();
            }
        spec = spec with { Ui = inputs };
        // "set total $total + $price" is a calculation; a calculation that cannot be read ("$list[$index]") is dropped.
        var known = states.ToDictionary(state => state.Name, StringComparer.Ordinal);
        // A formula that cannot be read, or that is not on a number, is dropped (the model added them to unrelated states).
        states = states.Select(state => state.Formula is { Length: > 0 } formula &&
                (state.Type != "number" || CompileExpression(formula, known) is null || Regex.IsMatch(formula, $@"\b{Regex.Escape(state.Name)}\b"))
            ? state with { Formula = null } : state).ToList();
        actions = actions.Select(action => action with
        {
            Steps = action.Steps.Select(step => step.Op == "set" && step.Value is { } value && value.Contains('$') && Regex.IsMatch(value, @"[-+*/]")
                    ? step with { Op = "compute", Value = null, Expression = value.Replace("$", "", StringComparison.Ordinal) } : step)
                .Where(step => step.Op != "compute" || CompileExpression(step.Expression ?? "", known) is not null).ToArray()
        }).ToList();

        // A step aimed at a state that does not exist is dropped; an action left empty goes, with the item buttons using it.
        actions = actions.Select(action => action with { Steps = (action.Steps ?? []).Where(step => Ops.Contains(step.Op ?? "") && names.Contains(step.Target ?? "")).ToArray() }).ToList();
        var emptied = actions.Where(action => action.Steps.Count == 0).Select(action => action.Name).ToHashSet(StringComparer.Ordinal);
        actions.RemoveAll(action => emptied.Contains(action.Name));
        // An input with no label takes the words a text shows before its value ("合計金額: {total}" labels the total input).
        string? LabelFromTexts(string? bind) => bind is null ? null : spec.Ui.Select(element => element.Template).OfType<string>()
            .Select(template => Regex.Match(template, $@"([^{{}}:：,、。\s]+)\s*[:：]\s*\{{{Regex.Escape(bind)}\}}", RegexOptions.CultureInvariant))
            .FirstOrDefault(match => match.Success)?.Groups[1].Value;
        var ui = spec.Ui.Where(element => element.Id is not null)
            .Select(element => element.Type is "text-input" or "number-input" or "textarea" && string.IsNullOrWhiteSpace(element.Label) && LabelFromTexts(element.Bind) is { } fromText
                ? element with { Label = fromText } : element).GroupBy(element => element.Id, StringComparer.Ordinal).Select(group => group.First())
            .Select(element => element.ItemAction is { } item && emptied.Contains(item.Action ?? "") ? element with { ItemAction = null } : element)
            // An item button naming an action that does not exist is the list's own delete action, when it has one.
            .Select(element => element.ItemAction is { } item && !actions.Any(action => action.Name == item.Action)
                ? actions.FirstOrDefault(action => action.Steps.Any(step => step.Op == "removeAt" && step.Target == element.Source)) is { } remover
                    ? element with { ItemAction = item with { Action = remover.Name, Label = string.IsNullOrWhiteSpace(item.Label) ? "削除" : item.Label } }
                    : element with { ItemAction = null }
                : element)
            // "+1ボタン、-1ボタン": the request's own names for the buttons come first (a +1 button was labelled 追加).
            .Select(element => element.Type == "button" && string.IsNullOrWhiteSpace(element.Label) && RequestedButtonLabel(element) is { } requested
                ? element with { Label = requested } : element)
            .Select(element => element.Type == "button" && string.IsNullOrWhiteSpace(element.Label) && element.Action is { } named &&
                                           ButtonLabels.FirstOrDefault(item => item.Action.IsMatch(named)) is { Label: { } label }
            ? element with { Label = label } : element).ToList();
        // A measured time must be visible somewhere.
        if (timerState is not null && !ui.Any(element => element.Type == "text" && (element.Template ?? "").Contains("{" + timerState, StringComparison.Ordinal)) &&
            ui.Count < 14 && !ui.Any(element => element.Id == "elapsed-display"))
            ui.Insert(0, new SpecElement("elapsed-display", "text", Template: $"経過時間: {{{timerState}}}秒"));
        // So must a number the buttons change (a counter showed its count only inside an input box, a draw nowhere).
        var buttonActions = ui.Where(element => element.Type == "button").Select(element => element.Action).OfType<string>().ToHashSet(StringComparer.Ordinal);
        foreach (var changed in actions.Where(action => buttonActions.Contains(action.Name)).SelectMany(action => action.Steps)
                     .Where(step => step.Op is "add" or "random" or "compute").Select(step => step.Target).Distinct().ToArray())
        {
            if (changed == timerState || ui.Count >= 14 || ui.Any(element => element.Type == "text" && (element.Template ?? "").Contains("{" + changed, StringComparison.Ordinal))) continue;
            var displayId = Regex.Replace(changed.ToLowerInvariant(), "[^a-z0-9]", "") + "-display";
            if (ui.Any(element => element.Id == displayId)) continue;
            ui.Insert(0, new SpecElement(displayId, "text", Template: "{" + changed + "}"));
        }

        var movedIds = new Dictionary<string, string>(StringComparer.Ordinal);
        // A list that items are added to must be shown (trial 2026-10-13: a shopping list kept its items out of sight).
        // Its delete action ("removeAt …, $index") belongs on each item, not on a lone button above the list.
        foreach (var listState in actions.SelectMany(action => action.Steps).Where(step => step.Op == "append").Select(step => step.Target).Distinct().ToArray())
        {
            if (ui.Any(element => element.Type == "list" && element.Source == listState) ||
                ui.Any(element => (element.Template ?? "").Contains("{" + listState, StringComparison.Ordinal)) || ui.Count >= 14) continue;
            var remover = actions.FirstOrDefault(action => action.Steps.Any(step => step.Op == "removeAt" && step.Target == listState));
            var listId = Regex.Replace(listState.ToLowerInvariant(), "[^a-z0-9]", "") + "-list";
            ui.Add(new SpecElement(listId, "list", Source: listState, ItemAction: remover is null ? null : new SpecItemAction("削除", remover.Name)));
            if (remover is null) continue;
            foreach (var lone in ui.Where(element => element.Type == "button" && element.Action == remover.Name).Select(element => element.Id).ToArray())
                movedIds[lone] = listId;
            ui.RemoveAll(element => element.Type == "button" && element.Action == remover.Name);
        }
        // A list's per-item action pressed from a lone button above the list has no item to act on (it removed the first one).
        foreach (var perItem in ui.Where(element => element.Type == "list" && element.ItemAction is not null).Select(element => (element.Id, Action: element.ItemAction!.Action)).ToArray())
        {
            foreach (var lone in ui.Where(element => element.Type == "button" && element.Action == perItem.Action).Select(element => element.Id).ToArray()) movedIds[lone] = perItem.Id;
            ui.RemoveAll(element => element.Type == "button" && element.Action == perItem.Action);
        }
        var requirements = spec.Requirements.Select(mapping => mapping.Ui is { } id && movedIds.TryGetValue(id, out var moved) ? mapping with { Ui = moved } : mapping).ToArray();

        // A calculation no button runs ("calculateAmountPerPerson", trial 2026-10-13) is meant to happen by itself: its
        // compute steps become formulas, and the requirement it met is met by drawing the result.
        var triggered = ui.Select(element => element.Action).Concat(ui.Select(element => element.ItemAction?.Action)).OfType<string>().ToHashSet(StringComparer.Ordinal);
        foreach (var calculation in actions.Where(action => !triggered.Contains(action.Name) && action.Steps.Count > 0 && action.Steps.All(step => step.Op == "compute")).ToArray())
        {
            foreach (var step in calculation.Steps)
            {
                var index = states.FindIndex(state => state.Name == step.Target && state.Type == "number" && string.IsNullOrWhiteSpace(state.Formula));
                if (index >= 0) states[index] = states[index] with { Formula = step.Expression };
            }
            actions.Remove(calculation);
            requirements = requirements.Select(mapping => mapping.Action == calculation.Name ? mapping with { Action = "render" } : mapping).ToArray();
        }

        // Actions nothing calls are dropped (they can only carry mistakes).
        var used = ui.Select(element => element.Action).Concat(ui.Select(element => element.ItemAction?.Action)).Concat(requirements.Select(mapping => mapping.Action))
            .OfType<string>().ToHashSet(StringComparer.Ordinal);
        actions.RemoveAll(action => !used.Contains(action.Name));

        // A lone button for an action that does not exist, with exactly one action no button runs: that action.
        var unrun = actions.Where(action => !ui.Any(element => element.Action == action.Name || element.ItemAction?.Action == action.Name)).ToArray();
        var orphans = ui.Where(element => element.Type == "button" && (element.Action is null || !actions.Any(action => action.Name == element.Action))).ToArray();
        if (orphans.Length == 1 && unrun.Length == 1)
        {
            ui[ui.IndexOf(orphans[0])] = orphans[0] with { Action = unrun[0].Name };
            requirements = requirements.Select(mapping => mapping.Ui == orphans[0].Id && (mapping.Action is null || !actions.Any(action => action.Name == mapping.Action)) ? mapping with { Action = unrun[0].Name } : mapping).ToArray();
        }
        // A button for an action that does not exist does nothing (the model made one for "ブラウザを閉じても残る").
        var actionNamesLeft = actions.Select(action => action.Name).ToHashSet(StringComparer.Ordinal);
        if (ui.Count(element => element.Type == "button") > 1 || ui.Any(element => element.Type is "text-input" or "number-input" or "textarea"))
        {
            var dead = ui.Where(element => element.Type == "button" && (element.Action is null || !actionNamesLeft.Contains(element.Action))).Select(element => element.Id).ToHashSet(StringComparer.Ordinal);
            ui.RemoveAll(element => dead.Contains(element.Id));
            requirements = requirements.Select(mapping => mapping.Ui is { } id && dead.Contains(id) ? mapping with { Ui = null, Action = mapping.Action is { } named && actionNamesLeft.Contains(named) ? named : null } : mapping).ToArray();
        }
        // "閉じても残る" / "保存": when nothing is kept, the lists are (or, without lists, the values the actions change);
        // saving itself is the compiler's job.
        if (Regex.IsMatch(RequestedText, "残る|残す|保存|閉じても|再読み込み|次回", RegexOptions.CultureInvariant) && !states.Any(state => state.Persist))
        {
            var changed = actions.SelectMany(action => action.Steps).Select(step => step.Target).ToHashSet(StringComparer.Ordinal);
            var keep = states.Any(state => state.Type == "list")
                ? states.Where(state => state.Type == "list").Select(state => state.Name).ToHashSet(StringComparer.Ordinal)
                : states.Where(state => state.Formula is null && changed.Contains(state.Name)).Select(state => state.Name).ToHashSet(StringComparer.Ordinal);
            states = states.Select(state => keep.Contains(state.Name) ? state with { Persist = true } : state).ToList();
        }
        // What was typed and added to a list is cleared from the field (otherwise the next item is typed after it:
        // "牛乳卵", trial 2026-10-13).
        var typedStates = ui.Where(element => element.Type is "text-input" or "number-input" or "textarea").Select(element => element.Bind).OfType<string>().ToHashSet(StringComparer.Ordinal);
        actions = actions.Select(action =>
        {
            var added = action.Steps.Where(step => step.Op == "append" && step.Value is { } value && value.StartsWith('$') && typedStates.Contains(value[1..])).Select(step => step.Value![1..]).Distinct().ToArray();
            var cleared = added.Where(name => !action.Steps.Any(step => step.Target == name && step.Op is "set" or "clear")).ToArray();
            return cleared.Length == 0 || action.Steps.Count + cleared.Length > 8 ? action : action with
            {
                Steps = action.Steps.Concat(cleared.Select(name => new SpecStep("set", name, states.FirstOrDefault(state => state.Name == name)?.Type == "number" ? "0" : ""))).ToArray()
            };
        }).ToList();
        // An input still without a label takes the words the request uses for it ("品名を入力して" → 品名).
        // "身長(cm)と体重(kg)を入力" names several inputs in order.
        if (Regex.Match(RequestedText, @"([^\s、。「」]{1,40}?)(?:を|に|の)入力", RegexOptions.CultureInvariant) is { Success: true } named)
        {
            var unlabeled = ui.Where(element => element.Type is "text-input" or "number-input" or "textarea").ToArray();
            // Split into several names only for several inputs ("やること" must stay whole).
            var labels = unlabeled.Length == 1 ? [named.Groups[1].Value.Trim()]
                : Regex.Split(named.Groups[1].Value, "と|・|、", RegexOptions.CultureInvariant).Select(label => label.Trim()).Where(label => label.Length is > 0 and <= 16).ToArray();
            if (labels.Length == unlabeled.Length && unlabeled.Any(element => string.IsNullOrWhiteSpace(element.Label)))
                for (var at = 0; at < unlabeled.Length; at++)
                    if (string.IsNullOrWhiteSpace(unlabeled[at].Label)) ui[ui.IndexOf(unlabeled[at])] = unlabeled[at] with { Label = labels[at] };
        }

        // A button still without a label is named after what it does (a lottery's only button was blank, trial 2026-10-13).
        ui = ui.Select(element => element.Type == "button" && string.IsNullOrWhiteSpace(element.Label) &&
                                  actions.FirstOrDefault(action => action.Name == element.Action)?.Steps.FirstOrDefault() is { } firstStep
            ? element with
            {
                Label = firstStep.Op switch
                {
                    "random" => "ランダムに出す",
                    "add" => double.TryParse(firstStep.Value ?? "1", NumberStyles.Float, CultureInfo.InvariantCulture, out var amount)
                        ? (amount < 0 ? "-" : "+") + Math.Abs(amount).ToString(CultureInfo.InvariantCulture) : "+1",
                    "append" => "追加", "removeAt" => "削除", "clear" => "クリア",
                    "compute" => "計算", "startTimer" => "スタート", "stopTimer" => "ストップ", _ => "実行"
                }
            }
            : element).ToList();
        // An input still without a label is at least named as one.
        ui = ui.Select(element => element.Type is "text-input" or "number-input" or "textarea" && string.IsNullOrWhiteSpace(element.Label) ? element with { Label = "入力" } : element).ToList();

        // A style for an element that is no longer there is dropped with it.
        var idsLeft = ui.Select(element => element.Id).ToHashSet(StringComparer.Ordinal);
        var style = spec.Style?.Where(item => item.Target is not null && idsLeft.Contains(item.Target)).ToArray();
        return spec with { State = states, Actions = actions, Ui = ui, Requirements = requirements, Style = style };
    }

    // Slips seen with 1.5B–4B models, each with one obvious meaning: camelCase element ids ("memoInput"), an action
    // named after a compiler helper ("save"), a step value naming the input element instead of its bound state, and
    // a state declared as text although it is only ever appended to / removed from like a list.
    private static AppSpec NormalizeNames(AppSpec spec)
    {
        var takenIds = spec.Ui.Select(item => item.Id ?? "").ToHashSet(StringComparer.Ordinal);
        var idMap = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var id in spec.Ui.Select(item => item.Id).OfType<string>())
        {
            if (ElementId.IsMatch(id)) continue;
            var kebab = Regex.Replace(id, "(?<=[a-z0-9])([A-Z])", "-$1", RegexOptions.CultureInvariant).Replace('_', '-').ToLowerInvariant();
            if (ElementId.IsMatch(kebab) && kebab != "ls-status" && takenIds.Add(kebab)) idMap[id] = kebab;
        }
        string? MapId(string? id) => id is not null && idMap.TryGetValue(id, out var mapped) ? mapped : id;

        var stateNames = spec.State.Select(item => item.Name ?? "").ToHashSet(StringComparer.Ordinal);
        var takenActions = spec.Actions.Select(item => item.Name ?? "").ToHashSet(StringComparer.Ordinal);
        var actionMap = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in spec.Actions.Select(item => item.Name).OfType<string>())
        {
            // "add-item" / "add_item" are camelCase names written another way.
            if (Regex.IsMatch(name, "[-_]") && Regex.Replace(name, "[-_]+([a-zA-Z0-9])", match => match.Groups[1].Value.ToUpperInvariant()) is var camel &&
                Name.IsMatch(camel) && !Reserved.Contains(camel) && !stateNames.Contains(camel) && takenActions.Add(camel)) { actionMap[name] = camel; continue; }
            if (!Reserved.Contains(name) && !stateNames.Contains(name)) continue;
            var renamed = name + "Action";
            if (Name.IsMatch(renamed) && !stateNames.Contains(renamed) && takenActions.Add(renamed)) actionMap[name] = renamed;
        }
        string? MapAction(string? name) => name is not null && actionMap.TryGetValue(name, out var mapped) ? mapped : name;

        var bindings = spec.Ui.Where(item => item.Id is not null && item.Bind is not null)
            .GroupBy(item => item.Id!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Bind!, StringComparer.Ordinal);
        string? MapValue(string? value)
        {
            if (value is null || !value.StartsWith('$') || value == "$index" || stateNames.Contains(value[1..])) return value;
            var element = value[1..];
            return bindings.TryGetValue(element, out var bound) || bindings.TryGetValue(MapId(element) ?? "", out bound) ? "$" + bound : value;
        }

        var actions = spec.Actions.Select(action => action with
        {
            Name = MapAction(action.Name)!,
            Steps = (action.Steps ?? []).Select(step => step with { Value = MapValue(step.Value) }).ToArray()
        }).ToArray();
        var steps = actions.SelectMany(action => action.Steps).ToArray();
        var boundStates = spec.Ui.Select(item => item.Bind).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var states = spec.State.Select(state =>
        {
            if (state.Type == "list" || boundStates.Contains(state.Name)) return state;
            var ops = steps.Where(step => step.Target == state.Name).Select(step => step.Op).ToArray();
            return ops.Length > 0 && ops.All(op => op is "append" or "removeAt" or "clear") && ops.Any(op => op is "append" or "removeAt")
                ? state with { Type = "list" }
                : state;
        }).ToArray();
        var ui = spec.Ui.Select(element => element with
        {
            Id = MapId(element.Id)!,
            Action = MapAction(element.Action),
            ItemAction = element.ItemAction is { } item ? item with { Action = MapAction(item.Action)! } : null
        }).ToArray();
        var requirements = spec.Requirements.Select(mapping => mapping with { Ui = MapId(mapping.Ui), Action = MapAction(mapping.Action) }).ToArray();
        var style = spec.Style?.Select(item => item with { Target = MapId(item.Target)! }).ToArray();
        return spec with { State = states, Ui = ui, Actions = actions, Requirements = requirements, Style = style };
    }

    private static SpecStep NormalizeStep(SpecStep step)
    {
        var op = step.Op switch
        {
            "increment" => "add",
            "push" or "addItem" => "append",
            "remove" or "delete" or "removeItem" => "removeAt",
            "reset" => "clear",
            "calculate" or "calc" => "compute",
            _ => step.Op
        };
        var expression = step.Expression;
        if (expression is not null)
        {
            expression = Regex.Replace(expression, @"\bstate\.", "", RegexOptions.CultureInvariant)
                .Replace("Math.round(", "round(", StringComparison.Ordinal)
                .Trim().TrimEnd(';').Trim();
            // "count = count + 1", "count += 1" and "count++" all mean "the new value of the target".
            expression = Regex.Replace(expression, $@"^\s*{Regex.Escape(step.Target ?? "")}\s*=(?!=)\s*", "", RegexOptions.CultureInvariant);
            var compound = Regex.Match(expression, $@"^\s*{Regex.Escape(step.Target ?? "")}\s*(?<op>[-+*/])=\s*(?<rest>.+)$", RegexOptions.CultureInvariant);
            if (compound.Success) expression = $"{step.Target} {compound.Groups["op"].Value} ({compound.Groups["rest"].Value})";
            var increment = Regex.Match(expression, $@"^\s*{Regex.Escape(step.Target ?? "")}\s*(?<op>\+\+|--)\s*$", RegexOptions.CultureInvariant);
            if (increment.Success) expression = $"{step.Target} {(increment.Groups["op"].Value == "++" ? "+" : "-")} 1";
        }
        // startTimer / stopTimer take no value; the model sometimes adds one ("1000", "setInterval") that would reject the
        // whole spec (trial 2026-10-13).
        return step with { Op = op, Expression = expression, Value = op is "startTimer" or "stopTimer" ? null : step.Value };
    }

    /// <param name="storageName">The app's name: saved values are kept under a key of their own, since every app opened
    /// from this PC's files shares one browser storage (trial 2026-10-13: a new shopping list showed another app's items).</param>
    public static CompiledApp Compile(AppSpec spec, IReadOnlyList<GeneratedRequirement> requirements, string? storageName = null)
    {
        storagePrefix = "ls-app-" + (storageName is { Length: > 0 }
            ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(storageName)))[..8].ToLowerInvariant() + "-"
            : "");
        var errors = Validate(spec, requirements);
        if (errors.Count > 0) throw new InvalidDataException("アプリ記述が規則に合いません: " + string.Join(", ", errors.Take(5)));
        var states = spec.State.ToDictionary(item => item.Name, StringComparer.Ordinal);
        return new(new GeneratedWebAppCandidate(BuildBody(spec), BuildScript(spec, states), BuildCss(spec)), BuildEvidence(spec, requirements));
    }

    [ThreadStatic] private static string? storagePrefix;

    private static string BuildBody(AppSpec spec)
    {
        var body = new StringBuilder("<section class=\"panel\">\n");
        foreach (var element in spec.Ui)
        {
            var label = WebUtility.HtmlEncode(element.Label ?? "");
            body.Append(element.Type switch
            {
                "text-input" => $"<label for=\"{element.Id}\">{label}</label><input id=\"{element.Id}\" type=\"text\" aria-label=\"{label}\">",
                "number-input" => $"<label for=\"{element.Id}\">{label}</label><input id=\"{element.Id}\" type=\"number\" step=\"any\" aria-label=\"{label}\">",
                "textarea" => $"<label for=\"{element.Id}\">{label}</label><textarea id=\"{element.Id}\" aria-label=\"{label}\"></textarea>",
                "button" => $"<button id=\"{element.Id}\" type=\"button\">{label}</button>",
                "list" => $"<ul id=\"{element.Id}\"></ul>",
                _ => $"<p id=\"{element.Id}\"></p>"
            }).Append('\n');
        }
        return body.Append("</section>").ToString();
    }

    private static string BuildScript(AppSpec spec, IReadOnlyDictionary<string, SpecState> states)
    {
        var js = new StringBuilder("'use strict';\nconst statusLine = document.getElementById('ls-status');\n");
        js.Append($"const storeKey = '{storagePrefix}';\n");
        js.Append("function load(name, fallback) { try { const raw = localStorage.getItem(storeKey + name); return raw === null ? fallback : JSON.parse(raw); } catch { return fallback; } }\n");
        js.Append("function round(value, digits) { const factor = Math.pow(10, digits || 0); return Math.round(value * factor) / factor; }\n");
        js.Append("const timers = {};\nconst state = {\n");
        foreach (var state in spec.State)
        {
            var initial = Initial(state.Type);
            js.Append($"  {state.Name}: {(state.Persist ? $"load('{state.Name}', {initial})" : initial)},\n");
        }
        js.Append("};\nfunction save() {\n");
        foreach (var state in spec.State.Where(item => item.Persist))
            js.Append($"  if ({Empty(state)}) localStorage.removeItem(storeKey + '{state.Name}'); else localStorage.setItem(storeKey + '{state.Name}', JSON.stringify(state.{state.Name}));\n");
        js.Append("}\nfunction render() {\n");
        foreach (var derived in spec.State.Where(item => item.Formula is { Length: > 0 }))
            js.Append($"  {{ const value = {CompileExpression(derived.Formula!, states)}; state.{derived.Name} = Number.isFinite(value) ? round(value, 2) : 0; }}\n");
        foreach (var element in spec.Ui)
        {
            var node = $"document.getElementById('{element.Id}')";
            switch (element.Type)
            {
                case "text-input" or "number-input" or "textarea":
                    js.Append($"  if (document.activeElement !== {node}) {node}.value = state.{element.Bind};\n");
                    break;
                case "list":
                    js.Append($"  {node}.replaceChildren(...state.{element.Source}.map((value, index) => {{ const item = document.createElement('li'); item.textContent = String(value);");
                    if (element.ItemAction is { } itemAction)
                        js.Append($" const remove = document.createElement('button'); remove.type = 'button'; remove.textContent = {Js(itemAction.Label)}; remove.addEventListener('click', () => {itemAction.Action}(index)); item.append(' ', remove);");
                    js.Append(" return item; }));\n");
                    break;
                case "text":
                    js.Append($"  {node}.textContent = {CompileTemplate(element.Template!)};\n");
                    break;
            }
        }
        js.Append("}\n");
        foreach (var action in spec.Actions)
        {
            js.Append($"function {action.Name}(index) {{\n");
            foreach (var step in action.Steps) js.Append("  ").Append(CompileStep(step, states)).Append('\n');
            js.Append("  save();\n  render();\n  statusLine.textContent = '更新しました。';\n}\n");
        }
        foreach (var element in spec.Ui)
        {
            var node = $"document.getElementById('{element.Id}')";
            if (element.Type == "button")
                js.Append($"{node}.addEventListener('click', () => {element.Action}());\n");
            else if (element.Bind is { } bind && element.Type is "text-input" or "number-input" or "textarea")
            {
                var value = states[bind].Type == "number" ? "Number(event.target.value)" : "event.target.value";
                var persist = states[bind].Persist ? $" localStorage.setItem(storeKey + '{bind}', JSON.stringify(state.{bind}));" : "";
                js.Append($"{node}.addEventListener('input', (event) => {{ state.{bind} = {value};{persist} render(); }});\n");
            }
        }
        return js.Append("render();\n").ToString();
    }

    private static string CompileStep(SpecStep step, IReadOnlyDictionary<string, SpecState> states)
    {
        var target = $"state.{step.Target}";
        var value = step.Value is null ? "''" : Value(step.Value, states);
        return step.Op switch
        {
            "set" => $"{target} = {value};",
            "add" => $"{target} += {(step.Value is null ? "1" : value)};",
            "append" => $"if (String({value}).trim() !== '') {target}.push({value});",
            "removeAt" => $"{target}.splice(index, 1);",
            "clear" => $"{target} = {Initial(states[step.Target].Type)};",
            "random" => $"{target} = {step.Min} + Math.floor(Math.random() * ({step.Max} - {step.Min} + 1));",
            "compute" => $"{target} = {CompileExpression(step.Expression!, states)};",
            "startTimer" => $"if (!timers.{step.Target}) timers.{step.Target} = setInterval(() => {{ {target} = round({target} + 0.1, 1); render(); }}, 100);",
            _ => $"clearInterval(timers.{step.Target}); timers.{step.Target} = null;"
        };
    }

    /// <summary>Arithmetic over state names, numbers, + - * / ( ) and round(x, digits) only.</summary>
    private static string? CompileExpression(string expression, IReadOnlyDictionary<string, SpecState> states)
    {
        if (expression.Length is 0 or > 160) return null;
        var output = new StringBuilder();
        var position = 0;
        foreach (Match token in ExpressionToken.Matches(expression))
        {
            if (token.Index != position) return null;
            position = token.Index + token.Length;
            if (token.Groups["number"].Success) output.Append(token.Groups["number"].Value);
            else if (token.Groups["symbol"].Success) output.Append(' ').Append(token.Groups["symbol"].Value).Append(' ');
            else if (token.Groups["member"].Success)
            {
                // "text.length" counts characters (as the template hole does); "items.length" counts list entries.
                if (!states.TryGetValue(token.Groups["name"].Value, out var measured) || measured.Type == "number") return null;
                output.Append(measured.Type == "list" ? $"state.{measured.Name}.length" : $"[...String(state.{measured.Name})].length");
            }
            else if (token.Groups["name"].Value == "round") output.Append("round");
            else if (states.TryGetValue(token.Groups["name"].Value, out var state) && state.Type == "number") output.Append("Number(state.").Append(state.Name).Append(')');
            else return null;
        }
        return position == expression.TrimEnd().Length || position == expression.Length ? output.ToString().Trim() : null;
    }

    private static string CompileTemplate(string template)
    {
        var parts = new List<string>();
        var last = 0;
        foreach (Match hole in TemplateHole.Matches(template))
        {
            if (hole.Index > last) parts.Add(Js(template[last..hole.Index]));
            var name = hole.Groups["name"].Value;
            parts.Add(hole.Groups["member"].Value switch
            {
                "count" => $"String(state.{name}.length)",
                "length" => $"String([...String(state.{name})].length)",
                "lines" => $"String(String(state.{name}).split('\\n').length)",
                _ => $"String(state.{name})"
            });
            last = hole.Index + hole.Length;
        }
        if (last < template.Length) parts.Add(Js(template[last..]));
        return parts.Count == 0 ? "''" : string.Join(" + ", parts);
    }

    private static string BuildCss(AppSpec spec)
    {
        var css = new StringBuilder();
        foreach (var style in spec.Style ?? [])
        {
            var rules = new List<string>();
            if (style.Size == "large") rules.Add("font-size: 1.35rem; padding: 0.8em 1.6em");
            if (style.Size == "small") rules.Add("font-size: 0.85rem");
            if (style.Color == "accent") rules.Add("background: #0a66d8; color: #fff");
            if (style.Color == "danger") rules.Add("background: #c62828; color: #fff");
            if (style.Color == "muted") rules.Add("color: #5f6b7a");
            if (rules.Count > 0) css.Append($"#{style.Target} {{ {string.Join("; ", rules)}; }}\n");
        }
        return css.ToString();
    }

    private static IReadOnlyList<GeneratedRequirementReview> BuildEvidence(AppSpec spec, IReadOnlyList<GeneratedRequirement> requirements) =>
        requirements.Select(requirement =>
        {
            var mapping = spec.Requirements.First(item => item.Id == requirement.Id);
            var behaviour = mapping.Action ?? (spec.State.Any(state => state.Persist) ? "localStorage.setItem" : "");
            return new GeneratedRequirementReview(requirement.Id, true, mapping.Ui ?? "", VisualRequirementPolicy.IsVisual(requirement.Text) ? "" : behaviour);
        }).ToArray();

    private static bool IsValue(string value, IReadOnlyDictionary<string, SpecState> states) =>
        value == "$index" ||
        (value.StartsWith('$') && states.ContainsKey(value[1..])) ||
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _) ||
        (value.Length <= 80 && !value.StartsWith('$'));

    private static string Value(string value, IReadOnlyDictionary<string, SpecState> states) =>
        value == "$index" ? "index"
        : value.StartsWith('$') ? $"state.{value[1..]}"
        : double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number.ToString(CultureInfo.InvariantCulture)
        : Js(value);

    private static string Initial(string type) => type switch { "number" => "0", "list" => "[]", _ => "''" };
    private static string Empty(SpecState state) => state.Type switch { "list" => $"state.{state.Name}.length === 0", "number" => "false", _ => $"state.{state.Name} === ''" };

    /// <summary>A JavaScript string literal for text from the spec (labels and template text only).</summary>
    private static string Js(string text) =>
        "'" + text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal).Replace("\r", "", StringComparison.Ordinal).Replace("<", "\\u003c", StringComparison.Ordinal) + "'";
}
