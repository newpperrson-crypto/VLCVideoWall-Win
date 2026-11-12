using System;
using System.Globalization;
using System.IO;

namespace VLCVideoWall;

/// <summary>
/// Состояние звука, которое публикует сторож в <c>audio.status</c>:
/// время записи (UTC-тики), источник, признак «без звука», громкость, играет ли поток.
/// Отдельный файл, потому что звук выводит не стена VLC, а второй процесс.
/// </summary>
public sealed class AudioStatus
{
    public DateTime StampUtc { get; set; }
    public string Source { get; set; } = "";
    public bool Muted { get; set; }
    public int Volume { get; set; } = 80;
    public bool Playing { get; set; }

    public bool HasSource { get { return !string.IsNullOrEmpty(Source) && Source != "off"; } }

    /// <summary>Свежая ли запись: сторож пишет её каждый цикл опроса.</summary>
    public bool IsFresh(DateTime nowUtc, int maxAgeSeconds)
    {
        if (StampUtc == default(DateTime)) return false;
        var age = nowUtc.ToUniversalTime() - StampUtc;
        return age >= TimeSpan.Zero && age < TimeSpan.FromSeconds(maxAgeSeconds);
    }

    public static bool TryParse(string text, out AudioStatus status)
    {
        status = null;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var lines = text.Replace("\r", "").Split('\n');
        long ticks;
        if (!long.TryParse(lines[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out ticks) || ticks <= 0)
            return false;

        var st = new AudioStatus { StampUtc = new DateTime(ticks, DateTimeKind.Utc) };
        for (int i = 1; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line.Substring(0, eq).Trim().ToLowerInvariant();
            var value = line.Substring(eq + 1).Trim();

            switch (key)
            {
                case "source":
                    st.Source = value.Equals("off", StringComparison.OrdinalIgnoreCase) ? "" : value;
                    break;
                case "muted":
                    st.Muted = value.Equals("on", StringComparison.OrdinalIgnoreCase) ||
                               value.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
                               value == "1";
                    break;
                case "volume":
                    int vol;
                    if (int.TryParse(value, out vol)) st.Volume = Math.Clamp(vol, 0, 100);
                    break;
                case "playing":
                    st.Playing = value == "1" ||
                                 value.Equals("on", StringComparison.OrdinalIgnoreCase) ||
                                 value.Equals("yes", StringComparison.OrdinalIgnoreCase);
                    break;
            }
        }

        status = st;
        return true;
    }
}
