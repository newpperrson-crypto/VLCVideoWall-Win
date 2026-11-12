namespace VLCVideoWall;

/// <summary>
/// Содержимое файла <c>watchdog.status</c>, который пишет сторож:
/// первая строка — UTC-тики записи, далее <c>vlm=ok|fail</c>, <c>playing=N</c>,
/// <c>total=N</c>.
///
/// Зачем: графическое приложение намеренно не знает пароль HTTP-интерфейса VLC,
/// поэтому не может само опросить <c>/requests/vlm.xml</c>. Открытый TCP-порт
/// ничего не доказывает (порт может слушать чужая программа или VLC без рабочего
/// VLM), а этот файл — подтверждение сторожа, что он действительно получил ответ
/// от VLM недавно.
/// </summary>
public sealed class WallStatusFile
{
    public long Ticks { get; set; }
    public bool VlmOk { get; set; }
    public int Playing { get; set; }
    public int Total { get; set; }

    /// <summary>Запись не старше maxAgeSeconds — иначе состоянию верить нельзя.</summary>
    public bool IsFresh(DateTime nowUtc, int maxAgeSeconds)
    {
        if (Ticks <= 0) return false;
        long age = nowUtc.ToUniversalTime().Ticks - Ticks;
        return age >= 0 && age <= maxAgeSeconds * TimeSpan.TicksPerSecond;
    }

    public static bool TryParse(string content, out WallStatusFile status)
    {
        status = null;
        if (string.IsNullOrWhiteSpace(content)) return false;

        var lines = content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0) return false;

        long ticks;
        if (!long.TryParse(lines[0].Trim(), out ticks) || ticks <= 0) return false;

        bool vlmOk = false;
        int playing = 0, total = 0;

        for (int i = 1; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line.Substring(0, eq).Trim().ToLowerInvariant();
            var val = line.Substring(eq + 1).Trim();

            switch (key)
            {
                case "vlm":
                    vlmOk = val.Equals("ok", StringComparison.OrdinalIgnoreCase);
                    break;
                case "playing":
                    int.TryParse(val, out playing);
                    break;
                case "total":
                    int.TryParse(val, out total);
                    break;
            }
        }

        status = new WallStatusFile { Ticks = ticks, VlmOk = vlmOk, Playing = playing, Total = total };
        return true;
    }
}
