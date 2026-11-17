using System.Collections.Concurrent;
using JsonKnownTypes;
using Newtonsoft.Json;
using OneRemote.Linux.Core.Models;
using OneRemote.Linux.Core.Security;

namespace OneRemote.Linux.Core.DataSources;

[JsonConverter(typeof(JsonKnownTypesConverter<DataSourceBase>))]
[JsonKnownType(typeof(MysqlSource), nameof(MysqlSource))]
[JsonKnownType(typeof(SqliteSource), nameof(SqliteSource))]
[JsonKnownType(typeof(PgsqlSource), nameof(PgsqlSource))]
public abstract class DataSourceBase : NotifyBase
{
    private readonly Dictionary<string, long> _lastReadTimestamps = new();
    private DapperDatabaseFree? _db;

    private string _dataSourceName = string.Empty;
    public string DataSourceName
    {
        get => _dataSourceName;
        set => SetAndNotifyIfChanged(ref _dataSourceName, value ?? string.Empty);
    }

    private EnumDatabaseStatus _status = EnumDatabaseStatus.NotConnectedYet;
    [JsonIgnore]
    public EnumDatabaseStatus Status
    {
        get => _status;
        protected set => SetAndNotifyIfChanged(ref _status, value);
    }

    private string _statusMessage = string.Empty;
    [JsonIgnore]
    public string StatusMessage
    {
        get => _statusMessage;
        protected set => SetAndNotifyIfChanged(ref _statusMessage, value);
    }

    [JsonIgnore]
    public bool IsWritable { get; protected set; } = true;

    [JsonIgnore]
    public List<ProtocolBase> CachedServers { get; private set; } = new();

    [JsonIgnore]
    public List<Credential> CachedCredentials { get; private set; } = new();

    [JsonIgnore]
    public abstract DatabaseType DatabaseType { get; }

    [JsonIgnore]
    public abstract string Description { get; }

    public abstract string GetConnectionString(int connectTimeoutSeconds = 10);

    public DapperDatabaseFree GetDatabase()
    {
        lock (this)
        {
            _db ??= new DapperDatabaseFree(DataSourceName, DatabaseType);
            _db.DatabaseName = DataSourceName;
            _db.ConfigureConnectionString(GetConnectionString(10));
            return _db;
        }
    }

    public void ClearTimestamps()
    {
        lock (this)
        {
            _lastReadTimestamps.Clear();
        }
    }

    public DatabaseCheckResult SelfCheck(int connectTimeoutSeconds = 10)
    {
        lock (this)
        {
            var db = GetDatabase();
            db.ConfigureConnectionString(GetConnectionString(connectTimeoutSeconds));

            var open = db.TestOpen();
            if (!open.IsSuccess)
            {
                Status = Status == EnumDatabaseStatus.NotConnectedYet
                    ? EnumDatabaseStatus.AccessDenied
                    : EnumDatabaseStatus.LostConnection;
                StatusMessage = open.ErrorInfo;
                return new DatabaseCheckResult(Status, open.ErrorInfo);
            }

            var init = db.InitTables();
            if (!init.IsSuccess)
            {
                Status = EnumDatabaseStatus.OtherError;
                StatusMessage = init.ErrorInfo;
                return new DatabaseCheckResult(Status, init.ErrorInfo);
            }

            IsWritable = db.CheckWritable();

            if (!db.CheckEncryptionTest())
            {
                Status = EnumDatabaseStatus.EncryptKeyError;
                StatusMessage = "Encryption salt mismatch with existing database EncryptionTest.";
                return new DatabaseCheckResult(Status, StatusMessage);
            }

            Status = EnumDatabaseStatus.OK;
            StatusMessage = "Connected";
            return new DatabaseCheckResult(EnumDatabaseStatus.OK);
        }
    }

    public bool NeedReload(string tableName)
    {
        lock (this)
        {
            if (!_lastReadTimestamps.TryGetValue(tableName, out var localTs) || localTs <= 0)
                return true;
            if (Status != EnumDatabaseStatus.OK)
                return true;

            var remoteTs = GetDatabase().GetTableUpdateTimestamp(tableName);
            if (!remoteTs.IsSuccess)
            {
                Status = EnumDatabaseStatus.LostConnection;
                StatusMessage = remoteTs.ErrorInfo;
                return true;
            }
            return localTs < remoteTs.Value;
        }
    }

