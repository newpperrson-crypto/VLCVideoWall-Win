using System;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace VLCVideoWall;

/// <summary>
/// Плейлист для звукового процесса. Адрес камеры с логином и паролем передаётся VLC
/// файлом, а не аргументом командной строки: в списке процессов Windows секретов не видно.
///
/// Файлы лежат в каталоге пользовательских данных (%LOCALAPPDATA%\VLCVideoWall\audio-temp),
/// а не рядом с программой: туда нет доступа у других учётных записей компьютера,
/// и каталог не попадает в резервные копии папки программы. На каталог ставится явная
/// таблица доступа «только текущий пользователь». Удаляются только собственные файлы
/// вида audio-*.xspf.
/// </summary>
internal static class AudioPlaylist
{
    private const string Prefix = "audio-";

    private static readonly object Sync = new object();
    private static bool _aclApplied;

    internal static string Dir
    {
        get
        {
            var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(root))
                root = Path.Combine(Path.GetTempPath(), "VLCVideoWall");
            return Path.Combine(root, "VLCVideoWall", "audio-temp");
        }
    }

    private static string FileFor(string name)
    {
        var safe = new string((name ?? "ch").Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_').ToArray());
        if (safe.Length == 0) safe = "ch";
        return Path.Combine(Dir, Prefix + safe + ".xspf");
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
                RestrictDirectory();

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

    /// <summary>Удалить только свои файлы (audio-*.xspf) в своём каталоге.</summary>
    public static void DeleteAll()
    {
        lock (Sync)
        {
            try
            {
                if (!Directory.Exists(Dir)) return;
                foreach (var f in Directory.GetFiles(Dir, Prefix + "*.xspf"))
                {
                    try { File.Delete(f); } catch { }
                }
            }
            catch { }
        }
    }

    /// <summary>
    /// Ограничить доступ к каталогу текущим пользователем. Ошибки (например, запрет смены
    /// прав групповой политикой) не мешают работе: каталог и так лежит в профиле пользователя.
    /// </summary>
    private static void RestrictDirectory()
    {
        if (_aclApplied) return;
        try
        {
            var identity = WindowsIdentity.GetCurrent();
            if (identity == null || identity.User == null) return;

            var info = new DirectoryInfo(Dir);
            var security = info.GetAccessControl();

            security.SetAccessRuleProtection(true, false);   // снять наследование, копии правил не оставлять

            foreach (FileSystemAccessRule rule in security.GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList())
                security.RemoveAccessRule(rule);

            security.AddAccessRule(new FileSystemAccessRule(
                identity.User,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));

            info.SetAccessControl(security);
            _aclApplied = true;
        }
        catch { }
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
