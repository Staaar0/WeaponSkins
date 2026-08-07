using CounterStrikeSharp.API.Modules.Utils;
using MySqlConnector;

namespace WeaponSkins;

public sealed class LoadoutStore
{
	private readonly record struct WeaponSnapshot(int Paint, float Wear, int Seed, string? NameTag, int StatTrak);
	private readonly record struct StickerSnapshot(int Slot, int Id, float Wear, float Scale, float Rotation, float OffsetX, float OffsetY, int Schema);
	private readonly record struct CharmSnapshot(int Id, int Pattern, int Sticker, int Highlight, float OffsetX, float OffsetY, float OffsetZ);
	private readonly record struct StatTrakKey(ulong SteamId, CsTeam Team, int DefIndex, int Paint);
	private readonly record struct StatTrakWrite(CsTeam Team, int DefIndex, int Paint, int Count);
	private readonly record struct WriteKey(ulong SteamId, CsTeam Team, int DefIndex, WriteKind Kind);

	private enum WriteKind
	{
		Weapon,
		WeaponAndEquip,
		WeaponAndStickers,
		Stickers,
		StickersAndCharm,
		GeneratedWeapon,
		GeneratedKnife,
		Knife,
		Gloves,
		Agent,
		Music,
		Pin
	}

	private sealed class CoalescedWrite
	{
		public required Func<Task> Operation;
		public Task Runner = Task.CompletedTask;
		public long Version;
	}

	private const int MaxPendingWrites = 1024;
	private const int MaxPendingWritesPerPlayer = 64;

	private readonly Database database;
	private readonly object writeSync = new();
	private readonly Dictionary<ulong, Task> writeTails = [];
	private readonly Dictionary<ulong, int> pendingByPlayer = [];
	private readonly Dictionary<WriteKey, CoalescedWrite> coalescedWrites = [];
	private readonly Dictionary<StatTrakKey, int> pendingStatTrak = [];
	private readonly SemaphoreSlim writeSlots = new(8, 8);
	private int pendingWrites;
	private bool acceptingWrites = true;

	public LoadoutStore(Database database)
	{
		this.database = database;
	}

	private static TeamLoadout Side(PlayerLoadout loadout, sbyte team) => team == 3 ? loadout.CT : loadout.T;

