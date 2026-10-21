namespace SafeLocalHarness.Core;

public enum CapabilityRisk
{
    LocalRead,
    LocalReversibleWrite,
    NetworkDownload,
    AccountAuthorization,
    ExternalPublish
}

public sealed record CapabilityDecision(bool Allowed, bool RequiresConfirmation, string Explanation);

public static class CapabilityPolicy
{
    public static CapabilityDecision Evaluate(CapabilityRisk risk) => risk switch
    {
        CapabilityRisk.LocalRead => new(true, false, "読み取りだけを行います。"),
        CapabilityRisk.LocalReversibleWrite => new(true, false, "ユーザー領域へ復元可能な変更を行います。"),
        CapabilityRisk.NetworkDownload => new(true, true, "公式配布物を取得します。"),
        CapabilityRisk.AccountAuthorization => new(true, true, "外部アカウントの権限確認が必要です。"),
        CapabilityRisk.ExternalPublish => new(true, true, "外部サービスへ公開する前に内容と送信先を確認します。"),
        _ => new(false, true, "未定義の操作は実行しません。")
    };
}
