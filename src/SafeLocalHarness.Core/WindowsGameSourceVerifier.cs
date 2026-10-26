namespace SafeLocalHarness.Core;

public interface IWindowsGameSourceVerifier
{
    Task<ProjectVerificationResult> VerifyAsync(string projectRoot, CancellationToken cancellationToken = default);
}

public sealed class WindowsGameSourceVerifier : IWindowsGameSourceVerifier
{
    public async Task<ProjectVerificationResult> VerifyAsync(string projectRoot, CancellationToken cancellationToken = default)
    {
        var passed = new List<string>();
        var errors = new List<string>();
        var required = new[]
        {
            "LocalStudioPinball.slnx", "README.md",
            "src/LocalStudioPinball.Core/PinballGame.cs",
            "src/LocalStudioPinball.App/LocalStudioPinball.App.csproj",
            "src/LocalStudioPinball.App/app.manifest",
            "src/LocalStudioPinball.App/MainWindow.xaml",
            "src/LocalStudioPinball.App/PinballBoard.cs",
            "src/LocalStudioPinball.App/HighScoreStore.cs",
            "tests/LocalStudioPinball.Tests/Program.cs"
        };
        var content = new List<string>();
        foreach (var relative in required)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = Path.Combine(projectRoot, relative.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path)) { errors.Add("missing:" + relative); continue; }
            content.Add(await File.ReadAllTextAsync(path, cancellationToken));
            passed.Add("file:" + relative);
        }
        var combined = string.Join('\n', content);
        Check(combined.Contains("<UseWPF>true</UseWPF>", StringComparison.Ordinal), "windows.wpf", passed, errors);
        Check(combined.Contains("requestedExecutionLevel level=\"asInvoker\"", StringComparison.Ordinal), "security.as-invoker", passed, errors);
        Check(!combined.Contains("<PackageReference", StringComparison.OrdinalIgnoreCase), "supply-chain.no-packages", passed, errors);
        Check(!combined.Contains("HttpClient", StringComparison.Ordinal) && !combined.Contains("Process.Start", StringComparison.Ordinal) && !combined.Contains("Microsoft.Win32.Registry", StringComparison.Ordinal), "security.no-network-process-registry", passed, errors);
        Check(combined.Contains("class PinballGame", StringComparison.Ordinal) && combined.Contains("ResolveBumper", StringComparison.Ordinal) && combined.Contains("ResolveFlipper", StringComparison.Ordinal), "game.physics", passed, errors);
        Check(combined.Contains("Key.Space", StringComparison.Ordinal) && combined.Contains("Key.Left", StringComparison.Ordinal) && combined.Contains("Key.Right", StringComparison.Ordinal), "game.keyboard-controls", passed, errors);
        Check(combined.Contains("first-drain", StringComparison.Ordinal) && combined.Contains("bumper-score-and-kick", StringComparison.Ordinal), "tests.behavior", passed, errors);
        Check(!combined.Contains("CharacterSpacing=", StringComparison.Ordinal), "wpf.no-unsupported-character-spacing", passed, errors);
        Check(combined.Contains("using System.IO;", StringComparison.Ordinal), "wpf.explicit-system-io", passed, errors);
        Check(combined.Contains("new Typeface(new FontFamily(", StringComparison.Ordinal), "wpf.typeface-api", passed, errors);
        return new(errors.Count == 0, passed, errors, DateTimeOffset.Now);
    }

    private static void Check(bool condition, string name, ICollection<string> passed, ICollection<string> errors)
    {
        if (condition) passed.Add(name); else errors.Add(name);
    }
}
