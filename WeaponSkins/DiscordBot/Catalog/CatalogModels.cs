using WeaponSkinsBot.Database;

namespace WeaponSkinsBot.Catalog;

public sealed record WeaponDef(int DefIndex, string Name, string Category, TeamTarget Team = TeamTarget.Both);
public sealed record PaintDef(int Paint, string Name, float MinFloat, float MaxFloat, string Image, string DisplayName = "", string Rarity = "", string RarityColor = "#ffffff");
public sealed record KnifeDef(int DefIndex, string Name);
public sealed record GloveDef(int DefIndex, string Name);
public sealed record AgentDef(string Model, string Name, int Team, string Faction, string Image, string DisplayName = "", string RarityColor = "#ffffff");
public sealed record MusicDef(int Id, string Name, string Image);
public sealed record PinDef(int Id, string Name, string Group, string Image);
public sealed record StickerDef(int Id, string Name, string Image = "");
public sealed record CharmDef(int Id, string Name);
