namespace SafeLocalHarness.Core;

/// <summary>
/// Permissions are decided by LocalStudio, not by the planning model: in three of three real runs (2026-10-07/09) the
/// model asked for internet access for a purely local memo app. Generated apps get local save only unless the user's
/// own words ask for something external; only then is the model's request kept, and creation still waits for the
/// user's explicit choice.
/// </summary>
public static class BlueprintPermissionPolicy
{
    public static IReadOnlyList<string> LocalOnly { get; } = ["workspace.read", "workspace.write"];

    private static readonly string[] ExternalSignals =
    [
        "インターネット", "ネット", "オンライン", "API", "通信", "ダウンロード", "アップロード", "ログイン", "アカウント",
        "サーバー", "クラウド", "メール", "送信", "共有", "公開", "同期", "Slack", "LINE", "Google", "http", "Web上"
    ];

    public static bool AsksForExternalAccess(string request) =>
        ExternalSignals.Any(signal => request.Contains(signal, StringComparison.OrdinalIgnoreCase));

    // Words that ask a GAS project to reach beyond Google (Google itself, mail and sharing are normal for GAS).
    private static readonly string[] BeyondGoogleSignals =
    [
        "API", "http", "URL", "Webhook", "外部サイト", "外部サービス", "Slack", "LINE", "Chatwork", "Teams", "スクレイピング", "取得して"
    ];

    public static bool AsksBeyondGoogle(string request) =>
        BeyondGoogleSignals.Any(signal => request.Contains(signal, StringComparison.OrdinalIgnoreCase));

    public static StudioBlueprint Derive(StudioBlueprint blueprint, StudioProjectKind kind, string request)
    {
        // Agent designs are not built by the local generators; their permissions stay visible as proposed.
        if (kind is StudioProjectKind.Agent) return blueprint;
        // GAS always works with the user's Google account (authorized in their own editor); reaching other sites is
        // kept only when the user's words ask for it, as with apps.
        if (kind is StudioProjectKind.GoogleAppsScript)
            return AsksBeyondGoogle(request)
                ? blueprint
                : blueprint with { Capabilities = blueprint.Capabilities.Where(capability => capability is not ("network.fetch" or "external.publish")).ToArray() };
        if (AsksForExternalAccess(request)) return blueprint;
        return blueprint with { Capabilities = LocalOnly };
    }
}
