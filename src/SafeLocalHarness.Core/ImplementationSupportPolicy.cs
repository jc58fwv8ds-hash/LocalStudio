namespace SafeLocalHarness.Core;

public enum ProjectImplementationRoute
{
    VerifiedTemplate,
    VerifiedWindowsExecutable,
    GeneratedLocalWebApp,
    GeneratedWindowsExecutable,
    GeneratedAppsScript,
    DesignScaffold,
    Unsupported
}

public sealed record ProjectImplementationDecision(
    ProjectImplementationRoute Route,
    bool CanCreate,
    string Label,
    string Explanation);

public static class ImplementationSupportPolicy
{
    /// <summary>
    /// Every app is made for its own request. The fixed templates (price calculator, task list, character counter,
    /// pinball) are no longer used: they could not take the user's changes ("クラシックにして") and made the tool less
    /// general (user decision 2026-10-13).
    /// </summary>
    public static ProjectImplementationDecision Evaluate(StudioBlueprint blueprint, StudioProjectKind kind)
    {
        return kind switch
        {
            StudioProjectKind.DesktopOrWebApp when RequiresExternalAccess(blueprint) =>
                new(ProjectImplementationRoute.Unsupported, false, "現在は作成できません", "新規実装モードは現在、外部通信やアカウント連携を必要としないアプリだけに対応しています。固定テンプレートへの置き換えは行いません。"),
            StudioProjectKind.DesktopOrWebApp =>
                new(ProjectImplementationRoute.GeneratedLocalWebApp, true, "Webアプリとして新規実装", "ブラウザーで動くローカルWebアプリを、既存テンプレートへ当てはめずにローカルAIが生成し、LocalStudioが検証します。EXEではありません。"),
            StudioProjectKind.WindowsApp when RequiresExternalAccess(blueprint) =>
                new(ProjectImplementationRoute.Unsupported, false, "外部連携の確認が必要です", "外部通信やアカウント連携を含むWindowsアプリは、権限確認と接続先制御が未実装のため、現在はローカル実装を開始しません。"),
            StudioProjectKind.WindowsApp =>
                new(ProjectImplementationRoute.GeneratedWindowsExecutable, true, "Windows EXEを自律実装", "固定テンプレートへ置き換えず、ローカルAIがWindowsアプリを生成し、LocalStudioが安全検査・実コンパイル・失敗修復を有限回繰り返します。"),
            StudioProjectKind.WindowsGame when RequiresExternalAccess(blueprint) =>
                new(ProjectImplementationRoute.Unsupported, false, "外部連携の確認が必要です", "外部通信を伴うゲームは、接続先と権限の確認機能が整うまでローカル実装を開始しません。"),
            StudioProjectKind.WindowsGame =>
                new(ProjectImplementationRoute.GeneratedWindowsExecutable, true, "Windowsゲームを自律実装", "固定ゲームへ置き換えず、ローカルAIが依頼固有のオフラインWindowsゲームを生成し、安全検査・実コンパイル・失敗修復を有限回繰り返します。"),
            // Google sign-in and permissions happen in the user's own Apps Script editor, so account access is expected;
            // reaching other sites (UrlFetchApp) or publishing is not offered until such requests can be approved one by one.
            StudioProjectKind.GoogleAppsScript when blueprint.Capabilities.Any(capability => capability is "network.fetch" or "external.publish") =>
                new(ProjectImplementationRoute.Unsupported, false, "外部接続の確認が必要です", "Google以外のサイトへの接続や公開を含むGASは、接続先を一つずつ確認する仕組みが整うまで作成しません。Googleのサービス（シート、ドキュメント、カレンダー、Gmailなど）だけで動く形なら作成できます。"),
            StudioProjectKind.GoogleAppsScript =>
                new(ProjectImplementationRoute.GeneratedAppsScript, true, "GASとして作成", "ローカルAIがコードを書き、LocalStudioが権限（appsscript.json）を自動で決めて静的に検査します。Apps Scriptエディタへの貼り付けと実行・公開はご自身で行います。"),
            StudioProjectKind.Agent =>
                new(ProjectImplementationRoute.DesignScaffold, true, "安全な設計", "実働エージェントではなく、権限未付与・実行無効の設計スキャフォールドとして保存します。"),
            _ => new(ProjectImplementationRoute.Unsupported, false, "現在は作成できません", "この種類の安全な実装経路はまだありません。")
        };
    }

    public static bool IsPinballRequest(string request) =>
        !string.IsNullOrWhiteSpace(request) &&
        (request.Contains("ピンボール", StringComparison.OrdinalIgnoreCase) ||
         request.Contains("pinball", StringComparison.OrdinalIgnoreCase));

    public static bool IsTextCounterRequest(string request) => IsTextCounter(request);


    private static bool IsTextCounter(string intent) =>
        (intent.Contains("文字数", StringComparison.Ordinal) || intent.Contains("文字カウント", StringComparison.Ordinal)) &&
        (intent.Contains("行数", StringComparison.Ordinal) || intent.Contains("カウンター", StringComparison.Ordinal));

    internal static bool RequiresExternalAccess(StudioBlueprint blueprint) =>
        blueprint.Capabilities.Any(capability => capability is
            "network.fetch" or "account.authorize" or "message.send" or "external.publish");
}
