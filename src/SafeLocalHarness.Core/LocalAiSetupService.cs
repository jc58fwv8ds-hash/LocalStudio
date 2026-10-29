using System.Diagnostics;

namespace SafeLocalHarness.Core;

public sealed record SetupDownloadItem(
    string Kind,
    string Product,
    string Version,
    string Source,
    long SizeBytes,
    bool SizeIsApproximate,
    string Destination,
    string License,
    string Sha256);

/// <summary>
/// What the Setup screen shows before anything is downloaded. Building a plan never touches the network.
/// Items are listed only when every gate passed; otherwise <see cref="Items"/> is empty and
/// <see cref="Summary"/> says why, so unsuitable choices are never offered.
/// </summary>
public sealed record SetupDownloadPlan(
    bool CanDownload,
    string Summary,
    IReadOnlyList<SetupDownloadItem> Items,
    IReadOnlyList<string> Reasons,
    RuntimeResourceBand Band,
    string? Model,
    long TotalBytes,
    string? ExternalOllamaPath)
{
    public const string RuntimeKind = "runtime";
    public const string ModelKind = "model";

    public BootstrapDownloadApproval CreateApproval(DateTimeOffset approvedAt)
    {
        if (!CanDownload || Model is null)
            throw new InvalidOperationException("This setup plan cannot be approved.");
        var runtime = Items.Single(item => item.Kind == RuntimeKind);
        return new(runtime.Product, runtime.Version, runtime.Source, runtime.Sha256, runtime.Destination, Model, TotalBytes, approvedAt);
    }
}

/// <summary>What the user sees about memory: Local Studio's own usage, the current judgement and its reasons.</summary>
public sealed record ResourceUsageReport(
    long LocalStudioBytes,
    long OwnedRuntimeBytes,
    MemoryPressureAssessment Assessment);

