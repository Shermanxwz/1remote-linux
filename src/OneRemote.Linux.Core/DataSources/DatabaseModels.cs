using Microsoft.Data.Sqlite;
using MySqlConnector;
using Newtonsoft.Json;
using Npgsql;
using NUlid;
using OneRemote.Linux.Core.Models;

namespace OneRemote.Linux.Core.DataSources;

public enum DatabaseType
{
    MySql,
    SqlServer,
    PostgreSQL,
    Oracle,
    Sqlite,
}

public enum EnumDatabaseStatus
{
    NotConnectedYet,
    AccessDenied,
    LostConnection,
    OK,
    EncryptKeyError,
    OtherError,
}

public sealed record DatabaseCheckResult(EnumDatabaseStatus Status, string ErrorMessage = "")
{
    public bool IsOk => Status == EnumDatabaseStatus.OK;
}

public class DbResult
{
    public bool IsSuccess { get; init; }
    public string ErrorInfo { get; init; } = string.Empty;

    public static DbResult Success() => new() { IsSuccess = true };
    public static DbResult Fail(string error) => new() { IsSuccess = false, ErrorInfo = error };
}

public sealed class DbResult<T> : DbResult
{
    public T? Value { get; init; }

    public static DbResult<T> Ok(T? value) => new() { IsSuccess = true, Value = value };
    public new static DbResult<T> Fail(string error) => new() { IsSuccess = false, ErrorInfo = error };
}

public sealed class TableConfig
{
    public const string TABLE_NAME = "Configs";
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}

public sealed class TableServer
{
    public const string TABLE_NAME = "Servers";
    public string Id { get; set; } = string.Empty;
    public string Protocol { get; set; } = string.Empty;
    public string ClassVersion { get; set; } = string.Empty;
    public string Json { get; set; } = string.Empty;

    public static TableServer FromProtocol(ProtocolBase server) => new()
    {
        Id = server.Id,
        Protocol = server.Protocol,
        ClassVersion = server.ClassVersion,
        Json = server.ToJsonString(),
    };
}

public sealed class TableCredential
{
    public const string TABLE_NAME = "Credentials";
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Json { get; set; } = string.Empty;
    public string Hash { get; set; } = string.Empty;

    public static TableCredential FromCredential(Credential credential)
    {
        var id = string.IsNullOrEmpty(credential.DatabaseId) ? Ulid.NewUlid().ToString() : credential.DatabaseId;
        var name = string.IsNullOrEmpty(credential.Name) ? id : credential.Name;
        return new TableCredential
        {
            Id = id,
            Name = name,
            Hash = credential.GetHash(),
            Json = JsonConvert.SerializeObject(credential),
        };
    }

    public Credential? ToCredential()
    {
        try
        {
            var c = JsonConvert.DeserializeObject<Credential>(Json);
            if (c != null)
            {
                c.DatabaseId = Id;
                c.Name = Name;
                c.Hash = Hash;
            }
            return c;
        }
        catch
        {
            return null;
        }
    }
}

public static class DbConnectionStrings
{
    public static string BuildSqlite(string dbPath)
    {
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }
        var csb = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
        };
        return csb.ToString();
    }

    public static string BuildMySql(string host, int port, string dbName, string user, string password, int connectTimeoutSeconds = 10)
    {
        var timeout = (uint)Math.Max(connectTimeoutSeconds, 10);
        var csb = new MySqlConnectionStringBuilder
        {
            Server = host,
            Port = (uint)port,
            Database = dbName,
            UserID = user,
            Password = password,
            CharacterSet = "utf8mb4",
            ConnectionTimeout = timeout,
            DefaultCommandTimeout = Math.Max(timeout * 2, 20),
            Pooling = true,
            ConnectionReset = true,
            ConnectionLifeTime = 180,
            Keepalive = 30,
            AllowPublicKeyRetrieval = true,
            SslMode = MySqlSslMode.Preferred,
        };
        return csb.ConnectionString;
    }

    public static string BuildPostgreSql(string host, int port, string dbName, string user, string password, int connectTimeoutSeconds = 10)
    {
        var timeout = Math.Max(connectTimeoutSeconds, 10);
        var csb = new NpgsqlConnectionStringBuilder
        {
            Host = host,
            Port = port,
            Database = dbName,
            Username = user,
            Password = password,
            Timeout = timeout,
            CommandTimeout = Math.Max(timeout * 2, 20),
            KeepAlive = 30,
            Pooling = true,
        };
        return csb.ConnectionString;
    }
}