	public async Task<PlayerLoadout> Load(ulong steamId, CancellationToken cancellationToken = default)
	{
		var loadout = new PlayerLoadout();
		if (!database.Configured)
			return loadout;

		Task? pending;
		lock (writeSync)
			writeTails.TryGetValue(steamId, out pending);

		if (pending != null)
		{
			try
			{
				await pending.WaitAsync(cancellationToken);
			}
			catch when (!cancellationToken.IsCancellationRequested)
			{
			}
		}

		await using var connection = await database.Open(cancellationToken);
		await using var command = connection.CreateCommand();
		command.CommandText = """
			SELECT team, defindex, paint, wear, seed, nametag, stattrak FROM ws_weapons WHERE steamid = @sid;
			SELECT team, defindex, paint, slot, sticker, wear, scale, rotation, offset_x, offset_y, schema_slot FROM ws_stickers WHERE steamid = @sid;
			SELECT team, defindex, paint, charm, pattern, sticker, highlight, offset_x, offset_y, offset_z FROM ws_charms WHERE steamid = @sid;
			SELECT team, defindex, paint FROM ws_equipped WHERE steamid = @sid;
			SELECT team, defindex FROM ws_knives WHERE steamid = @sid;
			SELECT team, defindex, paint FROM ws_gloves WHERE steamid = @sid;
			SELECT team, model FROM ws_agents WHERE steamid = @sid;
			SELECT kit FROM ws_music WHERE steamid = @sid;
			SELECT pin FROM ws_pins WHERE steamid = @sid;
			""";
		command.Parameters.AddWithValue("@sid", steamId);

		await using var reader = await command.ExecuteReaderAsync(cancellationToken);

		while (await reader.ReadAsync(cancellationToken))
		{
			var entry = Side(loadout, reader.GetSByte(0)).Skin(reader.GetInt32(1), reader.GetInt32(2));
			entry.Wear = reader.GetFloat(3);
			entry.Seed = reader.GetInt32(4);
			entry.NameTag = reader.IsDBNull(5) ? null : reader.GetString(5);
			entry.StatTrak = reader.GetInt32(6);
		}

		await reader.NextResultAsync(cancellationToken);
		while (await reader.ReadAsync(cancellationToken))
		{
			var entry = Side(loadout, reader.GetSByte(0)).Skin(reader.GetInt32(1), reader.GetInt32(2));
			entry.Stickers.Add(new StickerEntry
			{
				Slot = reader.GetSByte(3),
				Id = reader.GetInt32(4),
				Wear = reader.GetFloat(5),
				Scale = reader.GetFloat(6),
				Rotation = reader.GetFloat(7),
				OffsetX = reader.GetFloat(8),
				OffsetY = reader.GetFloat(9),
				Schema = reader.GetSByte(10)
			});
		}

		await reader.NextResultAsync(cancellationToken);
		while (await reader.ReadAsync(cancellationToken))
		{
			var entry = Side(loadout, reader.GetSByte(0)).Skin(reader.GetInt32(1), reader.GetInt32(2));
			entry.Charm = new CharmEntry
			{
				Id = reader.GetInt32(3),
				Pattern = reader.GetInt32(4),
				Sticker = reader.GetInt32(5),
				Highlight = reader.GetInt32(6),
				OffsetX = reader.GetFloat(7),
				OffsetY = reader.GetFloat(8),
				OffsetZ = reader.GetFloat(9)
			};
		}

		await reader.NextResultAsync(cancellationToken);
		while (await reader.ReadAsync(cancellationToken))
			Side(loadout, reader.GetSByte(0)).Equip(reader.GetInt32(1), reader.GetInt32(2));

		await reader.NextResultAsync(cancellationToken);
		while (await reader.ReadAsync(cancellationToken))
			Side(loadout, reader.GetSByte(0)).Knife = reader.GetInt32(1);

		await reader.NextResultAsync(cancellationToken);
		while (await reader.ReadAsync(cancellationToken))
		{
			var side = Side(loadout, reader.GetSByte(0));
			side.GloveDef = reader.GetInt32(1);
			side.GlovePaint = reader.GetInt32(2);
		}

		await reader.NextResultAsync(cancellationToken);
		while (await reader.ReadAsync(cancellationToken))
			Side(loadout, reader.GetSByte(0)).AgentModel = reader.GetString(1);

		await reader.NextResultAsync(cancellationToken);
		while (await reader.ReadAsync(cancellationToken))
			loadout.MusicKit = reader.GetInt32(0);

		await reader.NextResultAsync(cancellationToken);
		while (await reader.ReadAsync(cancellationToken))
			loadout.Pin = reader.GetInt32(0);

		return loadout;
	}

	public Task SaveWeapon(ulong steamId, CsTeam team, int defIndex, WeaponEntry entry)
	{
		var weapon = Snapshot(entry);
		ForgetStatTrak(steamId, team, defIndex, weapon.Paint);
		return QueueLatest(new WriteKey(steamId, team, defIndex, WriteKind.Weapon), async () =>
		{
			await using var connection = await database.Open();
			await WriteWeapon(connection, null, steamId, team, defIndex, weapon);
		});
	}

	public Task SaveWeaponAndEquip(ulong steamId, CsTeam team, int defIndex, WeaponEntry entry)
	{
		var weapon = Snapshot(entry);
		ForgetStatTrak(steamId, team, defIndex, weapon.Paint);
		return QueueLatest(new WriteKey(steamId, team, defIndex, WriteKind.WeaponAndEquip), () => Transaction(async (connection, transaction) =>
		{
			await WriteWeapon(connection, transaction, steamId, team, defIndex, weapon);
			await WriteEquipped(connection, transaction, steamId, team, defIndex, weapon.Paint);
		}));
	}

	public Task SaveWeaponAndStickers(ulong steamId, CsTeam team, int defIndex, WeaponEntry entry)
	{
		var weapon = Snapshot(entry);
		var stickers = Snapshot(entry.Stickers);
		ForgetStatTrak(steamId, team, defIndex, weapon.Paint);
		return QueueLatest(new WriteKey(steamId, team, defIndex, WriteKind.WeaponAndStickers), () => Transaction(async (connection, transaction) =>
		{
			await WriteWeapon(connection, transaction, steamId, team, defIndex, weapon);
			await WriteStickers(connection, transaction, steamId, team, defIndex, weapon.Paint, stickers);
		}));
	}

