using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VLCVideoWall;

public class Camera
{
    public bool Enabled { get; set; } = true;
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";

    [JsonIgnore]
    public string Check { get; set; } = "";
}

/// <summary>Содержимое Mosaic.conf — единственного файла, который читает сторож.</summary>
public class WallConfig
{
    public string Layout { get; set; } = "2x2";
    public int CanvasW { get; set; } = 1920;
    public int CanvasH { get; set; } = 1080;
    public List<Camera> Cameras { get; set; } = new();
    public string LogDir { get; set; } = "";
    public int LogKeepDays { get; set; } = 30;
    public string VlcPath { get; set; } = "";

    /// <summary>
    /// Запускать стену в окне, а не на весь экран. Размер окна равен разрешению полотна
    /// (canvas). По умолчанию выключено — стена занимает весь экран.
    /// </summary>
    public bool Windowed { get; set; }

    /// <summary>Локальный HTTP-порт VLC стены (127.0.0.1).</summary>
    public int PortWall { get; set; } = 4212;

    /// <summary>Локальный порт сторожа — защита от второго экземпляра.</summary>
    public int PortGuard { get; set; } = 42130;

    /// <summary>Первый порт звуковых процессов: по одному на включённую камеру.</summary>
    public int PortAudio { get; set; } = 4213;

    [JsonIgnore] public int Cols { get; private set; } = 2;
    [JsonIgnore] public int Rows { get; private set; } = 2;
    [JsonIgnore] public int Windows { get { return Cols * Rows; } }

    public static WallConfig Load(string path)
    {
        var cfg = new WallConfig();
        if (!File.Exists(path))
        {
            // свежая установка: камер ещё нет, список пустой — пользователь добавит свои
            return cfg;
        }

        foreach (var raw in File.ReadAllLines(path, Encoding.UTF8))
        {
            var t = raw.Trim().TrimStart('\uFEFF');
            if (t.Length == 0 || t.StartsWith("#")) continue;

            var eq = t.IndexOf('=');
            if (t.StartsWith("layout") && eq > 0)
            {
                cfg.Layout = t.Substring(eq + 1).Trim();
                continue;
            }
            if (t.StartsWith("logdir") && eq > 0)
            {
                cfg.LogDir = t.Substring(eq + 1).Trim().Trim('"');
                continue;
            }
            if (t.StartsWith("logkeep") && eq > 0)
            {
                if (int.TryParse(t.Substring(eq + 1).Trim(), out var days) && days >= 0) cfg.LogKeepDays = days;
                continue;
            }
            if (t.StartsWith("vlcpath") && eq > 0)
            {
                cfg.VlcPath = t.Substring(eq + 1).Trim().Trim('"');
                continue;
            }
            if (t.StartsWith("port_wall") && eq > 0)
            {
                int v;
                if (int.TryParse(t.Substring(eq + 1).Trim(), out v) && v >= 1024 && v <= 65535) cfg.PortWall = v;
                continue;
            }
            if (t.StartsWith("port_guard") && eq > 0)
            {
                int v;
                if (int.TryParse(t.Substring(eq + 1).Trim(), out v) && v >= 1024 && v <= 65535) cfg.PortGuard = v;
                continue;
            }
            if (t.StartsWith("port_audio") && eq > 0)
            {
                int v;
                if (int.TryParse(t.Substring(eq + 1).Trim(), out v) && v >= 1024 && v <= 65535) cfg.PortAudio = v;
                continue;
            }
            if (t.StartsWith("windowed") && eq > 0)
            {
                var v = t.Substring(eq + 1).Trim().Trim('"').ToLowerInvariant();
                cfg.Windowed = v == "yes" || v == "on" || v == "true" || v == "1" || v == "да";
                continue;
            }
            if (t.StartsWith("canvas") && eq > 0)
            {
                var m2 = System.Text.RegularExpressions.Regex.Match(t.Substring(eq + 1), @"(\d+)\s*[xхX×]\s*(\d+)");
                if (m2.Success)
                {
                    cfg.CanvasW = int.Parse(m2.Groups[1].Value);
                    cfg.CanvasH = int.Parse(m2.Groups[2].Value);
                }
                continue;
            }
            if (t.StartsWith("off cam "))
            {
                // отключённая камера остаётся в конфиге и не теряется при перезапуске
                var parts = t.Split(new[] { ' ', '\t' }, 4, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 4)
                    cfg.Cameras.Add(new Camera { Name = parts[2], Url = parts[3].Trim().Trim('"'), Enabled = false });
                continue;
            }
            if (t.StartsWith("cam "))
            {
                var parts = t.Split(new[] { ' ', '\t' }, 3, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 3)
                    cfg.Cameras.Add(new Camera { Name = parts[1], Url = parts[2].Trim().Trim('"') });
            }
        }

        cfg.ApplyLayout();
        return cfg;
    }

