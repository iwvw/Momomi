namespace Momomi.Data;

public sealed record SettingsRepository(MomomiDatabase Database)
{
    public async Task<string?> GetAsync(string key)
    {
        return await Database.ExecuteReadAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT value FROM app_settings WHERE key = $key;";
            cmd.Parameters.AddWithValue("$key", key);
            var result = await cmd.ExecuteScalarAsync();
            return result as string;
        }).ConfigureAwait(false);
    }

    public async Task SetAsync(string key, string value)
    {
        await Database.ExecuteWriteAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO app_settings (key, value, updated_at)
                VALUES ($key, $value, $ts)
                ON CONFLICT(key) DO UPDATE SET value = excluded.value, updated_at = excluded.updated_at;
                """;
            cmd.Parameters.AddWithValue("$key", key);
            cmd.Parameters.AddWithValue("$value", value);
            cmd.Parameters.AddWithValue("$ts", DateTimeOffset.Now.ToString("O"));
            await cmd.ExecuteNonQueryAsync();
            return 0;
        }).ConfigureAwait(false);
    }

    public async Task RemoveAsync(string key)
    {
        await Database.ExecuteWriteAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM app_settings WHERE key = $key;";
            cmd.Parameters.AddWithValue("$key", key);
            await cmd.ExecuteNonQueryAsync();
            return 0;
        }).ConfigureAwait(false);
    }
}

public sealed record ProfileRecord(
    long Id,
    string Name,
    string Kind,
    string? Source,
    string FilePath,
    string? SubscriptionUserInfo,
    bool IsActive,
    int SortOrder,
    string CreatedAt,
    string UpdatedAt);

public sealed record ProfileRepository(MomomiDatabase Database)
{
    public async Task<IReadOnlyList<ProfileRecord>> ListAsync()
    {
        return await Database.ExecuteReadAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT id, name, kind, source, file_path, subscription_userinfo, is_active, sort_order, created_at, updated_at
                FROM profile
                ORDER BY sort_order ASC, id ASC;
                """;
            var list = new List<ProfileRecord>();
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                list.Add(new ProfileRecord(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.GetInt64(6) != 0,
                    reader.GetInt32(7),
                    reader.GetString(8),
                    reader.GetString(9)));
            }
            return (IReadOnlyList<ProfileRecord>)list;
        }).ConfigureAwait(false);
    }

    public async Task<ProfileRecord?> GetActiveAsync()
    {
        var all = await ListAsync().ConfigureAwait(false);
        return all.FirstOrDefault(p => p.IsActive) ?? all.FirstOrDefault();
    }

    public async Task<long> InsertAsync(string name, string kind, string? source, string filePath)
    {
        return await Database.ExecuteWriteAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO profile (name, kind, source, file_path, subscription_userinfo, is_active, sort_order, created_at, updated_at)
                VALUES ($name, $kind, $source, $filePath, NULL, 0, $sort, $ts, $ts);
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("$name", name);
            cmd.Parameters.AddWithValue("$kind", kind);
            cmd.Parameters.AddWithValue("$source", (object?)source ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$filePath", filePath);
            cmd.Parameters.AddWithValue("$sort", (int)DateTimeOffset.Now.ToUnixTimeSeconds());
            cmd.Parameters.AddWithValue("$ts", DateTimeOffset.Now.ToString("O"));
            var result = await cmd.ExecuteScalarAsync();
            return Convert.ToInt64(result);
        }).ConfigureAwait(false);
    }

    public async Task UpdateContentAsync(long id, string? subscriptionUserInfo = null)
    {
        await Database.ExecuteWriteAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE profile
                SET updated_at = $ts,
                    subscription_userinfo = COALESCE($info, subscription_userinfo)
                WHERE id = $id;
                """;
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$ts", DateTimeOffset.Now.ToString("O"));
            cmd.Parameters.AddWithValue("$info", (object?)subscriptionUserInfo ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync();
            return 0;
        }).ConfigureAwait(false);
    }

    public async Task RenameAsync(long id, string name)
    {
        await Database.ExecuteWriteAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE profile SET name = $name, updated_at = $ts WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$name", name);
            cmd.Parameters.AddWithValue("$ts", DateTimeOffset.Now.ToString("O"));
            await cmd.ExecuteNonQueryAsync();
            return 0;
        }).ConfigureAwait(false);
    }

    public async Task SetActiveAsync(long id)
    {
        await Database.ExecuteWriteAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE profile SET is_active = CASE WHEN id = $id THEN 1 ELSE 0 END;";
            cmd.Parameters.AddWithValue("$id", id);
            await cmd.ExecuteNonQueryAsync();
            return 0;
        }).ConfigureAwait(false);
    }

    public async Task DeleteAsync(long id)
    {
        await Database.ExecuteWriteAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM profile WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", id);
            await cmd.ExecuteNonQueryAsync();
            return 0;
        }).ConfigureAwait(false);
    }
}

public sealed record OverrideRecord(
    long Id,
    string Name,
    string Kind,
    string? Source,
    string FilePath,
    bool IsGlobal,
    int SortOrder,
    string CreatedAt,
    string UpdatedAt);

public sealed record OverrideRepository(MomomiDatabase Database)
{
    public async Task<IReadOnlyList<OverrideRecord>> ListAsync()
    {
        return await Database.ExecuteReadAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT id, name, kind, source, file_path, is_global, sort_order, created_at, updated_at
                FROM override_file
                ORDER BY sort_order ASC, id ASC;
                """;
            var list = new List<OverrideRecord>();
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                list.Add(new OverrideRecord(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.GetString(4),
                    reader.GetInt64(5) != 0,
                    reader.GetInt32(6),
                    reader.GetString(7),
                    reader.GetString(8)));
            }
            return (IReadOnlyList<OverrideRecord>)list;
        }).ConfigureAwait(false);
    }

    public async Task<long> InsertAsync(string name, string kind, string? source, string filePath, bool isGlobal)
    {
        return await Database.ExecuteWriteAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO override_file (name, kind, source, file_path, is_global, sort_order, created_at, updated_at)
                VALUES ($name, $kind, $source, $filePath, $global, $sort, $ts, $ts);
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("$name", name);
            cmd.Parameters.AddWithValue("$kind", kind);
            cmd.Parameters.AddWithValue("$source", (object?)source ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$filePath", filePath);
            cmd.Parameters.AddWithValue("$global", isGlobal ? 1 : 0);
            cmd.Parameters.AddWithValue("$sort", (int)DateTimeOffset.Now.ToUnixTimeSeconds());
            cmd.Parameters.AddWithValue("$ts", DateTimeOffset.Now.ToString("O"));
            var result = await cmd.ExecuteScalarAsync();
            return Convert.ToInt64(result);
        }).ConfigureAwait(false);
    }

    public async Task RenameAsync(long id, string name)
    {
        await Database.ExecuteWriteAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE override_file SET name = $name, updated_at = $ts WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$name", name);
            cmd.Parameters.AddWithValue("$ts", DateTimeOffset.Now.ToString("O"));
            await cmd.ExecuteNonQueryAsync();
            return 0;
        }).ConfigureAwait(false);
    }

    public async Task SetGlobalAsync(long id, bool isGlobal)
    {
        await Database.ExecuteWriteAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE override_file SET is_global = $global, updated_at = $ts WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$global", isGlobal ? 1 : 0);
            cmd.Parameters.AddWithValue("$ts", DateTimeOffset.Now.ToString("O"));
            await cmd.ExecuteNonQueryAsync();
            return 0;
        }).ConfigureAwait(false);
    }

    public async Task TouchAsync(long id)
    {
        await Database.ExecuteWriteAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE override_file SET updated_at = $ts WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$ts", DateTimeOffset.Now.ToString("O"));
            await cmd.ExecuteNonQueryAsync();
            return 0;
        }).ConfigureAwait(false);
    }

    public async Task DeleteAsync(long id)
    {
        await Database.ExecuteWriteAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                DELETE FROM override_file WHERE id = $id;
                DELETE FROM profile_override WHERE override_id = $id;
                """;
            cmd.Parameters.AddWithValue("$id", id);
            await cmd.ExecuteNonQueryAsync();
            return 0;
        }).ConfigureAwait(false);
    }

    /// <summary>读取某个订阅挂载的覆写 ID（按挂载顺序）。</summary>
    public async Task<IReadOnlyList<long>> GetProfileOverridesAsync(long profileId)
    {
        return await Database.ExecuteReadAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT override_id FROM profile_override
                WHERE profile_id = $pid
                ORDER BY sort_order ASC, override_id ASC;
                """;
            cmd.Parameters.AddWithValue("$pid", profileId);
            var list = new List<long>();
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync()) list.Add(reader.GetInt64(0));
            return (IReadOnlyList<long>)list;
        }).ConfigureAwait(false);
    }

    /// <summary>整体替换某个订阅的覆写挂载列表（顺序即合并顺序）。</summary>
    public async Task SetProfileOverridesAsync(long profileId, IReadOnlyList<long> overrideIds)
    {
        await Database.ExecuteWriteAsync(async conn =>
        {
            await using (var del = conn.CreateCommand())
            {
                del.CommandText = "DELETE FROM profile_override WHERE profile_id = $pid;";
                del.Parameters.AddWithValue("$pid", profileId);
                await del.ExecuteNonQueryAsync();
            }

            for (var i = 0; i < overrideIds.Count; i++)
            {
                await using var ins = conn.CreateCommand();
                ins.CommandText = """
                    INSERT OR REPLACE INTO profile_override (profile_id, override_id, sort_order)
                    VALUES ($pid, $oid, $sort);
                    """;
                ins.Parameters.AddWithValue("$pid", profileId);
                ins.Parameters.AddWithValue("$oid", overrideIds[i]);
                ins.Parameters.AddWithValue("$sort", i);
                await ins.ExecuteNonQueryAsync();
            }
            return 0;
        }).ConfigureAwait(false);
    }
}

