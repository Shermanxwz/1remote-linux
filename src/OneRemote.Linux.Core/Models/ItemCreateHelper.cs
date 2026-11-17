using Newtonsoft.Json;
using OneRemote.Linux.Core.DataSources;
using OneRemote.Linux.Core.Security;

namespace OneRemote.Linux.Core.Models;

public static class ItemCreateHelper
{
    private static readonly ProtocolBase[] KnownPrototypes =
    [
        new RDP(),
        new VNC(),
        new SSH(),
        new Telnet(),
        new Serial(),
        new SFTP(),
        new FTP(),
    ];

    public static ProtocolBase? CreateFromDbRow(TableServer row)
    {
        if (string.IsNullOrWhiteSpace(row.Json))
            return null;

        foreach (var proto in KnownPrototypes)
        {
            if (string.Equals(row.Protocol, proto.Protocol, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(row.ClassVersion, proto.ClassVersion, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var instance = proto.CreateFromJsonString(row.Json);
                    if (instance != null)
                    {
                        instance.Id = row.Id;
                        if (instance is ProtocolBaseWithAddressPortUserPwd p)
                        {
                            p.UsePrivateKeyForConnect ??= !string.IsNullOrEmpty(p.PrivateKey);
                            p.AskPasswordWhenConnect ??= string.IsNullOrEmpty(p.Password);
                        }
                        return instance;
                    }
                }
                catch
                {
                    // Fallback below
                }
            }
        }

        // Fallback matching by Protocol name even if ClassVersion bumped
        foreach (var proto in KnownPrototypes)
        {
            if (string.Equals(row.Protocol, proto.Protocol, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var instance = proto.CreateFromJsonString(row.Json);
                    if (instance != null)
                    {
                        instance.Id = row.Id;
                        return instance;
                    }
                }
                catch
                {
                    // Ignore and preserve as RawProtocol
                }
            }
        }

        var raw = new RawProtocol(row.Protocol, row.ClassVersion, row.Json).CreateFromJsonString(row.Json);
        if (raw != null)
        {
            raw.Id = row.Id;
        }
        return raw;
    }
}

public static class ProtocolEncryptionExtensions
{
    public static void EncryptToDatabaseLevel(this ProtocolBase server)
    {
        if (server is ProtocolBaseWithAddressPortUserPwd s)
        {
            if (!string.IsNullOrEmpty(s.Password))
                s.Password = UnSafeStringEncipher.EncryptOnce(s.Password);
            foreach (var cred in s.AlternateCredentials)
                cred.EncryptToDatabaseLevel();
        }

        switch (server)
        {
            case SSH ssh when !string.IsNullOrWhiteSpace(ssh.PrivateKey):
                ssh.PrivateKey = UnSafeStringEncipher.EncryptOnce(ssh.PrivateKey);
                break;
            case RDP rdp when !string.IsNullOrWhiteSpace(rdp.GatewayPassword):
                rdp.GatewayPassword = UnSafeStringEncipher.EncryptOnce(rdp.GatewayPassword);
                break;
        }
    }

    public static void DecryptToConnectLevel(this ProtocolBase server)
    {
        if (server is ProtocolBaseWithAddressPortUserPwd s)
        {
            if (!string.IsNullOrEmpty(s.Password))
                s.Password = UnSafeStringEncipher.DecryptOrReturnOriginalString(s.Password);
            foreach (var cred in s.AlternateCredentials)
                cred.DecryptToConnectLevel();
        }

        switch (server)
        {
            case SSH ssh when !string.IsNullOrWhiteSpace(ssh.PrivateKey):
                ssh.PrivateKey = UnSafeStringEncipher.DecryptOrReturnOriginalString(ssh.PrivateKey);
                break;
            case RDP rdp when !string.IsNullOrWhiteSpace(rdp.GatewayPassword):
                rdp.GatewayPassword = UnSafeStringEncipher.DecryptOrReturnOriginalString(rdp.GatewayPassword);
                break;
        }
    }

    public static void EncryptToDatabaseLevel(this Credential credential)
    {
        if (!string.IsNullOrEmpty(credential.Password))
            credential.Password = UnSafeStringEncipher.EncryptOnce(credential.Password);
        if (!string.IsNullOrEmpty(credential.PrivateKeyPath))
            credential.PrivateKeyPath = UnSafeStringEncipher.EncryptOnce(credential.PrivateKeyPath);
    }

    public static void DecryptToConnectLevel(this Credential credential)
    {
        if (!string.IsNullOrEmpty(credential.Password))
            credential.Password = UnSafeStringEncipher.DecryptOrReturnOriginalString(credential.Password);
        if (!string.IsNullOrEmpty(credential.PrivateKeyPath))
            credential.PrivateKeyPath = UnSafeStringEncipher.DecryptOrReturnOriginalString(credential.PrivateKeyPath);
    }
}
