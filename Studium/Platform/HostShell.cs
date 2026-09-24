using System.Diagnostics;
using Microsoft.Win32;

namespace Studium.Platform;

/// <summary>
/// Opens folders (and later, the FFLogs Uploader) with the host OS. The game usually runs under Wine here,
/// where Windows' explorer would open Wine's own file manager, so on Wine we hand off to Linux's xdg-open.
/// </summary>
public static class HostShell
{
    private static readonly Lazy<bool> IsWine = new(() =>
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Wine");
            return key != null;
        }
        catch
        {
            return false;
        }
    });

    public static void OpenFolder(string windowsPath)
    {
        System.IO.Directory.CreateDirectory(windowsPath);
        if (IsWine.Value)
            StartUnix("/usr/bin/xdg-open", ToUnixPath(windowsPath));
        else
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{windowsPath}\"") { UseShellExecute = true });
    }

    public static void OpenUrl(string url)
    {
        if (IsWine.Value)
            StartUnix("/usr/bin/xdg-open", url);
        else
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    /// <summary>
    /// Starts a program: a Windows .exe directly, anything else (e.g. the Linux AppImage of the
    /// FFLogs Uploader) as a Linux program. Accepts Linux paths or Wine paths (Z:\...).
    /// </summary>
    public static void Launch(string path)
    {
        if (path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            var exe = path.StartsWith('/') && IsWine.Value ? ToWindowsPath(path) : path;
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe) ?? "" });
            return;
        }

        if (!IsWine.Value)
            throw new InvalidOperationException("Only .exe files can be launched outside Wine.");

        // The game runs with NoNewPrivs, which every child inherits and which stops AppImages from
        // mounting themselves (fusermount is setuid). systemd-run starts the program from the user's
        // systemd instead, outside the game's process tree. Without systemd, run the AppImage unmounted.
        const string script =
            "if command -v systemd-run >/dev/null 2>&1; then exec systemd-run --user --quiet --collect \"$0\"; " +
            "else APPIMAGE_EXTRACT_AND_RUN=1 exec \"$0\"; fi";
        StartUnix("/bin/sh", "-c", script, path.StartsWith('/') ? path : ToUnixPath(path));
    }

    /// <summary>Runs a Linux program from inside Wine (cmd's <c>start /unix</c>).</summary>
    private static void StartUnix(string unixExecutable, params string[] arguments)
    {
        var info = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { "/c", "start", "", "/unix", unixExecutable }.Concat(arguments))
            info.ArgumentList.Add(arg);
        Process.Start(info);
    }

    /// <summary>Wine path → Linux path, via Wine's own winepath tool.</summary>
    private static string ToUnixPath(string windowsPath) => WinePath("-u", windowsPath);

    /// <summary>Linux path → Wine path.</summary>
    private static string ToWindowsPath(string unixPath) => WinePath("-w", unixPath);

    private static string WinePath(string direction, string path)
    {
        var info = new ProcessStartInfo("winepath.exe") { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
        info.ArgumentList.Add(direction);
        info.ArgumentList.Add(path);
        using var process = Process.Start(info)!;
        var converted = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit(2000);
        return string.IsNullOrEmpty(converted) ? path : converted;
    }
}
