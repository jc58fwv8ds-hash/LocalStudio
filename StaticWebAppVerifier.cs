using System.Text;
using System.Text.Json;

namespace SafeLocalHarness.Core;

public sealed record ProjectVerificationResult(
    bool Passed,
    IReadOnlyList<string> PassedChecks,
    IReadOnlyList<string> Errors,
    DateTimeOffset VerifiedAt);

public interface IStaticWebAppVerifier
{
    Task<ProjectVerificationResult> VerifyAsync(string projectRoot, CancellationToken cancellationToken = default);
}

public sealed class StaticWebAppVerifier : IStaticWebAppVerifier
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public async Task<ProjectVerificationResult> VerifyAsync(string projectRoot, CancellationToken cancellationToken = default)
    {
        var passed = new List<string>();
        var errors = new List<string>();
        var expected = new[] { "index.html", "styles.css", "app.js", "requirements.json" };
        var content = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var relative in expected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = Path.Combine(projectRoot, relative);
            if (!File.Exists(path)) { errors.Add($"missing:{relative}"); continue; }
            var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
            try { content[relative] = StrictUtf8.GetString(bytes); }
            catch (DecoderFallbackException) { errors.Add($"invalid-utf8:{relative}"); }
        }

        Check(content.ContainsKey("index.html"), "files.index", passed, errors);
        Check(content.ContainsKey("styles.css"), "files.styles", passed, errors);
        Check(content.ContainsKey("app.js"), "files.script", passed, errors);
        Check(content.ContainsKey("requirements.json"), "files.requirements", passed, errors);
        if (errors.Count > 0) return new(false, passed, errors, DateTimeOffset.Now);

        var html = content["index.html"];
        var css = content["styles.css"];
        var script = content["app.js"];
        var appCss = "";
        var appCssPath = Path.Combine(projectRoot, AppStylesheetPolicy.FileName);
        var linksAppCss = html.Contains($"href=\"{AppStylesheetPolicy.FileName}\"", StringComparison.OrdinalIgnoreCase);
        if (File.Exists(appCssPath))
        {
            try { appCss = StrictUtf8.GetString(await File.ReadAllBytesAsync(appCssPath, cancellationToken)); }
            catch (DecoderFallbackException) { errors.Add("invalid-utf8:" + AppStylesheetPolicy.FileName); }
        }
        // The optional app.css is held to the same rules the generator applies, and is the only extra stylesheet allowed.
        Check(AppStylesheetPolicy.Validate(appCss).Count == 0 && linksAppCss == File.Exists(appCssPath), "security.app-css", passed, errors);
        var stylesheetLinks = System.Text.RegularExpressions.Regex.Matches(html, @"<link\b[^>]*\bhref\s*=\s*[""']([^""']+)[""']", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            .Select(match => match.Groups[1].Value);
        // 4-5: library files the user added are allowed when declared in vendor/vendor.json with matching hashes.
        var vendorProblems = LibraryShelf.VerifyProject(projectRoot, html);
        Check(vendorProblems.Count == 0, "security.vendor-libraries", passed, errors);
        foreach (var problem in vendorProblems) errors.Add(problem);
        Check(stylesheetLinks.All(href => href is "styles.css" or AppStylesheetPolicy.FileName || href.StartsWith(LibraryShelf.VendorFolder + "/", StringComparison.Ordinal)), "security.local-stylesheets-only", passed, errors);
        var combined = string.Join('\n', html, css, appCss, script);

        Check(!combined.Contains('\uFFFD') && !LocalWebAppGenerationEngine.HasForbiddenControlCharacters(combined), "encoding.no-invalid-control-characters", passed, errors);
        Check(html.Contains("Content-Security-Policy", StringComparison.Ordinal), "security.csp", passed, errors);
        Check(!combined.Contains("http://", StringComparison.OrdinalIgnoreCase) && !combined.Contains("https://", StringComparison.OrdinalIgnoreCase), "security.no-external-resources", passed, errors);
        Check(!script.Contains("innerHTML", StringComparison.Ordinal) && !script.Contains("document.write", StringComparison.Ordinal) && !script.Contains("eval(", StringComparison.Ordinal) && !script.Contains("new Function", StringComparison.Ordinal), "security.no-dynamic-code-or-html", passed, errors);
        // Labels are required for form controls only: an app made of buttons and text (a dice or a stopwatch) has
        // nothing to label (2026-10-09 benchmark: two such apps were rejected after passing the generator's checks).
        Check(HasAccessibleFormControls(html) && html.Contains("aria-live", StringComparison.Ordinal), "accessibility.labels", passed, errors);

        string? appType = null;
        string? implementationMode = null;
        HashSet<string> declaredChecks = new(StringComparer.Ordinal);
        var requestedFeatures = new List<GeneratedRequirement>();
        try
        {
            using var requirements = JsonDocument.Parse(content["requirements.json"]);
            var root = requirements.RootElement;
            Check(root.GetProperty("networkRequired").ValueKind == JsonValueKind.False, "requirements.offline", passed, errors);
            appType = root.GetProperty("appType").GetString();
            implementationMode = root.TryGetProperty("implementationMode", out var mode) ? mode.GetString() : null;
            declaredChecks = root.GetProperty("checks").EnumerateArray()
                .Select(item => item.GetString())
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => item!)
                .ToHashSet(StringComparer.Ordinal);
            if (root.TryGetProperty("requestedFeatures", out var features) && features.ValueKind == JsonValueKind.Array)
            {
                requestedFeatures.AddRange(features.EnumerateArray().Select(item => new GeneratedRequirement(
                    item.GetProperty("id").GetString() ?? "",
                    item.GetProperty("text").GetString() ?? "")));
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            errors.Add("requirements.invalid");
        }

        if (appType == "local-static-web-app")
        {
            Check(script.Contains("function addTask", StringComparison.Ordinal) && script.Contains("addEventListener('submit'", StringComparison.Ordinal), "task.add", passed, errors);
            Check(script.Contains("function toggleTask", StringComparison.Ordinal), "task.toggle", passed, errors);
            Check(script.Contains("function deleteTask", StringComparison.Ordinal), "task.delete", passed, errors);
            Check(script.Contains("function startEdit", StringComparison.Ordinal) && script.Contains("'Enter'", StringComparison.Ordinal) && script.Contains("'Escape'", StringComparison.Ordinal), "task.edit.keyboard", passed, errors);
            Check(script.Contains("let settled = false", StringComparison.Ordinal) && script.Contains("if (settled) return", StringComparison.Ordinal), "task.edit.single-settlement", passed, errors);
            Check(script.Contains("classList.remove", StringComparison.Ordinal) && script.Contains("classList.add", StringComparison.Ordinal) && script.Contains("visibleTasks", StringComparison.Ordinal), "task.filter", passed, errors);
            Check(script.Contains("localStorage.getItem", StringComparison.Ordinal) && script.Contains("localStorage.setItem", StringComparison.Ordinal), "task.persist", passed, errors);
            Check(ContainsAll(declaredChecks, ["task.add", "task.toggle", "task.delete", "task.edit.keyboard", "task.filter", "task.persist"]), "requirements.task-alignment", passed, errors);
        }
        else if (appType == "price-calculator")
        {
            Check(html.Contains("id=\"price-input\"", StringComparison.Ordinal) && html.Contains("id=\"discount-input\"", StringComparison.Ordinal) && html.Contains("id=\"tax-input\"", StringComparison.Ordinal), "calculator.inputs", passed, errors);
            Check(script.Contains("function calculateTotal", StringComparison.Ordinal) && script.Contains("discount / 100", StringComparison.Ordinal) && script.Contains("tax / 100", StringComparison.Ordinal) && script.Contains("Math.round", StringComparison.Ordinal), "calculator.formula", passed, errors);
            Check(script.Contains("function addHistory", StringComparison.Ordinal) && script.Contains("localStorage.getItem", StringComparison.Ordinal) && script.Contains("localStorage.setItem", StringComparison.Ordinal), "history.persist", passed, errors);
            Check(script.Contains("function clearHistory", StringComparison.Ordinal) && html.Contains("id=\"clear-history\"", StringComparison.Ordinal), "history.delete", passed, errors);
            Check(!combined.Contains("task-form", StringComparison.Ordinal) && !script.Contains("function addTask", StringComparison.Ordinal), "calculator.no-task-template", passed, errors);
            Check(ContainsAll(declaredChecks, ["calculator.inputs", "calculator.formula", "history.persist", "history.delete"]), "requirements.calculator-alignment", passed, errors);
        }
        else if (appType == "generated-local-web-app")
        {
            Check(implementationMode == "local-llm-bounded", "generated.bounded-mode", passed, errors);
            Check(LocalWebAppGenerationEngine.ExtractGeneratedBody(html) is not { } generatedBody || LocalWebAppGenerationEngine.TagBalanceProblems(generatedBody).Count == 0, "structure.tags-balanced", passed, errors);
            Check(LocalWebAppGenerationEngine.ExtractGeneratedBody(html) is not { } lookedUpBody || LocalWebAppGenerationEngine.MissingElementIds(lookedUpBody, script).Count == 0, "behavior.elements-exist", passed, errors);
            Check(requestedFeatures.Count is > 0 and <= 8 && requestedFeatures.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() == requestedFeatures.Count, "generated.requirements-valid", passed, errors);
            // Same rule as generation: a button with a listener, or an input that reacts while typing.
            // Controls the script makes with createElement count as well (the run check sees whether they reach the page).
            var buttonDriven = (html.Contains("<button", StringComparison.OrdinalIgnoreCase) || System.Text.RegularExpressions.Regex.IsMatch(script, @"createElement\(\s*['""]button['""]")) &&
                               script.Contains("addEventListener", StringComparison.Ordinal);
            var inputDriven = (System.Text.RegularExpressions.Regex.IsMatch(html, @"<(input|select|textarea)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase) ||
                               System.Text.RegularExpressions.Regex.IsMatch(script, @"createElement\(\s*['""](input|select|textarea)['""]")) &&
                              System.Text.RegularExpressions.Regex.IsMatch(script, @"addEventListener\s*\(\s*['""](input|change)['""]");
            Check(buttonDriven || inputDriven, "generated.interactive", passed, errors);
            if (declaredChecks.Contains("generated.no-visible-internal-contract"))
                Check(!html.Contains("data-ls-requirement", StringComparison.OrdinalIgnoreCase) &&
                      !html.Contains("ls-feature-contract", StringComparison.OrdinalIgnoreCase) &&
                      !html.Contains("実装対象の機能", StringComparison.Ordinal), "generated.no-visible-internal-contract", passed, errors);
            var forbidden = new[] { "fetch(", "XMLHttpRequest", "WebSocket", "EventSource", "sendBeacon", "window.open", "location.href", "location.assign", "ActiveXObject", "WScript", "navigator.clipboard" };
            Check(forbidden.All(token => !script.Contains(token, StringComparison.OrdinalIgnoreCase)), "generated.no-external-or-host-api", passed, errors);
            Check(ContainsAll(declaredChecks, ["generated.interactive", "generated.requirement-evidence", "generated.manual-verification-plan", "security.no-external-resources", "security.no-dangerous-script-api", "accessibility.labels"]), "requirements.generated-alignment", passed, errors);
            var behaviorContractErrors = LocalWebAppGenerationEngine.ValidateKnownBehaviorContracts(script, requestedFeatures);
            Check(behaviorContractErrors.Count == 0, "generated.known-behavior-contracts", passed, errors);
            foreach (var contractError in behaviorContractErrors) errors.Add(contractError);
            VerifyGeneratedEvidence(projectRoot, html, script, appCss, requestedFeatures, passed, errors);
            VerifyGeneratedPlan(projectRoot, requestedFeatures, passed, errors);
            VerifyGeneratedManifest(projectRoot, html, requestedFeatures, passed, errors);
        }
        else
        {
            errors.Add("requirements.unsupported-app-type");
        }

        // Last, the app is run (loaded, typed into, clicked); only when everything else holds, as it is the slowest check.
        if (errors.Count == 0)
        {
            var behavior = await WebAppBehaviorChecker.CheckAsync(projectRoot, cancellationToken);
            Check(behavior.Passed, "behavior.runs", passed, errors);
            foreach (var problem in behavior.Problems) errors.Add(problem);
        }

        return new(errors.Count == 0, passed, errors, DateTimeOffset.Now);
    }

    private static void Check(bool condition, string name, ICollection<string> passed, ICollection<string> errors)
    {
        if (condition) passed.Add(name); else errors.Add(name);
    }

    private static bool ContainsAll(ISet<string> values, IEnumerable<string> expected) => expected.All(values.Contains);

    private static bool HasAccessibleFormControls(string html)
    {
        var hasControls = System.Text.RegularExpressions.Regex.IsMatch(html, @"<(input|select|textarea)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        if (!hasControls) return true;
        return html.Contains("<label", StringComparison.OrdinalIgnoreCase) ||
               System.Text.RegularExpressions.Regex.IsMatch(html, @"<(input|select|textarea)\b[^>]*\baria-label\s*=", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    }

    private static void VerifyGeneratedManifest(
        string projectRoot,
        string html,
        IReadOnlyList<GeneratedRequirement> requestedFeatures,
        ICollection<string> passed,
        ICollection<string> errors)
    {
        try
        {
            using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(projectRoot, "localstudio.json"), StrictUtf8));
            var root = manifest.RootElement;
            var blueprint = root.GetProperty("blueprint");
            var name = blueprint.GetProperty("name").GetString() ?? "";
            var features = blueprint.GetProperty("features").EnumerateArray().Select(item => item.GetString() ?? "").ToArray();
            Check(root.GetProperty("implementationMode").GetString() == "generated-local-web-app", "generated.manifest-mode", passed, errors);
            Check(!string.IsNullOrWhiteSpace(name) && html.Contains(System.Net.WebUtility.HtmlEncode(name), StringComparison.OrdinalIgnoreCase), "generated.manifest-name", passed, errors);
            Check(features.SequenceEqual(requestedFeatures.Select(item => item.Text), StringComparer.Ordinal), "generated.manifest-requirements", passed, errors);
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or KeyNotFoundException)
        {
            errors.Add("generated.manifest-invalid");
        }
    }

    private static void VerifyGeneratedPlan(
        string projectRoot,
        IReadOnlyList<GeneratedRequirement> requestedFeatures,
        ICollection<string> passed,
        ICollection<string> errors)
    {
        try
        {
            using var plan = JsonDocument.Parse(File.ReadAllText(Path.Combine(projectRoot, "verification-plan.json"), StrictUtf8));
            var root = plan.RootElement;
            var planned = root.GetProperty("requirements").EnumerateArray()
                .Select(item => new
                {
                    Id = item.GetProperty("id").GetString() ?? "",
                    Text = item.GetProperty("text").GetString() ?? "",
                    Instruction = item.GetProperty("instruction").GetString() ?? ""
                }).ToArray();
            Check(root.GetProperty("scope").GetString() == "guided-manual-runtime-check", "generated.plan-scope", passed, errors);
            Check(planned.Select(item => item.Id).SequenceEqual(requestedFeatures.Select(item => item.Id), StringComparer.Ordinal), "generated.plan-ids", passed, errors);
            Check(planned.Select(item => item.Text).SequenceEqual(requestedFeatures.Select(item => item.Text), StringComparer.Ordinal), "generated.plan-requirements", passed, errors);
            Check(planned.All(item => !string.IsNullOrWhiteSpace(item.Instruction) && item.Instruction.Length <= 1000), "generated.plan-instructions", passed, errors);
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or KeyNotFoundException)
        {
            errors.Add("generated.plan-invalid");
        }
    }

    private static void VerifyGeneratedEvidence(
        string projectRoot,
        string html,
        string script,
        string appCss,
        IReadOnlyList<GeneratedRequirement> requestedFeatures,
        ICollection<string> passed,
        ICollection<string> errors)
    {
        try
        {
            var visualIds = requestedFeatures.Where(feature => VisualRequirementPolicy.IsVisual(feature.Text)).Select(feature => feature.Id).ToHashSet(StringComparer.Ordinal);
            if (visualIds.Count > 0)
                Check(VisualRequirementPolicy.HasStyleEvidence(appCss, html), "generated.style-evidence-grounded", passed, errors);
            var path = Path.Combine(projectRoot, "implementation-evidence.json");
            using var evidence = JsonDocument.Parse(File.ReadAllText(path, StrictUtf8));
            var root = evidence.RootElement;
            var reviewed = root.GetProperty("requirements").EnumerateArray()
                .Select(item => new GeneratedRequirementReview(
                    item.GetProperty("id").GetString() ?? "",
                    item.GetProperty("passed").GetBoolean(),
                    item.GetProperty("ui_evidence").GetString() ?? "",
                    item.GetProperty("behavior_evidence").GetString() ?? ""))
                .ToArray();
            Check(root.GetProperty("reviewer").GetString() == "local-model-grounded-review", "generated.evidence-reviewer", passed, errors);
            Check(reviewed.Length == requestedFeatures.Count && reviewed.Select(item => item.Id).SequenceEqual(requestedFeatures.Select(item => item.Id), StringComparer.Ordinal), "generated.evidence-ids", passed, errors);
            var behavioural = reviewed.Where(item => !visualIds.Contains(item.Id)).ToArray();
            var needsUi = requestedFeatures.Where(feature => RequirementEvidencePolicy.NeedsUiEvidence(feature.Text)).Select(feature => feature.Id).ToHashSet(StringComparer.Ordinal);
            Check(behavioural.Where(item => needsUi.Contains(item.Id)).All(item => item.Passed && LocalWebAppGenerationEngine.IsEvidenceToken(item.UiEvidence, allowMember: false) && LocalWebAppGenerationEngine.ContainsHtmlId(html, item.UiEvidence)), "generated.ui-evidence-grounded", passed, errors);
            Check(behavioural.All(item => item.Passed && LocalWebAppGenerationEngine.IsEvidenceToken(item.BehaviorEvidence, allowMember: true) && script.Contains(item.BehaviorEvidence, StringComparison.Ordinal)), "generated.behavior-evidence-grounded", passed, errors);
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or KeyNotFoundException)
        {
            errors.Add("generated.evidence-invalid");
        }
    }
}
