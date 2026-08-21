# WeaponSkins

A CSSharp plugin for CS2 that gives players full control over how their loadout looks.

## Features

- Weapon skins with float, pattern, name tag and StatTrak
- Knife selector
- Gloves selector
- Agents selector
- Music kit selector
- Pins selector
- Sticker menu with 4 slots per weapon (with optional **VIP-Only** option **OFF** by default) NOTE: !gen,!stickers share same VIP-Only optional setting
- Gen codes: apply a full custom craft with one command from [cs2inspects](https://cs2inspects.com/sticker-customizer)/[csfloat](https://csfloat.com/db)/[steam](https://steamcommunity.com/market/search?appid=730)/[cs2preview](https://cs2preview.com/craft) inspect codes
- Auto skins update after game update
- Everything is saved per player and per team in MySQL
- Optional Discord bot for linking and changing skins from Discord

## Showcase

<table>
<tr>
<td width="50%" align="center">
<img src="docs/Knife_Gloves_Agents.gif" width="100%" alt="Knife,Gloves,Agents Showcase"><br>
<strong>Knife,Gloves,Agents Showcase</strong>
</td>
<td width="50%" align="center">
<img src="docs/Pin_Music.gif" width="100%" alt="Pin,Music Showcase"><br>
<strong>Pin,Music Showcase</strong>
</td>
</tr>
<tr>
<td width="50%" align="center">
<img src="docs/Skins_Stickers.gif" width="100%" alt="Skins,Stickers Showcase"><br>
<strong>Skins,Stickers Showcase</strong>
</td>
<td width="50%" align="center">
<img src="docs/Gen.gif" width="100%" alt="Gen Showcase"><br>
<strong>Gen Showcase</strong>
</td>
</tr>
</table>

## Requirements

- CounterStrikeSharp v1.0.371 or newer
- MySQL (optional but strongly recommended — without it nothing persists across reconnects)
- Disable `FollowCS2ServerGuidelines` path: `addons/counterstrikesharp/configs/core.json`

## Install

1. Download the latest build from Releases
2. Extract the zip into `game/csgo/`
3. Start the server once, then fill the database settings in `configs/plugins/WeaponSkins/WeaponSkins.json`
4. use `css_plugins load WeaponSkins` or Restart the server

## Commands

| Command | Description |
| --- | --- |
| `!ws` | Skins menu |
| `!skins` | Skins menu |
| `!knife` | Knife menu |
| `!gloves` | Gloves menu |
| `!agents` | Agents menu |
| `!music` | Music kit menu |
| `!pin` | Pins menu |
| `!stickers` | Stickers menu |
| `!wear` | Set weapon wear |
| `!seed` | Set weapon seed |
| `!st` | Toggle StatTrak |
| `!g <code>` | Apply a gen code |
| `!link` | Get a Discord link code (only when `link_required` is on) |

Command names can be changed in the config, except `!link`.

## Default Config
<details>
<summary><strong></strong></summary>

```json
{
  "ConfigVersion": 2,
  "api": {
    "base_url": "https://cdn.jsdelivr.net/gh/ByMykel/CSGO-API@main/public/api",
    "language": "en",
    "timeout_seconds": 15
  },
  "database": {
    "host": "",
    "port": 3306,
    "user": "",
    "password": "",
    "name": "",
    "ssl_mode": "Preferred"
  },
  "link_required": false,
  "discord_link": "",
  "stickers": {
    "enabled": true,
    "vip_only": false,
    "vip_flag": "@css/vip"
  },
  "agents": {
    "change_cooldown": 5
  },
  "menu": {
    "freeze_player": false,
    "items_per_page": 4,
    "show_image": true,
    "image_seconds": 2
  },
  "commands": {
    "skins": [
      "ws",
      "skins",
      "skin"
    ],
    "knife": [
      "knife",
      "knives"
    ],
    "gloves": [
      "gloves",
      "glove"
    ],
    "agents": [
      "agents",
      "agent"
    ],
    "music": [
      "music",
      "mk"
    ],
    "pins": [
      "pins",
      "pin"
    ],
    "stickers": [
      "stickers",
      "sticker"
    ],
    "nametag": [
      "nametag",
      "tag"
    ],
    "stattrak": [
      "stattrak",
      "st"
    ],
    "wear": [
      "wear",
      "float"
    ],
    "seed": [
      "seed",
      "pattern"
    ],
    "gen": [
      "g",
      "gen"
    ],
    "reload": [
      "ws_reload"
    ]
  }
}
```
</details>

## Discord Bot

The bot is ready to use, you do not have to build anything: **[WeaponSkins_Bot](https://github.com/Staaar0/WeaponSkins_Bot)**

It gives players two things:

- **Linking** — a player links their Steam account to their Discord account
- **Skins from Discord** — linked players change their skins, knife, gloves, agent, music kit and pin from Discord, and it shows up in game right away

### 1. Turn it on in the plugin

In `WeaponSkins.json`:

```json
"link_required": true,
"discord_link": "https://discord.gg/yourserver"
```

Restart the server once so the plugin creates the linking tables.

With `link_required` on, skin commands are blocked until the player links, and `!link` always works. Leave it `false` if you want the Discord menus without forcing anyone to link.

### 2. Run the bot

1. Download the latest build from the [bot releases](https://github.com/Staaar0/WeaponSkins_Bot/releases), Windows and Linux are both there
2. See discord bot [README](https://github.com/Staaar0/WeaponSkins_Bot/blob/main/README.md) about how to run the bot or making your own

### 3. How players use it

| Where | What they type |
| --- | --- |
| In game | `!link` → the server gives a code like `ABCD-1234` |
| Discord | `/link ABCD-1234` |
| Discord | `/skins`, `/knife`, `/gloves`, `/agents`, `/music`, `/pins`, `/loadout` |

Changes land in game in a second or two. No `!wp`, no reconnect, no respawn, no map change.

Want to write your own bot instead? It only needs the `ws_links`, `ws_link_codes`, `ws_sync_queue` and `ws_permissions` tables, and [WeaponSkins_Bot](https://github.com/Staaar0/WeaponSkins_Bot) is an open example of how to use them.

## NOTES:
- in-game inspect codes from [csfloat](https://csfloat.com/db) and [steam](https://steamcommunity.com/market/search?appid=730) does work but the inspects link contain this `//` if you want to ues inspect links from both you must remove one of the `//`
- full inspect links work with or without `//` in discord bot with !gen command
