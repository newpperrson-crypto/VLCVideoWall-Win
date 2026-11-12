namespace VLCVideoWall;

/// <summary>
/// Разбор служебного файла <c>vlc.pid</c> (PID и время старта своего VLC).
/// Вынесено отдельно, чтобы покрыть разбор тестами: приложение не должно
/// принять чужой процесс за свой ни при повреждённом файле, ни после
/// переиспользования PID.
/// </summary>
public static class VlcProcessInfo
{
    /// <summary>Формат файла: «PID время_старта_utc_ticks» (разделитель — пробел).</summary>
    public static bool TryParse(string content, out int pid, out long startTicks)
    {
        pid = 0;
        startTicks = 0;
        if (string.IsNullOrWhiteSpace(content)) return false;

        var parts = content.Trim()
            .Split(new[] { ' ', ';', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return false;

        if (!int.TryParse(parts[0], out pid) || pid <= 0) { pid = 0; return false; }
        if (!long.TryParse(parts[1], out startTicks) || startTicks <= 0) { pid = 0; startTicks = 0; return false; }
        return true;
    }

    /// <summary>Совпадает ли время старта найденного процесса с записанным (±2 c на округления).</summary>
    public static bool MatchesStartTime(long expectedTicks, DateTime actualStartUtc)
    {
        if (expectedTicks <= 0) return false;
        return Math.Abs(actualStartUtc.ToUniversalTime().Ticks - expectedTicks) <= 2 * TimeSpan.TicksPerSecond;
    }
}
