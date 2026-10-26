using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace SafeLocalHarness.Core;

public interface IWindowsExecutableCompiler
{
    Task<string> CompileAsync(string projectRoot, CancellationToken cancellationToken = default);
}

public sealed class WindowsBuildEnvironmentException : InvalidOperationException
{
    public WindowsBuildEnvironmentException(string message) : base(message) { }
}

public sealed class WindowsExecutableCompiler : IWindowsExecutableCompiler
{
    public async Task<string> CompileAsync(string projectRoot, CancellationToken cancellationToken = default)
    {
        var sources = Directory.Exists(projectRoot)
            ? Directory.EnumerateFiles(projectRoot, "*.cs", SearchOption.TopDirectoryOnly)
                .OrderBy(path => Path.GetFileName(path).Equals("Program.cs", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : [];
        var manifest = Path.Combine(projectRoot, "app.manifest");
        if (sources.Length == 0 || !sources.Any(path => Path.GetFileName(path).Equals("Program.cs", StringComparison.OrdinalIgnoreCase)) || !File.Exists(manifest))
            throw new InvalidDataException("Windowsアプリのソースを確認できませんでした。");
        var compiler = FindCompiler() ?? throw new WindowsBuildEnvironmentException(
            "このPCでユーザー権限から利用できるWindows標準コンパイラーが見つかりません。AIによる再生成では解決できないため、安全に停止しました。");
        var output = Path.Combine(projectRoot, "LocalStudioApp.exe");
        var start = new ProcessStartInfo
        {
            FileName = compiler,
            WorkingDirectory = projectRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        try
        {
            var compilerEncoding = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
            start.StandardOutputEncoding = compilerEncoding;
            start.StandardErrorEncoding = compilerEncoding;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            // Error codes and source positions remain usable if the host code page is unavailable.
        }
        start.ArgumentList.Add("/nologo");
        // Messages in English (ASCII): the Japanese ones arrived garbled through the console code page, so neither the
        // model nor the log could read which name was missing (trial 2026-10-13).
        start.ArgumentList.Add("/preferreduilang:en-US");
        start.ArgumentList.Add("/target:winexe");
        start.ArgumentList.Add("/optimize+");
        start.ArgumentList.Add("/platform:anycpu");
        start.ArgumentList.Add("/out:" + output);
        start.ArgumentList.Add("/win32manifest:" + manifest);
        start.ArgumentList.Add("/reference:System.dll");
        // System.Core holds System.Linq (in-memory queries only; network and process APIs stay forbidden by the source check).
        start.ArgumentList.Add("/reference:System.Core.dll");
        start.ArgumentList.Add("/reference:System.Drawing.dll");
        start.ArgumentList.Add("/reference:System.Windows.Forms.dll");
        foreach (var source in sources) start.ArgumentList.Add(source);

        using var process = new Process { StartInfo = start };
        if (!process.Start()) throw new InvalidOperationException("Windowsアプリの作成を開始できませんでした。");
        var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(true);
            throw;
        }
        var diagnostics = (await standardOutput + "\n" + await standardError).Trim();
        if (process.ExitCode != 0 || !File.Exists(output))
            throw new InvalidDataException("Windows EXEの作成に失敗しました。" + (diagnostics.Length == 0 ? "" : " " + diagnostics[..Math.Min(diagnostics.Length, 500)]));
        await using var stream = File.OpenRead(output);
        var header = new byte[2];
        if (await stream.ReadAsync(header, cancellationToken) != 2 || header[0] != (byte)'M' || header[1] != (byte)'Z')
            throw new InvalidDataException("作成結果がWindows EXE形式ではありません。");
        return output;
    }

    private static string? FindCompiler()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var candidates = new[]
        {
            Path.Combine(windows, "Microsoft.NET", "Framework64", "v4.0.30319", "csc.exe"),
            Path.Combine(windows, "Microsoft.NET", "Framework", "v4.0.30319", "csc.exe")
        };
        return candidates.FirstOrDefault(File.Exists);
    }
}

public sealed class WindowsAppSourceVerifier
{
    public async Task<ProjectVerificationResult> VerifyAsync(string projectRoot, CancellationToken cancellationToken = default)
    {
        var passed = new List<string>();
        var errors = new List<string>();
        var sourcePath = Path.Combine(projectRoot, "Program.cs");
        var sourcePaths = Directory.Exists(projectRoot)
            ? Directory.EnumerateFiles(projectRoot, "*.cs", SearchOption.TopDirectoryOnly)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : [];
        var manifestPath = Path.Combine(projectRoot, "app.manifest");
        var requirementsPath = Path.Combine(projectRoot, "requirements.json");
        var source = sourcePaths.Length == 0
            ? ""
            : string.Join("\n", await Task.WhenAll(sourcePaths.Select(path => File.ReadAllTextAsync(path, Encoding.UTF8, cancellationToken))));
        var manifest = File.Exists(manifestPath) ? await File.ReadAllTextAsync(manifestPath, Encoding.UTF8, cancellationToken) : "";
        var requirements = File.Exists(requirementsPath) ? await File.ReadAllTextAsync(requirementsPath, Encoding.UTF8, cancellationToken) : "";
        Check(File.Exists(sourcePath) && source.Length > 0 && manifest.Length > 0 && requirements.Length > 0, "files.native-windows", passed, errors);
        string? appType = null;
        var requestedFeatureCount = 0;
        try
        {
            using var document = JsonDocument.Parse(requirements);
            appType = document.RootElement.GetProperty("appType").GetString();
            if (document.RootElement.TryGetProperty("requestedFeatures", out var requested) && requested.ValueKind == JsonValueKind.Array)
                requestedFeatureCount = requested.GetArrayLength();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            errors.Add("requirements.invalid");
        }

        if (appType == "native-windows-character-counter")
        {
            Check(source.Contains("TextChanged +=", StringComparison.Ordinal) && source.Contains("editor.Text.Length", StringComparison.Ordinal), "counter.realtime", passed, errors);
            Check(source.Contains("editor.Lines.Length", StringComparison.Ordinal), "counter.lines", passed, errors);
            Check(source.Contains("Environment.SpecialFolder.LocalApplicationData", StringComparison.Ordinal) && source.Contains("File.WriteAllText", StringComparison.Ordinal), "draft.local-user-storage", passed, errors);
            Check(source.Contains("File.Delete", StringComparison.Ordinal), "draft.delete", passed, errors);
        }
        else if (appType == "generated-local-windows-app")
        {
            Check(source.Length is > 200 and <= 256 * 1024, "generated.source-size", passed, errors);
            Check(source.Contains("Application.Run", StringComparison.Ordinal) && source.Contains(": Form", StringComparison.Ordinal), "generated.windows-entrypoint", passed, errors);
            Check(source.Contains("+=", StringComparison.Ordinal) &&
                  (source.Contains("Timer", StringComparison.Ordinal) || source.Contains("OnPaint", StringComparison.Ordinal) || source.Contains(".Click", StringComparison.Ordinal) || source.Contains("KeyDown", StringComparison.Ordinal)), "generated.interactive", passed, errors);
            Check(requestedFeatureCount is > 0 and <= 8, "generated.requirements-valid", passed, errors);
            var usesFileSystem = source.Contains("File.", StringComparison.Ordinal) || source.Contains("Directory.", StringComparison.Ordinal);
            Check(!usesFileSystem || source.Contains("Environment.SpecialFolder.LocalApplicationData", StringComparison.Ordinal), "generated.user-storage-only", passed, errors);
        }
        else
        {
            errors.Add("requirements.unsupported-native-app-type");
        }

        var forbidden = new[] { "System.Net", "HttpClient", "WebClient", "Process.Start", "System.Diagnostics", "Registry", "Microsoft.Win32", "DllImport", "System.Reflection", "Assembly.Load", "unsafe", "dynamic", "runas", "cmd.exe", "powershell" };
        Check(forbidden.All(token => !source.Contains(token, StringComparison.OrdinalIgnoreCase)), "security.no-network-or-process", passed, errors);
        Check(manifest.Contains("requestedExecutionLevel level=\"asInvoker\"", StringComparison.Ordinal), "security.as-invoker", passed, errors);
        Check(requirements.Contains("\"adminRequired\": false", StringComparison.Ordinal) && requirements.Contains("\"networkRequired\": false", StringComparison.Ordinal), "requirements.native-windows", passed, errors);
        return new(errors.Count == 0, passed, errors, DateTimeOffset.Now);
    }

    private static void Check(bool condition, string name, ICollection<string> passed, ICollection<string> errors)
    {
        if (condition) passed.Add(name); else errors.Add(name);
    }
}
