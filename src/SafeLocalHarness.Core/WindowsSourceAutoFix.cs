using System.Text;
using System.Text.RegularExpressions;

namespace SafeLocalHarness.Core;

/// <summary>
/// Mechanical repairs of the Windows Forms source a small model writes, each with one obvious meaning, made before
/// asking the model again (as GeneratedScriptAutoFix does for web apps). Trial 2026-10-13: a timer app written as if
/// a Visual Studio designer file existed (InitializeComponent, no Main) with C# 6/7 syntax ($"…", out int x) failed
/// every repair, because the model could not produce an applicable patch.
/// </summary>
public static class WindowsSourceAutoFix
{
    public sealed record Result(string Source, IReadOnlyList<string> Fixes);

    public static Result Apply(string source)
    {
        var fixes = new List<string>();
        var text = source;

        var interpolated = RewriteInterpolatedStrings(text);
        if (interpolated != text) { fixes.Add("interpolated-strings"); text = interpolated; }

        var outVariables = HoistOutVariables(text);
        if (outVariables != text) { fixes.Add("out-variables"); text = outVariables; }

        // A call to the designer's InitializeComponent with no such method: there is no designer file.
        if (Regex.IsMatch(text, @"\bInitializeComponent\s*\(\s*\)\s*;") && !Regex.IsMatch(text, @"void\s+InitializeComponent\s*\("))
        {
            // A call on a line of its own goes with its line; one inside other code ("{ InitializeComponent(); }") alone.
            text = Regex.Replace(text, @"^[ \t]*(?:this\.)?InitializeComponent\s*\(\s*\)\s*;[ \t]*\r?\n?", "", RegexOptions.Multiline);
            text = Regex.Replace(text, @"(?:this\.)?\bInitializeComponent\s*\(\s*\)\s*;\s*", "");
            fixes.Add("designer-call");
        }
        // "partial class" without another part only invites a missing designer half.
        if (Regex.IsMatch(text, @"\bpartial\s+class\b"))
        {
            text = Regex.Replace(text, @"\bpartial\s+class\b", "class");
            fixes.Add("partial-class");
        }

        // Path.Combine(Environment.SpecialFolder.X, …): the folder's path is Environment.GetFolderPath(…) (trial 2026-10-13).
        var folders = Regex.Replace(text, @"(?<!GetFolderPath\(\s*)\bEnvironment\.SpecialFolder\.(\w+)(?!\s*\))", "Environment.GetFolderPath(Environment.SpecialFolder.$1)");
        folders = Regex.Replace(folders, @"(?<=[(,]\s*)(?<!GetFolderPath\(\s*)\bEnvironment\.SpecialFolder\.(\w+)(?=\s*\))", "Environment.GetFolderPath(Environment.SpecialFolder.$1)");
        if (folders != text) { fixes.Add("special-folder-path"); text = folders; }

        // C-style arrays ("Button buttons[9];") are C# arrays, and a one-letter text is a string ("X"), not a char.
        var arrays = Regex.Replace(text, @"\b(?<type>[A-Z]\w*|int|string|char|bool|double)\s+(?<name>[A-Za-z_]\w*)\s*\[\s*(?<size>[\w\s*+,]+?)\s*\]\s*;",
            match => match.Groups["type"].Value + "[" + new string(',', match.Groups["size"].Value.Count(character => character == ',')) + "] " + match.Groups["name"].Value +
                     " = new " + match.Groups["type"].Value + "[" + match.Groups["size"].Value + "];");
        if (arrays != text) { fixes.Add("c-style-arrays"); text = arrays; }
        var characters = Regex.Replace(text, @"(\.Text\s*(?:=|==|!=)\s*)'(?<c>[^'\\])'", "$1\"${c}\"");
        if (characters != text) { fixes.Add("char-texts"); text = characters; }

        // Application.Run(this) inside the form runs a second message loop from the constructor; Main runs the form.
        var run = Regex.Replace(Regex.Replace(text, @"^[ \t]*Application\.Run\(\s*this\s*\)\s*;[ \t]*\r?\n?", "", RegexOptions.Multiline), @"\bApplication\.Run\(\s*this\s*\)\s*;\s*", "");
        if (run != text) { fixes.Add("form-run-loop"); text = run; }

        // "int currentPlayer = 'X'" means a character; a character shown as text is its string.
        var chars = Regex.Replace(text, @"\bint(\s+[A-Za-z_]\w*\s*=\s*'[^'\\]'\s*;)", "char$1");
        var charNames = Regex.Matches(chars, @"\bchar\s+(?<name>[A-Za-z_]\w*)\s*[=;]").Select(match => match.Groups["name"].Value).Distinct().ToArray();
        foreach (var name in charNames)
            chars = Regex.Replace(chars, $@"(\.Text\s*=\s*){Regex.Escape(name)}\s*;", "$1" + name + ".ToString();");
        if (chars != text) { fixes.Add("char-values"); text = chars; }

        // Saving into a folder that does not exist yet stops the app (trial 2026-10-14: …\MemoApp\memo.txt); the folder is
        // made first. Reading a file that is not there yet (the first start) gives empty text instead of an error.
        var files = Regex.Replace(text, @"^(?<indent>[ \t]*)(?<call>File\.(?:WriteAllText|WriteAllLines|AppendAllText|AppendAllLines)\(\s*(?<path>[A-Za-z_][\w.]*)\s*,)",
            match => text.Contains("Directory.CreateDirectory(Path.GetDirectoryName(" + match.Groups["path"].Value + "))", StringComparison.Ordinal) ? match.Value
                : match.Groups["indent"].Value + "Directory.CreateDirectory(Path.GetDirectoryName(" + match.Groups["path"].Value + "));\n" + match.Groups["indent"].Value + match.Groups["call"].Value,
            RegexOptions.Multiline);
        files = Regex.Replace(files, @"\breturn\s+File\.ReadAllText\(\s*(?<path>[A-Za-z_][\w.]*)\s*\)\s*;", "return File.Exists(${path}) ? File.ReadAllText(${path}) : \"\";");
        files = Regex.Replace(files, @"(?<target>[\w.]+\.Text\s*=\s*)File\.ReadAllText\(\s*(?<path>[A-Za-z_][\w.]*)\s*\)\s*;", "${target}File.Exists(${path}) ? File.ReadAllText(${path}) : \"\";");
        if (files != text) { fixes.Add("file-folders"); text = files; }

        var completed = CompleteDesignerControls(text);
        if (completed != text) { fixes.Add("designer-controls"); text = completed; }

        var entry = EnsureEntryPoint(text);
        if (entry != text) { fixes.Add("entry-point"); text = entry; }
        return new Result(text, fixes);
    }

