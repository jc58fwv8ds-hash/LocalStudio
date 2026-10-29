using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Js;

namespace SafeLocalHarness.Core;

/// <summary>One thing done to the page and what followed.</summary>
public sealed record BehaviorStep(string Action, string Target, bool Changed, IReadOnlyList<string> Errors);

public sealed record BehaviorReport(string? LoadError, IReadOnlyList<BehaviorStep> Steps, IReadOnlyList<string> Problems, IReadOnlyList<string> Notes)
{
    public bool Passed => Problems.Count == 0;
}

/// <summary>
/// Runs a generated web app without a browser (AngleSharp builds the page, Jint runs the JavaScript, both inside
/// LocalStudio): the page must load without a script error, and every button and input must react without an error.
/// Storage and timers are stand-ins inside the run (nothing is written on the PC); a library's global is replaced by a
/// stand-in that accepts any use, because canvas drawing cannot be checked here. This catches apps that every static
/// check passes but that stop the moment they are used (drill 2026-10-12).
/// </summary>
public static class WebAppBehaviorChecker
{
    public const int MaxScriptCharacters = 64 * 1024;
    public static readonly TimeSpan ScriptTimeLimit = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan OverallTimeLimit = TimeSpan.FromSeconds(30);
    public const int MaxStatements = 2_000_000;
    public const long MaxMemoryBytes = 256L * 1024 * 1024;

    private static bool IsLimit(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
            if (current is TimeoutException or Jint.Runtime.StatementsCountOverflowException or Jint.Runtime.MemoryLimitExceededException) return true;
        return false;
    }

