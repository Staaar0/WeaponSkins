using System.Text;
using MySqlConnector;

namespace WeaponSkins;

public sealed class LinkStore
{
	public const int CodeLifetimeSeconds = 600;

	private const int DuplicateKey = 1062;
	private const int SyncBatch = 200;
	private const int StaleSyncMinutes = 5;
	private const int PermissionBatch = 100;

	private readonly Database database;

	public LinkStore(Database database)
	{
		this.database = database;
	}

	public async Task<bool> IsLinked(ulong steamId, CancellationToken cancellationToken)
	{
		await using var connection = await database.Open(cancellationToken);
		await using var command = connection.CreateCommand();
		command.CommandText = "SELECT 1 FROM ws_links WHERE steamid = @sid LIMIT 1;";
		command.Parameters.AddWithValue("@sid", steamId);
		return await command.ExecuteScalarAsync(cancellationToken) != null;
	}

	public async Task<List<ulong>> FindLinked(IReadOnlyList<ulong> steamIds, CancellationToken cancellationToken)
	{
		var linked = new List<ulong>();
		if (steamIds.Count == 0)
			return linked;

		await using var connection = await database.Open(cancellationToken);
		await using var command = connection.CreateCommand();
		command.CommandText = $"SELECT steamid FROM ws_links WHERE steamid IN ({Fill(command, steamIds)});";

		await using var reader = await command.ExecuteReaderAsync(cancellationToken);
		while (await reader.ReadAsync(cancellationToken))
			linked.Add(reader.GetUInt64(0));

		return linked;
	}

	public async Task<bool> StoreCode(ulong steamId, byte[] hash, CancellationToken cancellationToken)
	{
		await using var connection = await database.Open(cancellationToken);
		await using var command = connection.CreateCommand();
		command.CommandText = $"""
			DELETE FROM ws_link_codes WHERE steamid = @sid OR expires_at < NOW();

			INSERT INTO ws_link_codes (steamid, code_hash, expires_at)
			VALUES (@sid, @hash, DATE_ADD(NOW(), INTERVAL {CodeLifetimeSeconds} SECOND));
			""";
		command.Parameters.AddWithValue("@sid", steamId);
		command.Parameters.AddWithValue("@hash", hash);

		try
		{
			await command.ExecuteNonQueryAsync(cancellationToken);
			return true;
		}
		catch (MySqlException ex) when ((int)ex.ErrorCode == DuplicateKey)
		{
			return false;
		}
	}

	public async Task<List<(long Id, ulong SteamId)>> ReadSync(IReadOnlyList<ulong> steamIds, CancellationToken cancellationToken)
	{
		var rows = new List<(long, ulong)>();
		if (steamIds.Count == 0)
			return rows;

		await using var connection = await database.Open(cancellationToken);
		await using var command = connection.CreateCommand();
		command.CommandText = $"SELECT id, steamid FROM ws_sync_queue WHERE steamid IN ({Fill(command, steamIds)}) ORDER BY id LIMIT {SyncBatch};";

		await using var reader = await command.ExecuteReaderAsync(cancellationToken);
		while (await reader.ReadAsync(cancellationToken))
			rows.Add((reader.GetInt64(0), reader.GetUInt64(1)));

		return rows;
	}

	public async Task DeleteSync(IReadOnlyList<long> ids, CancellationToken cancellationToken)
	{
		if (ids.Count == 0)
			return;

		await using var connection = await database.Open(cancellationToken);
		await using var command = connection.CreateCommand();
		var names = new string[ids.Count];
		for (var i = 0; i < ids.Count; i++)
		{
			names[i] = $"@p{i}";
			command.Parameters.AddWithValue(names[i], ids[i]);
		}

		command.CommandText = $"DELETE FROM ws_sync_queue WHERE id IN ({string.Join(",", names)});";
		await command.ExecuteNonQueryAsync(cancellationToken);
	}

	public async Task SavePermissions(ulong steamId, bool stickers, bool gen, CancellationToken cancellationToken)
	{
		await using var connection = await database.Open(cancellationToken);
		await using var command = connection.CreateCommand();
		command.CommandText = """
			INSERT INTO ws_permissions (steamid, stickers, gen) VALUES (@sid, @stickers, @gen)
			ON DUPLICATE KEY UPDATE stickers = @stickers, gen = @gen;
			""";
		command.Parameters.AddWithValue("@sid", steamId);
		command.Parameters.AddWithValue("@stickers", stickers ? 1 : 0);
		command.Parameters.AddWithValue("@gen", gen ? 1 : 0);
		await command.ExecuteNonQueryAsync(cancellationToken);
	}

	public async Task<List<ulong>> ReadLinked(CancellationToken cancellationToken)
	{
		var steamIds = new List<ulong>();
		await using var connection = await database.Open(cancellationToken);
		await using var command = connection.CreateCommand();
		command.CommandText = "SELECT steamid FROM ws_links;";

		await using var reader = await command.ExecuteReaderAsync(cancellationToken);
		while (await reader.ReadAsync(cancellationToken))
			steamIds.Add(reader.GetUInt64(0));

		return steamIds;
	}

	public async Task SavePermissions(IReadOnlyList<(ulong SteamId, bool Stickers, bool Gen)> rows, CancellationToken cancellationToken)
	{
		if (rows.Count == 0)
			return;

		await using var connection = await database.Open(cancellationToken);
		for (var start = 0; start < rows.Count; start += PermissionBatch)
		{
			await using var command = connection.CreateCommand();
			var text = new StringBuilder();
			var end = Math.Min(start + PermissionBatch, rows.Count);

			for (var i = start; i < end; i++)
			{
				text.Append($"INSERT INTO ws_permissions (steamid, stickers, gen) VALUES (@s{i}, @p{i}, @g{i}) ON DUPLICATE KEY UPDATE stickers = @p{i}, gen = @g{i};");
				command.Parameters.AddWithValue($"@s{i}", rows[i].SteamId);
				command.Parameters.AddWithValue($"@p{i}", rows[i].Stickers ? 1 : 0);
				command.Parameters.AddWithValue($"@g{i}", rows[i].Gen ? 1 : 0);
			}

			command.CommandText = text.ToString();
			await command.ExecuteNonQueryAsync(cancellationToken);
		}
	}

	public async Task PurgeCodes(CancellationToken cancellationToken)
	{
		await Execute("DELETE FROM ws_link_codes WHERE expires_at < NOW();", cancellationToken);
	}

	public async Task PurgeSync(CancellationToken cancellationToken)
	{
		await Execute($"DELETE FROM ws_sync_queue WHERE created_at < DATE_SUB(NOW(), INTERVAL {StaleSyncMinutes} MINUTE);", cancellationToken);
	}

	private async Task Execute(string sql, CancellationToken cancellationToken)
	{
		await using var connection = await database.Open(cancellationToken);
		await using var command = connection.CreateCommand();
		command.CommandText = sql;
		await command.ExecuteNonQueryAsync(cancellationToken);
	}

	private static string Fill(MySqlCommand command, IReadOnlyList<ulong> values)
	{
		var names = new string[values.Count];
		for (var i = 0; i < values.Count; i++)
		{
			names[i] = $"@p{i}";
			command.Parameters.AddWithValue(names[i], values[i]);
		}

		return string.Join(",", names);
	}
}