	public Task SaveStickers(ulong steamId, CsTeam team, int defIndex, int paint, List<StickerEntry> stickers)
	{
		var snapshot = Snapshot(stickers);
		return QueueLatest(new WriteKey(steamId, team, defIndex, WriteKind.Stickers), () => Transaction((connection, transaction) =>
			WriteStickers(connection, transaction, steamId, team, defIndex, paint, snapshot)));
	}

	public Task SaveStickersAndCharm(ulong steamId, CsTeam team, int defIndex, int paint, List<StickerEntry> stickers, CharmEntry? charm)
	{
		var stickerSnapshot = Snapshot(stickers);
		var charmSnapshot = Snapshot(charm);
		return QueueLatest(new WriteKey(steamId, team, defIndex, WriteKind.StickersAndCharm), () => Transaction(async (connection, transaction) =>
		{
			await WriteStickers(connection, transaction, steamId, team, defIndex, paint, stickerSnapshot);
			await WriteCharm(connection, transaction, steamId, team, defIndex, paint, charmSnapshot);
		}));
	}

	public Task SaveGeneratedWeapon(ulong steamId, CsTeam team, int defIndex, WeaponEntry entry)
	{
		var weapon = Snapshot(entry);
		var stickers = Snapshot(entry.Stickers);
		var charm = Snapshot(entry.Charm);
		ForgetStatTrak(steamId, team, defIndex, weapon.Paint);
		return QueueLatest(new WriteKey(steamId, team, defIndex, WriteKind.GeneratedWeapon), () => Transaction(async (connection, transaction) =>
		{
			await WriteWeapon(connection, transaction, steamId, team, defIndex, weapon);
			await WriteEquipped(connection, transaction, steamId, team, defIndex, weapon.Paint);
			await WriteStickers(connection, transaction, steamId, team, defIndex, weapon.Paint, stickers);
			await WriteCharm(connection, transaction, steamId, team, defIndex, weapon.Paint, charm);
		}));
	}

	public Task SaveGeneratedKnife(ulong steamId, CsTeam team, int defIndex, WeaponEntry entry)
	{
		var weapon = Snapshot(entry);
		var stickers = Snapshot(entry.Stickers);
		var charm = Snapshot(entry.Charm);
		ForgetStatTrak(steamId, team, defIndex, weapon.Paint);
		return QueueLatest(new WriteKey(steamId, team, defIndex, WriteKind.GeneratedKnife), () => Transaction(async (connection, transaction) =>
		{
			await WriteKnife(connection, transaction, steamId, team, defIndex);
			await WriteWeapon(connection, transaction, steamId, team, defIndex, weapon);
			await WriteEquipped(connection, transaction, steamId, team, defIndex, weapon.Paint);
			await WriteStickers(connection, transaction, steamId, team, defIndex, weapon.Paint, stickers);
			await WriteCharm(connection, transaction, steamId, team, defIndex, weapon.Paint, charm);
		}));
	}

	public void MarkStatTrak(ulong steamId, CsTeam team, int defIndex, int paint, int count)
	{
		if (!database.Configured)
			return;

		lock (writeSync)
		{
			if (acceptingWrites)
				pendingStatTrak[new StatTrakKey(steamId, team, defIndex, paint)] = count;
		}
	}

	public Task FlushStatTrak(ulong? steamId = null)
	{
		if (!database.Configured)
			return Task.CompletedTask;

		var tasks = new List<Task>();
		lock (writeSync)
		{
			var groups = pendingStatTrak
				.Where(entry => !steamId.HasValue || entry.Key.SteamId == steamId.Value)
				.GroupBy(entry => entry.Key.SteamId)
				.Select(group => (SteamId: group.Key, Writes: group.Select(entry => new StatTrakWrite(entry.Key.Team, entry.Key.DefIndex, entry.Key.Paint, entry.Value)).ToArray()))
				.ToArray();

			foreach (var group in groups)
			{
				foreach (var write in group.Writes)
					pendingStatTrak.Remove(new StatTrakKey(group.SteamId, write.Team, write.DefIndex, write.Paint));

				tasks.Add(Queue(group.SteamId, () => WriteStatTrakBatch(group.SteamId, group.Writes)));
			}
		}

		return tasks.Count == 0 ? Task.CompletedTask : Task.WhenAll(tasks);
	}

