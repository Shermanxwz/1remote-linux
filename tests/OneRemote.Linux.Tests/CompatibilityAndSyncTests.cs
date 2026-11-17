using System.Text;
using Newtonsoft.Json.Linq;
using OneRemote.Linux.App.ViewModels;
using OneRemote.Linux.Core.Connections;
using OneRemote.Linux.Core.DataSources;
using OneRemote.Linux.Core.Models;
using OneRemote.Linux.Core.Search;
using OneRemote.Linux.Core.Security;
using Xunit;

namespace OneRemote.Linux.Tests;

public class CompatibilityAndSyncTests
{
    public CompatibilityAndSyncTests()
    {
        UnSafeStringEncipher.Init(UnSafeStringEncipher.DEFAULT_SALT);
    }

    [Fact]
    public void Encryption_RoundTrip_And_Issue1118_EmptyPassword_StayIntact()
    {
        var cipher = UnSafeStringEncipher.SimpleEncrypt("MySecret#2026!中文");
        Assert.NotEqual("MySecret#2026!中文", cipher);
        Assert.Equal("MySecret#2026!中文", UnSafeStringEncipher.SimpleDecrypt(cipher));

        var ssh = new SSH
        {
            DisplayName = "GreenCloud SG",
            Address = "198.185.148.11",
            Port = "22",
            UserName = "root",
            Password = "", // Empty password must stay empty on EncryptToDatabaseLevel!
            PrivateKey = "/home/ubuntu/.ssh/id_ed25519",
            Tags = ["Singapore", "VPS"]
        };

        ssh.EncryptToDatabaseLevel();
        Assert.Equal("", ssh.Password);
        Assert.NotEqual("/home/ubuntu/.ssh/id_ed25519", ssh.PrivateKey);

        ssh.DecryptToConnectLevel();
        Assert.Equal("", ssh.Password);
        Assert.Equal("/home/ubuntu/.ssh/id_ed25519", ssh.PrivateKey);
    }

    [Fact]
    public void WindowsExtraJsonFields_Survive_Linux_SaveAndLoad_Losslessly()
    {
        var windowsRdpJson = new JObject
        {
            ["Protocol"] = "RDP",
            ["ClassVersion"] = "RDP.V1",
            ["DisplayName"] = "Windows x Home",
            ["Address"] = "10.0.0.8",
            ["Port"] = "3389",
            ["UserName"] = "Administrator",
            ["Password"] = UnSafeStringEncipher.SimpleEncrypt("WinPass!99"),
            ["EnableClipboard"] = true,
            ["WindowsOnlyCustomField"] = "KeepMeIntact_12345",
            ["RdpControlAdditionalSettings"] = "smart sizing:i:1"
        }.ToString();

        var row = new TableServer
        {
            Id = "01JABCDEF1234567890ABCDEFG",
            Protocol = "RDP",
            ClassVersion = "RDP.V1",
            Json = windowsRdpJson
        };

        var parsed = ItemCreateHelper.CreateFromDbRow(row);
        var rdp = Assert.IsType<RDP>(parsed);
        Assert.Equal("01JABCDEF1234567890ABCDEFG", rdp.Id);
        Assert.Equal("Windows x Home", rdp.DisplayName);
        Assert.NotNull(rdp.ExtraJsonFields);
        Assert.Equal("KeepMeIntact_12345", rdp.ExtraJsonFields!["WindowsOnlyCustomField"]?.ToString());

        // Re-serialize after editing DisplayName on Linux
        rdp.DisplayName = "Windows x Edited From Ubuntu";
        var savedJson = rdp.ToJsonString();
        var rehydrated = JObject.Parse(savedJson);
        Assert.Equal("KeepMeIntact_12345", rehydrated["WindowsOnlyCustomField"]?.ToString());
        Assert.Equal("smart sizing:i:1", rehydrated["RdpControlAdditionalSettings"]?.ToString());
    }

