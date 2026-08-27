using System.Security.Cryptography;
using System.Text;
using MySqlConnector;
using WeaponSkinsBot.Catalog;

namespace WeaponSkinsBot.Database;

public sealed class WeaponSkinsDatabase
{
	private const int TableMissing = 1146;
	private const int ColumnMissing = 1054;
	private readonly global::WeaponSkins.Database database;

	public WeaponSkinsDatabase(global::WeaponSkins.Database database)
	{
		this.database = database;
	}

    private async Task<MySqlConnection> OpenAsync(CancellationToken cancellationToken = default)
    {
		return await database.Open(cancellationToken);
	}

    public async Task<(bool Success, string Message, ulong SteamId)> CompleteLinkAsync(ulong discordId, string code)
    {
        var normalized = NormalizeCode(code);
        if (normalized.Length != 8)
            return (false, "Invalid link code.", 0);

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        await using var connection = await OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        try
        {
            ulong steamId;
            await using (var find = connection.CreateCommand())
            {
                find.Transaction = transaction;
                find.CommandText = "SELECT steamid FROM ws_link_codes WHERE code_hash = @hash AND expires_at > NOW() LIMIT 1 FOR UPDATE;";
                find.Parameters.AddWithValue("@hash", hash);
                var value = await find.ExecuteScalarAsync();
                if (value == null)
                {
                    await transaction.RollbackAsync();
                    return (false, "Invalid or expired link code.", 0);
                }
                steamId = Convert.ToUInt64(value);
            }

            var links = new List<(ulong SteamId, ulong DiscordId)>();
            await using (var existing = connection.CreateCommand())
            {
                existing.Transaction = transaction;
                existing.CommandText = "SELECT steamid, discord_id FROM ws_links WHERE steamid = @sid OR discord_id = @did FOR UPDATE;";
                existing.Parameters.AddWithValue("@sid", steamId);
                existing.Parameters.AddWithValue("@did", discordId);
                await using var reader = await existing.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                    links.Add((reader.GetUInt64(0), reader.GetUInt64(1)));
            }

            foreach (var link in links)
            {
                if (link.SteamId == steamId && link.DiscordId == discordId)
                {
                    await DeleteCode(connection, transaction, steamId);
                    await transaction.CommitAsync();
                    return (true, "Your Steam account is already linked.", steamId);
                }
                if (link.DiscordId == discordId)
                {
                    await transaction.RollbackAsync();
                    return (false, "Your Discord account is already linked to another Steam account.", 0);
                }
                if (link.SteamId == steamId)
                {
                    await transaction.RollbackAsync();
                    return (false, "That Steam account is already linked to another Discord account.", 0);
                }
            }

            await using (var insert = connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = "INSERT INTO ws_links (steamid, discord_id) VALUES (@sid, @did);";
                insert.Parameters.AddWithValue("@sid", steamId);
                insert.Parameters.AddWithValue("@did", discordId);
                await insert.ExecuteNonQueryAsync();
            }

            await DeleteCode(connection, transaction, steamId);
            await transaction.CommitAsync();
            return (true, "Steam account linked successfully.", steamId);
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    public async Task<bool> UnlinkAsync(ulong discordId)
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM ws_links WHERE discord_id = @did;";
        command.Parameters.AddWithValue("@did", discordId);
        return await command.ExecuteNonQueryAsync() > 0;
    }

    public async Task<ulong?> GetSteamIdAsync(ulong discordId)
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT steamid FROM ws_links WHERE discord_id = @did LIMIT 1;";
        command.Parameters.AddWithValue("@did", discordId);
        var value = await command.ExecuteScalarAsync();
        return value == null ? null : Convert.ToUInt64(value);
    }