    // Stand-ins installed before the app's script. Listener errors are recorded instead of disappearing, timers are
    // collected so the check can let time pass, and storage lives only in this run.
    private const string Prelude = """
        var __ls_errors = [];
        var __ls_timers = {};
        var __ls_nextTimer = 1;
        var __ls_offset = 0;
        var __ls_env = [];
        var __ls_intervals = 0; var __ls_sounds = 0; var __ls_downloads = 0;
        var __ls_draws = 0;
        var __ls_blobs = [];
        var __ls_strokes = 0;
        var __ls_pen = null;
        var __ls_dragged = 0;
        (function () {
          // A clock that moves only when the check lets time pass (1.5 s per step), so a stopwatch shows elapsed time.
          var RealDate = Date;
          var FakeDate = function () {
            if (!(this instanceof FakeDate)) return new RealDate(RealDate.now() + __ls_offset).toString();
            var args = Array.prototype.slice.call(arguments);
            return args.length ? new (Function.prototype.bind.apply(RealDate, [null].concat(args)))() : new RealDate(RealDate.now() + __ls_offset);
          };
          FakeDate.prototype = RealDate.prototype;
          FakeDate.now = function () { return RealDate.now() + __ls_offset; };
          FakeDate.parse = RealDate.parse; FakeDate.UTC = RealDate.UTC;
          var fakePerformance = { now: function () { return __ls_offset; } };
          var store = {};
          var storage = {
            getItem: function (k) { return Object.prototype.hasOwnProperty.call(store, k) ? store[k] : null; },
            setItem: function (k, v) { store[k] = String(v); },
            removeItem: function (k) { delete store[k]; },
            clear: function () { store = {}; },
            key: function (i) { return Object.keys(store)[i] || null; }
          };
          Object.defineProperty(storage, 'length', { get: function () { return Object.keys(store).length; } });

          // Timers as a browser keeps them: a timeout runs once, an interval runs at every step until it is cleared with
          // the id it returned (a stop button that clears the wrong thing leaves the time running, trial 2026-10-13).
          var add = function (f, repeating) { var id = __ls_nextTimer++; if (typeof f === 'function') __ls_timers[id] = { f: f, r: repeating }; return id; };
          var later = function (f) { return add(f, false); };
          var clear = function (id) { delete __ls_timers[id]; };
          var frame = function (f) { __ls_intervals++; return add(function () { f(__ls_offset); }, false); };
          // The engine's window ignores assignments, so the app receives these as function parameters that shadow the
          // globals of the same name (see AppParameters).
          var repeat = function (f) { __ls_intervals++; return add(f, true); };
          // Sound (Audio, Web Audio, speech) accepts any use and only counts it: an app that really makes a sound was refused
          // with "Audio is not defined" (metronome, catalogue 2026-10-19). Nothing is played.
          var sound = function () {
            var target = function () {};
            return new Proxy(target, {
              // A source node starts once, as in a browser: a metronome that called osc.start() on every beat played once
              // and then threw (catalogue 2026-10-20), which a stand-in that accepted anything could not show.
              get: function (t, p) { if (p === Symbol.toPrimitive) return function () { return 0; }; if (p === 'then') return undefined; if (p === 'currentTime' || p === 'length') return 0;
                if (p === 'start') return function () { if (t.__ls_started) throw new Error('InvalidStateError: start() can only be called once on this sound source; create a new oscillator (ctx.createOscillator()) for every beat'); t.__ls_started = true; __ls_sounds++; return sound(); };
                return sound(); },
              set: function () { return true; },
              apply: function () { __ls_sounds++; return sound(); },
              construct: function () { return sound(); }
            });
          };
          // Files made for download: the engine has no Blob ("Illegal constructor", CSV cleaner 2026-10-19). The parts are kept;
          // nothing is written or downloaded.
          var FakeBlob = function (parts, options) { this.parts = parts || []; this.type = (options && options.type) || ''; this.size = this.parts.join('').length; __ls_downloads++; __ls_blobs.push(this.parts.join('')); };
          FakeBlob.prototype.text = function () { return Promise.resolve(this.parts.join('')); };
          var fakeUrl = { createObjectURL: function () { return 'blob:localstudio-check'; }, revokeObjectURL: function () {} };
          // A chosen file is read as text after a moment, as in a browser (onload / load listeners, result).
          var FakeFileReader = function () { this.result = null; this.onload = null; this.onloadend = null; this.onerror = null; this.__ls_l = {}; };
          FakeFileReader.prototype.__ls_on = FakeFileReader.prototype.addEventListener = function (type, f) { (this.__ls_l[type] = this.__ls_l[type] || []).push(f); };
          FakeFileReader.prototype.readAsText = function (file) {
            var self = this;
            later(function () {
              self.result = file && file.__ls_text !== undefined ? file.__ls_text : '';
              var event = { target: self, currentTarget: self };
              if (typeof self.onload === 'function') self.onload(event);
              (self.__ls_l.load || []).forEach(function (f) { f.call(self, event); });
              if (typeof self.onloadend === 'function') self.onloadend(event);
              (self.__ls_l.loadend || []).forEach(function (f) { f.call(self, event); });
            });
          };
          __ls_env = [later, repeat, clear, clear, frame, clear, FakeDate, fakePerformance, storage, storage,
            function () {}, function () { return true; }, function () { return ''; }, sound(), sound(), sound(), sound(), sound(), FakeBlob, fakeUrl, FakeFileReader];
          // append/prepend/replaceChildren with text and elements mixed fail inside this engine (a cast error), and its
          // methods cannot be replaced, so the app's calls are pointed to these browser-equivalent versions. Anything
          // that is not a page node (FormData.append) is passed on to its own method.
          var toNode = function (n) { return n !== null && typeof n === 'object' ? n : document.createTextNode(String(n)); };
          var isNode = function (o) { return o !== null && typeof o === 'object' && typeof o.appendChild === 'function' && typeof o.nodeType === 'number'; };
          var define = function (name, own, method) {
            Object.defineProperty(Object.prototype, name, { configurable: true, writable: true, enumerable: false, value: function () {
              return isNode(this) ? method.apply(this, arguments) : this[own].apply(this, arguments);
            } });
          };
          // This engine has no canvas drawing: getContext gives a stand-in that accepts every call and setting and counts
          // the drawing, so a drawing app or game is neither stopped by a null context nor taken for one that shows nothing.
          var makeContext = function () {
            var target = function () {};
            return new Proxy(target, {
              get: function (t, p) {
                if (p === Symbol.toPrimitive) return function () { return 0; };
                if (p === 'width' || p === 'height' || p === 'length') return 0;
                // Lines are followed: a stroke counts only when it reaches a point other than where it started, so an
                // app that draws from a point to the same point (dots, trial 2026-10-19) is seen as drawing nothing.
                return function () {
                  __ls_draws++;
                  if (p === 'moveTo') __ls_pen = [arguments[0], arguments[1]];
                  else if (p === 'lineTo' || p === 'quadraticCurveTo' || p === 'bezierCurveTo') {
                    var end = [arguments[arguments.length - 2], arguments[arguments.length - 1]];
                    if (__ls_pen && (end[0] !== __ls_pen[0] || end[1] !== __ls_pen[1])) __ls_strokes++;
                    __ls_pen = end;
                  }
                  else if (/^(arc|ellipse|rect|fillRect|strokeRect|fillText|strokeText|drawImage|fill|putImageData)$/.test(p)) __ls_strokes++;
                  return makeContext();
                };
              },
              set: function () { return true; },
              apply: function () { __ls_draws++; return makeContext(); }
            });
          };
          define('__ls_getContext', 'getContext', function () { return makeContext(); });
          // No layout here: an element sits at the page's top left, with the size its attributes give.
          define('__ls_rect', 'getBoundingClientRect', function () { var w = Number(this.getAttribute('width')) || 300, h = Number(this.getAttribute('height')) || 150; return { left: 0, top: 0, x: 0, y: 0, right: w, bottom: h, width: w, height: h }; });
          define('__ls_append', 'append', function () { for (var i = 0; i < arguments.length; i++) this.appendChild(toNode(arguments[i])); });
          define('__ls_prepend', 'prepend', function () { for (var i = arguments.length - 1; i >= 0; i--) this.insertBefore(toNode(arguments[i]), this.firstChild); });
          define('__ls_replaceChildren', 'replaceChildren', function () {
            for (var guard = 0; this.firstChild && guard < 100000; guard++) this.removeChild(this.firstChild);
            for (var i = 0; i < arguments.length; i++) this.appendChild(toNode(arguments[i]));
          });
          // The engine's addEventListener cannot be replaced (read-only on every prototype level) and it swallows listener
          // errors, so each level gets __ls_on, a recording version that the app's .addEventListener( calls are pointed to.
          var seen = [];
          var wrap = function (proto) {
            if (!proto || seen.indexOf(proto) >= 0) return;
            seen.push(proto);
            wrap(Object.getPrototypeOf(proto));
            if (!Object.prototype.hasOwnProperty.call(proto, 'addEventListener')) return;
            var original = proto.addEventListener;
            if (typeof original !== 'function' || Object.prototype.hasOwnProperty.call(proto, '__ls_on')) return;
            var wrapped = function (type, listener, options) {
              var target = this;
              var safe = function (event) {
                try { return typeof listener === 'function' ? listener.call(target, event) : listener.handleEvent(event); }
                catch (error) { __ls_errors.push(type + ': ' + error); }
              };
              // Kept so a drag on a canvas can be played to the app's own handlers with real coordinates.
              try { var all = target.__ls_listeners || (target.__ls_listeners = {}); (all[type] || (all[type] = [])).push(safe); } catch (e) {}
              return original.call(this, type, safe, options);
            };
            try { proto.__ls_on = wrapped; } catch (e) {}
          };
          var tags = ['button', 'input', 'textarea', 'select', 'option', 'form', 'div', 'span', 'p', 'ul', 'ol', 'li', 'a', 'label',
            'canvas', 'img', 'table', 'tr', 'td', 'section', 'main', 'header', 'h1', 'h2', 'h3', 'article', 'details', 'dialog'];
          wrap(Object.getPrototypeOf(window)); wrap(Object.getPrototypeOf(document));
          for (var t = 0; t < tags.length; t++) wrap(Object.getPrototypeOf(document.createElement(tags[t])));
        })();
        """;

    private const string AppParameters = "setTimeout, setInterval, clearTimeout, clearInterval, requestAnimationFrame, cancelAnimationFrame, Date, performance, localStorage, sessionStorage, alert, confirm, prompt, Audio, AudioContext, webkitAudioContext, speechSynthesis, SpeechSynthesisUtterance, Blob, URL, FileReader";

