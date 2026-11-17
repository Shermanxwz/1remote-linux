using System.Collections.ObjectModel;
using System.ComponentModel;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OneRemote.Linux.Core.DataSources;
using OneRemote.Linux.Core.Security;

namespace OneRemote.Linux.Core.Models;

public enum ERdpWindowResizeMode
{
    AutoResize = 0,
    Stretch = 1,
    Fixed = 2,
    StretchFullScreen = 3,
    FixedFullScreen = 4,
}

public enum ERdpFullScreenFlag
{
    Disable = 0,
    EnableFullScreen = 1,
    EnableFullAllScreens = 2,
}

public enum EDisplayPerformance
{
    Auto = 0,
    Low = 1,
    Middle = 2,
    High = 3,
}

public enum EGatewayMode
{
    AutomaticallyDetectGatewayServerSettings = 0,
    UseTheseGatewayServerSettings = 1,
    DoNotUseGateway = 2,
}

public enum EAudioRedirectionMode
{
    RedirectToLocal = 0,
    LeaveOnRemote = 1,
    Disabled = 2,
}

public enum EAudioQualityMode
{
    Dynamic = 0,
    Medium = 1,
    High = 2,
}

public abstract class ProtocolBase : NotifyBase
{
    protected ProtocolBase(string protocol, string classVersion, string protocolDisplayName)
    {
        Protocol = protocol;
        ClassVersion = classVersion;
        _protocolDisplayName = protocolDisplayName;
    }

    private string _id = string.Empty;

    [JsonIgnore]
    public string Id
    {
        get
        {
            if (string.IsNullOrEmpty(_id))
                _id = "TMP_SESSION_" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            return _id;
        }
        set => SetAndNotifyIfChanged(ref _id, value ?? string.Empty);
    }

    public static bool IsTmpSession(string id) => string.IsNullOrEmpty(id) || id.StartsWith("TMP_SESSION_", StringComparison.Ordinal);
    public bool IsTmpSession() => IsTmpSession(Id);

    public string Protocol { get; protected set; }
    public string ClassVersion { get; protected set; }

    private readonly string _protocolDisplayName;
    [JsonIgnore]
    public string ProtocolDisplayName => _protocolDisplayName;

    private string _displayName = string.Empty;
    public string DisplayName
    {
        get => _displayName;
        set => SetAndNotifyIfChanged(ref _displayName, value ?? string.Empty);
    }

    [JsonIgnore]
    public string SubTitle => GetSubTitle();

    private bool? _alwaysOpenInNewTabWindow = false;
    public bool? AlwaysOpenInNewTabWindow
    {
        get => _alwaysOpenInNewTabWindow;
        set => SetAndNotifyIfChanged(ref _alwaysOpenInNewTabWindow, value);
    }