    public void ApplyLayout()
    {
        var m = System.Text.RegularExpressions.Regex.Match(Layout ?? "", @"^\s*(\d+)\s*[xхX×]\s*(\d+)\s*$");
        if (m.Success)
        {
            Cols = Math.Clamp(int.Parse(m.Groups[1].Value), 1, 16);
            Rows = Math.Clamp(int.Parse(m.Groups[2].Value), 1, 16);
            // не больше 16 окон: лишние строки отбрасываем
            while (Cols * Rows > 16 && Rows > 1) Rows--;
        }
        else
        {
            Cols = 2; Rows = 2;
        }
        Layout = Cols + "x" + Rows;
    }

    public void Save(string path)
    {
        ApplyLayout();

        // полотно: 640x480 ... 3840x2160, чётные стороны
        CanvasW = Math.Clamp(CanvasW / 2 * 2, 640, 3840);
        CanvasH = Math.Clamp(CanvasH / 2 * 2, 480, 2160);

        // имена каналов: уникальные, латиница/цифры, иначе chN по порядку
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int idx = 0;
        foreach (var cam in Cameras)
        {
            idx++;
            var name = (cam.Name ?? "").Trim();
            if (name.Length == 0 || !System.Text.RegularExpressions.Regex.IsMatch(name, @"^[A-Za-z][A-Za-z0-9_]*$") || used.Contains(name))
            {
                name = "ch" + idx;
                while (used.Contains(name)) { idx++; name = "ch" + idx; }
            }
            used.Add(name);
            cam.Name = name;
        }

        var sb = new StringBuilder();
        sb.AppendLine("# ============================================================");
        sb.AppendLine("#  Конфиг видеостены. Файл правится приложением VLCVideoWall.");
        sb.AppendLine("#  Вручную тоже можно: строки layout / cam / logdir / logkeep.");
        sb.AppendLine("#");
        sb.AppendLine("#  layout = КОЛОНКИxСТРОКИ  (1x1 … 4x4, но не больше 16 окон)");
        sb.AppendLine("#  canvas = ШИРИНАxВЫСОТА полотна (1280x720, 1920x1080, 2560x1440,");
        sb.AppendLine("#           3840x2160, 1280x1024 или своё от 640x480 до 3840x2160)");
        sb.AppendLine("#  cam ИМЯ rtsp://ЛОГИН:ПАРОЛЬ@АДРЕС:554/ПУТЬ");
        sb.AppendLine("#  off cam ИМЯ rtsp://...  — камера выключена в приложении, но остаётся в списке");
        sb.AppendLine("#      порядок строк = порядок окон: слева-направо, сверху-вниз");
        sb.AppendLine("#  audio = ИМЯ камеры — звук этой камеры выводится отдельно");
        sb.AppendLine("#          (пусто = без звука; Mosaic в VLC звук не собирает)");
        sb.AppendLine("#  volume = громкость звука 0..100");
        sb.AppendLine("#  windowed = yes|no — запускать стену в окне размером с полотно");
        sb.AppendLine("#  port_wall / port_guard / port_audio — локальные порты (1024..65535),");
        sb.AppendLine("#      должны быть разными; звук занимает port_audio … port_audio+15");
        sb.AppendLine("#             (no = на весь экран, по умолчанию)");
        sb.AppendLine("#  vlcpath = путь к vlc.exe (пусто = найти автоматически)");
        sb.AppendLine("#  logdir  = папка журнала   (пусто = папка программы)");
        sb.AppendLine("#  logkeep = сколько дней хранить журналы (0 = не удалять)");
        sb.AppendLine("# ============================================================");
        sb.AppendLine();
        sb.AppendLine("layout = " + Layout);
        sb.AppendLine("canvas = " + CanvasW + "x" + CanvasH);
        sb.AppendLine("windowed = " + (Windowed ? "yes" : "no"));
        sb.AppendLine("port_wall = " + PortWall);
        sb.AppendLine("port_guard = " + PortGuard);
        sb.AppendLine("port_audio = " + PortAudio);
        if (!string.IsNullOrWhiteSpace(VlcPath)) sb.AppendLine("vlcpath = " + VlcPath.Trim());
        if (!string.IsNullOrWhiteSpace(LogDir)) sb.AppendLine("logdir = " + LogDir.Trim());
        sb.AppendLine("logkeep = " + LogKeepDays);
        sb.AppendLine();
        foreach (var cam in Cameras)
        {
            // включённая камера — обычная строка cam, отключённая — off cam:
            // обе читаются обратно, поэтому выключенная камера не исчезает из настроек
            sb.AppendLine((cam.Enabled ? "cam " : "off cam ") + cam.Name + " " + (cam.Url ?? "").Trim());
        }

        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }
}