	public Task SaveKnife(ulong steamId, CsTeam team, int defIndex)
	{
		return QueueLatest(new WriteKey(steamId, team, 0, WriteKind.Knife), async () =>
		{
			await using var connection = await database.Open();
			await WriteKnife(connection, null, steamId, team, defIndex);
		});
	}

	public Task SaveGloves(ulong steamId, CsTeam team, TeamLoadout side)
	{
		var defIndex = side.GloveDef;
		var paint = side.GlovePaint;
		var weapon = defIndex > 0 ? Snapshot(side.Gloves) : default;
		return QueueLatest(new WriteKey(steamId, team, 0, WriteKind.Gloves), () => Transaction(async (connection, transaction) =>
		{
			await WriteGloves(connection, transaction, steamId, team, defIndex, paint);
			if (defIndex > 0)
				await WriteWeapon(connection, transaction, steamId, team, defIndex, weapon);
		}));
	}

	public Task SaveAgent(ulong steamId, CsTeam team, string? model)
	{
		return QueueLatest(new WriteKey(steamId, team, 0, WriteKind.Agent), () => !string.IsNullOrEmpty(model)
			? Run("INSERT INTO ws_agents (steamid, team, model) VALUES (@sid, @team, @value) ON DUPLICATE KEY UPDATE model = @value;", steamId, team, ("@value", model))
			: Run("DELETE FROM ws_agents WHERE steamid = @sid AND team = @team;", steamId, team));
	}

	public Task SaveMusic(ulong steamId, int kit)
	{
		return QueueLatest(new WriteKey(steamId, CsTeam.None, 0, WriteKind.Music), () => kit > 0
			? Run("INSERT INTO ws_music (steamid, kit) VALUES (@sid, @value) ON DUPLICATE KEY UPDATE kit = @value;", steamId, CsTeam.None, ("@value", kit))
			: Run("DELETE FROM ws_music WHERE steamid = @sid;", steamId, CsTeam.None));
	}

	public Task SavePin(ulong steamId, int pin)
	{
		return QueueLatest(new WriteKey(steamId, CsTeam.None, 0, WriteKind.Pin), () => pin > 0
			? Run("INSERT INTO ws_pins (steamid, pin) VALUES (@sid, @value) ON DUPLICATE KEY UPDATE pin = @value;", steamId, CsTeam.None, ("@value", pin))
			: Run("DELETE FROM ws_pins WHERE steamid = @sid;", steamId, CsTeam.None));
	}

	private void ForgetStatTrak(ulong steamId, CsTeam team, int defIndex, int paint)
	{
		lock (writeSync)
			pendingStatTrak.Remove(new StatTrakKey(steamId, team, defIndex, paint));
	}

	private Task QueueLatest(WriteKey key, Func<Task> operation)
	{
		if (!database.Configured)
			return Task.CompletedTask;

		lock (writeSync)
		{
			if (coalescedWrites.TryGetValue(key, out var pending) &&
				writeTails.TryGetValue(key.SteamId, out var tail) &&
				ReferenceEquals(tail, pending.Runner))
			{
				pending.Operation = operation;
				pending.Version++;
				return pending.Runner;
			}

			var state = new CoalescedWrite { Operation = operation };
			coalescedWrites[key] = state;
			state.Runner = Queue(key.SteamId, () => RunCoalesced(key, state));
			if (state.Runner.IsCompleted && state.Runner.IsFaulted)
				coalescedWrites.Remove(key);
			return state.Runner;
		}
	}

	private async Task RunCoalesced(WriteKey key, CoalescedWrite state)
	{
		try
		{
			while (true)
			{
				Func<Task> operation;
				long version;
				lock (writeSync)
				{
					operation = state.Operation;
					version = state.Version;
				}

				await operation();

				lock (writeSync)
				{
					if (state.Version != version)
						continue;

					if (coalescedWrites.TryGetValue(key, out var current) && ReferenceEquals(current, state))
						coalescedWrites.Remove(key);
					return;
				}
			}
		}
		finally
		{
			lock (writeSync)
			{
				if (coalescedWrites.TryGetValue(key, out var current) && ReferenceEquals(current, state))
					coalescedWrites.Remove(key);
			}
		}
	}