    private List<string> _tags = new();
    public List<string> Tags
    {
        get => _tags.Distinct().OrderBy(x => x).ToList();
        set
        {
            if (value == null)
                SetAndNotifyIfChanged(ref _tags, new List<string>());
            else
                SetAndNotifyIfChanged(ref _tags, value.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim().Replace(" ", "")).Distinct().OrderBy(x => x).ToList());
        }
    }

    private List<string> _treeNodes = new();
    public List<string> TreeNodes
    {
        get => _treeNodes;
        set
        {
            if (value == null)
                SetAndNotifyIfChanged(ref _treeNodes, new List<string>());
            else
                SetAndNotifyIfChanged(ref _treeNodes, value.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).ToList());
        }
    }

    private string _iconBase64 = string.Empty;
    public string IconBase64
    {
        get => _iconBase64;
        set => SetAndNotifyIfChanged(ref _iconBase64, value ?? string.Empty);
    }

    private string _colorHex = "#00000000";
    public string ColorHex
    {
        get => _colorHex;
        set => SetAndNotifyIfChanged(ref _colorHex, value ?? "#00000000");
    }

    private string _commandBeforeConnected = string.Empty;
    public string CommandBeforeConnected
    {
        get => _commandBeforeConnected;
        set => SetAndNotifyIfChanged(ref _commandBeforeConnected, value ?? string.Empty);
    }

    private bool _hideCommandBeforeConnectedWindow;
    public bool HideCommandBeforeConnectedWindow
    {
        get => _hideCommandBeforeConnectedWindow;
        set => SetAndNotifyIfChanged(ref _hideCommandBeforeConnectedWindow, value);
    }

    private string _commandAfterDisconnected = string.Empty;
    public string CommandAfterDisconnected
    {
        get => _commandAfterDisconnected;
        set => SetAndNotifyIfChanged(ref _commandAfterDisconnected, value ?? string.Empty);
    }

    private string _note = string.Empty;
    public string Note
    {
        get => _note;
        set => SetAndNotifyIfChanged(ref _note, value ?? string.Empty);
    }

    private string _selectedRunnerName = string.Empty;
    public string SelectedRunnerName
    {
        get => _selectedRunnerName;
        set => SetAndNotifyIfChanged(ref _selectedRunnerName, value ?? string.Empty);
    }

    /// <summary>
    /// Preserves any Windows-specific JSON fields (e.g. RdpControlAdditionalSettings, Icon data, Kitty paths)
    /// losslessly when Linux reads, edits, and writes back to the shared MySQL database.
    /// </summary>
    [JsonExtensionData]
    public IDictionary<string, JToken>? ExtraJsonFields { get; set; }

    [JsonIgnore]
    public DataSourceBase? DataSource { get; set; }

    [JsonIgnore]
    public string DataSourceBadge => DataSource?.DataSourceName ?? "Local";

    [JsonIgnore]
    public string TagsSummary => Tags.Count > 0 ? string.Join(", ", Tags) : (TreeNodes.Count > 0 ? string.Join(" / ", TreeNodes) : string.Empty);

    public virtual string ToJsonString() => JsonConvert.SerializeObject(this);

    public abstract ProtocolBase? CreateFromJsonString(string jsonString);
    protected abstract string GetSubTitle();
    public abstract double GetListOrder();

    public virtual ProtocolBase Clone()
    {
        var clone = (ProtocolBase)MemberwiseClone();
        clone.Tags = new List<string>(Tags);
        clone.TreeNodes = new List<string>(TreeNodes);
        if (ExtraJsonFields != null)
        {
            clone.ExtraJsonFields = new Dictionary<string, JToken>(ExtraJsonFields);
        }
        if (this is ProtocolBaseWithAddressPort p && clone is ProtocolBaseWithAddressPort c)
        {
            c.AlternateCredentials = new ObservableCollection<Credential>(p.AlternateCredentials.Select(x => x.CloneMe()));
        }
        clone.DataSource = DataSource;
        return clone;
    }
}

public abstract class ProtocolBaseWithAddressPort : ProtocolBase
{
    protected ProtocolBaseWithAddressPort(string protocol, string classVersion, string protocolDisplayName)
        : base(protocol, classVersion, protocolDisplayName)
    {
    }

    private string _address = string.Empty;
    public string Address
    {
        get => _address;
        set
        {
            var old = _address;
            if (SetAndNotifyIfChanged(ref _address, value ?? string.Empty))
            {
                if (string.IsNullOrEmpty(DisplayName) || DisplayName == old)
                    DisplayName = _address;
                RaisePropertyChanged(nameof(SubTitle));
            }
        }
    }

    private string _port = "22";
    public string Port
    {
        get => _port;
        set
        {
            if (SetAndNotifyIfChanged(ref _port, value ?? string.Empty))
                RaisePropertyChanged(nameof(SubTitle));
        }
    }

    public int GetPort(int defaultPort = 22)
    {
        return int.TryParse(Port, out var p) && p is > 0 and <= 65535 ? p : defaultPort;
    }

    private ObservableCollection<Credential>? _alternateCredentials = new();
    public ObservableCollection<Credential> AlternateCredentials
    {
        get => _alternateCredentials ??= new ObservableCollection<Credential>();
        set => SetAndNotifyIfChanged(ref _alternateCredentials, value);
    }

    [DefaultValue(true)]
    [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate, NullValueHandling = NullValueHandling.Ignore)]
    public bool? IsPingBeforeConnect { get; set; } = true;

    [DefaultValue(true)]
    [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate, NullValueHandling = NullValueHandling.Ignore)]
    public bool? IsAutoAlternateAddressSwitching { get; set; } = true;

    protected override string GetSubTitle() => $"{Address}:{Port}";
}

public abstract class ProtocolBaseWithAddressPortUserPwd : ProtocolBaseWithAddressPort
{
    protected ProtocolBaseWithAddressPortUserPwd(string protocol, string classVersion, string protocolDisplayName)
        : base(protocol, classVersion, protocolDisplayName)
    {
    }

    private string _inheritedCredentialName = string.Empty;
    public string InheritedCredentialName
    {
        get => _inheritedCredentialName;
        set => SetAndNotifyIfChanged(ref _inheritedCredentialName, value ?? string.Empty);
    }

    private string _userName = string.Empty;
    public string UserName
    {
        get => _userName;
        set
        {
            if (SetAndNotifyIfChanged(ref _userName, value ?? string.Empty))
                RaisePropertyChanged(nameof(SubTitle));
        }
    }

    [DefaultValue(null)]
    public bool? AskPasswordWhenConnect { get; set; } = false;

