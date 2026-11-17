using System.ComponentModel;
using System.Net.Sockets;
using JsonKnownTypes;
using Newtonsoft.Json;
using OneRemote.Linux.Core.DataSources;
using OneRemote.Linux.Core.Security;

namespace OneRemote.Linux.Core.Models;

[JsonConverter(typeof(JsonKnownTypesConverter<Credential>))]
public class Credential : NotifyBase, ICloneable
{
    public Credential(bool? isEditable = true)
    {
        IsEditable = isEditable;
    }

    [JsonIgnore]
    public string DatabaseId { get; set; } = string.Empty;

    [JsonIgnore]
    public string Hash { get; set; } = string.Empty;

    [JsonIgnore]
    [DefaultValue(true)]
    public bool? IsEditable { get; }

    private string _name = string.Empty;
    public string Name
    {
        get => _name;
        set
        {
            var v = value != null && value.Length > 128 ? value.Substring(0, 100) : (value ?? string.Empty);
            SetAndNotifyIfChanged(ref _name, v);
        }
    }

    private string _address = string.Empty;
    public string Address
    {
        get => _address;
        set
        {
            if (SetAndNotifyIfChanged(ref _address, value ?? string.Empty))
                GetHash();
        }
    }

    private string _port = string.Empty;
    public string Port
    {
        get => _port;
        set
        {
            if (SetAndNotifyIfChanged(ref _port, value ?? string.Empty))
                GetHash();
        }
    }

    private string _userName = string.Empty;
    public string UserName
    {
        get => _userName;
        set
        {
            if (SetAndNotifyIfChanged(ref _userName, value ?? string.Empty))
                GetHash();
        }
    }

    private string _password = string.Empty;
    public string Password
    {
        get => _password;
        set
        {
            if (SetAndNotifyIfChanged(ref _password, value ?? string.Empty))
                GetHash();
        }
    }

    private string _privateKeyPath = string.Empty;
    public string PrivateKeyPath
    {
        get => _privateKeyPath;
        set
        {
            if (SetAndNotifyIfChanged(ref _privateKeyPath, value ?? string.Empty))
                GetHash();
        }
    }

    [JsonIgnore]
    public DataSourceBase? DataSource { get; set; }

    public object Clone() => MemberwiseClone();

    public Credential CloneMe() => (Credential)MemberwiseClone();

    public string GetHash()
    {
        var plainAddr = Address;
        var plainPort = Port;
        var plainUser = UserName;
        var plainPwd = UnSafeStringEncipher.DecryptOrReturnOriginalString(Password);
        var plainKey = UnSafeStringEncipher.DecryptOrReturnOriginalString(PrivateKeyPath);
        var raw = $"{plainAddr}|{plainPort}|{plainUser}|{plainPwd}|{plainKey}";
        Hash = UnSafeStringEncipher.Md5Hex32(raw);
        return Hash;
    }

    public static async Task<bool> TestAddressPortAvailableAsync(string address, int port, int timeoutMs = 3000)
    {
        try
        {
            using var client = new TcpClient();
            using var cts = new CancellationTokenSource(timeoutMs);
            await client.ConnectAsync(address, port, cts.Token);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