	public async Task Stop()
	{
		Task[] pending;
		lock (writeSync)
		{
			acceptingWrites = false;
			pending = writeTails.Values.ToArray();
		}

		if (pending.Length > 0)
			await Task.WhenAll(pending);
	}

	private Task Queue(ulong steamId, Func<Task> operation)
	{
		if (!database.Configured)
			return Task.CompletedTask;

		Task next;
		lock (writeSync)
		{
			if (!acceptingWrites)
				return Task.FromException(new InvalidOperationException("The loadout store is stopping"));

			var playerPending = pendingByPlayer.GetValueOrDefault(steamId);
			if (pendingWrites >= MaxPendingWrites || playerPending >= MaxPendingWritesPerPlayer)
				return Task.FromException(new InvalidOperationException("The database write queue is full"));

			pendingWrites++;
			pendingByPlayer[steamId] = playerPending + 1;

			var previous = writeTails.GetValueOrDefault(steamId, Task.CompletedTask);
			next = previous.ContinueWith(
				_ => RunWrite(operation),
				CancellationToken.None,
				TaskContinuationOptions.None,
				TaskScheduler.Default).Unwrap();
			writeTails[steamId] = next;
		}

		_ = next.ContinueWith(
			_ => CompleteWrite(steamId, next),
			CancellationToken.None,
			TaskContinuationOptions.ExecuteSynchronously,
			TaskScheduler.Default);

		return next;
	}

	private async Task RunWrite(Func<Task> operation)
	{
		await writeSlots.WaitAsync();
		try
		{
			await operation();
		}
		finally
		{
			writeSlots.Release();
		}
	}

	private void CompleteWrite(ulong steamId, Task completed)
	{
		lock (writeSync)
		{
			pendingWrites--;

			var playerPending = pendingByPlayer.GetValueOrDefault(steamId);
			if (playerPending <= 1)
				pendingByPlayer.Remove(steamId);
			else
				pendingByPlayer[steamId] = playerPending - 1;

			if (writeTails.TryGetValue(steamId, out var tail) && ReferenceEquals(tail, completed))
				writeTails.Remove(steamId);
		}
	}

	private async Task WriteStatTrakBatch(ulong steamId, StatTrakWrite[] writes)
	{
		await using var connection = await database.Open();
		await using var transaction = await connection.BeginTransactionAsync();
		await using var command = connection.CreateCommand();
		command.Transaction = transaction;
		command.CommandText = "UPDATE ws_weapons SET stattrak = @count WHERE steamid = @sid AND team = @team AND defindex = @def AND paint = @paint;";
		command.Parameters.AddWithValue("@sid", steamId);
		command.Parameters.AddWithValue("@team", 0);
		command.Parameters.AddWithValue("@def", 0);
		command.Parameters.AddWithValue("@paint", 0);
		command.Parameters.AddWithValue("@count", 0);

		foreach (var write in writes)
		{
			command.Parameters["@team"].Value = (sbyte)write.Team;
			command.Parameters["@def"].Value = write.DefIndex;
			command.Parameters["@paint"].Value = write.Paint;
			command.Parameters["@count"].Value = write.Count;
			await command.ExecuteNonQueryAsync();
		}

		await transaction.CommitAsync();
	}

	private async Task Transaction(Func<MySqlConnection, MySqlTransaction, Task> operation)
	{
		await using var connection = await database.Open();
		await using var transaction = await connection.BeginTransactionAsync();
		await operation(connection, transaction);
		await transaction.CommitAsync();
	}

	private async Task Run(string sql, ulong steamId, CsTeam team, params (string Name, object Value)[] extra)
	{
		await using var connection = await database.Open();
		await using var command = connection.CreateCommand();
		command.CommandText = sql;
		command.Parameters.AddWithValue("@sid", steamId);
		command.Parameters.AddWithValue("@team", (sbyte)team);
		foreach (var (name, value) in extra)
			command.Parameters.AddWithValue(name, value);
		await command.ExecuteNonQueryAsync();
	}