    // Side-effect-free setter matching 1Remote issue #1118 fix
    private string _password = string.Empty;
    public string Password
    {
        get => _password;
        set => SetAndNotifyIfChanged(ref _password, value ?? string.Empty);
    }

    [DefaultValue(null)]
    public bool? UsePrivateKeyForConnect { get; set; }

    // Side-effect-free setter matching 1Remote issue #1118 fix
    private string _privateKey = string.Empty;
    public string PrivateKey
    {
        get => _privateKey;
        set => SetAndNotifyIfChanged(ref _privateKey, value ?? string.Empty);
    }

    protected override string GetSubTitle() =>
        string.IsNullOrEmpty(UserName) ? base.GetSubTitle() : $"{Address}:{Port} ({UserName})";

    public Credential ResolveEffectiveCredential(IEnumerable<Credential>? vaultCredentials = null)
    {
        var clone = (ProtocolBaseWithAddressPortUserPwd)Clone();
        clone.DecryptToConnectLevel();

        var cred = new Credential
        {
            Name = clone.InheritedCredentialName,
            Address = clone.Address,
            Port = clone.Port,
            UserName = clone.UserName,
            Password = clone.Password,
            PrivateKeyPath = clone.PrivateKey
        };

        if (!string.IsNullOrEmpty(clone.InheritedCredentialName) && vaultCredentials != null)
        {
            var matched = vaultCredentials.FirstOrDefault(x => string.Equals(x.Name, clone.InheritedCredentialName, StringComparison.Ordinal));
            if (matched != null)
            {
                var vaultClone = matched.CloneMe();
                vaultClone.DecryptToConnectLevel();
                if (!string.IsNullOrEmpty(vaultClone.UserName)) cred.UserName = vaultClone.UserName;
                if (!string.IsNullOrEmpty(vaultClone.Password)) cred.Password = vaultClone.Password;
                if (!string.IsNullOrEmpty(vaultClone.PrivateKeyPath)) cred.PrivateKeyPath = vaultClone.PrivateKeyPath;
                if (!string.IsNullOrEmpty(vaultClone.Address)) cred.Address = vaultClone.Address;
                if (!string.IsNullOrEmpty(vaultClone.Port)) cred.Port = vaultClone.Port;
            }
        }

        return cred;
    }
}

public class SSH : ProtocolBaseWithAddressPortUserPwd
{
    public const string ProtocolName = "SSH";

    public SSH() : base(ProtocolName, "Putty.SSH.V1", ProtocolName)
    {
        UserName = "root";
        Port = "22";
    }

    public int? SshVersion { get; set; } = 2;
    public string StartupAutoCommand { get; set; } = string.Empty;
    public string ExternalKittySessionConfigPath { get; set; } = string.Empty;
    public string ExternalSessionConfigPath { get; set; } = string.Empty;
    public bool OpenSftpOnConnected { get; set; }

    public override ProtocolBase? CreateFromJsonString(string jsonString) =>
        JsonConvert.DeserializeObject<SSH>(jsonString);

    public override double GetListOrder() => 2;
}

public sealed class RDP : ProtocolBaseWithAddressPortUserPwd
{
    public const string ProtocolName = "RDP";

    public RDP() : base(ProtocolName, "RDP.V1", ProtocolName)
    {
        Port = "3389";
        UserName = "Administrator";
    }

    public bool? IsAdministrativePurposes { get; set; } = false;
    public string Domain { get; set; } = string.Empty;
    public string LoadBalanceInfo { get; set; } = string.Empty;

    public ERdpFullScreenFlag? RdpFullScreenFlag { get; set; } = ERdpFullScreenFlag.EnableFullScreen;
    public bool? IsConnWithFullScreen { get; set; } = false;
    public bool? IsFullScreenWithConnectionBar { get; set; } = true;
    public bool? IsPinTheConnectionBarByDefault { get; set; } = false;
    public ERdpWindowResizeMode? RdpWindowResizeMode { get; set; } = ERdpWindowResizeMode.AutoResize;
    public int? RdpWidth { get; set; } = 1440;
    public int? RdpHeight { get; set; } = 900;
    public bool? IsScaleFactorFollowSystem { get; set; } = true;
    public uint? ScaleFactorCustomValue { get; set; } = 100;
    public EDisplayPerformance? DisplayPerformance { get; set; } = EDisplayPerformance.Auto;