public sealed record TrafficMinute(long Id, string Bucket, long Up, long Down, long Memory, int Connections);
public sealed record TrafficRepository(MomomiDatabase Database)
{
    public async Task InsertAsync(string bucket, long up, long down, long memory, int connections)
    {
        await Database.ExecuteWriteAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO traffic_minute (bucket, up, down, memory, connections)
                VALUES ($bucket, $up, $down, $memory, $conns);
                """;
            cmd.Parameters.AddWithValue("$bucket", bucket);
            cmd.Parameters.AddWithValue("$up", up);
            cmd.Parameters.AddWithValue("$down", down);
            cmd.Parameters.AddWithValue("$memory", memory);
            cmd.Parameters.AddWithValue("$conns", connections);
            await cmd.ExecuteNonQueryAsync();
            return 0;
        }).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<TrafficMinute>> QueryAsync(DateTimeOffset from, int limit = 1440)
    {
        return await Database.ExecuteReadAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT id, bucket, up, down, memory, connections
                FROM traffic_minute
                WHERE bucket >= $from
                ORDER BY bucket DESC
                LIMIT $limit;
                """;
            cmd.Parameters.AddWithValue("$from", from.ToString("O"));
            cmd.Parameters.AddWithValue("$limit", limit);

            var list = new List<TrafficMinute>();
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                list.Add(new TrafficMinute(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.GetInt64(2),
                    reader.GetInt64(3),
                    reader.GetInt64(4),
                    reader.GetInt32(5)));
            }
            return (IReadOnlyList<TrafficMinute>)list;
        }).ConfigureAwait(false);
    }

    public async Task PruneAsync(DateTimeOffset before)
    {
        await Database.ExecuteWriteAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM traffic_minute WHERE bucket < $before;";
            cmd.Parameters.AddWithValue("$before", before.ToString("O"));
            await cmd.ExecuteNonQueryAsync();
            return 0;
        }).ConfigureAwait(false);
    }
}
