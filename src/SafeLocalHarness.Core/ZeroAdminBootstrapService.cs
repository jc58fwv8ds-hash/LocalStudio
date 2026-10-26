namespace SafeLocalHarness.Core;

public sealed record BootstrapDownloadApproval(
    string RuntimeProduct,
    string RuntimeVersion,
    string RuntimeSource,
    string RuntimeSha256,
    string RuntimeDestination,
    string Model,
    long MaximumDownloadBytes,
    DateTimeOffset ApprovedAt);

/// <summary>
/// Phase 1a bootstrap. It is callable only after a user has approved the exact, pinned
/// runtime/model plan. Node, npm and .NET SDK provisioning remain deferred to Phase 1b.
/// </summary>
public sealed class ZeroAdminBootstrapService
{
    private readonly AppPaths _paths;
    private readonly RuntimeArtifactManager _runtimeArtifacts;
    private readonly OfficialRuntimeCatalog _catalog;
    private readonly ILocalModelRuntime _inferenceRuntime;
    private readonly ManagedModelManager _models;

    public ZeroAdminBootstrapService(
        AppPaths paths,
        RuntimeArtifactManager runtimeArtifacts,
        OfficialRuntimeCatalog catalog,
        ILocalModelRuntime inferenceRuntime,
        ManagedModelManager models)
    {
        _paths = paths;
        _runtimeArtifacts = runtimeArtifacts;
        _catalog = catalog;
        _inferenceRuntime = inferenceRuntime;
        _models = models;
    }

    public async Task<BootstrapState> RunUserInitiatedAsync(
        HardwareSnapshot hardware,
        RuntimeResourcePlan resourcePlan,
        BootstrapDownloadApproval approval,
        IProgress<SetupProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var artifact = await _catalog.ResolveOllamaAsync(cancellationToken);
        var model = resourcePlan.RecommendedModel is null
            ? null
            : ManagedModelManager.GetPinnedCandidate(resourcePlan.RecommendedModel);
        ValidateApproval(artifact, model, approval, hardware, resourcePlan);

        var context = new BootstrapContext(_paths, hardware, resourcePlan);
        LocalModelProfile? selectedProfile = null;
        RuntimeArtifactReceipt? runtimeReceipt = null;
        var steps = new IBootstrapStep[]
        {
            new DelegateBootstrapStep(BootstrapStage.EnvironmentProbed, (_, _) =>
            {
                if (UserSpaceGuard.IsElevated())
                    return Task.FromResult(new BootstrapStepResult(false, "Elevated execution is not supported.", RuntimeFailureKind.EnvironmentRestriction));
                if (!OperatingSystem.IsWindows() || !Environment.Is64BitOperatingSystem)
                    return Task.FromResult(new BootstrapStepResult(false, "A 64-bit Windows environment is required.", RuntimeFailureKind.EnvironmentRestriction));
                return Task.FromResult(new BootstrapStepResult(true, "User-space 64-bit Windows environment confirmed."));
            }),
            new DelegateBootstrapStep(BootstrapStage.CoreRuntimeVerified, (_, _) =>
            {
                var executable = Environment.ProcessPath;
                if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
                    return Task.FromResult(new BootstrapStepResult(false, "The LocalStudio executable could not be verified.", RuntimeFailureKind.EnvironmentRestriction));
                return Task.FromResult(new BootstrapStepResult(true, "LocalStudio core process is available without elevation.", ReadyArtifact: "localstudio-core"));
            }),
            new DelegateBootstrapStep(BootstrapStage.InferenceRuntimeResolved, async (_, token) =>
            {
                var verifier = OllamaManager.CreateIsolatedVersionVerifier(root =>
                    Directory.EnumerateFiles(root, "ollama.exe", SearchOption.AllDirectories).FirstOrDefault()
                    ?? Path.Combine(root, "ollama.exe"));
                var result = await _runtimeArtifacts.ProvisionAsync(artifact, verifier, AdaptProgress(progress, "ollama"), token);
                if (!result.Passed) return new(false, result.Summary, result.FailureKind, IsRetryable(result.FailureKind));
                runtimeReceipt = result.Receipt;
                return new(true, "LocalStudio-owned Ollama runtime is verified.", ReadyArtifact: "ollama");
            }),
            new DelegateBootstrapStep(BootstrapStage.ModelProfileSelected, (_, _) =>
                Task.FromResult(new BootstrapStepResult(
                    true,
                    "A pinned model was selected without exceeding the resource plan.",
                    ModelProfileId: model!.ProfileId))),
            new DelegateBootstrapStep(BootstrapStage.RequiredArtifactsDownloaded, async (_, token) =>
            {
                selectedProfile = await _models.PrepareAsync(
                    hardware,
                    resourcePlan,
                    [model!],
                    progress,
                    token);
                return selectedProfile is not null
                    ? new(true, "The pinned model is present and benchmarked.", ReadyArtifact: "model", ModelProfileId: selectedProfile.ProfileId)
                    : new(false, "The pinned model did not meet measured responsiveness and resource limits.", RuntimeFailureKind.EnvironmentRestriction, true);
            }),
            new DelegateBootstrapStep(BootstrapStage.ArtifactsVerified, (_, _) =>
            {
                var runtimeVerified = runtimeReceipt is not null &&
                    runtimeReceipt.Status is RuntimeArtifactStatus.Ready or RuntimeArtifactStatus.RollbackAvailable &&
                    runtimeReceipt.ExpectedSha256.Equals(runtimeReceipt.ActualSha256, StringComparison.OrdinalIgnoreCase);
                var modelVerified = selectedProfile is not null && selectedProfile.VerifiedBlobSha256.Count > 0;
                return Task.FromResult(runtimeVerified && modelVerified
                    ? new BootstrapStepResult(true, "Pinned runtime and model SHA-256 evidence are complete.")
                    : new BootstrapStepResult(false, "Runtime or model verification evidence is incomplete.", RuntimeFailureKind.ChecksumMismatch));
            }),
            new DelegateBootstrapStep(BootstrapStage.SmokePassed, async (_, token) =>
            {
                await _inferenceRuntime.EnsureServerAsync(token);
                var installed = await _inferenceRuntime.GetInstalledModelsAsync(token);
                var ready = ModelSelectionPolicy.FindCompatible(model!.Model, installed) is not null;
                return ready
                    ? new(true, "Owned inference runtime and verified model passed smoke.")
                    : new(false, "The selected model did not pass final runtime smoke.", RuntimeFailureKind.SmokeFailure, true);
            })
        };
        // Every explicit setup request re-runs all verification stages. Step results that later stages depend on
        // (runtime receipt, model profile) live only within this run, so skipping completed stages would leave the
        // final evidence check without inputs. Verified downloads are reused, so a restart does not re-download.
        return await new BootstrapCoordinator(new BootstrapStateStore(_paths), steps, resumeCompletedStages: false)
            .RunAsync(context, cancellationToken);
    }

