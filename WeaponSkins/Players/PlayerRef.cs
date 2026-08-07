using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;

namespace WeaponSkins;

internal readonly record struct PlayerRef(int Slot, ulong SteamId, uint PawnHandle, CsTeam Team)
{
	public static PlayerRef? Capture(CCSPlayerController player)
	{
		if (!player.IsValid || player.IsBot || player.SteamID == 0 || !player.PawnIsAlive)
			return null;

		var pawn = player.PlayerPawn.Value;
		if (pawn == null || !pawn.IsValid)
			return null;

		return new PlayerRef(player.Slot, player.SteamID, pawn.EntityHandle.Raw, player.Team);
	}

	public CCSPlayerController? Resolve(bool requireAlive = true, bool requireTeam = true)
	{
		var player = Utilities.GetPlayerFromSlot(Slot);
		if (player == null || !player.IsValid || player.IsBot || player.SteamID != SteamId)
			return null;

		if (requireTeam && player.Team != Team)
			return null;

		if (requireAlive && !player.PawnIsAlive)
			return null;

		var pawn = player.PlayerPawn.Value;
		if (pawn == null || !pawn.IsValid || pawn.EntityHandle.Raw != PawnHandle)
			return null;

		return player;
	}
}