    /// <summary>$"残り: {left} 秒" → string.Format("残り: {0} 秒", left); format specifiers are kept ({x:F1} → {0:F1}).</summary>
    internal static string RewriteInterpolatedStrings(string source)
    {
        var output = new StringBuilder();
        var index = 0;
        while (index < source.Length)
        {
            // $"…" (not @$"…", which stays for the validator to report).
            if (source[index] == '$' && index + 1 < source.Length && source[index + 1] == '"' && (index == 0 || source[index - 1] != '@'))
            {
                var (format, arguments, end) = ReadInterpolated(source, index + 2);
                if (end < 0) { output.Append(source[index]); index++; continue; }
                output.Append(arguments.Count == 0 ? "\"" + format + "\"" : "string.Format(\"" + format + "\", " + string.Join(", ", arguments) + ")");
                index = end;
                continue;
            }
            // Ordinary string and character literals are copied whole, so a "$" inside them is left alone.
            if (source[index] is '"' or '\'')
            {
                var quote = source[index];
                var start = index++;
                while (index < source.Length && source[index] != quote) index += source[index] == '\\' ? 2 : 1;
                index = Math.Min(index + 1, source.Length);
                output.Append(source, start, index - start);
                continue;
            }
            output.Append(source[index++]);
        }
        return output.ToString();
    }