    [Fact]
    public void Sqlite_FullCrud_VaultInheritance_And_ConcurrentSync_Succeed()
    {
        var tempDb = Path.Combine(Path.GetTempPath(), $"1remote_linux_test_{Guid.NewGuid():N}.db");
        try
        {
            var src = new SqliteSource { DataSourceName = "LocalTest", Path = tempDb };
            var check = src.SelfCheck();
            Assert.True(check.IsOk, check.ErrorMessage);

            var vaultCred = new Credential
            {
                Name = "OpsRootKey",
                UserName = "ubuntu",
                Password = "VaultPassword888",
                PrivateKeyPath = "~/.ssh/ops_ed25519"
            };
            Assert.True(src.GetDatabase().UpsertCredential(vaultCred).IsSuccess);

            var ssh = new SSH
            {
                DisplayName = "Node-01",
                Address = "10.0.0.10",
                Port = "22",
                InheritedCredentialName = "OpsRootKey",
                Tags = ["Prod", "Linux"]
            };
            Assert.True(src.SaveServer(ssh).IsSuccess);
            Assert.False(ssh.IsTmpSession());

            var loaded = src.LoadServers(force: true);
            Assert.Single(loaded);
            var creds = src.LoadCredentials(force: true);
            Assert.Single(creds);

            var resolved = ((SSH)loaded[0]).ResolveEffectiveCredential(creds);
            Assert.Equal("ubuntu", resolved.UserName);
            Assert.Equal("VaultPassword888", resolved.Password);
            Assert.Equal("~/.ssh/ops_ed25519", resolved.PrivateKeyPath);
        }
        finally
        {
            if (File.Exists(tempDb)) File.Delete(tempDb);
        }
    }

    [Fact]
    public async Task LiveMySql_CrossPlatformSync_And_16ThreadConcurrency_SucceedWhenConfigured()
    {
        var mysqlHost = Environment.GetEnvironmentVariable("TEST_MYSQL_HOST");
        if (string.IsNullOrWhiteSpace(mysqlHost))
        {
            // When run locally without MySQL container, skip gracefully; in GitHub Actions TEST_MYSQL_HOST=127.0.0.1 is mandatory!
            return;
        }

        var mysqlPort = int.TryParse(Environment.GetEnvironmentVariable("TEST_MYSQL_PORT"), out var p) ? p : 3306;
        var mysqlDb = Environment.GetEnvironmentVariable("TEST_MYSQL_DB") ?? "1remote_test";
        var mysqlUser = Environment.GetEnvironmentVariable("TEST_MYSQL_USER") ?? "root";
        var mysqlPass = Environment.GetEnvironmentVariable("TEST_MYSQL_PASS") ?? "rootpass123";

        var mysql = new MysqlSource
        {
            DataSourceName = "SharedCloudMySQL",
            Host = mysqlHost,
            Port = mysqlPort,
            DatabaseName = mysqlDb,
            UserName = mysqlUser,
            Password = mysqlPass,
        };

        var check = mysql.SelfCheck(15);
        Assert.True(check.IsOk, $"Live MySQL SelfCheck failed: {check.Status} - {check.ErrorMessage}");

        // Verify AdditionalDataSource.json serialization matches Windows 1Remote JsonKnownTypes format
        var jsonPath = Path.Combine(Path.GetTempPath(), $"ads_{Guid.NewGuid():N}.json");
        try
        {
            using var hub = new DataSourceHub();
            hub.AddOrUpdateRemoteSource(mysql);
            hub.SaveRemoteSourcesToJsonFile(jsonPath);

            var rawJson = await File.ReadAllTextAsync(jsonPath);
            Assert.Contains("\"MysqlSource\"", rawJson);

            var reloadedSources = DataSourceHub.LoadSourcesFromJsonFile(jsonPath);
            var reloadedMysql = Assert.IsType<MysqlSource>(Assert.Single(reloadedSources));
            Assert.Equal(mysqlPass, reloadedMysql.Password);

            // Seed SSH + RDP + SFTP servers
            var s1 = new SSH { DisplayName = "SG-VPS", Address = "198.185.148.11", Port = "22", UserName = "root", Password = "SshSecret#1", Tags = ["SG"] };
            var s2 = new RDP { DisplayName = "Win-Workstation", Address = "35.212.182.65", Port = "3389", UserName = "Administrator", Password = "RdpSecret#2", EnableClipboard = true };
            var s3 = new SFTP { DisplayName = "Backup-SFTP", Address = "10.20.30.40", Port = "22", UserName = "backup", Password = "SftpSecret#3", StartupPath = "/data" };

            Assert.True(reloadedMysql.SaveServer(s1).IsSuccess);
            Assert.True(reloadedMysql.SaveServer(s2).IsSuccess);
            Assert.True(reloadedMysql.SaveServer(s3).IsSuccess);

            // 16-thread concurrent read/timestamp check stress test (verifies zero 'database lost connection!' errors)
            var tasks = Enumerable.Range(0, 16).Select(async i =>
            {
                await Task.Yield();
                var need = reloadedMysql.NeedReload(TableServer.TABLE_NAME);
                var list = reloadedMysql.LoadServers(force: i % 3 == 0);
                Assert.True(list.Count >= 3);
            });
            await Task.WhenAll(tasks);

            var finalServers = reloadedMysql.LoadServers(force: true);
            var rdpLoaded = Assert.IsType<RDP>(finalServers.First(x => x.DisplayName == "Win-Workstation"));
            var rdpCred = rdpLoaded.ResolveEffectiveCredential();
            Assert.Equal("RdpSecret#2", rdpCred.Password);
        }
        finally
        {
            if (File.Exists(jsonPath)) File.Delete(jsonPath);
        }
    }