    public async Task SetSkinAsync(ulong steamId, TeamTarget target, int defIndex, PaintDef? paint, bool knife)
    {
        await using var connection = await OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        try
        {
            foreach (var team in target.Teams())
            {
                if (knife)
                    await WriteKnife(connection, transaction, steamId, team, defIndex);

                var paintId = paint?.Paint ?? 0;
                var minimum = paint == null ? (knife ? 0.01f : 0.000001f) : MinimumWear(defIndex, paint, knife);
                await EnsureWeapon(connection, transaction, steamId, team, defIndex, paintId, minimum);
                await WriteEquipped(connection, transaction, steamId, team, defIndex, paintId);
            }
            await QueueSync(connection, transaction, steamId);
            await transaction.CommitAsync();
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    /// <summary>
    /// Writes a whole build in one go: skin, pattern, wear, StatTrak and stickers.
    /// One transaction and one sync row, so the player sees the finished weapon
    /// in game instead of it changing under them step by step.
    /// </summary>
    public async Task ApplyBuildAsync(
        ulong steamId,
        TeamTarget target,
        int defIndex,
        PaintDef? paint,
        ItemKind kind,
        int seed,
        float? wear,
        bool statTrak,
        IReadOnlyDictionary<int, int>? stickers,
        bool clearStickers = false)
    {
        var paintId = paint?.Paint ?? 0;
        var floor = kind == ItemKind.Knife ? 0.01f : 0.000001f;
        if (paint != null)
            floor = Math.Min(paint.MaxFloat, Math.Max(paint.MinFloat, floor));
        var ceiling = paint?.MaxFloat ?? 1f;
        var finalWear = Math.Clamp(wear ?? floor, floor, ceiling);
        var pattern = Math.Clamp(seed, 0, 1000);

        await using var connection = await OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        try
        {
            foreach (var team in target.Teams())
            {
                if (kind == ItemKind.Knife)
                    await WriteKnife(connection, transaction, steamId, team, defIndex);

                if (kind == ItemKind.Glove)
                {
                    await using var glove = connection.CreateCommand();
                    glove.Transaction = transaction;
                    glove.CommandText = "INSERT INTO ws_gloves (steamid, team, defindex, paint) VALUES (@sid, @team, @def, @paint) ON DUPLICATE KEY UPDATE defindex = @def, paint = @paint;";
                    AddWeaponKey(glove, steamId, team, defIndex, paintId);
                    await glove.ExecuteNonQueryAsync();
                }

                await WriteBuiltWeapon(connection, transaction, steamId, team, defIndex, paintId, finalWear, pattern, statTrak);

                if (kind != ItemKind.Glove)
                    await WriteEquipped(connection, transaction, steamId, team, defIndex, paintId);

                if (kind == ItemKind.Weapon && clearStickers)
                    await ClearStickersAndCharm(connection, transaction, steamId, team, defIndex, paintId);

                if (kind == ItemKind.Weapon && stickers is { Count: > 0 })
                    await WriteChosenStickers(connection, transaction, steamId, team, defIndex, paintId, stickers);
            }

            await QueueSync(connection, transaction, steamId);
            await transaction.CommitAsync();
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    private static async Task WriteBuiltWeapon(MySqlConnection connection, MySqlTransaction transaction, ulong steamId, int team, int defIndex, int paint, float wear, int seed, bool statTrak)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        // The name tag is left alone, and an existing StatTrak count is kept
        // instead of being reset to zero.
        command.CommandText = statTrak
            ? "INSERT INTO ws_weapons (steamid, team, defindex, paint, wear, seed, nametag, stattrak) VALUES (@sid, @team, @def, @paint, @wear, @seed, NULL, 0) ON DUPLICATE KEY UPDATE wear = @wear, seed = @seed, stattrak = CASE WHEN stattrak < 0 THEN 0 ELSE stattrak END;"
            : "INSERT INTO ws_weapons (steamid, team, defindex, paint, wear, seed, nametag, stattrak) VALUES (@sid, @team, @def, @paint, @wear, @seed, NULL, -1) ON DUPLICATE KEY UPDATE wear = @wear, seed = @seed, stattrak = -1;";
        AddWeaponKey(command, steamId, team, defIndex, paint);
        command.Parameters.AddWithValue("@wear", wear);
        command.Parameters.AddWithValue("@seed", seed);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Wipes every sticker slot and the charm, the same as "Remove all" in game.
    /// This is the only place the bot touches slots a gen code filled, a normal
    /// sticker change leaves them alone and lets the plugin sort them out.
    /// </summary>
    private static async Task ClearStickersAndCharm(MySqlConnection connection, MySqlTransaction transaction, ulong steamId, int team, int defIndex, int paint)
    {
        await using (var stickers = connection.CreateCommand())
        {
            stickers.Transaction = transaction;
            stickers.CommandText = "DELETE FROM ws_stickers WHERE steamid = @sid AND team = @team AND defindex = @def AND paint = @paint;";
            AddWeaponKey(stickers, steamId, team, defIndex, paint);
            await stickers.ExecuteNonQueryAsync();
        }

        await using var charm = connection.CreateCommand();
        charm.Transaction = transaction;
        charm.CommandText = "DELETE FROM ws_charms WHERE steamid = @sid AND team = @team AND defindex = @def AND paint = @paint;";
        AddWeaponKey(charm, steamId, team, defIndex, paint);
        await charm.ExecuteNonQueryAsync();
    }

    private static async Task WriteChosenStickers(MySqlConnection connection, MySqlTransaction transaction, ulong steamId, int team, int defIndex, int paint, IReadOnlyDictionary<int, int> stickers)
    {
        foreach (var (slot, id) in stickers)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            if (id <= 0)
            {
                command.CommandText = "DELETE FROM ws_stickers WHERE steamid = @sid AND team = @team AND defindex = @def AND paint = @paint AND slot = @slot;";
            }
            else
            {
                command.CommandText = "INSERT INTO ws_stickers (steamid, team, defindex, paint, slot, sticker, wear, scale, rotation, offset_x, offset_y, schema_slot) VALUES (@sid, @team, @def, @paint, @slot, @sticker, 0, 1, 0, 0, 0, -1) ON DUPLICATE KEY UPDATE sticker = @sticker, wear = 0, scale = 1, rotation = 0, offset_x = 0, offset_y = 0, schema_slot = -1;";
                command.Parameters.AddWithValue("@sticker", id);
            }
            AddWeaponKey(command, steamId, team, defIndex, paint);
            command.Parameters.AddWithValue("@slot", slot);
            await command.ExecuteNonQueryAsync();
        }
    }

    public async Task ResetKnifeAsync(ulong steamId, TeamTarget target)
    {
        await using var connection = await OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        foreach (var team in target.Teams())
            await WriteKnife(connection, transaction, steamId, team, 0);
        await QueueSync(connection, transaction, steamId);
        await transaction.CommitAsync();
    }

    public async Task SetGlovesAsync(ulong steamId, TeamTarget target, int defIndex, PaintDef? paint)
    {
        await using var connection = await OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        try
        {
            foreach (var team in target.Teams())
            {
                if (defIndex <= 0 || paint == null)
                {
                    await using var remove = connection.CreateCommand();
                    remove.Transaction = transaction;
                    remove.CommandText = "DELETE FROM ws_gloves WHERE steamid = @sid AND team = @team;";
                    remove.Parameters.AddWithValue("@sid", steamId);
                    remove.Parameters.AddWithValue("@team", team);
                    await remove.ExecuteNonQueryAsync();
                    continue;
                }

                await using (var glove = connection.CreateCommand())
                {
                    glove.Transaction = transaction;
                    glove.CommandText = "INSERT INTO ws_gloves (steamid, team, defindex, paint) VALUES (@sid, @team, @def, @paint) ON DUPLICATE KEY UPDATE defindex = @def, paint = @paint;";
                    glove.Parameters.AddWithValue("@sid", steamId);
                    glove.Parameters.AddWithValue("@team", team);
                    glove.Parameters.AddWithValue("@def", defIndex);
                    glove.Parameters.AddWithValue("@paint", paint.Paint);
                    await glove.ExecuteNonQueryAsync();
                }
                await EnsureWeapon(connection, transaction, steamId, team, defIndex, paint.Paint, Math.Max(paint.MinFloat, 0.000001f));
            }
            await QueueSync(connection, transaction, steamId);
            await transaction.CommitAsync();
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    public async Task SetAgentAsync(ulong steamId, int team, AgentDef? agent)
    {
        await using var connection = await OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        if (agent == null)
        {
            await using var remove = connection.CreateCommand();
            remove.Transaction = transaction;
            remove.CommandText = "DELETE FROM ws_agents WHERE steamid = @sid AND team = @team;";
            remove.Parameters.AddWithValue("@sid", steamId);
            remove.Parameters.AddWithValue("@team", team);
            await remove.ExecuteNonQueryAsync();
        }
        else
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO ws_agents (steamid, team, model) VALUES (@sid, @team, @model) ON DUPLICATE KEY UPDATE model = @model;";
            command.Parameters.AddWithValue("@sid", steamId);
            command.Parameters.AddWithValue("@team", team);
            command.Parameters.AddWithValue("@model", agent.Model);
            await command.ExecuteNonQueryAsync();
        }
        await QueueSync(connection, transaction, steamId);
        await transaction.CommitAsync();
    }

    public async Task SetMusicAsync(ulong steamId, int kit)
    {
        await using var connection = await OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = kit > 0
                ? "INSERT INTO ws_music (steamid, kit) VALUES (@sid, @value) ON DUPLICATE KEY UPDATE kit = @value;"
                : "DELETE FROM ws_music WHERE steamid = @sid;";
            command.Parameters.AddWithValue("@sid", steamId);
            if (kit > 0)
                command.Parameters.AddWithValue("@value", kit);
            await command.ExecuteNonQueryAsync();
        }
        await QueueSync(connection, transaction, steamId);
        await transaction.CommitAsync();
    }

    public async Task SetPinAsync(ulong steamId, int pin)
    {
        await using var connection = await OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = pin > 0
                ? "INSERT INTO ws_pins (steamid, pin) VALUES (@sid, @value) ON DUPLICATE KEY UPDATE pin = @value;"
                : "DELETE FROM ws_pins WHERE steamid = @sid;";
            command.Parameters.AddWithValue("@sid", steamId);
            if (pin > 0)
                command.Parameters.AddWithValue("@value", pin);
            await command.ExecuteNonQueryAsync();
        }
        await QueueSync(connection, transaction, steamId);
        await transaction.CommitAsync();
    }

    public async Task<bool> SetWearAsync(ulong steamId, TeamTarget target, int defIndex, float wear, CatalogService catalog)
    {
        return await UpdateActiveWeapons(steamId, target, defIndex, async (connection, transaction, team, paint) =>
        {
            var def = catalog.FindPaint(defIndex, paint);
            var minimum = def == null ? 0.000001f : Math.Max(def.MinFloat, 0.000001f);
            if (defIndex is 42 or 59 || defIndex >= 500)
                minimum = Math.Max(minimum, 0.01f);
            var maximum = def?.MaxFloat ?? 1f;
            var value = Math.Clamp(wear, minimum, maximum);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE ws_weapons SET wear = @value WHERE steamid = @sid AND team = @team AND defindex = @def AND paint = @paint;";
            AddWeaponKey(command, steamId, team, defIndex, paint);
            command.Parameters.AddWithValue("@value", value);
            await command.ExecuteNonQueryAsync();
        });
    }

    public async Task<bool> SetSeedAsync(ulong steamId, TeamTarget target, int defIndex, int seed)
    {
        return await UpdateActiveWeapons(steamId, target, defIndex, async (connection, transaction, team, paint) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE ws_weapons SET seed = @value WHERE steamid = @sid AND team = @team AND defindex = @def AND paint = @paint;";
            AddWeaponKey(command, steamId, team, defIndex, paint);
            command.Parameters.AddWithValue("@value", seed);
            await command.ExecuteNonQueryAsync();
        });
    }

    public async Task<bool> SetNameTagAsync(ulong steamId, TeamTarget target, int defIndex, string? tag)
    {
        return await UpdateActiveWeapons(steamId, target, defIndex, async (connection, transaction, team, paint) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE ws_weapons SET nametag = @value WHERE steamid = @sid AND team = @team AND defindex = @def AND paint = @paint;";
            AddWeaponKey(command, steamId, team, defIndex, paint);
            command.Parameters.AddWithValue("@value", (object?)tag ?? DBNull.Value);
            await command.ExecuteNonQueryAsync();
        });
    }

