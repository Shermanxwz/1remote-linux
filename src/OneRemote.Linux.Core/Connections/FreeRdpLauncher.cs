using System.Diagnostics;
using OneRemote.Linux.Core.Models;

namespace OneRemote.Linux.Core.Connections;

public enum LinuxDisplayServer
{
    Wayland,
    X11,
    Headless,
}

public sealed record LinuxDesktopEnvironmentInfo(
    LinuxDisplayServer DisplayServer,
    string SessionTypeRaw,
    string WaylandDisplay,
    string X11Display);

/// <summary>
/// Native Ubuntu/Linux FreeRDP launcher prioritizing xfreerdp3 / xfreerdp with automatic
/// Wayland & X11 environment detection, dynamic resolution resizing, clipboard sync, and stdin password piping.
/// </summary>
public static class FreeRdpLauncher
{
    public static readonly string[] DefaultBinaryPriority =
    [
        "xfreerdp3",
        "xfreerdp",
        "wlfreerdp3",
        "sdl-freerdp3",
        "wlfreerdp",
        "sdl-freerdp",
    ];

    public static LinuxDesktopEnvironmentInfo DetectDesktopEnvironment(Func<string, string?>? envReader = null)
    {
        var get = envReader ?? Environment.GetEnvironmentVariable;
        var sessionType = (get("XDG_SESSION_TYPE") ?? string.Empty).Trim();
        var wayland = (get("WAYLAND_DISPLAY") ?? string.Empty).Trim();
        var x11 = (get("DISPLAY") ?? string.Empty).Trim();

        LinuxDisplayServer server;
        if (string.Equals(sessionType, "wayland", StringComparison.OrdinalIgnoreCase) || !string.IsNullOrEmpty(wayland))
            server = LinuxDisplayServer.Wayland;
        else if (string.Equals(sessionType, "x11", StringComparison.OrdinalIgnoreCase) || !string.IsNullOrEmpty(x11))
            server = LinuxDisplayServer.X11;
        else
            server = LinuxDisplayServer.Headless;

        return new LinuxDesktopEnvironmentInfo(server, sessionType, wayland, x11);
    }

    public static string? ResolveBinary(string? preferredBinary = null, Func<string, bool>? commandExists = null)
    {
        var probe = commandExists ?? CommandExistsInPath;

        if (!string.IsNullOrWhiteSpace(preferredBinary) && probe(preferredBinary.Trim()))
            return preferredBinary.Trim();

        foreach (var candidate in DefaultBinaryPriority)
        {
            if (probe(candidate))
                return candidate;
        }

        return null;
    }