    public static string ManagedRuntimeDestination(AppPaths paths) =>
        Path.Combine(paths.RuntimeRoot, "ollama", "stable", "content");

    public static long RequiredFreeDiskBytes(long downloadBytes) => checked(downloadBytes * 2);

    private void ValidateApproval(
        RuntimeArtifact artifact,
        ManagedModelCandidate? model,
        BootstrapDownloadApproval approval,
        HardwareSnapshot hardware,
        RuntimeResourcePlan resourcePlan)
    {
        if (resourcePlan.Band == RuntimeResourceBand.DeterministicOnly || model is null)
            throw new RuntimeProvisioningException(RuntimeFailureKind.EnvironmentRestriction, "No pinned model is safe for this resource plan.");
        if (DateTimeOffset.UtcNow - approval.ApprovedAt > TimeSpan.FromHours(1) || approval.ApprovedAt > DateTimeOffset.UtcNow.AddMinutes(1))
            throw new RuntimeProvisioningException(RuntimeFailureKind.InvalidSource, "The setup approval is missing or expired.");

        var destination = ManagedRuntimeDestination(_paths);
        var exactMatch = approval.RuntimeProduct.Equals(artifact.Product, StringComparison.OrdinalIgnoreCase) &&
            approval.RuntimeVersion.Equals(artifact.Version, StringComparison.OrdinalIgnoreCase) &&
            approval.RuntimeSource.Equals(artifact.OfficialSource.AbsoluteUri, StringComparison.Ordinal) &&
            approval.RuntimeSha256.Equals(artifact.ExpectedSha256, StringComparison.OrdinalIgnoreCase) &&
            Path.GetFullPath(approval.RuntimeDestination).Equals(Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase) &&
            approval.Model.Equals(model.Model, StringComparison.OrdinalIgnoreCase);
        if (!exactMatch)
            throw new RuntimeProvisioningException(RuntimeFailureKind.InvalidSource, "The approved setup plan does not match the pinned download plan.");

        var expectedBytes = checked((artifact.ExpectedSizeBytes ?? 0) + model.ApproximateDownloadBytes);
        if (expectedBytes <= 0 || approval.MaximumDownloadBytes < expectedBytes)
            throw new RuntimeProvisioningException(RuntimeFailureKind.InvalidSource, "The approved download size does not cover the pinned plan.");
        if (hardware.FreeDiskBytes < RequiredFreeDiskBytes(expectedBytes))
            throw new RuntimeProvisioningException(RuntimeFailureKind.InsufficientDisk, "There is not enough free disk space for candidate, Stable, and rollback data.");
        if (!AdaptiveRuntimePolicy.ModelFitsBudget(hardware, resourcePlan, model.Model))
            throw new RuntimeProvisioningException(RuntimeFailureKind.EnvironmentRestriction, "Available RAM fell below the approved resource plan.");
    }

    private static IProgress<RuntimeDownloadProgress>? AdaptProgress(IProgress<SetupProgress>? progress, string stage)
    {
        if (progress is null) return null;
        return new Progress<RuntimeDownloadProgress>(value => progress.Report(new SetupProgress(
            stage,
            value.TotalBytes is > 0 ? (double)value.CompletedBytes / value.TotalBytes.Value : null,
            value.Resumed ? $"Resuming {stage} download." : $"Downloading {stage}.",
            value.CompletedBytes,
            value.TotalBytes,
            value.BytesPerSecond,
            value.EstimatedRemaining)));
    }

    private static bool IsRetryable(RuntimeFailureKind failure) => failure is
        RuntimeFailureKind.Dns or RuntimeFailureKind.Proxy or RuntimeFailureKind.Tls or RuntimeFailureKind.Http or
        RuntimeFailureKind.Interrupted or RuntimeFailureKind.InsufficientDisk or RuntimeFailureKind.AccessDenied;
}
