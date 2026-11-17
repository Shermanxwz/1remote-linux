using System.Data;
using System.Data.Common;
using Dapper;
using Microsoft.Data.Sqlite;
using MySqlConnector;
using Npgsql;
using NUlid;
using OneRemote.Linux.Core.Models;
using OneRemote.Linux.Core.Security;

namespace OneRemote.Linux.Core.DataSources;

/// <summary>
/// Thread-safe, connection-pool-friendly Dapper database engine compatible with 1Remote v1.3.2
/// (Configs, Servers, Credentials tables, EncryptionTest validation, and ClearPool stale-socket recovery).
/// </summary>
public sealed class DapperDatabaseFree
{
    private readonly object _syncLock = new();
    private string _connectionString = string.Empty;

    public string DatabaseName { get; set; }
    public DatabaseType DatabaseType { get; }

    public DapperDatabaseFree(string databaseName, DatabaseType databaseType)
    {
        DatabaseName = databaseName;
        DatabaseType = databaseType;
    }

    public void ConfigureConnectionString(string connectionString)
    {
        lock (_syncLock)
        {
            _connectionString = connectionString;
        }
    }

    private string NormalizeSql(string sql) =>
        DatabaseType == DatabaseType.PostgreSQL ? sql.Replace('`', '"') : sql;

    private DbConnection CreateConnection()
    {
        return DatabaseType switch
        {
            DatabaseType.MySql => new MySqlConnection(_connectionString),
            DatabaseType.Sqlite => new SqliteConnection(_connectionString),
            DatabaseType.PostgreSQL => new NpgsqlConnection(_connectionString),
            _ => throw new NotSupportedException($"Database type {DatabaseType} is not supported.")
        };
    }

    private void ClearStalePool(DbConnection conn)
    {
        try
        {
            if (conn is MySqlConnection mysql)
                MySqlConnection.ClearPool(mysql);
            else if (conn is NpgsqlConnection npgsql)
                NpgsqlConnection.ClearPool(npgsql);
            else if (conn is SqliteConnection sqlite)
                SqliteConnection.ClearPool(sqlite);
        }
        catch
        {
            // Ignore pool clear errors
        }
    }

    private T ExecuteWithRetry<T>(Func<IDbConnection, T> action, Func<string, T> onFail)
    {
        lock (_syncLock)
        {
            if (string.IsNullOrWhiteSpace(_connectionString))
                return onFail("Database connection string is empty.");

            for (var attempt = 0; attempt < 2; attempt++)
            {
                DbConnection? conn = null;
                try
                {
                    conn = CreateConnection();
                    conn.Open();
                    var res = action(conn);
                    conn.Close();
                    return res;
                }
                catch (Exception ex)
                {
                    if (conn != null && DatabaseType is DatabaseType.MySql or DatabaseType.PostgreSQL)
                    {
                        ClearStalePool(conn);
                    }
                    if (attempt == 1 || DatabaseType == DatabaseType.Sqlite)
                    {
                        return onFail(ex.Message);
                    }
                }
                finally
                {
                    try { conn?.Dispose(); } catch { /* ignore */ }
                }
            }

            return onFail("Database operation failed after retry.");
        }
    }

    public DbResult TestOpen()
    {
        return ExecuteWithRetry(
            _ => DbResult.Success(),
            DbResult.Fail);
    }