    // A library global that accepts any use: calls, new, property chains (chart.data.labels.push(…)).
    private static string LibraryStandIn(string global) => $$"""
        (function () {
          var make = function () {
            var target = function () {};
            return new Proxy(target, {
              get: function (t, p) { if (p === Symbol.toPrimitive) return function () { return 0; }; if (p === 'length') return 0; return make(); },
              set: function () { return true; },
              apply: function () { return make(); },
              construct: function () { return make(); }
            });
          };
          window['{{global}}'] = make();
        })();
        """;

    public static async Task<BehaviorReport> CheckAsync(string projectRoot, CancellationToken cancellationToken = default)
    {
        var html = await File.ReadAllTextAsync(Path.Combine(projectRoot, "index.html"), cancellationToken);
        var script = await File.ReadAllTextAsync(Path.Combine(projectRoot, "app.js"), cancellationToken);
        // Globals recorded in vendor.json; for apps made before they were recorded, read from the bundled file itself.
        var globals = LibraryShelf.ProjectGlobals(projectRoot).ToList();
        var vendor = Path.Combine(projectRoot, LibraryShelf.VendorFolder);
        if (globals.Count == 0 && Directory.Exists(vendor))
            globals.AddRange(Directory.EnumerateFiles(vendor, "*.js", SearchOption.AllDirectories).Select(file => NpmLibrarySource.DetectGlobal(File.ReadAllText(file))).OfType<string>());
        // A stopwatch or timer app (by its recorded requirements or name) must start a repeating timer.
        var expectsTimer = false;
        foreach (var record in new[] { "requirements.json", "localstudio.json" }.Select(name => Path.Combine(projectRoot, name)).Where(File.Exists))
            expectsTimer |= TimerWords.IsMatch(await File.ReadAllTextAsync(record, cancellationToken));
        return await CheckPageAsync(html, script, globals, cancellationToken, expectsTimer);
    }

    private static readonly Regex StartWords = new(@"スタート|開始|再開|\bstart\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex StopWords = new(@"ストップ|停止|止め|リセット|\bstop\b|\bpause\b|\breset\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Requests about measuring or counting time.</summary>
    public static readonly Regex TimerWords = new("ストップウォッチ|タイマー|経過時間|カウントダウン|ラップ|stopwatch|timer|countdown", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Checks a page before it is saved. The run happens on a worker thread with an overall deadline as well as the
    /// per-script limits; a run that cannot finish is inconclusive (a note), never a pass of a broken app by itself.
    /// </summary>
    public static async Task<BehaviorReport> CheckPageAsync(string html, string script, IReadOnlyList<string> libraryGlobals, CancellationToken cancellationToken = default, bool expectsTimer = false)
    {
        var run = Task.Run(() => CheckPageCoreAsync(html, script, libraryGlobals, cancellationToken, expectsTimer), cancellationToken);
        var finished = await Task.WhenAny(run, Task.Delay(OverallTimeLimit, cancellationToken));
        if (finished != run)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new("timeout", [], [], ["この確かめ方では時間内に確かめきれませんでした"]);
        }
        try { return await run; }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new("checker-failed", [], [], ["この確かめ方では判定できませんでした: " + ex.GetType().Name]);
        }
    }