/// <summary>Настройки самого приложения (не конфиг стены).</summary>
public class AppSettings
{
    public bool AutostartApp { get; set; }
    public bool AutostartWall { get; set; } = true;
    public bool MinimizeToTray { get; set; } = true;
    public bool CloseToTray { get; set; } = true;
    public bool LogAutoRefresh { get; set; } = true;
    public bool TopMost { get; set; } = true;
    public int LastTab { get; set; }
    public int Width { get; set; } = 1020;
    public int Height { get; set; } = 740;
    public int Left { get; set; } = -1;
    public int Top { get; set; } = -1;

    /// <summary>Общая поддержка звука в стене. По умолчанию выключена.</summary>
    public bool AudioEnabled { get; set; }

    /// <summary>Угол, в котором рисуется значок звука на плитке. По умолчанию правый нижний.</summary>
    public string AudioBadgeCorner { get; set; } = "bottom-right";

    /// <summary>Громкость звука в процентах (0..100).</summary>
    public int AudioVolume { get; set; } = 80;

    /// <summary>Камеры, у которых звук включён. По умолчанию ни у одной.</summary>
    public System.Collections.Generic.List<string> AudioChannels { get; set; }
        = new System.Collections.Generic.List<string>();

    /// <summary>
    /// Звук через локальную копию потока: камеру читает один процесс, стена и звук
    /// берут видео и звук из этой копии. Нужно камерам, которые разрешают только одно
    /// подключение. По умолчанию выключено (лишняя пересылка не всем нужна).
    /// </summary>
    public bool RelayAudio { get; set; }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static AppSettings Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                // файл мог быть сохранён другой программой (или PowerShell) с BOM:
                // JsonSerializer на строке с BOM падает, а настройки при этом молча
                // заменяются значениями по умолчанию — то есть выключенным звуком
                var text = File.ReadAllText(path).TrimStart('\uFEFF', ' ', '\r', '\n', '\t');
                if (text.Length == 0) return new AppSettings();
                return JsonSerializer.Deserialize<AppSettings>(text) ?? new AppSettings();
            }
        }
        catch { }
        return new AppSettings();
    }

    public void Save(string path)
    {
        try { File.WriteAllText(path, JsonSerializer.Serialize(this, Json), new UTF8Encoding(false)); } catch { }
    }
}
