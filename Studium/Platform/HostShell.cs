using System.Diagnostics;
using Microsoft.Win32;

namespace Studium.Platform;

// Under Wine, explorer opens Wine's file manager, so use xdg-open.
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

        // The game runs with NoNewPrivs, so AppImages can't mount themselves (fusermount is setuid). systemd-run starts
        // them outside the game's process tree; without systemd, run unmounted.
        const string script =
            "if command -v systemd-run >/dev/null 2>&1; then exec systemd-run --user --quiet --collect \"$0\"; " +
            "else APPIMAGE_EXTRACT_AND_RUN=1 exec \"$0\"; fi";
        StartUnix("/bin/sh", "-c", script, path.StartsWith('/') ? path : ToUnixPath(path));
    }

    private static void StartUnix(string unixExecutable, params string[] arguments)
    {
        var info = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { "/c", "start", "", "/unix", unixExecutable }.Concat(arguments))
            info.ArgumentList.Add(arg);
        Process.Start(info);
    }

    private static string ToUnixPath(string windowsPath) => WinePath("-u", windowsPath);

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