    private static async Task<BehaviorReport> CheckPageCoreAsync(string html, string script, IReadOnlyList<string> libraryGlobals, CancellationToken cancellationToken, bool expectsTimer)
    {
        var problems = new List<string>();
        var notes = new List<string>();
        var steps = new List<BehaviorStep>();
        if (script.Length > MaxScriptCharacters) return new("script-too-large", [], [], ["スクリプトが大きいため動かして確かめていません"]);
        // The page without its scripts and CSP: the scripts are run one by one below so their errors can be seen.
        var page = Regex.Replace(html, @"<script\b[^>]*>.*?</script>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        page = Regex.Replace(page, @"<meta[^>]+Content-Security-Policy[^>]*>", "", RegexOptions.IgnoreCase);
        // Every script run is bounded, so an app that loops forever is reported instead of stopping Local Studio.
        EngineCreator limited = (_, options) =>
        {
            Jint.ConstraintsOptionsExtensions.TimeoutInterval(options, ScriptTimeLimit);
            Jint.ConstraintsOptionsExtensions.MaxStatements(options, MaxStatements);
            Jint.ConstraintsOptionsExtensions.LimitMemory(options, MaxMemoryBytes);
            return new Jint.Engine(options);
        };
        var config = Configuration.Default.WithJs().With(limited);
        using var context = BrowsingContext.New(config);
        using var document = await context.OpenAsync(request => request.Content(page).Address("https://localstudio.invalid/"), cancellationToken);

        // Intervals running now (not cleared).
        int Running()
        {
            try { return Convert.ToInt32(document.ExecuteScript("(function () { var n = 0; for (var k in __ls_timers) if (__ls_timers[k].r) n++; return n; })()"), System.Globalization.CultureInfo.InvariantCulture); }
            catch (Exception) { return 0; }
        }

        int Draws()
        {
            try { return Convert.ToInt32(document.ExecuteScript("__ls_draws"), System.Globalization.CultureInfo.InvariantCulture); }
            catch (Exception) { return 0; }
        }

        int Intervals()
        {
            try { return Convert.ToInt32(document.ExecuteScript("__ls_intervals"), System.Globalization.CultureInfo.InvariantCulture); }
            catch (Exception) { return 0; }
        }

        // Engine-internal failures say nothing about the app; they make the run inconclusive instead of failed.
        var inconclusive = new List<string>();
        string? Run(string code)
        {
            try { document.ExecuteScript(code); return null; }
            catch (Exception ex) when (IsLimit(ex)) { return "止まらない処理: " + ex.GetType().Name; }
            catch (Exception ex) when (ex.Message.Contains("[Internal]", StringComparison.Ordinal) || ex is not Jint.Runtime.JavaScriptException)
            {
                inconclusive.Add(ex.GetType().Name + ": " + ex.Message);
                return null;
            }
            // A run stopped by the limits reaches here wrapped, with no message of its own.
            catch (Exception ex) when (string.IsNullOrWhiteSpace(ex.Message)) { return "止まらない処理: 実行の上限に達しました"; }
            catch (Exception ex) { return ex.GetType().Name + ": " + ex.Message; }
        }

        if (Run(Prelude) is { } preludeError) notes.Add("prelude: " + preludeError);
        var globals = libraryGlobals;
        foreach (var global in globals.Distinct().Where(global => Regex.IsMatch(global, @"^[A-Za-z_$][\w$]*$")))
        {
            Run(LibraryStandIn(global));
            notes.Add($"ライブラリ {global} は代わりの物で動かしました（描画は確かめていません）");
        }
        // The app runs in its own function scope: browsers let a top-level const shadow a window property (status,
        // name, …) that this engine would report as already declared. Then the page tells the app it has loaded.
        var loadError = Run("(function (" + AppParameters + ") {\n" + PointToStandIns(script) + "\n}).apply(window, __ls_env);");
        if (loadError is null)
            Run("document.dispatchEvent(new Event('DOMContentLoaded', { bubbles: true })); window.dispatchEvent(new Event('load')); if (typeof window.onload === 'function') { try { window.onload(); } catch (e) { __ls_errors.push('load: ' + e); } }");
        loadError = Readable(loadError);
        if (loadError is not null) problems.Add("behavior.load-error: " + loadError);
        RunTimers(Run);
        foreach (var error in DrainErrors(document)) problems.Add("behavior.load-error: " + error);
        if (loadError is not null) return new(loadError, steps, problems, notes);

        // Inputs first (typing), then every button (clicking), each followed by a little "time passing".
        var controls = document.QuerySelectorAll("#app input, #app textarea, #app select, #app button").ToArray();
        // Buttons made with createElement but never added to the page (trial 2026-10-13) leave nothing to use.
        if (controls.Length == 0)
            problems.Add("behavior.no-controls: 開いた画面にボタンや入力欄が1つもありません（作った部品が画面に加えられていない可能性）");
        var index = 0;
        string? startMarker = null;
        var startMarkerName = "";
        var runningAfterStart = 0;
        foreach (var control in controls)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var id = control.Id ?? "";
            // An earlier step may have replaced this part of the page (a list redrawn); the removed copy cannot be used.
            if (!document.Contains(control))
            {
                notes.Add($"{(id.Length > 0 ? "#" + id : control.LocalName)} は前の操作で画面から消えたため押していません");
                continue;
            }
            var marker = "__ls_target_" + index++;
            control.SetAttribute("data-ls-target", marker);
            var before = Snapshot(document);
            var drawsBefore = Draws();
            var intervalsBefore = Intervals();
            var tag = control.LocalName;
            string action;
            string? error;
            if (tag == "button")
            {
                action = "click";
                // A submit button inside a form submits it when clicked, as in a browser (without leaving the page).
                error = Run($$"""(function () { var el = document.querySelector('[data-ls-target="{{marker}}"]'); el.dispatchEvent(new Event('click', { bubbles: true, cancelable: true })); var type = (el.getAttribute('type') || 'submit').toLowerCase(); var form = el.closest ? el.closest('form') : null; if (form && type === 'submit') form.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true })); })();""");
            }
            else
            {
                var type = (control.GetAttribute("type") ?? "text").ToLowerInvariant();
                if (type is "button" or "submit" or "reset") { action = "click"; error = Run($$"""document.querySelector('[data-ls-target="{{marker}}"]').dispatchEvent(new Event('click', { bubbles: true }));"""); }
                else
                {
                    action = "input";
                    var value = type is "number" or "range" ? "12" : type == "checkbox" ? "" : "テスト 12";
                    var setValue = type == "checkbox" ? "el.checked = !el.checked;" : $"el.value = {JsonSerializer.Serialize(value)};";
                    error = Run($$"""(function () { var el = document.querySelector('[data-ls-target="{{marker}}"]'); {{setValue}} el.dispatchEvent(new Event('input', { bubbles: true })); el.dispatchEvent(new Event('change', { bubbles: true })); })();""");
                }
            }
            RunTimers(Run);
            // A repeating timer this step started runs a second time too: what goes wrong on the second beat (a sound
            // source started twice, 2026-10-20) only shows then.
            if (Intervals() > intervalsBefore) RunTimers(Run);
            var errors = DrainErrors(document).ToList();
            if (error is not null) errors.Insert(0, error);
            // Drawing on a canvas changes what is seen without changing the page's elements.
            var changed = Snapshot(document) != before || Draws() > drawsBefore;
            // A button that starts a repeating timer whose runs show nothing: a stopwatch that only shows the time
            // when stopped (hands-on 2026-10-13, H14). One more moment is given before deciding.
            if (action == "click" && !changed && errors.Count == 0 && Intervals() > intervalsBefore)
            {
                RunTimers(Run);
                if (Snapshot(document) == before)
                    problems.Add($"behavior.timer-silent:{(id.Length > 0 ? id : tag)}: 押すと時間を計り始めますが、時間が進んでも画面の表示が変わりません");
                else changed = true;
            }
            if (action == "click" && errors.Count == 0)
            {
                var label = string.Join(" ", control.TextContent, control.GetAttribute("aria-label"), id);
                // After stop (or reset), time passing must not change the screen.
                if (StopWords.IsMatch(label))
                {
                    RunTimers(Run);
                    var stopped = Snapshot(document);
                    RunTimers(Run);
                    RunTimers(Run);
                    if (Snapshot(document) != stopped && Running() > 0)
                        problems.Add($"behavior.timer-not-stopped:{(id.Length > 0 ? id : tag)}: 押しても時間が止まらず、表示が進み続けます");
                    // Stop, then start again: the timer of the first run must not still be there (trial 2026-10-13: the
                    // screen looked stopped, but every start added one more running timer).
                    else if (startMarker is not null && startMarker != marker && document.QuerySelector($"[data-ls-target=\"{startMarker}\"]") is not null)
                    {
                        Run($$"""document.querySelector('[data-ls-target="{{startMarker}}"]').dispatchEvent(new Event('click', { bubbles: true, cancelable: true }));""");
                        RunTimers(Run);
                        if (Running() > runningAfterStart) problems.Add($"behavior.timer-duplicated:{startMarkerName}: 停止してからもう一度スタートすると、時間を進める処理が二重に動きます（停止で止まっていません）");
                        Run($$"""document.querySelector('[data-ls-target="{{marker}}"]').dispatchEvent(new Event('click', { bubbles: true, cancelable: true }));""");
                        RunTimers(Run);
                        DrainErrors(document).ToList();
                    }
                }
                if (StartWords.IsMatch(label) && Running() > 0 && startMarker is null)
                {
                    startMarker = marker;
                    startMarkerName = id.Length > 0 ? id : tag;
                    runningAfterStart = Running();
                }
            }
            steps.Add(new BehaviorStep(action, id.Length > 0 ? "#" + id : tag, changed, errors));
            foreach (var stepError in errors) problems.Add($"behavior.{action}-error:{(id.Length > 0 ? id : tag)}: {stepError}");
        }
        // A canvas that follows the mouse is drawn on as a hand would: pressed, moved and released, with coordinates
        // (trial 2026-10-19: a drawing app passed whose moves raised errors and drew only dots, as no drag was played).
        foreach (var canvas in document.QuerySelectorAll("#app canvas").ToArray())
        {
            var marker = "__ls_target_" + index++;
            canvas.SetAttribute("data-ls-target", marker);
            int Read(string expression)
            {
                try { return Convert.ToInt32(document.ExecuteScript(expression), System.Globalization.CultureInfo.InvariantCulture); }
                catch (Exception) { return 0; }
            }
            var strokesBefore = Read("__ls_strokes");
            var dragError = Run($$"""
                (function () {
                  var el = document.querySelector('[data-ls-target="{{marker}}"]');
                  var all = el.__ls_listeners || {};
                  var has = function (t) { return (all[t] || []).length > 0; };
                  __ls_dragged = has('mousemove') || has('pointermove') || has('touchmove') ? 1 : 0;
                  if (!__ls_dragged) return;
                  var event = function (t, x, y) {
                    var point = { clientX: x, clientY: y, pageX: x, pageY: y };
                    return { type: t, target: el, currentTarget: el, offsetX: x, offsetY: y, clientX: x, clientY: y, pageX: x, pageY: y,
                      layerX: x, layerY: y, button: 0, buttons: 1, pointerId: 1, touches: [point], changedTouches: [point],
                      preventDefault: function () {}, stopPropagation: function () {} };
                  };
                  var fire = function (t, x, y) { (all[t] || []).forEach(function (f) { f(event(t, x, y)); }); };
                  fire('mousedown', 10, 10); fire('pointerdown', 10, 10); fire('touchstart', 10, 10);
                  [[30, 20], [50, 35], [70, 50]].forEach(function (p) { fire('mousemove', p[0], p[1]); fire('pointermove', p[0], p[1]); fire('touchmove', p[0], p[1]); });
                  fire('mouseup', 70, 50); fire('pointerup', 70, 50); fire('touchend', 70, 50);
                })();
                """);
            if (Read("__ls_dragged") == 0) continue;
            var dragErrors = DrainErrors(document).ToList();
            if (dragError is not null) dragErrors.Insert(0, dragError);
            var name = canvas.Id is { Length: > 0 } canvasId ? canvasId : "canvas";
            foreach (var problem in dragErrors) problems.Add($"behavior.drag-error:{name}: {problem}");
            if (dragErrors.Count == 0 && Read("__ls_strokes") == strokesBefore)
                problems.Add($"behavior.draw-dots:{name}: なぞっても線が引かれません（同じ点から同じ点へ描いているか、描く処理が呼ばれていません）");
            steps.Add(new BehaviorStep("drag", "#" + name, Read("__ls_strokes") > strokesBefore, dragErrors));
        }
        var buttons = steps.Where(step => step.Action == "click").ToArray();
        // Hands-on 2026-10-13: a "stopwatch" that shows the time only at the moment a button is pressed.
        if (expectsTimer && Intervals() == 0)
            problems.Add("behavior.timer-missing: 時間を計るアプリですが、ボタンを押しても時間を進める処理（setInterval など）が動きません");
        // With a library the visible change may happen only in its drawing (a chart), which this check cannot see.
        // An error already explains a button that changed nothing.
        if (buttons.Length > 0 && buttons.All(step => !step.Changed && step.Errors.Count == 0))
            (globals.Count > 0 ? notes : problems).Add("behavior.nothing-changes: どのボタンを押しても画面が変わりません");
        foreach (var silent in buttons.Where(step => !step.Changed && step.Errors.Count == 0))
            notes.Add($"{silent.Target} を押しても画面は変わりませんでした（入力が空・条件付きの可能性）");
        if (inconclusive.Count > 0) notes.Add("この確かめ方では判定できない部分がありました: " + string.Join(" / ", inconclusive.Distinct().Take(3)));
        return new(null, steps, problems, notes);
    }

    /// <summary>
    /// Plays acceptance scenarios on the app, each on a freshly opened page (storage carried over only by a "reload"
    /// step), and returns what did not hold: a control the scenario names that is not on the screen, an error, or text
    /// that should (or should not) be shown afterwards. Empty when every scenario holds.
    /// </summary>
    public static async Task<IReadOnlyList<string>> CheckScenariosAsync(string html, string script, IReadOnlyList<string> libraryGlobals,
        IReadOnlyList<AcceptanceScenario> scenarios, CancellationToken cancellationToken = default, bool reportInconclusive = false)
    {
        // Generation does not repair what the engine could not play; a yardstick (the catalogue) must not count it as held.
        var failures = new List<string>();
        foreach (var scenario in AcceptanceScenario.Usable(scenarios))
        {
            var run = Task.Run(() => PlayScenarioAsync(html, script, libraryGlobals, scenario, cancellationToken), cancellationToken);
            var finished = await Task.WhenAny(run, Task.Delay(OverallTimeLimit, cancellationToken));
            if (finished != run)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (reportInconclusive) failures.Add($"acceptance.inconclusive:{scenario.Title}: took too long to play");
                continue;
            }
            try { failures.AddRange(await run); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (reportInconclusive) failures.Add($"acceptance.inconclusive:{scenario.Title}: could not be played ({ex.GetType().Name})");
            }
        }
        return failures;
    }

