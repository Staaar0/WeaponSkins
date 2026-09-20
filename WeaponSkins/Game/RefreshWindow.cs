namespace WeaponSkins;

// Replacements read the latest loadout when they are given. Requests for the
// same scope before that stage do not need another remove/give cycle.
internal sealed class RefreshWindow(string? slot, bool includesKnife, IEnumerable<uint> capturedWeapons)
{
    private bool open = true;
    private readonly HashSet<uint> captured = capturedWeapons.ToHashSet();

    internal bool TryAbsorb(string? requestedSlot, bool needsKnife, IEnumerable<uint> currentWeapons, bool hasPending = false) =>
        open && !hasPending && requestedSlot == slot &&
        (slot == null || !needsKnife || includesKnife) && currentWeapons.All(captured.Contains);

    internal void Close() => open = false;
}
