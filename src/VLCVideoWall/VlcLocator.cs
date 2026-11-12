using System;
using System.Collections.Generic;
using System.IO;

namespace VLCVideoWall;

/// <summary>Поиск vlc.exe: путь из конфигурации стены, затем типовые места установки.</summary>
internal sealed class VlcLocator
{
    /// <summary>Путь, указанный в настройках стены (может быть пустым).</summary>
    public static string Configured = "";

    public static bool TryResolve(out string path)
    {
        foreach (var candidate in Candidates())
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
                {
                    path = candidate;
                    return true;
                }
            }
            catch { }
        }
        path = "";
        return false;
    }

    private static IEnumerable<string> Candidates()
    {
        if (!string.IsNullOrWhiteSpace(Configured)) yield return Configured.Trim().Trim('"');

        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        yield return Path.Combine(pf, "VideoLAN", "VLC", "vlc.exe");

        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        yield return Path.Combine(pf86, "VideoLAN", "VLC", "vlc.exe");

        string found = null;
        try
        {
            var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\VideoLAN\VLC");
            if (key != null)
            {
                var install = key.GetValue("InstallDir") as string;
                if (!string.IsNullOrWhiteSpace(install)) found = Path.Combine(install, "vlc.exe");
            }
        }
        catch { }
        if (found != null) yield return found;
    }
}
