using MySqlConnector;

namespace WeaponSkins;

public sealed class Database
{
	private readonly string connectionString;

	public bool Configured { get; }

	public Database(DatabaseConfig config)
	{
		Configured = config.Host.Length > 0 && config.Name.Length > 0 && config.User.Length > 0;

		var builder = new MySqlConnectionStringBuilder
		{
			Server = config.Host,
			Port = config.Port,
			UserID = config.User,
			Password = config.Password,
			Database = config.Name,
			Pooling = true,
			MinimumPoolSize = 0,
			MaximumPoolSize = 10,
			ConnectionTimeout = 30,
			ConnectionIdleTimeout = 30,
			DefaultCommandTimeout = 30
		};

		if (Enum.TryParse<MySqlSslMode>(config.SslMode, true, out var sslMode))
			builder.SslMode = sslMode;

		connectionString = builder.ConnectionString;
	}

	public async Task<MySqlConnection> Open(CancellationToken cancellationToken = default)
	{
		var connection = new MySqlConnection(connectionString);
		try
		{
			await connection.OpenAsync(cancellationToken);
			return connection;
		}
		catch
		{
			await connection.DisposeAsync();
			throw;
		}
	}

	private const int SchemaTimeout = 180;

	private const string WeaponsBody = """
		steamid BIGINT UNSIGNED NOT NULL,
		team TINYINT NOT NULL,
		defindex INT NOT NULL,
		paint INT NOT NULL DEFAULT 0,
		wear FLOAT NOT NULL DEFAULT 0.000001,
		seed INT NOT NULL DEFAULT 0,
		nametag VARCHAR(64) NULL,
		stattrak INT NOT NULL DEFAULT -1,
		PRIMARY KEY (steamid, team, defindex, paint)
		""";

	private const string StickersBody = """
		steamid BIGINT UNSIGNED NOT NULL,
		team TINYINT NOT NULL,
		defindex INT NOT NULL,
		paint INT NOT NULL DEFAULT 0,
		slot TINYINT NOT NULL,
		sticker INT NOT NULL,
		wear FLOAT NOT NULL DEFAULT 0,
		scale FLOAT NOT NULL DEFAULT 1,
		rotation FLOAT NOT NULL DEFAULT 0,
		offset_x FLOAT NOT NULL DEFAULT 0,
		offset_y FLOAT NOT NULL DEFAULT 0,
		schema_slot TINYINT NOT NULL DEFAULT -1,
		PRIMARY KEY (steamid, team, defindex, paint, slot)
		""";

	private const string CharmsBody = """
		steamid BIGINT UNSIGNED NOT NULL,
		team TINYINT NOT NULL,
		defindex INT NOT NULL,
		paint INT NOT NULL DEFAULT 0,
		charm INT NOT NULL,
		pattern INT NOT NULL DEFAULT 0,
		sticker INT NOT NULL DEFAULT 0,
		highlight INT NOT NULL DEFAULT 0,
		offset_x FLOAT NOT NULL DEFAULT 0,
		offset_y FLOAT NOT NULL DEFAULT 0,
		offset_z FLOAT NOT NULL DEFAULT 0,
		PRIMARY KEY (steamid, team, defindex, paint)
		""";

	private const string WeaponsColumns = "steamid, team, defindex, paint, wear, seed, nametag, stattrak";
	private const string StickersColumns = "steamid, team, defindex, paint, slot, sticker, wear, scale, rotation, offset_x, offset_y, schema_slot";
	private const string CharmsColumns = "steamid, team, defindex, paint, charm, pattern, sticker, highlight, offset_x, offset_y, offset_z";

	private const string MusicBody = """
		steamid BIGINT UNSIGNED NOT NULL,
		kit INT NOT NULL,
		PRIMARY KEY (steamid)
		""";

	private const string PinsBody = """
		steamid BIGINT UNSIGNED NOT NULL,
		pin INT NOT NULL,
		PRIMARY KEY (steamid)
		""";

	private const string LinkTables = """
		CREATE TABLE IF NOT EXISTS ws_links (
			steamid BIGINT UNSIGNED NOT NULL,
			discord_id BIGINT UNSIGNED NOT NULL,
			linked_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
			PRIMARY KEY (steamid),
			UNIQUE KEY uq_ws_links_discord (discord_id)
		) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

		CREATE TABLE IF NOT EXISTS ws_link_codes (
			steamid BIGINT UNSIGNED NOT NULL,
			code_hash BINARY(32) NOT NULL,
			expires_at DATETIME NOT NULL,
			created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
			PRIMARY KEY (steamid),
			UNIQUE KEY uq_ws_link_codes_hash (code_hash),
			INDEX ix_ws_link_codes_expiry (expires_at)
		) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
		""";