	private static async Task WriteWeapon(
		MySqlConnection connection,
		MySqlTransaction? transaction,
		ulong steamId,
		CsTeam team,
		int defIndex,
		WeaponSnapshot weapon)
	{
		await using var command = connection.CreateCommand();
		command.Transaction = transaction;
		command.CommandText = """
			INSERT INTO ws_weapons (steamid, team, defindex, paint, wear, seed, nametag, stattrak)
			VALUES (@sid, @team, @def, @paint, @wear, @seed, @nametag, @stattrak)
			ON DUPLICATE KEY UPDATE wear = @wear, seed = @seed, nametag = @nametag, stattrak = @stattrak;
			""";
		AddKey(command, steamId, team, defIndex, weapon.Paint);
		command.Parameters.AddWithValue("@wear", weapon.Wear);
		command.Parameters.AddWithValue("@seed", weapon.Seed);
		command.Parameters.AddWithValue("@nametag", (object?)weapon.NameTag ?? DBNull.Value);
		command.Parameters.AddWithValue("@stattrak", weapon.StatTrak);
		await command.ExecuteNonQueryAsync();
	}

	private static async Task WriteEquipped(
		MySqlConnection connection,
		MySqlTransaction? transaction,
		ulong steamId,
		CsTeam team,
		int defIndex,
		int paint)
	{
		await using var command = connection.CreateCommand();
		command.Transaction = transaction;
		command.CommandText = """
			INSERT INTO ws_equipped (steamid, team, defindex, paint)
			VALUES (@sid, @team, @def, @paint)
			ON DUPLICATE KEY UPDATE paint = @paint;
			""";
		AddKey(command, steamId, team, defIndex, paint);
		await command.ExecuteNonQueryAsync();
	}

	private static async Task WriteStickers(
		MySqlConnection connection,
		MySqlTransaction transaction,
		ulong steamId,
		CsTeam team,
		int defIndex,
		int paint,
		StickerSnapshot[] stickers)
	{
		await using (var command = connection.CreateCommand())
		{
			command.Transaction = transaction;
			command.CommandText = "DELETE FROM ws_stickers WHERE steamid = @sid AND team = @team AND defindex = @def AND paint = @paint;";
			AddKey(command, steamId, team, defIndex, paint);
			await command.ExecuteNonQueryAsync();
		}

		foreach (var sticker in stickers)
		{
			await using var insert = connection.CreateCommand();
			insert.Transaction = transaction;
			insert.CommandText = """
				INSERT INTO ws_stickers (steamid, team, defindex, paint, slot, sticker, wear, scale, rotation, offset_x, offset_y, schema_slot)
				VALUES (@sid, @team, @def, @paint, @slot, @sticker, @wear, @scale, @rotation, @x, @y, @schema);
				""";
			AddKey(insert, steamId, team, defIndex, paint);
			insert.Parameters.AddWithValue("@slot", (sbyte)sticker.Slot);
			insert.Parameters.AddWithValue("@sticker", sticker.Id);
			insert.Parameters.AddWithValue("@wear", sticker.Wear);
			insert.Parameters.AddWithValue("@scale", sticker.Scale);
			insert.Parameters.AddWithValue("@rotation", sticker.Rotation);
			insert.Parameters.AddWithValue("@x", sticker.OffsetX);
			insert.Parameters.AddWithValue("@y", sticker.OffsetY);
			insert.Parameters.AddWithValue("@schema", (sbyte)sticker.Schema);
			await insert.ExecuteNonQueryAsync();
		}
	}

