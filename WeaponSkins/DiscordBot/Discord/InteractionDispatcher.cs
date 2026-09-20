using Microsoft.Extensions.Logging;

namespace WeaponSkinsBot.Discord;

/// <summary>Gateway callbacks return immediately. Work is bounded, observed, and drained on shutdown.</summary>
internal sealed class InteractionDispatcher(ILogger logger, CancellationToken cancellationToken)
{
    private readonly object sync = new();
    private readonly HashSet<Task> running = [];
    private bool stopping;
    private int jobs, rejections;

    public Task Dispatch(Func<Task> action, Func<Task>? busy = null)
    {
        lock (sync)
        {
            if (stopping || cancellationToken.IsCancellationRequested) return Task.CompletedTask;
            bool rejected = jobs >= 64;
            if (rejected && (busy == null || rejections >= 16)) return Task.CompletedTask;
            if (rejected) rejections++; else jobs++;
            Task? task = null;
            task = Task.Run(async () =>
            {
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await (rejected ? busy! : action)();
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
                catch (Exception ex) { logger.LogError(ex, "WeaponSkinsBOT interaction failed"); }
                finally
                {
                    lock (sync)
                    {
                        running.Remove(task!);
                        if (rejected) rejections--; else jobs--;
                    }
                }
            });
            running.Add(task);
        }
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        lock (sync)
        {
            stopping = true;
            return Task.WhenAll(running.ToArray());
        }
    }
}