    public IReadOnlyList<ProtocolBase> LoadServers(bool force = false)
    {
        lock (this)
        {
            if (Status != EnumDatabaseStatus.OK)
            {
                var check = SelfCheck(10);
                if (!check.IsOk)
                    return CachedServers;
            }

            if (!force && !NeedReload(TableServer.TABLE_NAME))
                return CachedServers;

            var db = GetDatabase();
            var remoteTs = db.GetTableUpdateTimestamp(TableServer.TABLE_NAME);
            var res = db.GetServers();
            if (!res.IsSuccess || res.Value == null)
            {
                Status = EnumDatabaseStatus.LostConnection;
                StatusMessage = res.ErrorInfo;
                return CachedServers;
            }

            foreach (var srv in res.Value)
            {
                srv.DataSource = this;
            }

            CachedServers = res.Value;
            _lastReadTimestamps[TableServer.TABLE_NAME] = remoteTs.IsSuccess && remoteTs.Value > 0
                ? remoteTs.Value
                : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            Status = EnumDatabaseStatus.OK;
            StatusMessage = $"{CachedServers.Count} 台设备";
            return CachedServers;
        }
    }

    public IReadOnlyList<Credential> LoadCredentials(bool force = false)
    {
        lock (this)
        {
            if (Status != EnumDatabaseStatus.OK && !SelfCheck(10).IsOk)
                return CachedCredentials;

            if (!force && !NeedReload(TableCredential.TABLE_NAME))
                return CachedCredentials;

            var db = GetDatabase();
            var remoteTs = db.GetTableUpdateTimestamp(TableCredential.TABLE_NAME);
            var res = db.GetCredentials();
            if (res.IsSuccess && res.Value != null)
            {
                foreach (var c in res.Value)
                    c.DataSource = this;
                CachedCredentials = res.Value;
                _lastReadTimestamps[TableCredential.TABLE_NAME] = remoteTs.IsSuccess && remoteTs.Value > 0
                    ? remoteTs.Value
                    : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            }
            return CachedCredentials;
        }
    }

    public DbResult SaveServer(ProtocolBase server)
    {
        lock (this)
        {
            if (!IsWritable)
                return DbResult.Fail($"数据源 {DataSourceName} 为只读模式。");

            var isNew = server.IsTmpSession() || CachedServers.All(x => x.Id != server.Id);
            var res = isNew ? GetDatabase().AddServer(server) : GetDatabase().UpdateServer(server);
            if (res.IsSuccess)
            {
                server.DataSource = this;
                _lastReadTimestamps[TableServer.TABLE_NAME] = 0;
                LoadServers(true);
            }
            return res;
        }
    }

    public DbResult DeleteServer(string id)
    {
        lock (this)
        {
            if (!IsWritable)
                return DbResult.Fail($"数据源 {DataSourceName} 为只读模式。");

            var res = GetDatabase().DeleteServers([id]);
            if (res.IsSuccess)
            {
                CachedServers.RemoveAll(x => x.Id == id);
                _lastReadTimestamps[TableServer.TABLE_NAME] = 0;
            }
            return res;
        }
    }
}

public sealed class SqliteSource : DataSourceBase
{
    public string Path { get; set; } = string.Empty;

    public override DatabaseType DatabaseType => DatabaseType.Sqlite;
    public override string Description => $"SQLite ({Path})";

    public override string GetConnectionString(int connectTimeoutSeconds = 10) =>
        DbConnectionStrings.BuildSqlite(Path);
}

public sealed class MysqlSource : DataSourceBase
{
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 3306;
    public string DatabaseName { get; set; } = "1Remote";
    public string UserName { get; set; } = string.Empty;

    [JsonProperty(nameof(Password))]
    private string EncryptedPassword { get; set; } = string.Empty;

    [JsonIgnore]
    public string Password
    {
        get
        {
            if (string.IsNullOrEmpty(EncryptedPassword))
                return string.Empty;
            return UnSafeStringEncipher.DecryptOrReturnOriginalString(EncryptedPassword);
        }
        set
        {
            EncryptedPassword = string.IsNullOrEmpty(value)
                ? string.Empty
                : UnSafeStringEncipher.EncryptOnce(value);
            RaisePropertyChanged();
        }
    }

    public override DatabaseType DatabaseType => DatabaseType.MySql;
    public override string Description => $"MySQL ({UserName}@{Host}:{Port}/{DatabaseName})";

    public override string GetConnectionString(int connectTimeoutSeconds = 10) =>
        DbConnectionStrings.BuildMySql(Host, Port, DatabaseName, UserName, Password, connectTimeoutSeconds);
}

public sealed class PgsqlSource : DataSourceBase
{
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 5432;
    public string DatabaseName { get; set; } = "1Remote";
    public string UserName { get; set; } = string.Empty;

    [JsonProperty(nameof(Password))]
    private string EncryptedPassword { get; set; } = string.Empty;

    [JsonIgnore]
    public string Password
    {
        get
        {
            if (string.IsNullOrEmpty(EncryptedPassword))
                return string.Empty;
            return UnSafeStringEncipher.DecryptOrReturnOriginalString(EncryptedPassword);
        }
        set
        {
            EncryptedPassword = string.IsNullOrEmpty(value)
                ? string.Empty
                : UnSafeStringEncipher.EncryptOnce(value);
            RaisePropertyChanged();
        }
    }