public interface ILocalAiSetupService
{
    string StorageRoot { get; }
    ResourceMode Mode { get; }
    SetupDownloadPlan CreatePlan();
    ResourceUsageReport GetUsage();
    Task SetModeAsync(ResourceMode mode, CancellationToken cancellationToken = default);
    Task<BootstrapState> RunApprovedAsync(
        BootstrapDownloadApproval approval,
        IProgress<SetupProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The only path that downloads a runtime or model: the Setup screen shows <see cref="CreatePlan"/>,
/// the user approves exactly that plan, and <see cref="ZeroAdminBootstrapService"/> re-validates the
/// approval against the pinned catalog and current resources before any network request.
/// </summary>
public sealed class LocalAiSetupService : ILocalAiSetupService
{
    private readonly AppPaths _paths;
    private readonly OfficialRuntimeCatalog _catalog;
    private readonly ZeroAdminBootstrapService _bootstrap;
    private readonly Func<string?> _detectExternalOllama;
    private readonly ISettingsStore _settings;
    private readonly MemoryPressureMonitor _monitor;
    private readonly ILocalModelRuntime _runtime;
    private readonly ResourceModeState _modeState;

    public LocalAiSetupService(
        AppPaths paths,
        OfficialRuntimeCatalog catalog,
        ZeroAdminBootstrapService bootstrap,
        Func<string?> detectExternalOllama,
        ISettingsStore settings,
        MemoryPressureMonitor monitor,
        ILocalModelRuntime runtime,
        ResourceModeState modeState)
    {
        _paths = paths;
        _catalog = catalog;
        _bootstrap = bootstrap;
        _detectExternalOllama = detectExternalOllama;
        _settings = settings;
        _monitor = monitor;
        _runtime = runtime;
        _modeState = modeState;
    }

    public string StorageRoot => _paths.Root;
    public ResourceMode Mode => _modeState.Current;

    public SetupDownloadPlan CreatePlan()
    {
        var settings = _settings.Load();
        var hardware = SystemHardwareProbe.Capture(_paths);
        var resources = AdaptiveRuntimePolicy.Create(hardware, settings.SelectedModel, _modeState.Current, MemoryRelief.EstimateReclaimable());
        return BuildPlan(_paths, _catalog, hardware, resources, ManagedModelManager.DefaultCandidates, _detectExternalOllama());
    }

    public ResourceUsageReport GetUsage()
    {
        var assessment = _monitor.SampleAndAssess();
        long own;
        using (var self = Process.GetCurrentProcess()) own = self.WorkingSet64;
        // Server plus model runner (all processes in the owned job), not the server alone.
        var runtime = OwnedProcessMetrics.Measure(_runtime.OwnedProcessIds).WorkingSetBytes;
        return new(own, runtime, assessment);
    }

    /// <summary>Focus and Expert are only ever set here, from an explicit user choice (the Setup screen or the chat's mode button). Expert is session-only.</summary>
    public Task SetModeAsync(ResourceMode mode, CancellationToken cancellationToken = default) =>
        _modeState.SetAsync(mode, cancellationToken);

    public async Task<BootstrapState> RunApprovedAsync(
        BootstrapDownloadApproval approval,
        IProgress<SetupProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        // Resources are measured again at the moment of the download, not trusted from when the plan was shown.
        var settings = _settings.Load();
        var hardware = SystemHardwareProbe.Capture(_paths);
        var resources = AdaptiveRuntimePolicy.Create(hardware, settings.SelectedModel, _modeState.Current, MemoryRelief.EstimateReclaimable());
        return await _bootstrap.RunUserInitiatedAsync(hardware, resources, approval, progress, cancellationToken);
    }

    public static SetupDownloadPlan BuildPlan(
        AppPaths paths,
        OfficialRuntimeCatalog catalog,
        HardwareSnapshot hardware,
        RuntimeResourcePlan resources,
        IReadOnlyList<ManagedModelCandidate> candidates,
        string? externalOllamaPath)
    {
        var reasons = resources.Reasons.ToList();
        SetupDownloadPlan Blocked(string summary) =>
            new(false, summary, [], reasons, resources.Band, null, 0, externalOllamaPath);

        if (UserSpaceGuard.IsElevated())
            return Blocked("管理者権限では準備を行いません。通常の方法で起動し直してください。");
        if (resources.Band == RuntimeResourceBand.DeterministicOnly || resources.RecommendedModel is null)
            return Blocked("空きメモリまたはディスクが安全な基準を下回っているため、AIの準備は表示しません。AIを使わない機能はそのまま使えます。");

        var runtimeStatus = catalog.GetOllamaStatus();
        if (runtimeStatus.Artifact is not { } artifact)
            return Blocked("Ollamaの公式な版・取得元・SHA-256がまだ確定していません。ダウンロードも通信も行いません。");
        if (artifact.ExpectedSizeBytes is not { } runtimeBytes || runtimeBytes <= 0)
            return Blocked("Ollamaのダウンロードサイズが未確定のため、事前に表示できません。ダウンロードは行いません。");

        var model = candidates.FirstOrDefault(candidate =>
            candidate.Model.Equals(resources.RecommendedModel, StringComparison.OrdinalIgnoreCase) &&
            candidate.MinimumBand <= resources.Band &&
            candidate.ExpectedManifestSha256 is { Length: 64 } digest && digest.All(Uri.IsHexDigit));
        if (model is null)
            return Blocked("このPCに適した、版の固定済みモデルがまだ設定されていません。ダウンロードも通信も行いません。");

        var total = checked(runtimeBytes + model.ApproximateDownloadBytes);
        if (hardware.FreeDiskBytes < ZeroAdminBootstrapService.RequiredFreeDiskBytes(total))
            return Blocked("ダウンロード・検証・巻き戻し用の保持に必要な空きディスクが足りません。");
        if (!AdaptiveRuntimePolicy.ModelFitsBudget(hardware, resources, model.Model))
            return Blocked("いまの空きメモリでは、OSと他の作業のための予備を残したままこのモデルを動かせません。");

        IReadOnlyList<SetupDownloadItem> items =
        [
            new(SetupDownloadPlan.RuntimeKind, artifact.Product, artifact.Version, artifact.OfficialSource.AbsoluteUri,
                runtimeBytes, false, ZeroAdminBootstrapService.ManagedRuntimeDestination(paths),
                artifact.License, artifact.ExpectedSha256),
            new(SetupDownloadPlan.ModelKind, model.Model, model.Quantization, model.OfficialSource,
                model.ApproximateDownloadBytes, true, paths.ModelRoot, model.License, model.ExpectedManifestSha256!)
        ];
        return new(true, "内容を確認のうえ、ボタンを押すとダウンロードを開始します。", items, reasons, resources.Band, model.Model, total, externalOllamaPath);
    }
}