	private static async Task WriteCharm(
		MySqlConnection connection,
		MySqlTransaction? transaction,
		ulong steamId,
		CsTeam team,
		int defIndex,
		int paint,
		CharmSnapshot? charm)
	{
		await using var command = connection.CreateCommand();
		command.Transaction = transaction;

		if (!charm.HasValue || charm.Value.Id <= 0)
		{
			command.CommandText = "DELETE FROM ws_charms WHERE steamid = @sid AND team = @team AND defindex = @def AND paint = @paint;";
			AddKey(command, steamId, team, defIndex, paint);
			await command.ExecuteNonQueryAsync();
			return;
		}

		var value = charm.Value;
		command.CommandText = """
			INSERT INTO ws_charms (steamid, team, defindex, paint, charm, pattern, sticker, highlight, offset_x, offset_y, offset_z)
			VALUES (@sid, @team, @def, @paint, @charm, @pattern, @sticker, @highlight, @x, @y, @z)
			ON DUPLICATE KEY UPDATE charm = @charm, pattern = @pattern, sticker = @sticker, highlight = @highlight,
				offset_x = @x, offset_y = @y, offset_z = @z;
			""";
		AddKey(command, steamId, team, defIndex, paint);
		command.Parameters.AddWithValue("@charm", value.Id);
		command.Parameters.AddWithValue("@pattern", value.Pattern);
		command.Parameters.AddWithValue("@sticker", value.Sticker);
		command.Parameters.AddWithValue("@highlight", value.Highlight);
		command.Parameters.AddWithValue("@x", value.OffsetX);
		command.Parameters.AddWithValue("@y", value.OffsetY);
		command.Parameters.AddWithValue("@z", value.OffsetZ);
		await command.ExecuteNonQueryAsync();
	}

	private static async Task WriteKnife(
		MySqlConnection connection,
		MySqlTransaction? transaction,
		ulong steamId,
		CsTeam team,
		int defIndex)
	{
		await using var command = connection.CreateCommand();
		command.Transaction = transaction;
		command.Parameters.AddWithValue("@sid", steamId);
		command.Parameters.AddWithValue("@team", (sbyte)team);

		if (defIndex > 0)
		{
			command.CommandText = """
				INSERT INTO ws_knives (steamid, team, defindex)
				VALUES (@sid, @team, @def)
				ON DUPLICATE KEY UPDATE defindex = @def;
				""";
			command.Parameters.AddWithValue("@def", defIndex);
		}
		else
		{
			command.CommandText = "DELETE FROM ws_knives WHERE steamid = @sid AND team = @team;";
		}

		await command.ExecuteNonQueryAsync();
	}

	private static async Task WriteGloves(
		MySqlConnection connection,
		MySqlTransaction transaction,
		ulong steamId,
		CsTeam team,
		int defIndex,
		int paint)
	{
		await using var command = connection.CreateCommand();
		command.Transaction = transaction;
		command.Parameters.AddWithValue("@sid", steamId);
		command.Parameters.AddWithValue("@team", (sbyte)team);

		if (defIndex > 0)
		{
			command.CommandText = """
				INSERT INTO ws_gloves (steamid, team, defindex, paint)
				VALUES (@sid, @team, @def, @paint)
				ON DUPLICATE KEY UPDATE defindex = @def, paint = @paint;
				""";
			command.Parameters.AddWithValue("@def", defIndex);
			command.Parameters.AddWithValue("@paint", paint);
		}
		else
		{
			command.CommandText = "DELETE FROM ws_gloves WHERE steamid = @sid AND team = @team;";
		}

		await command.ExecuteNonQueryAsync();
	}

	private static void AddKey(MySqlCommand command, ulong steamId, CsTeam team, int defIndex, int paint)
	{
		command.Parameters.AddWithValue("@sid", steamId);
		command.Parameters.AddWithValue("@team", (sbyte)team);
		command.Parameters.AddWithValue("@def", defIndex);
		command.Parameters.AddWithValue("@paint", paint);
	}

	private static WeaponSnapshot Snapshot(WeaponEntry entry)
	{
		return new WeaponSnapshot(entry.Paint, entry.Wear, entry.Seed, entry.NameTag, entry.StatTrak);
	}

	private static StickerSnapshot[] Snapshot(List<StickerEntry> stickers)
	{
		return stickers
			.Where(sticker => sticker.Slot is >= 0 and <= 5 && sticker.Id > 0)
			.GroupBy(sticker => sticker.Slot)
			.Select(group => group.Last())
			.Select(sticker => new StickerSnapshot(
				sticker.Slot,
				sticker.Id,
				sticker.Wear,
				sticker.Scale,
				sticker.Rotation,
				sticker.OffsetX,
				sticker.OffsetY,
				sticker.Schema))
			.ToArray();
	}

	private static CharmSnapshot? Snapshot(CharmEntry? charm)
	{
		return charm == null
			? null
			: new CharmSnapshot(
				charm.Id,
				charm.Pattern,
				charm.Sticker,
				charm.Highlight,
				charm.OffsetX,
				charm.OffsetY,
				charm.OffsetZ);
	}
}
