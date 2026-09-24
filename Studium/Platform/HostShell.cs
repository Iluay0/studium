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

    /// <summary>Runs a Linux program from inside Wine (cmd's <c>start /unix</c>).</summary>
    private static void StartUnix(string unixExecutable, string argument)
    {
        var info = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { "/c", "start", "", "/unix", unixExecutable, argument })
            info.ArgumentList.Add(arg);
        Process.Start(info);
    }

    /// <summary>Wine path → Linux path, via Wine's own winepath tool.</summary>
    private static string ToUnixPath(string windowsPath)
    {
        var info = new ProcessStartInfo("winepath.exe") { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
        info.ArgumentList.Add("-u");
        info.ArgumentList.Add(windowsPath);
        using var process = Process.Start(info)!;
        var unixPath = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit(2000);
        return string.IsNullOrEmpty(unixPath) ? windowsPath : unixPath;
    }
}