    [DefaultValue(true)]
    [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate)]
    public bool? EnableClipboard { get; set; } = true;

    [DefaultValue(false)]
    [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate)]
    public bool? EnableDiskDrives { get; set; } = false;

    [DefaultValue(EAudioRedirectionMode.RedirectToLocal)]
    [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate)]
    public EAudioRedirectionMode? AudioRedirectionMode { get; set; } = EAudioRedirectionMode.RedirectToLocal;

    [DefaultValue(EAudioQualityMode.Dynamic)]
    [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate)]
    public EAudioQualityMode? AudioQualityMode { get; set; } = EAudioQualityMode.Dynamic;

    public EGatewayMode? GatewayMode { get; set; } = EGatewayMode.DoNotUseGateway;
    public string GatewayHostName { get; set; } = string.Empty;
    public string GatewayUserName { get; set; } = string.Empty;
    public string GatewayPassword { get; set; } = string.Empty;

    public bool MstscModeEnabled { get; set; }
    public string RdpFileAdditionalSettings { get; set; } = string.Empty;
    public string RdpControlAdditionalSettings { get; set; } = string.Empty;

    public override ProtocolBase? CreateFromJsonString(string jsonString) =>
        JsonConvert.DeserializeObject<RDP>(jsonString);

    public override double GetListOrder() => 0;
}

public class SFTP : ProtocolBaseWithAddressPortUserPwd
{
    public const string ProtocolName = "SFTP";

    public SFTP() : base(ProtocolName, "SFTP.V1", ProtocolName)
    {
        UserName = "root";
        Port = "22";
    }

    public string StartupPath { get; set; } = "/";

    public override ProtocolBase? CreateFromJsonString(string jsonString) =>
        JsonConvert.DeserializeObject<SFTP>(jsonString);

    public override double GetListOrder() => 5;
}

public class FTP : ProtocolBaseWithAddressPortUserPwd
{
    public const string ProtocolName = "FTP";

    public FTP() : base(ProtocolName, "FTP.V1", ProtocolName)
    {
        Port = "21";
    }

    public string StartupPath { get; set; } = "/";

    public override ProtocolBase? CreateFromJsonString(string jsonString) =>
        JsonConvert.DeserializeObject<FTP>(jsonString);

    public override double GetListOrder() => 6;
}

public class VNC : ProtocolBaseWithAddressPortUserPwd
{
    public const string ProtocolName = "VNC";

    public VNC() : base(ProtocolName, "VNC.V1", ProtocolName)
    {
        Port = "5900";
    }

    public int? VncWindowResizeMode { get; set; } = 0;

    public override ProtocolBase? CreateFromJsonString(string jsonString) =>
        JsonConvert.DeserializeObject<VNC>(jsonString);

    public override double GetListOrder() => 1;
}

public class Telnet : ProtocolBaseWithAddressPort
{
    public const string ProtocolName = "Telnet";

    public Telnet() : base(ProtocolName, "Putty.Telnet.V1", ProtocolName)
    {
        Port = "23";
    }

    public string StartupAutoCommand { get; set; } = string.Empty;

    public override ProtocolBase? CreateFromJsonString(string jsonString) =>
        JsonConvert.DeserializeObject<Telnet>(jsonString);

    public override double GetListOrder() => 3;
}

public class Serial : ProtocolBase
{
    public const string ProtocolName = "Serial";

    public Serial() : base(ProtocolName, "Putty.Serial.V1", ProtocolName)
    {
    }

    public string SerialPort { get; set; } = "/dev/ttyUSB0";
    public string BitRate { get; set; } = "115200";
    public string DataBits { get; set; } = "8";
    public string StopBits { get; set; } = "1";
    public string Parity { get; set; } = "NONE";
    public string FlowControl { get; set; } = "XON/XOFF";
    public string StartupAutoCommand { get; set; } = string.Empty;

    protected override string GetSubTitle() => $"{SerialPort} ({BitRate})";

    public override ProtocolBase? CreateFromJsonString(string jsonString) =>
        JsonConvert.DeserializeObject<Serial>(jsonString);

    public override double GetListOrder() => 4;
}

/// <summary>
/// Preserves unknown future or Windows-only protocol entries (e.g. RdpApp, LocalApp) losslessly.
/// </summary>
public sealed class RawProtocol : ProtocolBase
{
    private string _rawJson = "{}";

    public RawProtocol() : base("RAW", "RAW.V1", "Other")
    {
    }

    public RawProtocol(string protocol, string classVersion, string rawJson)
        : base(protocol, classVersion, protocol)
    {
        _rawJson = rawJson;
    }

    protected override string GetSubTitle() => $"{Protocol} ({ClassVersion})";

    public override string ToJsonString() =>
        ExtraJsonFields != null ? JsonConvert.SerializeObject(this) : _rawJson;

    public override ProtocolBase? CreateFromJsonString(string jsonString)
    {
        var obj = JsonConvert.DeserializeObject<RawProtocol>(jsonString);
        if (obj != null) obj._rawJson = jsonString;
        return obj;
    }

    public override double GetListOrder() => 99;
}