	public async Task Bootstrap(bool linkTables)
	{
		await using var connection = await Open();
		if (!await AcquireSchemaLock(connection))
			throw new TimeoutException("Timed out waiting for the WeaponSkins schema lock");

		try
		{
			await Resume(connection, "ws_weapons");
			await Resume(connection, "ws_stickers");
			await Resume(connection, "ws_charms");
			await Resume(connection, "ws_music");
			await Resume(connection, "ws_pins");

			await using var command = connection.CreateCommand();
			command.CommandTimeout = SchemaTimeout;
			command.CommandText = $"""
				CREATE TABLE IF NOT EXISTS ws_weapons ({WeaponsBody}) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

				CREATE TABLE IF NOT EXISTS ws_equipped (
					steamid BIGINT UNSIGNED NOT NULL,
					team TINYINT NOT NULL,
					defindex INT NOT NULL,
					paint INT NOT NULL DEFAULT 0,
					PRIMARY KEY (steamid, team, defindex)
				) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

				CREATE TABLE IF NOT EXISTS ws_stickers ({StickersBody}) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

				CREATE TABLE IF NOT EXISTS ws_charms ({CharmsBody}) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

				CREATE TABLE IF NOT EXISTS ws_knives (
					steamid BIGINT UNSIGNED NOT NULL,
					team TINYINT NOT NULL,
					defindex INT NOT NULL,
					PRIMARY KEY (steamid, team)
				) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

				CREATE TABLE IF NOT EXISTS ws_gloves (
					steamid BIGINT UNSIGNED NOT NULL,
					team TINYINT NOT NULL,
					defindex INT NOT NULL,
					paint INT NOT NULL DEFAULT 0,
					PRIMARY KEY (steamid, team)
				) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

				CREATE TABLE IF NOT EXISTS ws_agents (
					steamid BIGINT UNSIGNED NOT NULL,
					team TINYINT NOT NULL,
					model VARCHAR(160) NOT NULL,
					PRIMARY KEY (steamid, team)
				) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

				CREATE TABLE IF NOT EXISTS ws_music ({MusicBody}) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

				CREATE TABLE IF NOT EXISTS ws_pins ({PinsBody}) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

				CREATE TABLE IF NOT EXISTS ws_sync_queue (
					id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
					steamid BIGINT UNSIGNED NOT NULL,
					created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
					PRIMARY KEY (id),
					INDEX ix_ws_sync_steamid (steamid)
				) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

				CREATE TABLE IF NOT EXISTS ws_permissions (
					steamid BIGINT UNSIGNED NOT NULL,
					stickers TINYINT NOT NULL DEFAULT 0,
					gen TINYINT NOT NULL DEFAULT 0,
					updated_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
					PRIMARY KEY (steamid)
				) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

				{(linkTables ? LinkTables : "")}
				""";
			await command.ExecuteNonQueryAsync();
			await Migrate(connection);
		}
		finally
		{
			await ReleaseSchemaLock(connection);
		}
	}

	private static async Task<bool> AcquireSchemaLock(MySqlConnection connection)
	{
		await using var command = connection.CreateCommand();
		command.CommandTimeout = SchemaTimeout + 5;
		command.CommandText = "SELECT GET_LOCK('WeaponSkins.schema', @timeout);";
		command.Parameters.AddWithValue("@timeout", SchemaTimeout);
		return Convert.ToInt32(await command.ExecuteScalarAsync() ?? 0) == 1;
	}

	private static async Task ReleaseSchemaLock(MySqlConnection connection)
	{
		try
		{
			await using var command = connection.CreateCommand();
			command.CommandTimeout = 5;
			command.CommandText = "SELECT RELEASE_LOCK('WeaponSkins.schema');";
			await command.ExecuteScalarAsync();
		}
		catch
		{
		}
	}

