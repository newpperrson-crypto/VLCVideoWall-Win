using System;
using System.IO;
using System.Linq;
using System.Text;

namespace VLCVideoWall;

/// <summary>
/// Плейлист для звукового процесса. Адрес камеры с логином и паролем передаётся VLC
/// файлом, а не аргументом командной строки: в списке процессов Windows секретов не видно.
/// Файлы лежат в подпапке audio-temp рядом с программой и удаляются при остановке канала.
/// </summary>
internal static class AudioPlaylist
{
    private static readonly object Sync = new object();

    private static string Dir
    {
        get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "audio-temp"); }
    }

    private static string FileFor(string name)
    {
        var safe = new string((name ?? "ch").Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_').ToArray());
        if (safe.Length == 0) safe = "ch";
        return Path.Combine(Dir, "audio-" + safe + ".xspf");
    }

    /// <summary>Записать плейлист для камеры. null — не удалось.</summary>
    public static string Write(string name, string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        lock (Sync)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                var path = FileFor(name);

                var sb = new StringBuilder();
                sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
                sb.AppendLine("<playlist version=\"1\" xmlns=\"http://xspf.org/ns/0/\">");
                sb.AppendLine("  <trackList>");
                sb.AppendLine("    <track>");
                sb.Append("      <location>").Append(Escape(url.Trim())).AppendLine("</location>");
                sb.AppendLine("    </track>");
                sb.AppendLine("  </trackList>");
                sb.AppendLine("</playlist>");

                // перезапись целиком: старый адрес не остаётся в файле
                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
                return path;
            }
            catch
            {
                return null;
            }
        }
    }

    public static void Delete(string name)
    {
        lock (Sync)
        {
            try
            {
                var path = FileFor(name);
                if (File.Exists(path)) File.Delete(path);
            }
            catch { }
        }
    }

    public static void DeleteAll()
    {
        lock (Sync)
        {
            try
            {
                if (!Directory.Exists(Dir)) return;
                foreach (var f in Directory.GetFiles(Dir, "audio-*.xspf"))
                {
                    try { File.Delete(f); } catch { }
                }
            }
            catch { }
        }
    }

    private static string Escape(string text)
    {
        return text
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;")
            .Replace("'", "&apos;");
    }
}