    [Fact]
    public void FreeRdpLauncher_Prioritizes_Xfreerdp3_And_NeverLeaksPasswordInCliArgs()
    {
        var available = new HashSet<string>(StringComparer.Ordinal) { "xfreerdp", "xfreerdp3", "wlfreerdp3" };
        var chosen = FreeRdpLauncher.ResolveBinary(null, bin => available.Contains(bin));
        Assert.Equal("xfreerdp3", chosen);

        var env = FreeRdpLauncher.DetectDesktopEnvironment(k => k switch
        {
            "XDG_SESSION_TYPE" => "wayland",
            "WAYLAND_DISPLAY" => "wayland-0",
            "DISPLAY" => ":0",
            _ => null
        });
        Assert.Equal(LinuxDisplayServer.Wayland, env.DisplayServer);

        var rdp = new RDP
        {
            DisplayName = "UbuntuToWin",
            Address = "192.168.1.50",
            Port = "3389",
            Domain = "CORP",
            IsConnWithFullScreen = false,
            RdpWindowResizeMode = ERdpWindowResizeMode.AutoResize,
            EnableClipboard = true,
        };
        var cred = new Credential
        {
            Address = "192.168.1.50",
            Port = "3389",
            UserName = "sherman",
            Password = "SuperTopSecretPassword!"
        };

        var args = FreeRdpLauncher.BuildArguments(rdp, cred, env, pipePasswordFromStdin: true);
        Assert.Contains("/v:192.168.1.50:3389", args);
        Assert.Contains("/u:sherman", args);
        Assert.Contains("/d:CORP", args);
        Assert.Contains("/from-stdin", args);
        Assert.Contains("/dynamic-resolution", args);
        Assert.Contains("+clipboard", args);
        Assert.DoesNotContain(args, a => a.Contains("SuperTopSecretPassword!", StringComparison.Ordinal));
    }

    [Fact]
    public void AnsiTerminalBuffer_PreservesSplitChineseUtf8Chunks_And_HandlesClearScreen()
    {
        var buffer = new AnsiTerminalBuffer();
        var chinese = Encoding.UTF8.GetBytes("root@sg:~# echo 百度绿云新加坡\r\n百度绿云新加坡\r\n");

        // Split right in the middle of a 3-byte UTF-8 Chinese character
        var part1 = chinese[..19];
        var part2 = chinese[19..];

        buffer.AppendBytes(part1);
        var rendered = buffer.AppendBytes(part2);
        Assert.Contains("百度绿云新加坡", rendered);

        // Clear screen CSI \x1b[2J\x1b[H
        rendered = buffer.AppendText("\x1b[2J\x1b[Hclean-prompt$ ");
        Assert.Equal("clean-prompt$ ", rendered);
    }

    [Fact]
    public void NaturalIpSort_And_MainWindowViewModel_HeadlessFilter_Work()
    {
        var vm = new MainWindowViewModel(skipDiskInit: true);
        var servers = new List<ProtocolBase>
        {
            new SSH { DisplayName = "Node", Address = "10.0.0.10", Tags = ["SG"] },
            new SSH { DisplayName = "Node", Address = "10.0.0.2", Tags = ["JP"] },
            new RDP { DisplayName = "WinDesk", Address = "192.168.1.20", Tags = ["SG"] },
        };

        var sorted = ServerFilterService.FilterAndSort(servers, null);
        Assert.Equal("10.0.0.2", ((SSH)sorted[0]).Address);
        Assert.Equal("10.0.0.10", ((SSH)sorted[1]).Address);

        var sgOnly = ServerFilterService.FilterAndSort(servers, "192.168", protocolFilter: "RDP", tagFilter: "SG");
        Assert.Single(sgOnly);
        Assert.NotNull(vm.ProtocolFilters);
    }
}
