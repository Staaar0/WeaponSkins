using MySqlConnector;

namespace WeaponSkins;

internal static class DatabaseRetry
{
    private static bool IsTransient(Exception error) =>
        error is MySqlException { IsTransient: true } or TimeoutException;

    // Only used for idempotent loadout assignments, never linking, purchases,
    // arbitrary SQL, or bot ownership. Keep retries inside the player's queue.
    internal static async Task Run(Func<Task> operation, CancellationToken cancellationToken,
        Action? retried = null)
    {
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await operation();
                return;
            }
            catch (Exception error) when (attempt < 2 && !cancellationToken.IsCancellationRequested && IsTransient(error))
            {
                retried?.Invoke();
                await Task.Delay(TimeSpan.FromMilliseconds(attempt == 0 ? 200 : 600), cancellationToken);
            }
        }
    }
}