    public async Task<bool> SetStatTrakAsync(ulong steamId, TeamTarget target, int defIndex, bool enabled)
    {
        return await UpdateActiveWeapons(steamId, target, defIndex, async (connection, transaction, team, paint) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = enabled
                ? "UPDATE ws_weapons SET stattrak = CASE WHEN stattrak < 0 THEN 0 ELSE stattrak END WHERE steamid = @sid AND team = @team AND defindex = @def AND paint = @paint;"
                : "UPDATE ws_weapons SET stattrak = -1 WHERE steamid = @sid AND team = @team AND defindex = @def AND paint = @paint;";
            AddWeaponKey(command, steamId, team, defIndex, paint);
            await command.ExecuteNonQueryAsync();
        });
    }

    /// <summary>
    /// Sticker slots this bot and the in-game menu both manage: 1-4, indexes 0-3. A gen
    /// code can fill a fifth, which neither can show or clear, so any sticker edit drops
    /// it - the same rule the plugin's own sticker menu applies.
    /// </summary>
    public const int ManagedStickerSlots = 4;

    public Task<bool> SetStickerAsync(ulong steamId, TeamTarget target, int defIndex, int slot, StickerDef? sticker)
    {
        return SetStickersAsync(steamId, target, defIndex, [slot], sticker);
    }

    /// <summary>
    /// Writes every named slot in one transaction, so filling all four queues a single
    /// sync row and the player's weapon redraws once instead of once per slot.
    /// </summary>
    public async Task<bool> SetStickersAsync(ulong steamId, TeamTarget target, int defIndex, IReadOnlyList<int> slots, StickerDef? sticker)
    {
        return await UpdateActiveWeapons(steamId, target, defIndex, async (connection, transaction, team, paint) =>
        {
            foreach (var slot in slots)
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                if (sticker == null)
                {
                    command.CommandText = "DELETE FROM ws_stickers WHERE steamid = @sid AND team = @team AND defindex = @def AND paint = @paint AND slot = @slot;";
                }
                else
                {
                    command.CommandText = "INSERT INTO ws_stickers (steamid, team, defindex, paint, slot, sticker, wear, scale, rotation, offset_x, offset_y, schema_slot) VALUES (@sid, @team, @def, @paint, @slot, @sticker, 0, 1, 0, 0, 0, -1) ON DUPLICATE KEY UPDATE sticker = @sticker, wear = 0, scale = 1, rotation = 0, offset_x = 0, offset_y = 0, schema_slot = -1;";
                    command.Parameters.AddWithValue("@sticker", sticker.Id);
                }
                AddWeaponKey(command, steamId, team, defIndex, paint);
                command.Parameters.AddWithValue("@slot", slot);
                await command.ExecuteNonQueryAsync();
            }

            await DropUnmanagedStickers(connection, transaction, steamId, team, defIndex, paint);
        });
    }

    private static async Task DropUnmanagedStickers(MySqlConnection connection, MySqlTransaction transaction, ulong steamId, int team, int defIndex, int paint)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM ws_stickers WHERE steamid = @sid AND team = @team AND defindex = @def AND paint = @paint AND slot >= @managed;";
        AddWeaponKey(command, steamId, team, defIndex, paint);
        command.Parameters.AddWithValue("@managed", ManagedStickerSlots);
        await command.ExecuteNonQueryAsync();
    }

    public sealed class LoadoutSummary
    {
        public Dictionary<int, int> Knives { get; } = [];
        public Dictionary<int, (int Def, int Paint)> Gloves { get; } = [];
        public Dictionary<int, string> Agents { get; } = [];
        public Dictionary<int, List<(int Def, int Paint)>> Weapons { get; } = [];
        public int Music;
        public int Pin;
    }

    public async Task<LoadoutSummary> GetLoadoutAsync(ulong steamId)
    {
        var result = new LoadoutSummary();
        await using var connection = await OpenAsync();

        await Read(connection, "SELECT team, defindex FROM ws_knives WHERE steamid = @sid;", steamId,
            reader => result.Knives[reader.GetInt32(0)] = reader.GetInt32(1));

        await Read(connection, "SELECT team, defindex, paint FROM ws_gloves WHERE steamid = @sid;", steamId,
            reader => result.Gloves[reader.GetInt32(0)] = (reader.GetInt32(1), reader.GetInt32(2)));

        await Read(connection, "SELECT team, model FROM ws_agents WHERE steamid = @sid;", steamId,
            reader => result.Agents[reader.GetInt32(0)] = reader.GetString(1));

        await Read(connection, "SELECT team, defindex, paint FROM ws_equipped WHERE steamid = @sid AND paint > 0 ORDER BY defindex;", steamId,
            reader =>
            {
                var team = reader.GetInt32(0);
                if (!result.Weapons.TryGetValue(team, out var list))
                    result.Weapons[team] = list = [];
                list.Add((reader.GetInt32(1), reader.GetInt32(2)));
            });

        await Read(connection, "SELECT kit FROM ws_music WHERE steamid = @sid;", steamId,
            reader => result.Music = reader.GetInt32(0));

        await Read(connection, "SELECT pin FROM ws_pins WHERE steamid = @sid;", steamId,
            reader => result.Pin = reader.GetInt32(0));

        return result;
    }

    /// <summary>Clears every sticker and the charm on the equipped skin.</summary>
    public async Task<bool> ClearStickersAsync(ulong steamId, TeamTarget target, int defIndex)
    {
        return await UpdateActiveWeapons(steamId, target, defIndex, async (connection, transaction, team, paint) =>
            await ClearStickersAndCharm(connection, transaction, steamId, team, defIndex, paint));
    }

    private static async Task Read(MySqlConnection connection, string sql, ulong steamId, Action<MySqlDataReader> row)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@sid", steamId);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            row(reader);
    }

    /// <summary>What the plugin says a player may do. Both true on an older plugin.</summary>
    public readonly record struct Permissions(bool Stickers, bool Gen);

    /// <summary>
    /// Asks the plugin what this player may do, so `vip_only` in WeaponSkins.json
    /// applies here as well. The VIP flag lives in the game server's admin files,
    /// which the bot cannot read, so the plugin writes its own answer into
    /// ws_permissions on every join.
    /// </summary>
    public async Task<Permissions> PermissionsAsync(ulong steamId)
    {
        try
        {
            await using var connection = await OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT stickers, gen FROM ws_permissions WHERE steamid = @sid LIMIT 1;";
            command.Parameters.AddWithValue("@sid", steamId);

            await using var reader = await command.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
                return new Permissions(false, false);

            return new Permissions(reader.GetInt32(0) != 0, reader.GetInt32(1) != 0);
        }
        catch (MySqlException ex) when ((int)ex.ErrorCode is TableMissing or ColumnMissing)
        {
            // An older plugin, so there is nothing to go on. Restrict nothing.
            return new Permissions(true, true);
        }
    }

    public async Task<string> ApplyGenAsync(ulong steamId, TeamTarget target, EconItemPreview item, CatalogService catalog, bool allowStickers = true)
    {
        var isKnife = catalog.Knives.Any(value => value.DefIndex == item.DefIndex);
        var isGlove = catalog.Gloves.Any(value => value.DefIndex == item.DefIndex);
        var isWeapon = catalog.Paints.ContainsKey(item.DefIndex);
        if (!isKnife && !isGlove && !isWeapon)
            throw new InvalidOperationException("Unknown item in inspect code.");

        var paintDef = catalog.FindPaint(item.DefIndex, item.PaintIndex);
        var minimum = isKnife ? 0.01f : 0.000001f;
        if (paintDef != null)
            minimum = Math.Max(minimum, paintDef.MinFloat);
        var wear = item.PaintWear > 0 ? item.PaintWear : minimum;
        if (paintDef != null)
            wear = Math.Clamp(wear, Math.Max(minimum, paintDef.MinFloat), paintDef.MaxFloat);
        else
            wear = Math.Clamp(wear, minimum, 1f);

        await using var connection = await OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        try
        {
            foreach (var team in target.Teams())
            {
                if (isKnife)
                    await WriteKnife(connection, transaction, steamId, team, item.DefIndex);

                if (isGlove)
                {
                    await using var glove = connection.CreateCommand();
                    glove.Transaction = transaction;
                    glove.CommandText = "INSERT INTO ws_gloves (steamid, team, defindex, paint) VALUES (@sid, @team, @def, @paint) ON DUPLICATE KEY UPDATE defindex = @def, paint = @paint;";
                    AddWeaponKey(glove, steamId, team, item.DefIndex, item.PaintIndex);
                    await glove.ExecuteNonQueryAsync();
                }

                await WriteGeneratedWeapon(connection, transaction, steamId, team, item, wear);
                if (!isGlove)
                    await WriteEquipped(connection, transaction, steamId, team, item.DefIndex, item.PaintIndex);

                if (isKnife)
                {
                    await ClearGeneratedExtras(connection, transaction, steamId, team, item.DefIndex, item.PaintIndex);
                }
                else if (!isGlove)
                {
                    // Without the sticker permission the code still applies, it
                    // just arrives bare, exactly like the in-game command.
                    if (allowStickers)
                    {
                        await WriteGeneratedStickers(connection, transaction, steamId, team, item.DefIndex, item.PaintIndex, item.Stickers);
                        await WriteGeneratedCharm(connection, transaction, steamId, team, item.DefIndex, item.PaintIndex, item.Keychains);
                    }
                    else
                    {
                        await ClearGeneratedExtras(connection, transaction, steamId, team, item.DefIndex, item.PaintIndex);
                    }
                }
            }

            await QueueSync(connection, transaction, steamId);
            await transaction.CommitAsync();
            return paintDef?.Name ?? catalog.WeaponName(item.DefIndex);
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    private async Task<bool> UpdateActiveWeapons(ulong steamId, TeamTarget target, int defIndex, Func<MySqlConnection, MySqlTransaction, int, int, Task> action)
    {
        await using var connection = await OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var changed = false;
        try
        {
            foreach (var team in target.Teams())
            {
                var paint = await ActivePaint(connection, transaction, steamId, team, defIndex);
                if (!paint.HasValue || paint.Value <= 0)
                    continue;
                await action(connection, transaction, team, paint.Value);
                changed = true;
            }
            if (changed)
                await QueueSync(connection, transaction, steamId);
            await transaction.CommitAsync();
            return changed;
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    private static async Task<int?> ActivePaint(MySqlConnection connection, MySqlTransaction transaction, ulong steamId, int team, int defIndex)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT paint FROM ws_equipped WHERE steamid = @sid AND team = @team AND defindex = @def LIMIT 1;";
        command.Parameters.AddWithValue("@sid", steamId);
        command.Parameters.AddWithValue("@team", team);
        command.Parameters.AddWithValue("@def", defIndex);
        var value = await command.ExecuteScalarAsync();
        if (value != null)
            return Convert.ToInt32(value);

        await using var glove = connection.CreateCommand();
        glove.Transaction = transaction;
        glove.CommandText = "SELECT paint FROM ws_gloves WHERE steamid = @sid AND team = @team AND defindex = @def LIMIT 1;";
        glove.Parameters.AddWithValue("@sid", steamId);
        glove.Parameters.AddWithValue("@team", team);
        glove.Parameters.AddWithValue("@def", defIndex);
        value = await glove.ExecuteScalarAsync();
        return value == null ? null : Convert.ToInt32(value);
    }

    private static async Task EnsureWeapon(MySqlConnection connection, MySqlTransaction transaction, ulong steamId, int team, int defIndex, int paint, float minimumWear)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO ws_weapons (steamid, team, defindex, paint, wear, seed, nametag, stattrak) VALUES (@sid, @team, @def, @paint, @wear, 0, NULL, -1) ON DUPLICATE KEY UPDATE wear = GREATEST(wear, @wear);";
        AddWeaponKey(command, steamId, team, defIndex, paint);
        command.Parameters.AddWithValue("@wear", minimumWear);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task WriteEquipped(MySqlConnection connection, MySqlTransaction transaction, ulong steamId, int team, int defIndex, int paint)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO ws_equipped (steamid, team, defindex, paint) VALUES (@sid, @team, @def, @paint) ON DUPLICATE KEY UPDATE paint = @paint;";
        AddWeaponKey(command, steamId, team, defIndex, paint);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task WriteKnife(MySqlConnection connection, MySqlTransaction transaction, ulong steamId, int team, int defIndex)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.Parameters.AddWithValue("@sid", steamId);
        command.Parameters.AddWithValue("@team", team);
        if (defIndex > 0)
        {
            command.CommandText = "INSERT INTO ws_knives (steamid, team, defindex) VALUES (@sid, @team, @def) ON DUPLICATE KEY UPDATE defindex = @def;";
            command.Parameters.AddWithValue("@def", defIndex);
        }
        else
        {
            command.CommandText = "DELETE FROM ws_knives WHERE steamid = @sid AND team = @team;";
        }
        await command.ExecuteNonQueryAsync();
    }

    private static async Task WriteGeneratedWeapon(MySqlConnection connection, MySqlTransaction transaction, ulong steamId, int team, EconItemPreview item, float wear)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO ws_weapons (steamid, team, defindex, paint, wear, seed, nametag, stattrak) VALUES (@sid, @team, @def, @paint, @wear, @seed, @tag, @stattrak) ON DUPLICATE KEY UPDATE wear = @wear, seed = @seed, nametag = @tag, stattrak = @stattrak;";
        AddWeaponKey(command, steamId, team, item.DefIndex, item.PaintIndex);
        command.Parameters.AddWithValue("@wear", wear);
        command.Parameters.AddWithValue("@seed", Math.Clamp(item.PaintSeed, 0, 1000));
        command.Parameters.AddWithValue("@tag", item.CustomName is { Length: > 0 } name ? (name.Length > 64 ? name[..64] : name) : DBNull.Value);
        command.Parameters.AddWithValue("@stattrak", item.StatTrak ? Math.Max(0, item.KillEaterValue) : -1);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task ClearGeneratedExtras(MySqlConnection connection, MySqlTransaction transaction, ulong steamId, int team, int defIndex, int paint)
    {
        await using (var stickers = connection.CreateCommand())
        {
            stickers.Transaction = transaction;
            stickers.CommandText = "DELETE FROM ws_stickers WHERE steamid = @sid AND team = @team AND defindex = @def AND paint = @paint;";
            AddWeaponKey(stickers, steamId, team, defIndex, paint);
            await stickers.ExecuteNonQueryAsync();
        }

        await using var charms = connection.CreateCommand();
        charms.Transaction = transaction;
        charms.CommandText = "DELETE FROM ws_charms WHERE steamid = @sid AND team = @team AND defindex = @def AND paint = @paint;";
        AddWeaponKey(charms, steamId, team, defIndex, paint);
        await charms.ExecuteNonQueryAsync();
    }

    private static async Task WriteGeneratedStickers(MySqlConnection connection, MySqlTransaction transaction, ulong steamId, int team, int defIndex, int paint, List<EconSticker> source)
    {
        await using (var clear = connection.CreateCommand())
        {
            clear.Transaction = transaction;
            clear.CommandText = "DELETE FROM ws_stickers WHERE steamid = @sid AND team = @team AND defindex = @def AND paint = @paint;";
            AddWeaponKey(clear, steamId, team, defIndex, paint);
            await clear.ExecuteNonQueryAsync();
        }

        foreach (var sticker in MapGenStickers(source))
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO ws_stickers (steamid, team, defindex, paint, slot, sticker, wear, scale, rotation, offset_x, offset_y, schema_slot) VALUES (@sid, @team, @def, @paint, @slot, @sticker, @wear, @scale, @rotation, @x, @y, @schema);";
            AddWeaponKey(command, steamId, team, defIndex, paint);
            command.Parameters.AddWithValue("@slot", sticker.Slot);
            command.Parameters.AddWithValue("@sticker", sticker.Id);
            command.Parameters.AddWithValue("@wear", sticker.Wear);
            command.Parameters.AddWithValue("@scale", sticker.Scale == 0f ? 1f : sticker.Scale);
            command.Parameters.AddWithValue("@rotation", sticker.Rotation);
            command.Parameters.AddWithValue("@x", sticker.OffsetX);
            command.Parameters.AddWithValue("@y", sticker.OffsetY);
            command.Parameters.AddWithValue("@schema", sticker.Schema);
            await command.ExecuteNonQueryAsync();
        }
    }

    private static async Task WriteGeneratedCharm(MySqlConnection connection, MySqlTransaction transaction, ulong steamId, int team, int defIndex, int paint, List<EconSticker> source)
    {
        await using (var clear = connection.CreateCommand())
        {
            clear.Transaction = transaction;
            clear.CommandText = "DELETE FROM ws_charms WHERE steamid = @sid AND team = @team AND defindex = @def AND paint = @paint;";
            AddWeaponKey(clear, steamId, team, defIndex, paint);
            await clear.ExecuteNonQueryAsync();
        }

        var charm = source.FirstOrDefault(value => value.Id > 0);
        if (charm == null)
            return;

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO ws_charms (steamid, team, defindex, paint, charm, pattern, sticker, highlight, offset_x, offset_y, offset_z) VALUES (@sid, @team, @def, @paint, @charm, @pattern, @sticker, @highlight, @x, @y, @z);";
        AddWeaponKey(command, steamId, team, defIndex, paint);
        command.Parameters.AddWithValue("@charm", charm.Id);
        command.Parameters.AddWithValue("@pattern", charm.Pattern);
        command.Parameters.AddWithValue("@sticker", charm.Sticker);
        command.Parameters.AddWithValue("@highlight", charm.Highlight);
        command.Parameters.AddWithValue("@x", charm.OffsetX);
        command.Parameters.AddWithValue("@y", charm.OffsetY);
        command.Parameters.AddWithValue("@z", charm.OffsetZ);
        await command.ExecuteNonQueryAsync();
    }

    private sealed record GeneratedSticker(int Slot, int Id, float Wear, float Scale, float Rotation, float OffsetX, float OffsetY, int Schema);

    private static List<GeneratedSticker> MapGenStickers(List<EconSticker> source)
    {
        const int slots = 6;
        var result = new List<GeneratedSticker>();
        var used = new bool[slots];
        var nextFree = 0;
        var nextZero = 4;

        foreach (var sticker in source)
        {
            if (sticker.Id <= 0 || sticker.Slot < 0 || sticker.Slot > 31 || result.Count >= 5)
                continue;

            var origin = sticker.Slot;
            var slot = origin;
            var schema = 0;
            if (origin >= slots || used[origin])
            {
                schema = origin;
                if (origin == 0)
                {
                    while (nextZero < slots && used[nextZero])
                        nextZero++;
                    slot = nextZero < slots ? nextZero++ : TakeFreeSlot(used, ref nextFree);
                }
                else
                    slot = TakeFreeSlot(used, ref nextFree);
                if (slot < 0)
                    continue;
            }
            else if (origin >= 4)
                schema = origin;

            used[slot] = true;
            result.Add(new GeneratedSticker(slot, sticker.Id, sticker.Wear, sticker.Scale == 0f ? 1f : sticker.Scale,
                sticker.Rotation, sticker.OffsetX, sticker.OffsetY, schema));
        }

        return result;
    }

    private static int TakeFreeSlot(bool[] used, ref int cursor)
    {
        while (cursor < used.Length && used[cursor])
            cursor++;
        return cursor < used.Length ? cursor++ : -1;
    }

    private static async Task QueueSync(MySqlConnection connection, MySqlTransaction transaction, ulong steamId)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO ws_sync_queue (steamid) VALUES (@sid);";
        command.Parameters.AddWithValue("@sid", steamId);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task DeleteCode(MySqlConnection connection, MySqlTransaction transaction, ulong steamId)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM ws_link_codes WHERE steamid = @sid;";
        command.Parameters.AddWithValue("@sid", steamId);
        await command.ExecuteNonQueryAsync();
    }

    private static void AddWeaponKey(MySqlCommand command, ulong steamId, int team, int defIndex, int paint)
    {
        command.Parameters.AddWithValue("@sid", steamId);
        command.Parameters.AddWithValue("@team", team);
        command.Parameters.AddWithValue("@def", defIndex);
        command.Parameters.AddWithValue("@paint", paint);
    }

    private static float MinimumWear(int defIndex, PaintDef paint, bool knife)
    {
        var minimum = knife ? 0.01f : 0.000001f;
        return Math.Min(paint.MaxFloat, Math.Max(paint.MinFloat, minimum));
    }

    private static string NormalizeCode(string code)
    {
        var builder = new StringBuilder(8);
        foreach (var c in code)
        {
            if (c == '-' || char.IsWhiteSpace(c))
                continue;
            builder.Append(char.ToUpperInvariant(c));
        }
        return builder.ToString();
    }
}