    // Finds the control a user would mean by a name: its label, aria-label, placeholder, title, button text, the text just
    // before it, or its id; an exact name wins over a partial one.
    private const string FindControl = """
        (function (kind, wanted) {
          var norm = function (s) { return String(s || '').replace(/[\s:：「」()（）*＊]/g, '').toLowerCase(); };
          var wants = String(wanted || '').split('|').map(norm).filter(function (w) { return w.length > 0; });
          if (!wants.length) return '';
          var root = document.getElementById('app') || document.body;
          var selector = kind === 'press' ? 'button, input[type=button], input[type=submit]' :
                         kind === 'file' ? 'input[type=file]' :
                         kind === 'choose' ? 'select, input[type=radio]' : 'input:not([type=button]):not([type=submit]):not([type=radio]), textarea, select';
          var names = function (el) {
            var list = [el.getAttribute('aria-label'), el.getAttribute('placeholder'), el.getAttribute('title'), el.getAttribute('name'), el.id];
            if (el.tagName === 'BUTTON') list.push(el.textContent);
            if (el.tagName === 'INPUT' && /^(button|submit)$/i.test(el.type || '')) list.push(el.value);
            if (el.id) { var labels = root.querySelectorAll('label[for="' + el.id + '"]'); for (var i = 0; i < labels.length; i++) list.push(labels[i].textContent); }
            var parent = el.closest ? el.closest('label') : null; if (parent) list.push(parent.textContent);
            var before = el.previousElementSibling; if (before && /^(LABEL|SPAN|P|DIV|STRONG|B)$/.test(before.tagName)) list.push(before.textContent);
            if (el.parentElement && el.parentElement.firstChild && el.parentElement.firstChild.nodeType === 3) list.push(el.parentElement.firstChild.textContent);
            if (el.type === 'radio') { var r = el.nextSibling; if (r) list.push(r.textContent); }
            return list.map(norm).filter(function (n) { return n.length > 0; });
          };
          var best = null, score = 0;
          var all = root.querySelectorAll(selector);
          for (var i = 0; i < all.length; i++) {
            var ns = names(all[i]);
            for (var j = 0; j < ns.length; j++)
              for (var k = 0; k < wants.length; k++) {
                var want = wants[k];
                var s = ns[j] === want ? 3 : ns[j].indexOf(want) >= 0 ? 2 : want.indexOf(ns[j]) >= 0 && ns[j].length >= 2 ? 1 : 0;
                if (s > score) { score = s; best = all[i]; }
              }
          }
          // Something the user clicks that is not a button: a checkbox named so, or a list item, cell or card showing
          // exactly that text (a habit marked done by clicking it, 2026-10-20).
          // The only file picker on the screen is the one meant, whatever it is called.
          if (!best && kind === 'file' && all.length === 1) best = all[0];
          if (!best && kind === 'press') {
            var others = root.querySelectorAll('input[type=checkbox], li, td, span, div, label, a, p');
            // The innermost match wins (it comes last in document order), so the click reaches the item's own handler.
            for (var i = 0; i < others.length; i++) {
              var own = others[i].tagName === 'INPUT' ? names(others[i]) : [norm(others[i].textContent)];
              for (var k = 0; k < wants.length; k++) if (own.indexOf(wants[k]) >= 0) best = others[i];
            }
          }
          if (!best) return '';
          var mark = '__ls_scenario_' + Math.floor(Math.random() * 1e9);
          best.setAttribute('data-ls-scenario', mark);
          return mark;
        })
        """;