    private static (string Format, List<string> Arguments, int End) ReadInterpolated(string source, int position)
    {
        var format = new StringBuilder();
        var arguments = new List<string>();
        while (position < source.Length)
        {
            var character = source[position];
            if (character == '\\' && position + 1 < source.Length) { format.Append(character).Append(source[position + 1]); position += 2; continue; }
            if (character == '"') return (format.ToString(), arguments, position + 1);
            if (character == '{' && position + 1 < source.Length && source[position + 1] == '{') { format.Append("{{"); position += 2; continue; }
            if (character == '}' && position + 1 < source.Length && source[position + 1] == '}') { format.Append("}}"); position += 2; continue; }
            if (character == '{')
            {
                // The expression runs to the matching "}", allowing nested brackets and strings; ":" starts a format.
                var depth = 0;
                var expression = new StringBuilder();
                string? specifier = null;
                position++;
                while (position < source.Length)
                {
                    var current = source[position];
                    if (current == '"')
                    {
                        var start = position++;
                        while (position < source.Length && source[position] != '"') position += source[position] == '\\' ? 2 : 1;
                        expression.Append(source, start, Math.Min(position + 1, source.Length) - start);
                        position++;
                        continue;
                    }
                    if (current is '(' or '[') depth++;
                    if (current is ')' or ']') depth--;
                    if (depth == 0 && current == '}') break;
                    if (depth == 0 && current == ':' && specifier is null && !expression.ToString().Contains('?'))
                    {
                        var close = source.IndexOf('}', position);
                        if (close < 0) return ("", arguments, -1);
                        specifier = source[(position + 1)..close];
                        position = close;
                        break;
                    }
                    expression.Append(current);
                    position++;
                }
                if (position >= source.Length) return ("", arguments, -1);
                format.Append('{').Append(arguments.Count).Append(specifier is null ? "" : ":" + specifier).Append('}');
                arguments.Add(expression.ToString().Trim());
                position++;
                continue;
            }
            if (character is '\r' or '\n') return ("", arguments, -1);
            format.Append(character);
            position++;
        }
        return ("", arguments, -1);
    }