	private static async Task Migrate(MySqlConnection connection)
	{
		await AddColumn(connection, "ws_permissions", "gen", "TINYINT NOT NULL DEFAULT 0 AFTER stickers");

		await AddColumn(connection, "ws_stickers", "paint", "INT NOT NULL DEFAULT 0 AFTER defindex");
		if (!await HasPrimaryKeyColumn(connection, "ws_stickers", "paint"))
			await Execute(connection, """
				UPDATE ws_stickers s
				JOIN ws_weapons w ON w.steamid = s.steamid AND w.team = s.team AND w.defindex = s.defindex
				SET s.paint = w.paint;
				""");

		await AddColumn(connection, "ws_charms", "paint", "INT NOT NULL DEFAULT 0 AFTER defindex");
		if (!await HasPrimaryKeyColumn(connection, "ws_charms", "paint"))
			await Execute(connection, """
				UPDATE ws_charms c
				JOIN ws_weapons w ON w.steamid = c.steamid AND w.team = c.team AND w.defindex = c.defindex
				SET c.paint = w.paint;
				""");

		await RebuildTable(connection, "ws_stickers", StickersBody, StickersColumns);
		await RebuildTable(connection, "ws_charms", CharmsBody, CharmsColumns);
		await RebuildTable(connection, "ws_weapons", WeaponsBody, WeaponsColumns);

		if (await HasColumn(connection, "ws_gloves", "wear"))
		{
			await Execute(connection, """
				INSERT IGNORE INTO ws_weapons (steamid, team, defindex, paint, wear, seed)
				SELECT steamid, team, defindex, paint, wear, seed FROM ws_gloves WHERE defindex > 0;
				""");
			await Execute(connection, "ALTER TABLE ws_gloves DROP COLUMN wear;");
			await Execute(connection, "ALTER TABLE ws_gloves DROP COLUMN seed;");
		}

		await Execute(connection, """
			INSERT IGNORE INTO ws_equipped (steamid, team, defindex, paint)
			SELECT steamid, team, defindex, paint FROM ws_weapons;
			""");

		await Collapse(connection, "ws_music", MusicBody, "kit");
		await Collapse(connection, "ws_pins", PinsBody, "pin");
	}

	private static async Task Collapse(MySqlConnection connection, string table, string body, string column)
	{
		if (!await HasColumn(connection, table, "team"))
		{
			await Execute(connection, $"DROP TABLE IF EXISTS {table}_old;");
			return;
		}

		await Swap(connection, table, body, $"INSERT INTO {table}_new (steamid, {column}) SELECT steamid, MAX({column}) FROM {table} WHERE {column} > 0 GROUP BY steamid;");
	}

	private static async Task Swap(MySqlConnection connection, string table, string body, string copy)
	{
		await Execute(connection, $"DROP TABLE IF EXISTS {table}_new;");
		await Execute(connection, $"DROP TABLE IF EXISTS {table}_old;");
		await Execute(connection, $"CREATE TABLE {table}_new ({body}) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;");
		await Execute(connection, copy);
		await Execute(connection, $"RENAME TABLE {table} TO {table}_old, {table}_new TO {table};");
		await Execute(connection, $"DROP TABLE {table}_old;");
	}

	private static async Task Resume(MySqlConnection connection, string table)
	{
		if (await Exists(connection, table))
			return;

		if (await Exists(connection, $"{table}_new"))
			await Execute(connection, $"RENAME TABLE {table}_new TO {table};");
		else if (await Exists(connection, $"{table}_old"))
			await Execute(connection, $"RENAME TABLE {table}_old TO {table};");
	}

	private static async Task AddColumn(MySqlConnection connection, string table, string column, string definition)
	{
		if (await HasColumn(connection, table, column))
			return;

		await Execute(connection, $"ALTER TABLE {table} ADD COLUMN {column} {definition};");
	}

	private static async Task<bool> HasColumn(MySqlConnection connection, string table, string column)
	{
		return await Scalar(connection, """
			SELECT COUNT(*) FROM information_schema.COLUMNS
			WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @table AND COLUMN_NAME = @column;
			""", table, column) > 0;
	}

	private static async Task<bool> HasPrimaryKeyColumn(MySqlConnection connection, string table, string column)
	{
		return await Scalar(connection, """
			SELECT COUNT(*) FROM information_schema.KEY_COLUMN_USAGE
			WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @table
				AND CONSTRAINT_NAME = 'PRIMARY' AND COLUMN_NAME = @column;
			""", table, column) > 0;
	}

	private static async Task RebuildTable(MySqlConnection connection, string table, string body, string columns)
	{
		if (await HasPrimaryKeyColumn(connection, table, "paint"))
		{
			await Execute(connection, $"DROP TABLE IF EXISTS {table}_old;");
			return;
		}

		await Swap(connection, table, body, $"INSERT INTO {table}_new ({columns}) SELECT {columns} FROM {table};");
	}

	private static async Task<bool> Exists(MySqlConnection connection, string table)
	{
		return await Scalar(connection, """
			SELECT COUNT(*) FROM information_schema.TABLES
			WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @table;
			""", table, "") > 0;
	}

	private static async Task<long> Scalar(MySqlConnection connection, string sql, string table, string column)
	{
		await using var command = connection.CreateCommand();
		command.CommandTimeout = SchemaTimeout;
		command.CommandText = sql;
		command.Parameters.AddWithValue("@table", table);
		command.Parameters.AddWithValue("@column", column);
		return Convert.ToInt64(await command.ExecuteScalarAsync() ?? 0L);
	}

	private static async Task Execute(MySqlConnection connection, string sql)
	{
		await using var command = connection.CreateCommand();
		command.CommandTimeout = SchemaTimeout;
		command.CommandText = sql;
		await command.ExecuteNonQueryAsync();
	}
}
