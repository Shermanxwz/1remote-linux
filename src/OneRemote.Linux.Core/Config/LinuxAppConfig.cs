using Newtonsoft.Json;
using OneRemote.Linux.Core.Security;

namespace OneRemote.Linux.Core.Config;

public sealed class LinuxAppConfig
{
    public string EncryptionSalt { get; set; } = UnSafeStringEncipher.DEFAULT_SALT;
    public string PreferredFreeRdpBinary { get; set; } = string.Empty;
    public int SyncIntervalSeconds { get; set; } = 8;

    public static string GetConfigDirectory()
    {
        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        var baseDir = !string.IsNullOrWhiteSpace(xdg)
            ? xdg
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        var dir = Path.Combine(baseDir, "1remote-linux");
        if (!Directory.Exists(dir))
            Directory.CreateDirectory(dir);
        return dir;
    }

    public static string GetSettingsPath() => Path.Combine(GetConfigDirectory(), "settings.json");
    public static string GetLocalDatabasePath() => Path.Combine(GetConfigDirectory(), "1Remote.db");
    public static string GetAdditionalSourcesPath() => Path.Combine(GetConfigDirectory(), "AdditionalDataSource.json");

    public static LinuxAppConfig Load()
    {
        var path = GetSettingsPath();
        if (!File.Exists(path))
            return new LinuxAppConfig();
        try
        {
            return JsonConvert.DeserializeObject<LinuxAppConfig>(File.ReadAllText(path)) ?? new LinuxAppConfig();
        }
        catch
        {
            return new LinuxAppConfig();
        }
    }

    public void Save()
    {
        File.WriteAllText(GetSettingsPath(), JsonConvert.SerializeObject(this, Formatting.Indented));
    }
}