    public override DatabaseType DatabaseType => DatabaseType.PostgreSQL;
    public override string Description => $"PostgreSQL ({UserName}@{Host}:{Port}/{DatabaseName})";

    public override string GetConnectionString(int connectTimeoutSeconds = 10) =>
        DbConnectionStrings.BuildPostgreSql(Host, Port, DatabaseName, UserName, Password, connectTimeoutSeconds);
}

/// <summary>
/// Manages local SQLite + remote MySQL/PostgreSQL data sources with non-reentrant background sync.
/// Compatible with Windows 1Remote AdditionalDataSource.json profile files.
/// </summary>
public sealed class DataSourceHub : IDisposable
{
    private readonly ConcurrentDictionary<string, DataSourceBase> _remoteSources = new();
    private Timer? _syncTimer;
    private int _isTickBusy;
    private int _isPaused;

    public SqliteSource? LocalSource { get; private set; }
    public event Action? RemoteChangesDetected;

    public IReadOnlyList<DataSourceBase> AllSources
    {
        get
        {
            var list = new List<DataSourceBase>();
            if (LocalSource != null) list.Add(LocalSource);
            list.AddRange(_remoteSources.Values.OrderBy(x => x.DataSourceName));
            return list;
        }
    }

    public DatabaseCheckResult InitLocalSqlite(string dbFilePath)
    {
        var sqlite = new SqliteSource
        {
            DataSourceName = "Local",
            Path = dbFilePath,
        };
        var check = sqlite.SelfCheck(5);
        LocalSource = sqlite;
        return check;
    }

    public DatabaseCheckResult AddOrUpdateRemoteSource(DataSourceBase source)
    {
        PauseSync();
        try
        {
            if (string.IsNullOrWhiteSpace(source.DataSourceName))
                source.DataSourceName = source.DatabaseType.ToString();

            source.ClearTimestamps();
            var check = source.SelfCheck(10);
            _remoteSources[source.DataSourceName] = source;
            return check;
        }
        finally
        {
            ResumeSync();
        }
    }

    public void RemoveRemoteSource(string dataSourceName)
    {
        _remoteSources.TryRemove(dataSourceName, out _);
    }

    public List<ProtocolBase> GetAllServers(bool forceReload = false)
    {
        PauseSync();
        try
        {
            var list = new List<ProtocolBase>();
            if (LocalSource != null)
            {
                list.AddRange(LocalSource.LoadServers(forceReload));
            }
            foreach (var src in _remoteSources.Values.OrderBy(x => x.DataSourceName))
            {
                list.AddRange(src.LoadServers(forceReload));
            }
            return list;
        }
        finally
        {
            ResumeSync();
        }
    }

    public List<Credential> GetAllCredentials(bool forceReload = false)
    {
        var list = new List<Credential>();
        if (LocalSource != null)
            list.AddRange(LocalSource.LoadCredentials(forceReload));
        foreach (var src in _remoteSources.Values)
            list.AddRange(src.LoadCredentials(forceReload));
        return list;
    }

    public void StartBackgroundSync(TimeSpan interval)
    {
        _syncTimer?.Dispose();
        _syncTimer = new Timer(_ => PollRemoteUpdates(), null, interval, interval);
    }

    public void PauseSync() => Interlocked.Exchange(ref _isPaused, 1);
    public void ResumeSync() => Interlocked.Exchange(ref _isPaused, 0);

    private void PollRemoteUpdates()
    {
        if (Volatile.Read(ref _isPaused) == 1)
            return;
        if (Interlocked.CompareExchange(ref _isTickBusy, 1, 0) != 0)
            return;

        try
        {
            var changed = false;
            foreach (var src in _remoteSources.Values)
            {
                if (src.NeedReload(TableServer.TABLE_NAME))
                {
                    src.LoadServers(true);
                    changed = true;
                }
            }
            if (changed)
            {
                RemoteChangesDetected?.Invoke();
            }
        }
        catch
        {
            // Swallow background poll glitches; Status reflects connection state
        }
        finally
        {
            Interlocked.Exchange(ref _isTickBusy, 0);
        }
    }

    public static List<DataSourceBase> LoadSourcesFromJsonFile(string jsonPath)
    {
        if (!File.Exists(jsonPath))
            return new List<DataSourceBase>();
        var json = File.ReadAllText(jsonPath);
        return JsonConvert.DeserializeObject<List<DataSourceBase>>(json) ?? new List<DataSourceBase>();
    }

    public void SaveRemoteSourcesToJsonFile(string jsonPath)
    {
        var dir = Path.GetDirectoryName(jsonPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);
        var list = _remoteSources.Values.OrderBy(x => x.DataSourceName).ToList();
        File.WriteAllText(jsonPath, JsonConvert.SerializeObject(list, Formatting.Indented));
    }

    public void Dispose()
    {
        _syncTimer?.Dispose();
    }
}