    private static async Task<IReadOnlyList<string>> PlayScenarioAsync(string html, string script, IReadOnlyList<string> libraryGlobals, AcceptanceScenario scenario, CancellationToken cancellationToken)
    {
        var failures = new List<string>();
        var page = Regex.Replace(html, @"<script\b[^>]*>.*?</script>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        page = Regex.Replace(page, @"<meta[^>]+Content-Security-Policy[^>]*>", "", RegexOptions.IgnoreCase);
        EngineCreator limited = (_, options) =>
        {
            Jint.ConstraintsOptionsExtensions.TimeoutInterval(options, ScriptTimeLimit);
            Jint.ConstraintsOptionsExtensions.MaxStatements(options, MaxStatements);
            Jint.ConstraintsOptionsExtensions.LimitMemory(options, MaxMemoryBytes);
            return new Jint.Engine(options);
        };
        var config = Configuration.Default.WithJs().With(limited);
        using var context = BrowsingContext.New(config);
        AngleSharp.Dom.IDocument? document = null;
        string? Run(string code)
        {
            try { document!.ExecuteScript(code); return null; }
            catch (Exception ex) when (IsLimit(ex)) { return "止まらない処理"; }
            catch (Exception ex) when (ex is Jint.Runtime.JavaScriptException) { return ex.Message; }
            catch (Exception) { return null; }
        }
        string Eval(string code)
        {
            try { return document!.ExecuteScript(code)?.ToString() ?? ""; }
            catch (Exception) { return ""; }
        }

        // Days passed (a "next day" step): the app's clock (Date, Date.now) runs that many days ahead after each reopening.
        var days = 0;
        async Task<string?> OpenAsync(string storedJson)
        {
            document = await context.OpenAsync(request => request.Content(page).Address("https://localstudio.invalid/"), cancellationToken);
            Run(Prelude);
            if (days > 0) Run($"__ls_offset = {days} * 86400000;");
            if (storedJson.Length > 2) Run("(function (s) { for (var k in s) __ls_env[8].setItem(k, s[k]); })(" + storedJson + ");");
            foreach (var global in libraryGlobals.Distinct().Where(global => Regex.IsMatch(global, @"^[A-Za-z_$][\w$]*$"))) Run(LibraryStandIn(global));
            var loadError = Run("(function (" + AppParameters + ") {\n" + PointToStandIns(script) + "\n}).apply(window, __ls_env);");
            if (loadError is null)
                Run("document.dispatchEvent(new Event('DOMContentLoaded', { bubbles: true })); window.dispatchEvent(new Event('load')); if (typeof window.onload === 'function') { try { window.onload(); } catch (e) { __ls_errors.push('load: ' + e); } }");
            RunTimers(Run);
            return Readable(loadError) ?? DrainErrors(document!).FirstOrDefault();
        }

        var opened = await OpenAsync("{}");
        if (opened is not null) { failures.Add($"acceptance.error:{scenario.Title}: the page fails when opened: {opened}"); return failures; }
        var done = new List<string>();
        foreach (var step in scenario.Steps)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? error = null;
            if (step.Reload || step.NextDay)
            {
                if (step.NextDay) days++;
                var stored = Eval("(function () { var s = {}, st = __ls_env[8]; for (var i = 0; i < st.length; i++) { var k = st.key(i); s[k] = st.getItem(k); } return JSON.stringify(s); })()");
                error = await OpenAsync(string.IsNullOrEmpty(stored) ? "{}" : stored);
            }
            else if (step.Tick) { RunTimers(Run); RunTimers(Run); }
            else
            {
                var (kind, name) = step.Set is not null ? ("set", step.Set) : step.Choose is not null ? ("choose", step.Choose) : step.File is not null ? ("file", step.File) : ("press", step.Press!);
                var mark = Eval(FindControl + "(" + JsonSerializer.Serialize(kind) + ", " + JsonSerializer.Serialize(name) + ")");
                if (mark.Length == 0 && step.Optional) continue;
                if (mark.Length == 0)
                {
                    var what = kind == "press" ? "button" : kind == "choose" ? "choice (select or radio buttons)" : kind == "file" ? "file picker (<input type=\"file\">)" : "input field";
                    failures.Add($"acceptance.control-missing:{scenario.Title}: there is no {what} named \"{name}\" on the screen. Label it with these words (button text, <label for>, aria-label or placeholder).");
                    return failures;
                }
                var target = $"document.querySelector('[data-ls-scenario=\"{mark}\"]')";
                error = kind switch
                {
                    // A chosen file: files[0] (name, size, text(), and FileReader.readAsText) holds the scenario's content.
                    "file" => Run($$"""
                        (function (content, name) {
                          var el = {{target}};
                          var f = { name: name, size: content.length, type: /\.csv$/i.test(name) ? 'text/csv' : 'text/plain', lastModified: 0, __ls_text: content,
                                    text: function () { return Promise.resolve(content); } };
                          var list = [f]; list.item = function (i) { return list[i] || null; };
                          try { Object.defineProperty(el, 'files', { configurable: true, get: function () { return list; } }); }
                          catch (e) { __ls_errors.push('the file could not be given to the picker: ' + e); return; }
                          el.dispatchEvent(new Event('input', { bubbles: true })); el.dispatchEvent(new Event('change', { bubbles: true }));
                        })({{JsonSerializer.Serialize(step.Content ?? "")}}, {{JsonSerializer.Serialize(string.IsNullOrWhiteSpace(step.Value) ? "data.csv" : step.Value)}});
                        """),
                    "press" => Run($$"""(function () { var el = {{target}}; if (el.type === 'checkbox') { el.checked = !el.checked; el.dispatchEvent(new Event('click', { bubbles: true })); el.dispatchEvent(new Event('input', { bubbles: true })); el.dispatchEvent(new Event('change', { bubbles: true })); return; } el.dispatchEvent(new Event('click', { bubbles: true, cancelable: true })); var type = (el.getAttribute('type') || 'submit').toLowerCase(); var form = el.closest ? el.closest('form') : null; if (form && type === 'submit') form.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true })); })();"""),
                    _ => Run($$"""
                        (function (wanted) {
                          var el = {{target}};
                          var norm = function (s) { return String(s || '').replace(/\s/g, '').toLowerCase(); };
                          if (el.tagName === 'SELECT') {
                            var hit = null;
                            for (var i = 0; i < el.options.length; i++) { var o = el.options[i]; if (norm(o.text) === norm(wanted) || norm(o.value) === norm(wanted)) { hit = o; break; } }
                            if (!hit) for (var i = 0; i < el.options.length; i++) { var o = el.options[i]; if (norm(o.text).indexOf(norm(wanted)) >= 0) { hit = o; break; } }
                            if (!hit) { __ls_errors.push('no option "' + wanted + '"'); return; }
                            el.value = hit.value;
                          } else if (el.type === 'radio' || el.type === 'checkbox') { el.checked = true; el.dispatchEvent(new Event('click', { bubbles: true })); }
                          else el.value = wanted;
                          el.dispatchEvent(new Event('input', { bubbles: true })); el.dispatchEvent(new Event('change', { bubbles: true }));
                        })({{JsonSerializer.Serialize(step.Value ?? step.Option ?? "")}});
                        """)
                };
                RunTimers(Run);
            }
            done.Add(step.Describe());
            var errors = DrainErrors(document!).ToList();
            if (error is not null) errors.Insert(0, Readable(error)!);
            if (errors.Count > 0)
            {
                failures.Add($"acceptance.error:{scenario.Title}: after {string.Join(", ", done)}: {string.Join(" / ", errors.Take(2))}");
                return failures;
            }
        }
        var shown = VisibleText(document!);
        var plain = NormalizeShown(shown);
        bool Shows(string text) => text.Split('|').Select(NormalizeShown).Where(form => form.Length > 0).Any(form => plain.Contains(form, StringComparison.OrdinalIgnoreCase));
        foreach (var expected in scenario.Expect.Where(text => !string.IsNullOrWhiteSpace(text)))
            if (!Shows(expected))
                failures.Add($"acceptance.failed:{scenario.Title}: after {scenario.Describe()}, the screen should show \"{expected}\" but it shows: {Shorten(shown, 240)}");
        foreach (var unwanted in (scenario.Absent ?? []).Where(text => !string.IsNullOrWhiteSpace(text)))
            if (Shows(unwanted))
                failures.Add($"acceptance.failed:{scenario.Title}: after {scenario.Describe()}, the screen must not show \"{unwanted}\" any more, but it does: {Shorten(shown, 240)}");
        // The file the app made for download (the last Blob), compared line by line as the user would open it.
        if ((scenario.FileExpect ?? []).Count + (scenario.FileAbsent ?? []).Count > 0)
        {
            var made = Eval("__ls_blobs.length ? String(__ls_blobs[__ls_blobs.length - 1]) : '\\u0000none'");
            if (made == "\0none")
                failures.Add($"acceptance.failed:{scenario.Title}: after {scenario.Describe()}, no file was made for download (build it with new Blob([text]) and download it from a link)");
            else
            {
                // "line:X" in fileAbsent: the line X at most once (duplicates removed); "line:" alone: no blank lines.
                var lines = made.Replace("\r\n", "\n").TrimEnd('\n').Split('\n').Select(line => NormalizeShown(line)).ToArray();
                var whole = NormalizeShown(made);
                bool InFile(string text) => text.Split('|').Select(NormalizeShown).Where(form => form.Length > 0).Any(form => whole.Contains(form, StringComparison.OrdinalIgnoreCase));
                foreach (var expected in (scenario.FileExpect ?? []).Where(text => !string.IsNullOrWhiteSpace(text)))
                    if (!InFile(expected))
                        failures.Add($"acceptance.failed:{scenario.Title}: the downloaded file should contain \"{expected}\" but it is: {Shorten(made, 240)}");
                foreach (var unwanted in (scenario.FileAbsent ?? []).Where(text => !string.IsNullOrWhiteSpace(text)))
                    if (unwanted.StartsWith("line:", StringComparison.Ordinal)
                            ? lines.Count(line => line == NormalizeShown(unwanted[5..])) > (unwanted[5..].Length == 0 ? 0 : 1)
                            : InFile(unwanted))
                        failures.Add($"acceptance.failed:{scenario.Title}: the downloaded file must not contain {(unwanted.StartsWith("line:", StringComparison.Ordinal) ? $"the line \"{unwanted[5..]}\" more than once" : $"\"{unwanted}\"")}, but it is: {Shorten(made, 240)}");
            }
        }
        return failures;
    }

    /// <summary>What the user reads: the app's text, the values in its fields and the chosen options.</summary>
    private static string VisibleText(AngleSharp.Dom.IDocument document)
    {
        var app = document.GetElementById("app") ?? document.Body;
        if (app is null) return "";
        var values = app.QuerySelectorAll("input, textarea, select").Select(element =>
            element is AngleSharp.Html.Dom.IHtmlInputElement input ? (input.Type is "checkbox" or "radio" ? "" : input.Value) :
            element is AngleSharp.Html.Dom.IHtmlTextAreaElement area ? area.Value :
            element is AngleSharp.Html.Dom.IHtmlSelectElement select ? select.SelectedOptions.FirstOrDefault()?.Text ?? "" : "");
        return Regex.Replace(app.TextContent + " " + string.Join(" ", values), @"\s+", " ").Trim();
    }

    /// <summary>Text compared without spaces, digit grouping or full-width forms ("1,000 ｍ" and "1000m" are the same).</summary>
    internal static string NormalizeShown(string text)
    {
        var halfWidth = new string(text.Select(ch => ch is >= '０' and <= '９' or >= 'Ａ' and <= 'Ｚ' or >= 'ａ' and <= 'ｚ' ? (char)(ch - 0xFEE0) : ch == '．' ? '.' : ch == '，' ? ',' : ch).ToArray());
        var ungrouped = Regex.Replace(halfWidth, @"(?<=\d),(?=\d{3}\b)", "");
        return Regex.Replace(ungrouped, @"\s+", "");
    }

    private static string Shorten(string text, int length) => text.Length <= length ? "\"" + text + "\"" : "\"" + text[..length] + "…\"";

    private static void RunTimers(Func<string, string?> run) =>
        // Each collected timer runs once (as if a moment passed); a timer that schedules itself does not loop.
        run("(function () { __ls_offset += 1500; var ids = Object.keys(__ls_timers).slice(0, 20); for (var i = 0; i < ids.length; i++) { var t = __ls_timers[ids[i]]; if (!t) continue; if (!t.r) delete __ls_timers[ids[i]]; try { t.f(); } catch (e) { __ls_errors.push('timer: ' + e); } } })();");

    private static readonly (string Method, string StandIn)[] StandIns =
        [(".addEventListener(", ".__ls_on("), (".getContext(", ".__ls_getContext("), (".getBoundingClientRect(", ".__ls_rect("), (".append(", ".__ls_append("), (".prepend(", ".__ls_prepend("), (".replaceChildren(", ".__ls_replaceChildren(")];

    private static string PointToStandIns(string script) =>
        StandIns.Aggregate(script, (text, pair) => text.Replace(pair.Method, pair.StandIn, StringComparison.Ordinal));

    /// <summary>Messages name the methods the app wrote, not the stand-ins its calls were pointed to.</summary>
    private static string? Readable(string? message) =>
        message is null ? null : StandIns.Aggregate(message, (text, pair) => text.Replace(pair.StandIn.Trim('.', '('), pair.Method.Trim('.', '('), StringComparison.Ordinal));

    private static IEnumerable<string> DrainErrors(AngleSharp.Dom.IDocument document)
    {
        object? value;
        try { value = document.ExecuteScript("(function () { var e = __ls_errors.join('\\n'); __ls_errors = []; return e; })()"); }
        catch { yield break; }
        var text = value?.ToString() ?? "";
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries)) yield return Readable(line)!;
    }

    /// <summary>What the user would see: the app's text, form values and classes (a class toggle is a visible change).</summary>
    private static string Snapshot(AngleSharp.Dom.IDocument document)
    {
        var app = document.GetElementById("app");
        if (app is null) return "";
        var values = string.Join("|", app.QuerySelectorAll("input, textarea, select").Select(element =>
            element is AngleSharp.Html.Dom.IHtmlInputElement input ? input.Value + (input.IsChecked ? "*" : "") :
            element is AngleSharp.Html.Dom.IHtmlTextAreaElement area ? area.Value :
            element is AngleSharp.Html.Dom.IHtmlSelectElement select ? select.Value : ""));
        var markup = Regex.Replace(app.InnerHtml, @"\sdata-ls-target=""[^""]*""", "");
        return markup + "\n" + values;
    }
}
