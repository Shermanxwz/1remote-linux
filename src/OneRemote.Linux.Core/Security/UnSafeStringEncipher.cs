using System.Security.Cryptography;
using System.Text;

namespace OneRemote.Linux.Core.Security;

/// <summary>
/// 100% wire-compatible wrapper around official 1Remote.Security NuGet package.
/// Default salt matches Shermanxwz/1Remote v1.3.2 (Assert.STRING_SALT).
/// </summary>
public static class UnSafeStringEncipher
{
    public const string DEFAULT_SALT = "===REPLACE_ME_WITH_SALT===";
    private static readonly object SyncLock = new();
    private static string _currentSalt = string.Empty;
    private static bool _initialized;

    public static string CurrentSalt => _currentSalt;

    public static void Init(string? salt = null)
    {
        lock (SyncLock)
        {
            var effective = string.IsNullOrEmpty(salt) ? DEFAULT_SALT : salt;
            if (!_initialized || !string.Equals(_currentSalt, effective, StringComparison.Ordinal))
            {
                _currentSalt = effective;
                _1Remote.Security.Config.SetSalt(effective);
                _initialized = true;
            }
        }
    }

    private static void EnsureInitialized()
    {
        if (!_initialized)
        {
            Init(DEFAULT_SALT);
        }
    }

    public static string SimpleEncrypt(string plainText)
    {
        EnsureInitialized();
        lock (SyncLock)
        {
            return _1Remote.Security.SimpleStringEncipher.Encrypt(plainText);
        }
    }

    public static string? SimpleDecrypt(string encryptedText)
    {
        if (string.IsNullOrEmpty(encryptedText))
            return null;
        EnsureInitialized();
        lock (SyncLock)
        {
            var ret = _1Remote.Security.SimpleStringEncipher.Decrypt(encryptedText);
            return ret.IsSuccess ? ret.PlainText : null;
        }
    }

    public static string EncryptOnce(string value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        if (SimpleDecrypt(value) == null)
            return SimpleEncrypt(value);
        return value;
    }

    public static string DecryptOrReturnOriginalString(string value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        return SimpleDecrypt(value) ?? value;
    }

    public static string Md5Hex32(string input)
    {
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes(input ?? string.Empty));
        return Convert.ToHexStringLower(bytes);
    }

    public static string Md5Hex16(string input)
    {
        return Md5Hex32(input).Substring(8, 16);
    }
}