    public static bool CommandExistsInPath(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return false;
        if (Path.IsPathRooted(command))
            return File.Exists(command);

        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var full = Path.Combine(dir, command);
            if (File.Exists(full))
                return true;
        }
        return false;
    }

    public static List<string> BuildArguments(
        RDP rdp,
        Credential resolvedCred,
        LinuxDesktopEnvironmentInfo? env = null,
        bool pipePasswordFromStdin = true)
    {
        env ??= DetectDesktopEnvironment();
        var host = string.IsNullOrWhiteSpace(resolvedCred.Address) ? rdp.Address : resolvedCred.Address;
        var port = int.TryParse(resolvedCred.Port, out var cp) && cp > 0 ? cp : rdp.GetPort(3389);
        var user = resolvedCred.UserName;

        var args = new List<string>
        {
            $"/v:{host}:{port}",
            "/cert:ignore",
            $"/t:1Remote - {rdp.DisplayName} ({host}:{port})",
        };

        if (!string.IsNullOrWhiteSpace(user))
            args.Add($"/u:{user}");

        if (!string.IsNullOrWhiteSpace(rdp.Domain))
            args.Add($"/d:{rdp.Domain}");

        if (pipePasswordFromStdin && !string.IsNullOrEmpty(resolvedCred.Password))
        {
            args.Add("/from-stdin");
        }

        if (rdp.IsAdministrativePurposes == true)
        {
            args.Add("/admin");
        }

        // Resolution & dynamic resizing
        if (rdp.IsConnWithFullScreen == true ||
            rdp.RdpWindowResizeMode is ERdpWindowResizeMode.StretchFullScreen or ERdpWindowResizeMode.FixedFullScreen)
        {
            args.Add("/f");
            if (rdp.RdpFullScreenFlag == ERdpFullScreenFlag.EnableFullAllScreens)
                args.Add("/multimon");
        }
        else
        {
            var w = rdp.RdpWidth is > 320 ? rdp.RdpWidth.Value : 1440;
            var h = rdp.RdpHeight is > 240 ? rdp.RdpHeight.Value : 900;
            args.Add($"/w:{w}");
            args.Add($"/h:{h}");

            if (rdp.RdpWindowResizeMode is null or ERdpWindowResizeMode.AutoResize or ERdpWindowResizeMode.Stretch)
            {
                args.Add("/dynamic-resolution");
            }
        }

        if (rdp.IsScaleFactorFollowSystem == false && rdp.ScaleFactorCustomValue is >= 100 and <= 300)
        {
            var validScale = rdp.ScaleFactorCustomValue.Value switch
            {
                >= 160 => 180,
                >= 125 => 140,
                _ => 100
            };
            args.Add($"/scale:{validScale}");
        }

        // Clipboard sync
        if (rdp.EnableClipboard != false)
        {
            args.Add("+clipboard");
        }
        else
        {
            args.Add("-clipboard");
        }

        // Home folder drive share
        if (rdp.EnableDiskDrives == true)
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(home) && Directory.Exists(home))
            {
                args.Add($"/drive:Home,{home}");
            }
        }

        // Audio redirection
        switch (rdp.AudioRedirectionMode)
        {
            case EAudioRedirectionMode.LeaveOnRemote:
                args.Add("/audio-mode:1");
                break;
            case EAudioRedirectionMode.Disabled:
                args.Add("/audio-mode:2");
                break;
            default:
                args.Add("/audio-mode:0");
                break;
        }

        // Gateway support
        if (rdp.GatewayMode == EGatewayMode.UseTheseGatewayServerSettings && !string.IsNullOrWhiteSpace(rdp.GatewayHostName))
        {
            args.Add($"/g:{rdp.GatewayHostName}");
            if (!string.IsNullOrWhiteSpace(rdp.GatewayUserName))
                args.Add($"/gu:{rdp.GatewayUserName}");
            var gwPass = Security.UnSafeStringEncipher.DecryptOrReturnOriginalString(rdp.GatewayPassword);
            if (!string.IsNullOrEmpty(gwPass))
                args.Add($"/gp:{gwPass}");
        }

        return args;
    }

    public static Process StartProcess(
        string binary,
        RDP rdp,
        Credential resolvedCred,
        Action<string>? onLogLine = null,
        Action<int>? onExited = null)
    {
        var env = DetectDesktopEnvironment();
        var hasPassword = !string.IsNullOrEmpty(resolvedCred.Password);
        var args = BuildArguments(rdp, resolvedCred, env, pipePasswordFromStdin: hasPassword);

        var psi = new ProcessStartInfo
        {
            FileName = binary,
            UseShellExecute = false,
            RedirectStandardInput = hasPassword,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = false,
        };

        // Ensure XWayland fallback works smoothly on Ubuntu GNOME Wayland if running xfreerdp3/xfreerdp
        if (env.DisplayServer == LinuxDisplayServer.Wayland && binary.StartsWith("xfreerdp", StringComparison.OrdinalIgnoreCase))
        {
            psi.Environment["GDK_BACKEND"] = "x11";
        }

        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        var proc = new Process
        {
            StartInfo = psi,
            EnableRaisingEvents = true,
        };

        proc.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
                onLogLine?.Invoke(e.Data);
        };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
                onLogLine?.Invoke(e.Data);
        };
        proc.Exited += (_, _) =>
        {
            var exitCode = 0;
            try { exitCode = proc.ExitCode; } catch { /* ignore */ }
            onExited?.Invoke(exitCode);
        };

        if (!proc.Start())
            throw new InvalidOperationException($"无法启动 FreeRDP 进程：{binary}");

        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        if (hasPassword)
        {
            proc.StandardInput.WriteLine(resolvedCred.Password);
            proc.StandardInput.Flush();
            proc.StandardInput.Close();
        }

        return proc;
    }
}
