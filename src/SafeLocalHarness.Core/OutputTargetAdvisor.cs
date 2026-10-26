namespace SafeLocalHarness.Core;

public sealed record OutputTargetAdvice(StudioProjectKind? RecommendedKind, string Reason, bool IsConfident);

public static class OutputTargetAdvisor
{
    public static string Describe(string request, StudioProjectKind selected)
    {
        var selectedLabel = Label(selected);
        if (string.IsNullOrWhiteSpace(request))
            return $"出力形式: {selectedLabel}。用途を入力すると、LocalStudioが適した形式を提案します。";

        var advice = Analyze(request);
        if (advice.RecommendedKind is null)
            return $"形式を一つに決めるには情報が足りません — {advice.Reason} 現在は「{selectedLabel}」を選択しています。";

        var recommended = advice.RecommendedKind.Value;
        var recommendedLabel = Label(recommended);
        var support = SupportNotice(recommended, request);
        return recommended == selected
            ? $"用途上のおすすめ: {recommendedLabel} — {advice.Reason}{support}"
            : $"用途上のおすすめは「{recommendedLabel}」です — {advice.Reason} 現在は「{selectedLabel}」を選択しています。{support}";
    }

    public static OutputTargetAdvice Analyze(string request)
    {
        if (string.IsNullOrWhiteSpace(request)) return new(null, "作りたいものと使い方を入力してください。", false);

        if (ContainsAny(request, "Google Apps Script", "GAS", "スプレッドシート", "Gmail", "Googleフォーム"))
            return new(StudioProjectKind.GoogleAppsScript, "Googleサービス内の自動処理に向いています。", true);

        if (ContainsAny(request, "エージェント", "agent", "自律実行", "ツールを使って", "巡回して判断"))
            return new(StudioProjectKind.Agent, "複数手順の判断やツール利用を伴う目的です。", true);

        if (ContainsAny(request, "ゲーム", "テトリス", "ピンボール", "パズル", "RPG", "シューティング", "アクション"))
            return new(StudioProjectKind.WindowsGame, "キーボード操作や連続描画を持つゲームとして扱うのが自然です。", true);

        if (ContainsAny(request, "Webアプリ", "ウェブアプリ", "ブラウザーで", "ブラウザで", "URLで", "複数端末", "スマホでも"))
            return new(StudioProjectKind.DesktopOrWebApp, "ブラウザーから開けることが目的に含まれています。", true);

        if (ContainsAny(request, "EXE", "Windowsアプリ", "デスクトップアプリ", "ブラウザーを使わ", "ブラウザを使わ", "タスクトレイ", "常駐", "複数ウィンドウ", "ショートカット") ||
            ContainsAny(request, "文字数カウンター", "文字カウント", "行数カウンター"))
            return new(StudioProjectKind.WindowsApp, "独立したWindows画面とEXEでの起動が目的に合います。", true);

        if (ContainsAny(request, "計算フォーム", "入力フォーム", "管理画面", "ダッシュボード", "一覧表"))
            return new(StudioProjectKind.DesktopOrWebApp, "フォーム中心の小さなツールは、インストール不要のWeb形式で扱いやすくなります。", true);

        return new(null, "Webなら導入が簡単で、Windows EXEなら独立した画面やOS連携に向きます。", false);
    }

    private static string SupportNotice(StudioProjectKind kind, string request) => kind switch
    {
        StudioProjectKind.WindowsGame =>
            "ゲームは依頼に合わせてローカルAIが自律実装し、実コンパイル後に操作確認待ちとして保存します。",
        StudioProjectKind.WindowsApp =>
            "アプリは依頼に合わせてローカルAIが自律実装し、実コンパイル後に操作確認待ちとして保存します。",
        StudioProjectKind.GoogleAppsScript =>
            "現在はGoogle認可と公開を未実装のため、完成品としては保存しません。",
        StudioProjectKind.Agent =>
            "現在は権限未付与・実行無効の安全な設計として保存します。",
        _ => ""
    };

    private static string Label(StudioProjectKind kind) => kind switch
    {
        StudioProjectKind.DesktopOrWebApp => "Webアプリ（ブラウザー）",
        StudioProjectKind.WindowsApp => "Windowsアプリ（EXE）",
        StudioProjectKind.WindowsGame => "Windowsゲーム",
        StudioProjectKind.GoogleAppsScript => "Google Apps Script",
        StudioProjectKind.Agent => "エージェント機能",
        _ => "選択中の形式"
    };

    private static bool ContainsAny(string value, params string[] signals) =>
        signals.Any(signal => value.Contains(signal, StringComparison.OrdinalIgnoreCase));
}
