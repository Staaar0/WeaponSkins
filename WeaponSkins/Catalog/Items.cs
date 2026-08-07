using CounterStrikeSharp.API.Modules.Utils;

namespace WeaponSkins;


public sealed record WeaponDef(int DefIndex, string Name, string Category);
public sealed record PaintDef(int Paint, string Name, float MinFloat, float MaxFloat, bool Legacy);
public sealed record KnifeDef(int DefIndex, string Name);
public sealed record GloveDef(int DefIndex, string Name);
public sealed record CharmDef(int Id, string Name);
public sealed record MusicDef(int Id, string Name);

public sealed record StickerDef(int Id, string Name, string TournamentEvent, string TournamentPlayer, string Capsule, string Kind);
public sealed record AgentDef(string Model, string Name, CsTeam Team, string Faction);
public sealed record PinDef(int Id, string Name, string Group);