    /// <summary>int.TryParse(text, out int minutes) → "int minutes;" on the line before, and "out minutes".</summary>
    internal static string HoistOutVariables(string source)
    {
        var lines = source.Replace("\r\n", "\n").Split('\n').ToList();
        for (var at = 0; at < lines.Count; at++)
        {
            var matches = Regex.Matches(lines[at], @"\bout\s+(?<type>int|long|double|float|decimal|bool|string|DateTime|TimeSpan)\s+(?<name>[A-Za-z_]\w*)");
            // A declaration cannot go between "}" and "else …"; such lines are left for the compiler to report.
            if (matches.Count == 0 || lines[at].TrimStart().StartsWith("else", StringComparison.Ordinal)) continue;
            var indent = Regex.Match(lines[at], @"^\s*").Value;
            var declarations = matches.Select(match => $"{indent}{match.Groups["type"].Value} {match.Groups["name"].Value};").ToArray();
            lines[at] = Regex.Replace(lines[at], @"\bout\s+(?:int|long|double|float|decimal|bool|string|DateTime|TimeSpan)\s+([A-Za-z_]\w*)", "out $1");
            // A declaration inside an "if (…)" or "else if (…)" line goes before that line.
            lines.InsertRange(at, declarations);
            at += declarations.Length;
        }
        return string.Join(source.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n", lines);
    }

    private static readonly string[] ControlMembers =
    [
        "Text", "Value", "Enabled", "Visible", "Items", "Image", "Start", "Stop", "Interval", "Tick", "Click", "Checked", "SelectedItem",
        "SelectedIndex", "Location", "Size", "Width", "Height", "BackColor", "ForeColor", "Font", "Invalidate", "Refresh", "Focus", "Clear",
        "Maximum", "Minimum", "Tag", "SizeMode", "ReadOnly", "Multiline", "AppendText", "TextChanged", "ValueChanged"
    ];

    private static readonly HashSet<string> NotControls = new(StringComparer.Ordinal) { "this", "base", "e", "sender", "value", "args", "Application", "Console", "Math", "string", "item" };

    private static readonly string[] ControlTypes = ["Timer", "Label", "TextBox", "Button", "PictureBox", "ListBox", "CheckBox", "ComboBox", "NumericUpDown", "ProgressBar", "Panel", "RichTextBox"];

    /// <summary>
    /// What a Windows Forms designer file would have done, for a Form written as if one existed (trial 2026-10-13: every
    /// EXE attempt used lblRemainingTime, timer1 or pictureBox1 without declaring or creating them, and never wired its
    /// StartButton_Click handlers). Controls the code uses but never creates are declared (their type read from their
    /// name or use), created, laid out in a flowing panel, and handlers named Control_Event are attached. This only
    /// completes the program's own intent; it adds no feature.
    /// </summary>
    internal static string CompleteDesignerControls(string source)
    {
        var form = Regex.Match(source, @"\bclass\s+(?<name>[A-Za-z_]\w*)\s*:\s*(?:System\.Windows\.Forms\.)?Form\b[^{]*\{");
        if (!form.Success) return source;
        var formName = form.Groups["name"].Value;
        var declared = Regex.Matches(source, @"\b(?<type>[A-Za-z_][\w.]*(?:<[^<>;]*>)?(?:\[,*\])?)\s+(?<name>[A-Za-z_]\w*)\s*(?=[;=,)])")
            .Where(match => match.Groups["type"].Value is not ("return" or "new" or "else" or "case" or "goto" or "throw" or "in" or "out" or "ref"))
            .GroupBy(match => match.Groups["name"].Value, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Groups["type"].Value, StringComparer.Ordinal);
        var created = Regex.Matches(source, @"\b(?:this\.)?(?<name>[A-Za-z_]\w*)\s*=\s*new\s+").Select(match => match.Groups["name"].Value)
            .Concat(Regex.Matches(source, @"\b[\w.<>\[\]]+\s+(?<name>[A-Za-z_]\w*)\s*=\s*new\s+").Select(match => match.Groups["name"].Value)).ToHashSet(StringComparer.Ordinal);
        var used = Regex.Matches(source, $@"(?<![\w.])(?:this\.)?(?<name>[a-z_]\w*)\.(?:{string.Join("|", ControlMembers)})\b")
            .Select(match => match.Groups["name"].Value).Where(name => !NotControls.Contains(name)).Distinct().ToList();
        // Undeclared names used like controls, and control fields that are declared but never created.
        var missing = used.Where(name => !declared.ContainsKey(name)).ToList();
        var uncreated = declared.Where(pair => ControlTypes.Contains(pair.Value.Replace("System.Windows.Forms.", "")) && !created.Contains(pair.Key) &&
                                               Regex.IsMatch(source, $@"\b(?:private|protected|internal|public)?\s*(?:System\.Windows\.Forms\.)?{Regex.Escape(pair.Value.Replace("System.Windows.Forms.", ""))}\s+{Regex.Escape(pair.Key)}\s*;"))
            .Select(pair => pair.Key).ToList();
        // A "StartButton_Click" handler with no button anywhere: the designer would have made that button.
        // "StartButton" and "btnStart" are one name; "button1" and "textBox1" are not (both would leave only "1").
        static string Key(string name)
        {
            var key = Regex.Replace(name.ToLowerInvariant(), "^(btn|lbl|txt|pic|chk|lst|nud)|button|label|textbox|picturebox|_", "");
            return Regex.IsMatch(key, "[a-z]{2,}") ? key : "#" + name.ToLowerInvariant();
        }
        // A board of buttons used as grid[i, j] but never made (trial 2026-10-14, tic-tac-toe): a square grid of buttons,
        // sized by the program's own constant (BoardSize = 3), each wired to the board's click handler.
        var grids = Regex.Matches(source, @"(?<![\w.])(?<name>[a-z_]\w*)\s*\[[^\[\],]+,[^\[\]]+\]\s*\.\s*(?:Text|BackColor|Enabled|Tag|ForeColor|Font)\b")
            .Select(match => match.Groups["name"].Value).Where(name => !declared.ContainsKey(name)).Distinct().ToList();
        var gridSize = Regex.Match(source, @"\bconst\s+int\s+\w*(?:Size|SIZE|Count|Rows|Cols)\w*\s*=\s*(?<n>\d+)\s*;") is { Success: true } sized &&
                       int.Parse(sized.Groups["n"].Value, System.Globalization.CultureInfo.InvariantCulture) is var parsed and >= 2 and <= 20 ? parsed : 3;
        // A handler "button1_Click" belongs to the control "button1", "button1Button" (as made below) or "btnButton1"-like names.
        static bool Same(string handlerTarget, string control) =>
            control.Equals(handlerTarget, StringComparison.OrdinalIgnoreCase) || control.Equals(handlerTarget + "Button", StringComparison.OrdinalIgnoreCase) || Key(control) == Key(handlerTarget);
        var knownControls = missing.Concat(declared.Keys).Concat(grids).ToList();
        foreach (Match click in Regex.Matches(source, @"\bvoid\s+(?<target>[A-Za-z]\w*?)_Click\s*\(\s*object\b"))
        {
            var target = click.Groups["target"].Value;
            if (target.Equals(formName, StringComparison.OrdinalIgnoreCase) || knownControls.Any(name => name.Equals(target, StringComparison.OrdinalIgnoreCase) || (Key(name) == Key(target) && Key(name).Length > 0))) continue;
            var buttonName = char.ToLowerInvariant(target[0]) + target[1..];
            if (!Regex.IsMatch(buttonName, "button$|^btn", RegexOptions.IgnoreCase)) buttonName += "Button";
            if (declared.ContainsKey(buttonName) || missing.Contains(buttonName)) continue;
            missing.Add(buttonName);
            declared[buttonName] = "Button";
            knownControls.Add(buttonName);
        }
        if (missing.Count == 0 && uncreated.Count == 0 && grids.Count == 0) return source;

        string TypeOf(string name)
        {
            if (declared.TryGetValue(name, out var known)) return known.Replace("System.Windows.Forms.", "");
            var lower = name.ToLowerInvariant();
            string UsedWith(string member) => Regex.IsMatch(source, $@"\b{Regex.Escape(name)}\.{member}\b") ? member : "";
            // The kind a name ends with wins ("timerLabel" is a Label, "startButton" a Button).
            foreach (var (suffix, type) in new[] { ("label", "Label"), ("button", "Button"), ("textbox", "TextBox"), ("box", "TextBox"), ("timer", "Timer"), ("list", "ListBox"), ("panel", "Panel") })
                if (lower.EndsWith(suffix, StringComparison.Ordinal) && !(suffix == "box" && lower.EndsWith("picturebox", StringComparison.Ordinal))) return type;
            if (lower.Contains("timer") || UsedWith("Tick").Length > 0 || UsedWith("Interval").Length > 0) return "Timer";
            if (lower.StartsWith("lbl") || lower.Contains("label")) return "Label";
            if (lower.StartsWith("txt") || lower.Contains("textbox") || lower.Contains("input")) return "TextBox";
            if (lower.StartsWith("btn") || lower.Contains("button")) return "Button";
            if (lower.Contains("picture") || lower.StartsWith("pic") || UsedWith("Image").Length > 0) return "PictureBox";
            if (lower.Contains("listbox") || lower.StartsWith("lst") || UsedWith("Items").Length > 0) return "ListBox";
            if (lower.StartsWith("chk") || lower.Contains("checkbox") || UsedWith("Checked").Length > 0) return "CheckBox";
            if (lower.Contains("combo")) return "ComboBox";
            if (lower.Contains("numeric") || lower.StartsWith("nud") || UsedWith("Maximum").Length > 0) return "NumericUpDown";
            if (lower.Contains("progress")) return "ProgressBar";
            if (lower.Contains("panel")) return "Panel";
            if (UsedWith("Click").Length > 0) return "Button";
            return "Label";
        }
        // A button is named by what its click handler does (button3 whose handler calls Application.Exit is 閉じる), then by its name.
        string Caption(string name)
        {
            var handler = Regex.Matches(source, @"\bvoid\s+(?<target>[A-Za-z]\w*?)_Click\s*\(\s*object\b[^)]*\)\s*\{")
                .FirstOrDefault(match => Same(match.Groups["target"].Value, name));
            if (handler is not null)
            {
                // The handler's own body, up to its matching closing brace.
                var start = handler.Index + handler.Length;
                var depth = 1;
                var end = start;
                for (; end < source.Length && depth > 0; end++) depth += source[end] == '{' ? 1 : source[end] == '}' ? -1 : 0;
                var body = source[start..Math.Min(end, source.Length)];
                if (Regex.IsMatch(body, @"Application\.Exit\(|\bClose\(\)")) return "閉じる";
                if (Regex.IsMatch(body, @"File\.(?:Write|Append)|SaveFileDialog")) return "保存";
                if (Regex.IsMatch(body, @"File\.Read|OpenFileDialog")) return "開く";
                if (Regex.IsMatch(body, @"\.Clear\(\)|\.Text\s*=\s*""""|string\.Empty")) return "クリア";
                if (Regex.IsMatch(body, @"\.Start\(\)")) return "スタート";
                if (Regex.IsMatch(body, @"\.Stop\(\)")) return "ストップ";
            }
            var lower = name.ToLowerInvariant();
            return lower.Contains("restart") || lower.Contains("retry") || lower.Contains("again") ? "もう一度" : lower.Contains("start") ? "スタート" : lower.Contains("stop") || lower.Contains("pause") ? "ストップ" : lower.Contains("reset") ? "リセット"
                : lower.Contains("clear") ? "クリア" : lower.Contains("add") ? "追加" : lower.Contains("delete") || lower.Contains("remove") ? "削除"
                : lower.Contains("save") ? "保存" : lower.Contains("open") || lower.Contains("load") ? "開く" : lower.Contains("restart") || lower.Contains("retry") ? "もう一度" : lower.Contains("calc") ? "計算"
                : lower.Contains("edit") ? "編集" : lower.Contains("sort") ? "並べ替え" : lower.Contains("search") || lower.Contains("find") ? "検索"
                : lower.Contains("next") ? "次へ" : lower.Contains("prev") || lower.Contains("back") ? "前へ" : lower.Contains("close") || lower.Contains("exit") ? "閉じる"
                : lower.Contains("new") ? "新規" : lower.Contains("copy") ? "コピー" : lower.Contains("undo") ? "元に戻す" : lower.Contains("setting") ? "設定"
                : lower.Contains("cancel") ? "キャンセル" : lower.Contains("send") ? "送信" : lower.Contains("play") ? "再生" : lower.Contains("ok") ? "OK"
                // Otherwise the name without its control word ("btnShuffle" → "Shuffle"), never the code name itself.
                : Regex.Replace(name, "^(btn|button)|(Button|Btn)$", "") is { Length: > 0 } plain ? char.ToUpperInvariant(plain[0]) + plain[1..] : "OK";
        }

        var add = missing.Concat(uncreated).Distinct().ToList();
        var fields = new StringBuilder();
        foreach (var name in missing) fields.Append("        private ").Append(TypeOf(name)).Append(' ').Append(name).Append(";\n");
        foreach (var name in grids) fields.Append("        private Button[,] ").Append(name).Append(";\n");
        // A second pass (a repaired source already completed once) gets a layout of its own name.
        var layout = "localStudioLayout";
        for (var n = 2; source.Contains(layout, StringComparison.Ordinal); n++) layout = "localStudioLayout" + n;
        var init = new StringBuilder();
        init.Append("            FlowLayoutPanel ").Append(layout).Append(" = new FlowLayoutPanel();\n");
        init.Append("            ").Append(layout).Append(".Dock = DockStyle.Fill;\n            ").Append(layout).Append(".FlowDirection = FlowDirection.TopDown;\n");
        init.Append("            ").Append(layout).Append(".WrapContents = false;\n            ").Append(layout).Append(".AutoScroll = true;\n            ").Append(layout).Append(".Padding = new Padding(12);\n");
        foreach (var name in add)
        {
            var type = TypeOf(name);
            init.Append("            ").Append(name).Append(" = new ").Append(type).Append("();\n");
            if (type == "Timer") { init.Append("            ").Append(name).Append(".Interval = 1000;\n"); continue; }
            if (type == "Button") init.Append("            ").Append(name).Append(".Text = \"").Append(Caption(name)).Append("\";\n            ").Append(name).Append(".AutoSize = true;\n");
            if (type == "Label") init.Append("            ").Append(name).Append(".AutoSize = true;\n");
            if (type is "TextBox" or "ComboBox" or "ListBox") init.Append("            ").Append(name).Append(".Width = 240;\n");
            if (type == "PictureBox") init.Append("            ").Append(name).Append(".Size = new Size(320, 240);\n");
            init.Append("            ").Append(layout).Append(".Controls.Add(").Append(name).Append(");\n");
        }
        foreach (var name in grids)
        {
            var handler = Regex.Matches(source, @"\bvoid\s+(?<target>[A-Za-z]\w*?)_Click\s*\(\s*object\b").Select(match => match.Groups["target"].Value)
                .FirstOrDefault(target => Key(target) == Key(name) || Regex.IsMatch(target, "cell|grid|board|square|tile|mass|masu", RegexOptions.IgnoreCase));
            var size = gridSize.ToString(System.Globalization.CultureInfo.InvariantCulture);
            init.Append("            ").Append(name).Append(" = new Button[").Append(size).Append(", ").Append(size).Append("];\n");
            init.Append("            TableLayoutPanel ").Append(name).Append("Panel = new TableLayoutPanel();\n");
            init.Append("            ").Append(name).Append("Panel.RowCount = ").Append(size).Append(";\n            ").Append(name).Append("Panel.ColumnCount = ").Append(size).Append(";\n");
            init.Append("            ").Append(name).Append("Panel.AutoSize = true;\n");
            init.Append("            for (int localStudioRow = 0; localStudioRow < ").Append(size).Append("; localStudioRow++)\n            {\n");
            init.Append("                for (int localStudioColumn = 0; localStudioColumn < ").Append(size).Append("; localStudioColumn++)\n                {\n");
            init.Append("                    Button localStudioCell = new Button();\n                    localStudioCell.Size = new Size(72, 72);\n");
            init.Append("                    localStudioCell.Font = new Font(FontFamily.GenericSansSerif, 20f);\n                    localStudioCell.Tag = new Point(localStudioRow, localStudioColumn);\n");
            init.Append("                    ").Append(name).Append("[localStudioRow, localStudioColumn] = localStudioCell;\n");
            if (handler is not null) init.Append("                    localStudioCell.Click += ").Append(handler).Append("_Click;\n");
            init.Append("                    ").Append(name).Append("Panel.Controls.Add(localStudioCell, localStudioColumn, localStudioRow);\n                }\n            }\n");
            init.Append("            ").Append(layout).Append(".Controls.Add(").Append(name).Append("Panel);\n");
        }
        init.Append("            Controls.Add(").Append(layout).Append(");\n");

        // Handlers named after a control or the form ("StartButton_Click", "timer1_Tick", "MainForm_Load") are attached
        // when nothing attaches them yet.
        var controls = add.Concat(declared.Keys.Where(name => ControlTypes.Contains(declared[name].Replace("System.Windows.Forms.", "")))).Distinct().ToList();
        foreach (Match handler in Regex.Matches(source, @"\bvoid\s+(?<target>[A-Za-z]\w*?)_(?<event>Click|Tick|TextChanged|ValueChanged|CheckedChanged|SelectedIndexChanged|KeyDown|KeyUp|Load|Shown|FormClosing|FormClosed|Resize|Paint|MouseDown|MouseMove|MouseUp|MouseClick)\s*\(\s*object\b"))
        {
            var method = handler.Groups["target"].Value + "_" + handler.Groups["event"].Value;
            if (Regex.IsMatch(source, $@"\+=\s*(?:new\s+\w+\s*\(\s*)?{Regex.Escape(method)}\b")) continue;
            var target = handler.Groups["target"].Value;
            var owner = target.Equals(formName, StringComparison.OrdinalIgnoreCase) || target.StartsWith("Form", StringComparison.Ordinal) ? "this"
                : controls.FirstOrDefault(name => Same(target, name));
            if (owner is null) continue;
            init.Append("            ").Append(owner).Append('.').Append(handler.Groups["event"].Value).Append(" += ").Append(method).Append(";\n");
        }

        var result = source.Insert(form.Index + form.Length, "\n" + fields);
        var constructor = Regex.Match(result, $@"\b(?:public|internal|private)?\s*{Regex.Escape(formName)}\s*\(\s*\)\s*(?::\s*base\s*\([^)]*\)\s*)?\{{");
        if (constructor.Success) return result.Insert(constructor.Index + constructor.Length, "\n" + init);
        var body = Regex.Match(result, $@"\bclass\s+{Regex.Escape(formName)}\b[^{{]*\{{");
        return result.Insert(body.Index + body.Length, "\n        public " + formName + "()\n        {\n" + init + "        }\n");
    }

    /// <summary>A Form with no [STAThread] Main gets one that runs it (the model leaves it out when it imagines a designer project).</summary>
    internal static string EnsureEntryPoint(string source)
    {
        if (Regex.IsMatch(source, @"\bstatic\s+(?:void|int)\s+Main\s*\(")) return source;
        var form = Regex.Match(source, @"\bclass\s+(?<name>[A-Za-z_]\w*)\s*:\s*(?:System\.Windows\.Forms\.)?Form\b");
        if (!form.Success) return source;
        var name = form.Groups["name"].Value;
        var entry = $$"""


            static class LocalStudioProgram
            {
                [STAThread]
                static void Main()
                {
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    Application.Run(new {{name}}());
                }
            }
            """;
        // Inside the same namespace as the form when there is one (its closing brace is the last one in the file).
        var ns = Regex.Match(source, @"\bnamespace\s+[\w.]+\s*\{");
        if (ns.Success)
        {
            var last = source.LastIndexOf('}');
            if (last > ns.Index) return source[..last].TrimEnd() + entry.Replace("\n", "\n    ") + "\n}\n";
        }
        return source.TrimEnd() + entry + "\n";
    }
}
