namespace SafeLocalHarness.Core;

public static class ProjectImprovementRequestBuilder
{
    public static string Build(LocalProjectInfo source, string request)
    {
        if (source.Kind != nameof(StudioProjectKind.DesktopOrWebApp))
            throw new InvalidOperationException("現在はアプリだけを新しい版として改善できます。");
        request = request.Trim();
        if (request.Length == 0) throw new ArgumentException("改善したい内容を入力してください。", nameof(request));
        if (request.Length > 8000) throw new InvalidDataException("改善依頼は8000文字以内にしてください。");
        var features = source.Features.Count == 0
            ? "- 機能情報なし"
            : string.Join("\n", source.Features.Take(8).Select(feature => "- " + Limit(feature, 300)));
        return $"""
            既存アプリを改善した新しい版の設計を作ってください。既存アプリは変更せず、別のプロジェクトとして作成します。
            明示的に削除を求められていない既存機能は維持してください。

            既存アプリ名: {Limit(source.Name, 120)}
            既存の概要: {Limit(source.Summary, 500)}
            既存の機能:
            {features}

            ユーザーの改善依頼:
            {request}
            """;
    }

    private static string Limit(string value, int maximum) => value.Length <= maximum ? value : value[..maximum];
}
