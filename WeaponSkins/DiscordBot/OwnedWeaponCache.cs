namespace WeaponSkinsBot;

internal sealed record OwnedWeapons(ulong SteamId, int[] Defs);

/// <summary>Keeps successful suggestions visible while a single background refresh runs.</summary>
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
    // Suggestions must not queue behind skin-save transactions.
    private readonly SemaphoreSlim queries = new(2, 2);
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

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

        // Only a first lookup needs to wait. It keeps running if Discord's deadline expires.
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
                // A new account link must not inherit the previous account's choices.
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
                if (entry.Value != null && entry.Value.SteamId != steamId) continue;
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
                    // A save committed during this query: reload instead of publishing old data.
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
                // A failed query is not an empty loadout; retain the last good result.
                entry.RefreshAfter = clock.GetUtcNow().AddSeconds(2);
                entry.Refresh = null;
            }
            logError(ex);
        }
    }
}
