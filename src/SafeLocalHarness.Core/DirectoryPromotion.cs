namespace SafeLocalHarness.Core;

/// <summary>
/// Promotes a fully verified staging directory without treating a short antivirus/indexer lock as a failed build.
/// Permanent permission failures still fail closed; retries are allowed only while the source exists and the
/// destination has not appeared.
/// </summary>
internal static class DirectoryPromotion
{
    internal static async Task MoveWithTransientRetryAsync(
        string source,
        string destination,
        CancellationToken cancellationToken,
        Action<string, string>? move = null,
        int maxAttempts = 6,
        Func<int, TimeSpan>? retryDelay = null)
    {
        if (maxAttempts < 1 || maxAttempts > 10) throw new ArgumentOutOfRangeException(nameof(maxAttempts));
        UserSpaceGuard.EnsureAllowed(source);
        UserSpaceGuard.EnsureAllowed(destination);
        move ??= Directory.Move;
        retryDelay ??= attempt => TimeSpan.FromMilliseconds(Math.Min(1_000, 100 * (1 << (attempt - 1))));

        Exception? last = null;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                move(source, destination);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                last = ex;
                // A destination appearing is a collision, and a missing source means promotion state is uncertain.
                // Neither is safe to retry.
                if (attempt == maxAttempts || !Directory.Exists(source) || Directory.Exists(destination)) break;
                await Task.Delay(retryDelay(attempt), cancellationToken).ConfigureAwait(false);
            }
        }

        throw new IOException($"project.promotion-transient-lock: staging could not be promoted after {maxAttempts} attempts", last);
    }
}