    public DbResult InitTables()
    {
        return ExecuteWithRetry(conn =>
        {
            conn.Execute(NormalizeSql($@"
CREATE TABLE IF NOT EXISTS `{TableConfig.TABLE_NAME}` (
    `{nameof(TableConfig.Key)}` VARCHAR(64) PRIMARY KEY NOT NULL UNIQUE,
    `{nameof(TableConfig.Value)}` TEXT NOT NULL
);"));

            conn.Execute(NormalizeSql($@"
CREATE TABLE IF NOT EXISTS `{TableServer.TABLE_NAME}` (
    `{nameof(TableServer.Id)}` VARCHAR(64) PRIMARY KEY NOT NULL UNIQUE,
    `{nameof(TableServer.Protocol)}` VARCHAR(32) NOT NULL,
    `{nameof(TableServer.ClassVersion)}` VARCHAR(32) NOT NULL,
    `{nameof(TableServer.Json)}` TEXT NOT NULL
);"));

            conn.Execute(NormalizeSql($@"
CREATE TABLE IF NOT EXISTS `{TableCredential.TABLE_NAME}` (
    `{nameof(TableCredential.Id)}` VARCHAR(64) PRIMARY KEY NOT NULL UNIQUE,
    `{nameof(TableCredential.Name)}` VARCHAR(128) NOT NULL UNIQUE,
    `{nameof(TableCredential.Hash)}` VARCHAR(32) NOT NULL,
    `{nameof(TableCredential.Json)}` TEXT NOT NULL
);"));

            var encTest = QueryConfigInternal(conn, "EncryptionTest");
            if (string.IsNullOrEmpty(encTest))
            {
                UpsertConfigInternal(conn, "EncryptionTest", UnSafeStringEncipher.SimpleEncrypt("EncryptionTest"));
            }

            return DbResult.Success();
        }, err =>
        {
            // Fallback for read-only MySQL accounts if tables already exist
            var readCheck = GetConfig("EncryptionTest");
            return readCheck.IsSuccess ? DbResult.Success() : DbResult.Fail(err);
        });
    }

    private string? QueryConfigInternal(IDbConnection conn, string key)
    {
        var row = conn.QueryFirstOrDefault<TableConfig>(
            NormalizeSql($"SELECT * FROM `{TableConfig.TABLE_NAME}` WHERE `{nameof(TableConfig.Key)}` = @Key"),
            new { Key = key });
        return row?.Value;
    }

    private void UpsertConfigInternal(IDbConnection conn, string key, string? value)
    {
        if (value == null)
        {
            conn.Execute(
                NormalizeSql($"DELETE FROM `{TableConfig.TABLE_NAME}` WHERE `{nameof(TableConfig.Key)}` = @Key"),
                new { Key = key });
            return;
        }

        var existing = QueryConfigInternal(conn, key);
        if (!string.IsNullOrEmpty(existing))
        {
            conn.Execute(
                NormalizeSql($"UPDATE `{TableConfig.TABLE_NAME}` SET `{nameof(TableConfig.Value)}` = @Value WHERE `{nameof(TableConfig.Key)}` = @Key"),
                new { Key = key, Value = value });
        }
        else
        {
            conn.Execute(
                NormalizeSql($"INSERT INTO `{TableConfig.TABLE_NAME}` (`{nameof(TableConfig.Key)}`, `{nameof(TableConfig.Value)}`) VALUES (@Key, @Value)"),
                new { Key = key, Value = value });
        }
    }

    private void TouchTableTimestampInternal(IDbConnection conn, string tableName)
    {
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
        UpsertConfigInternal(conn, "UpdateTimestamp_" + tableName, nowMs);
    }

    public DbResult<string?> GetConfig(string key)
    {
        return ExecuteWithRetry(
            conn => DbResult<string?>.Ok(QueryConfigInternal(conn, key)),
            DbResult<string?>.Fail);
    }

    public DbResult SetConfig(string key, string? value)
    {
        return ExecuteWithRetry(conn =>
        {
            UpsertConfigInternal(conn, key, value);
            return DbResult.Success();
        }, DbResult.Fail);
    }

    public bool CheckWritable()
    {
        return SetConfig("permission_check", null).IsSuccess &&
               SetConfig("permission_check", "true").IsSuccess;
    }

    public bool CheckEncryptionTest()
    {
        var res = GetConfig("EncryptionTest");
        if (!res.IsSuccess || string.IsNullOrEmpty(res.Value))
            return true;
        return UnSafeStringEncipher.SimpleDecrypt(res.Value) == "EncryptionTest";
    }

    public DbResult<long> GetTableUpdateTimestamp(string tableName)
    {
        var res = GetConfig("UpdateTimestamp_" + tableName);
        if (!res.IsSuccess)
            return DbResult<long>.Fail(res.ErrorInfo);
        if (long.TryParse(res.Value, out var ts) && ts > 0)
            return DbResult<long>.Ok(ts);
        return DbResult<long>.Ok(0);
    }

    public DbResult<List<ProtocolBase>> GetServers()
    {
        return ExecuteWithRetry(conn =>
        {
            var rows = conn.Query<TableServer>(NormalizeSql($"SELECT * FROM `{TableServer.TABLE_NAME}`"));
            var list = rows
                .Select(ItemCreateHelper.CreateFromDbRow)
                .Where(x => x != null)
                .Cast<ProtocolBase>()
                .ToList();
            return DbResult<List<ProtocolBase>>.Ok(list);
        }, DbResult<List<ProtocolBase>>.Fail);
    }

    public DbResult AddServer(ProtocolBase server)
    {
        return ExecuteWithRetry(conn =>
        {
            if (server.IsTmpSession())
                server.Id = Ulid.NewUlid().ToString();

            var clone = server.Clone();
            clone.SetNotifyPropertyChangedEnabled(false);
            clone.Id = server.Id;
            clone.EncryptToDatabaseLevel();

            var sql = NormalizeSql($@"INSERT INTO `{TableServer.TABLE_NAME}`
(`{nameof(TableServer.Id)}`, `{nameof(TableServer.Protocol)}`, `{nameof(TableServer.ClassVersion)}`, `{nameof(TableServer.Json)}`)
VALUES (@Id, @Protocol, @ClassVersion, @Json);");

            var affected = conn.Execute(sql, TableServer.FromProtocol(clone));
            if (affected <= 0)
                return DbResult.Fail("Insert server affected 0 rows.");

            TouchTableTimestampInternal(conn, TableServer.TABLE_NAME);
            return DbResult.Success();
        }, DbResult.Fail);
    }

    public DbResult UpdateServer(ProtocolBase server)
    {
        return ExecuteWithRetry(conn =>
        {
            var clone = server.Clone();
            clone.SetNotifyPropertyChangedEnabled(false);
            clone.Id = server.Id;
            clone.EncryptToDatabaseLevel();

            var sql = NormalizeSql($@"UPDATE `{TableServer.TABLE_NAME}` SET
`{nameof(TableServer.Protocol)}` = @Protocol,
`{nameof(TableServer.ClassVersion)}` = @ClassVersion,
`{nameof(TableServer.Json)}` = @Json
WHERE `{nameof(TableServer.Id)}` = @Id;");

            var affected = conn.Execute(sql, TableServer.FromProtocol(clone));
            if (affected <= 0)
                return DbResult.Fail("Update server affected 0 rows.");

            TouchTableTimestampInternal(conn, TableServer.TABLE_NAME);
            return DbResult.Success();
        }, DbResult.Fail);
    }

    public DbResult DeleteServers(IEnumerable<string> ids)
    {
        var idArray = ids.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToArray();
        if (idArray.Length == 0)
            return DbResult.Success();

        return ExecuteWithRetry(conn =>
        {
            var sql = DatabaseType == DatabaseType.PostgreSQL
                ? $"DELETE FROM `{TableServer.TABLE_NAME}` WHERE `{nameof(TableServer.Id)}` = ANY(@Ids);"
                : $"DELETE FROM `{TableServer.TABLE_NAME}` WHERE `{nameof(TableServer.Id)}` IN @Ids;";

            var affected = conn.Execute(NormalizeSql(sql), new { Ids = idArray });
            if (affected > 0)
            {
                TouchTableTimestampInternal(conn, TableServer.TABLE_NAME);
            }
            return DbResult.Success();
        }, DbResult.Fail);
    }

    public DbResult<List<Credential>> GetCredentials()
    {
        return ExecuteWithRetry(conn =>
        {
            var rows = conn.Query<TableCredential>(NormalizeSql($"SELECT * FROM `{TableCredential.TABLE_NAME}`"));
            var items = rows
                .Select(r => r.ToCredential())
                .Where(c => c != null)
                .Cast<Credential>()
                .ToList();
            return DbResult<List<Credential>>.Ok(items);
        }, DbResult<List<Credential>>.Fail);
    }

    public DbResult UpsertCredential(Credential credential)
    {
        return ExecuteWithRetry(conn =>
        {
            var clone = credential.CloneMe();
            clone.SetNotifyPropertyChangedEnabled(false);
            clone.GetHash();
            clone.EncryptToDatabaseLevel();
            var row = TableCredential.FromCredential(clone);

            var existing = conn.QueryFirstOrDefault<TableCredential>(
                NormalizeSql($"SELECT * FROM `{TableCredential.TABLE_NAME}` WHERE `{nameof(TableCredential.Name)}` = @Name"),
                new { row.Name });

            if (existing != null)
            {
                row.Id = existing.Id;
                conn.Execute(NormalizeSql($@"UPDATE `{TableCredential.TABLE_NAME}` SET
`{nameof(TableCredential.Hash)}` = @Hash,
`{nameof(TableCredential.Json)}` = @Json
WHERE `{nameof(TableCredential.Id)}` = @Id;"), row);
                credential.DatabaseId = existing.Id;
            }
            else
            {
                conn.Execute(NormalizeSql($@"INSERT INTO `{TableCredential.TABLE_NAME}`
(`{nameof(TableCredential.Id)}`, `{nameof(TableCredential.Name)}`, `{nameof(TableCredential.Hash)}`, `{nameof(TableCredential.Json)}`)
VALUES (@Id, @Name, @Hash, @Json);"), row);
                credential.DatabaseId = row.Id;
            }

            TouchTableTimestampInternal(conn, TableCredential.TABLE_NAME);
            return DbResult.Success();
        }, DbResult.Fail);
    }
}
