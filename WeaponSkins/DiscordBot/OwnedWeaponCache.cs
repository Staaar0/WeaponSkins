namespace WeaponSkinsBot;

internal sealed record OwnedWeapons(ulong SteamId, int[] Defs);

// Keeps suggestions while a background refresh runs.
internal sealed class OwnedWeaponCache(
    Func<ulong, Task<OwnedWeapons>> load,
    Action<Exception> logError,
    CancellationToken cancellationToken,
    TimeProvider? timeProvider = null)
{
    private sealed class Entry
    {
        public OwnedWeapons? Value;
        public ulong? SteamId;
        public Task? Refresh;
        public DateTimeOffset RefreshAfter;
        public DateTimeOffset LastAccess;
        public long Revision;
    }

    private readonly object sync = new();
    private readonly Dictionary<ulong, Entry> entries = [];
    // Keep suggestions separate from skin saves.
    private readonly SemaphoreSlim queries = new(2, 2);
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    public Task StopAsync()
    {
        lock (sync)
            return Task.WhenAll(entries.Values.Select(entry => entry.Refresh).OfType<Task>().ToArray());
    }

    public async Task<OwnedWeapons?> GetAsync(ulong user, TimeSpan wait)
    {
        Entry? entry;
        Task? refresh;
        lock (sync)
        {
            entry = GetEntry(user);
            if (entry == null) return null;
            StartRefresh(user, entry);
            if (entry.Value != null) return entry.Value;
            refresh = entry.Refresh;
        }

        // Only the first lookup waits. A timed-out reply does not cancel the query.
        if (refresh != null)
        {
            try { await refresh.WaitAsync(wait, cancellationToken); }
            catch (TimeoutException) { }
        }
        lock (sync) return entry.Value;
    }

    public void Prime(ulong user, ulong steamId)
    {
        lock (sync)
        {
            var entry = GetEntry(user);
            if (entry == null) return;
            if (entry.SteamId != steamId)
            {
                // Clear suggestions when the linked account changes.
                entry.Value = null;
                entry.SteamId = steamId;
                entry.Revision++;
                entry.RefreshAfter = default;
            }
            StartRefresh(user, entry);
        }
    }

    public void Invalidate(ulong steamId)
    {
        lock (sync)
        {
            foreach (var (user, entry) in entries)
            {
                if (entry.SteamId.HasValue && entry.SteamId.Value != steamId) continue;
                entry.Revision++;
                entry.RefreshAfter = default;
                StartRefresh(user, entry);
            }
        }
    }

    private Entry? GetEntry(ulong user)
    {
        if (!entries.TryGetValue(user, out var entry))
        {
            if (entries.Count >= 512)
            {
                var oldest = entries.Where(x => x.Value.Refresh == null)
                    .MinBy(x => x.Value.LastAccess);
                if (oldest.Value == null) return null;
                entries.Remove(oldest.Key);
            }
            entries[user] = entry = new Entry();
        }
        entry.LastAccess = clock.GetUtcNow();
        return entry;
    }

    private void StartRefresh(ulong user, Entry entry)
    {
        if (cancellationToken.IsCancellationRequested || entry.Refresh != null || entry.RefreshAfter > clock.GetUtcNow())
            return;
        entry.Refresh = Task.Run(() => RefreshAsync(user, entry));
    }

    private async Task RefreshAsync(ulong user, Entry entry)
    {
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                long revision;
                lock (sync) revision = entry.Revision;
                await queries.WaitAsync(cancellationToken);
                OwnedWeapons result;
                try { result = await load(user); }
                finally { queries.Release(); }
                lock (sync)
                {
                    // A save finished during the query. Fetch the new loadout.
                    if (entry.Revision != revision) continue;
                    entry.Value = result;
                    entry.SteamId = result.SteamId;
                    entry.RefreshAfter = clock.GetUtcNow().AddSeconds(20);
                    entry.Refresh = null;
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            lock (sync) entry.Refresh = null;
        }
        catch (Exception ex)
        {
            lock (sync)
            {
                // Keep the last result if the query fails.
                entry.RefreshAfter = clock.GetUtcNow().AddSeconds(2);
                entry.Refresh = null;
            }
            logError(ex);
        }
    }
}
