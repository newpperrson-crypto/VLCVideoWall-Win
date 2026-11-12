using System.Diagnostics;
using System.Net.Sockets;

namespace VLCVideoWall;

/// <summary>Запуск/остановка видеостены и определение её состояния.</summary>
public class WallController
{
    /// <summary>Локальный порт сторожа (лок = watchdog.ps1, одна копия сторожа).</summary>
    public int GuardPort { get; set; } = 42130;

    public string BaseDir { get; private set; }
    public string VlcPath { get; set; } = "";     // где установлен VLC (пусто = любой vlc)
    /// <summary>Локальный HTTP-порт VLC стены.</summary>
    public int VlcHttpPort { get; set; } = 4212;
    private Process _proc;

    public WallController(string baseDir) { BaseDir = baseDir; }

    private string StopFlag { get { return Path.Combine(BaseDir, "stop.flag"); } }
    private string Watchdog { get { return Path.Combine(BaseDir, "watchdog.ps1"); } }
    private string PidFile { get { return Path.Combine(BaseDir, "vlc.pid"); } }
    private string HeartbeatFile { get { return Path.Combine(BaseDir, "watchdog.heartbeat"); } }
    private string StatusFile { get { return Path.Combine(BaseDir, "watchdog.status"); } }

    /// <summary>Сколько секунд heartbeat считается свежим (сторож пишет его ~раз в секунду).</summary>
    private const int HeartbeatTimeoutSeconds = 20;

    /// <summary>Сколько секунд подтверждение VLM считается актуальным.</summary>
    private const int StatusTimeoutSeconds = 20;

    /// <summary>
    /// VW-01: сторож пишет PID и время старта своего VLC. Свои процессы определяем только
    /// по этому файлу, а не по пути к vlc.exe — иначе закрывается VLC, открытый пользователем.
    /// </summary>
    private bool TryGetOwnVlc(out int pid, out Process proc)
    {
        pid = 0;
        proc = null;
        try
        {
            if (!File.Exists(PidFile)) return false;

            int parsedPid;
            long ticks;
            if (!VlcProcessInfo.TryParse(File.ReadAllText(PidFile), out parsedPid, out ticks)) return false;

            var p = Process.GetProcessById(parsedPid);
            // защита от переиспользования PID: сверяем время старта
            if (!VlcProcessInfo.MatchesStartTime(ticks, p.StartTime)) { p.Dispose(); return false; }
            if (!string.Equals(p.ProcessName, "vlc", StringComparison.OrdinalIgnoreCase)) { p.Dispose(); return false; }

            pid = parsedPid;
            proc = p;
            return true;
        }
        catch
        {
            pid = 0;
            proc = null;
            return false;
        }
    }

    /// <summary>
    /// FUP-05: сторож жив, если свежий heartbeat-файл. Порт слушателя — только резерв
    /// (для сборок сторожа без heartbeat): очередь подключений может копиться, а
    /// «порт открыт» само по себе ничего не доказывает.
    /// </summary>
    public bool IsGuardAlive()
    {
        try
        {
            if (File.Exists(HeartbeatFile))
            {
                long ticks;
                if (long.TryParse(File.ReadAllText(HeartbeatFile).Trim(), out ticks))
                {
                    long age = DateTime.UtcNow.Ticks - ticks;
                    return age >= 0 && age < TimeSpan.FromSeconds(HeartbeatTimeoutSeconds).Ticks;
                }
            }
        }
        catch { }
        return IsPortOpen(GuardPort, 400);
    }

    private bool IsPortOpen(int port, int timeoutMs)
    {
        try
        {
            using (var c = new TcpClient())
            {
                var ar = c.BeginConnect("127.0.0.1", port, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(timeoutMs)) return false;
                c.EndConnect(ar);
                return true;
            }
        }
        catch { return false; }
    }

    /// <summary>Сторож точно занят: свежий heartbeat или занятый лок-порт (запускать второй нельзя).</summary>
    private bool IsGuardBusy()
    {
        return IsGuardAlive() || IsPortOpen(GuardPort, 400);
    }

    /// <summary>Отвечает ли HTTP-интерфейс нашего VLC (порт задаёт сторож).</summary>
    public bool IsHttpUp()
    {
        try
        {
            using (var c = new TcpClient())
            {
                var ar = c.BeginConnect("127.0.0.1", VlcHttpPort, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(300)) return false;
                c.EndConnect(ar);
                return true;
            }
        }
        catch { return false; }
    }

    /// <summary>Работает ли наш (запущенный сторожем) VLC.</summary>
    public bool IsVlcRunning()
    {
        Process p;
        int pid;
        bool ok = TryGetOwnVlc(out pid, out p);
        if (p != null) p.Dispose();
        return ok;
    }

    public bool IsRunning { get { return State == WallState.Running; } }

    /// <summary>
    /// FUP-03/FUP2-04: «работает» — это живой сторож, свой VLC, отвечающий порт
    /// И подтверждение сторожа, что он действительно получил ответ VLM.
    /// Открытый TCP-порт сам по себе зелёного статуса не даёт.
    /// </summary>
    public WallState State
    {
        get
        {
            bool guard = IsGuardAlive();
            bool vlc = IsVlcRunning();

            if (guard && vlc)
                return IsHttpUp() && IsVlmConfirmed() ? WallState.Running : WallState.GuardOnly;
            if (guard) return WallState.Starting;          // сторож есть, VLC ещё поднимается
            if (vlc) return WallState.VlcWithoutGuard;     // VLC остался без сторожа
            return WallState.Stopped;
        }
    }

    /// <summary>Есть ли у стены живые компоненты: свой VLC или сторож.</summary>
    public bool HasAnyComponents { get { return IsVlcRunning() || IsGuardBusy(); } }

    /// <summary>FUP2-03: «можно запустить» — только когда останавливать нечего.</summary>
    public bool CanStart { get { return !HasAnyComponents; } }

    /// <summary>FUP2-03: «есть что останавливать» — и в состоянии VlcWithoutGuard тоже.</summary>
    public bool CanStop { get { return HasAnyComponents; } }

    /// <summary>Стена занята: её нельзя запускать заново (кнопки и меню трея).</summary>
    public bool IsActive { get { return HasAnyComponents; } }

    /// <summary>
    /// Запуск сторожа. Асинхронный: ожидание завершения прошлого VLC больше не
    /// замораживает окно (раньше здесь был Thread.Sleep в цикле до 8 секунд).
    /// </summary>
    public async Task StartAsync()
    {
        if (IsGuardBusy())
            throw new InvalidOperationException(
                "Порт сторожа (" + GuardPort + ") занят: вероятно, сторож уже работает. Сначала остановите стену.");

        // сторож не работает, но VLC от прошлого запуска ещё умирает — ждём, не блокируя интерфейс
        if (IsVlcRunning())
        {
            await KillOwnVlcAsync();
            var sw0 = Stopwatch.StartNew();
            while (sw0.ElapsedMilliseconds < 8000 && IsVlcRunning()) await Task.Delay(250);

            // FUP3-01: второй экземпляр не поднимаем, если прежний так и не завершился
            if (IsVlcRunning())
                throw new InvalidOperationException(
                    "Предыдущий VLC не завершился" +
                    (string.IsNullOrEmpty(_lastKillError) ? "" : " (" + _lastKillError + ")") +
                    ". Остановите стену и повторите запуск.");
        }

        if (!File.Exists(Watchdog))
            throw new FileNotFoundException(
                "Рядом с программой нет файла watchdog.ps1 (сторож стены).\r\n" +
                "Положите watchdog.ps1 из архива релиза в папку " + BaseDir + " и запустите снова.");
        try { if (File.Exists(StopFlag)) File.Delete(StopFlag); } catch { }

        var psi = new ProcessStartInfo("powershell.exe",
            "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"" + Watchdog + "\"" +
            " -WallPort " + VlcHttpPort + " -GuardPort " + GuardPort);
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.WorkingDirectory = BaseDir;
        _proc = Process.Start(psi);
    }

    public async Task<StopResult> StopAsync(int timeoutMs = 30000)
    {
        try { File.WriteAllText(StopFlag, "stop"); }
        catch (Exception e) { throw new IOException("Не удалось создать stop.flag: " + e.Message); }

        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (!File.Exists(StopFlag)) break;                  // сторож снял флаг — вышел штатно
            // FUP2-01: если сторожа нет, флаг снимать некому — нельзя ждать полный таймаут
            if (!IsGuardBusy()) break;
            await Task.Delay(200);
        }

        // FUP3-01: завершение подтверждаем ожиданием, PID-файл удаляем только после него
        try { if (_proc != null && !_proc.HasExited) _proc.Kill(true); } catch { }
        await KillOwnVlcAsync();
        try { if (File.Exists(StopFlag)) File.Delete(StopFlag); } catch { }

        // FUP2-01: успех — это отсутствие ВСЕХ наших компонентов, а не «состояние не Running»
        sw.Restart();
        while (sw.ElapsedMilliseconds < 12000 && (IsGuardBusy() || IsVlcRunning())) await Task.Delay(200);

        bool ownVlc = IsVlcRunning();
        bool guard = IsGuardBusy();
        bool foreign = !ownVlc && !File.Exists(PidFile) && AnyVlcProcessExists();

        string note = "";
        if (ownVlc || guard)
        {
            var parts = new List<string>();
            if (ownVlc) parts.Add("свой VLC");
            if (guard) parts.Add("сторож");
            note = "не остановились: " + string.Join(", ", parts);
            if (ownVlc && File.Exists(PidFile))
                note += "; vlc.pid сохранён — остановку можно повторить" +
                        (string.IsNullOrEmpty(_lastKillError) ? "" : " (" + _lastKillError + ")");
            if (foreign) note += "; есть посторонний vlc.exe, его не трогаем";
        }
        else if (foreign)
        {
            note = "свой VLC завершён; посторонний vlc.exe оставлен работать";
        }

        return new StopResult
        {
            Stopped = !ownVlc && !guard,
            OwnVlcRunning = ownVlc,
            GuardRunning = guard,
            ForeignVlcSeen = foreign,
            Note = note
        };
    }

    /// <summary>PID своего VLC из vlc.pid (нужен для поиска окна стены).</summary>
    public int OwnVlcPid()
    {
        Process p;
        int pid;
        if (!TryGetOwnVlc(out pid, out p)) return 0;
        try { p.Dispose(); } catch { }
        return pid;
    }

    /// <summary>Есть ли вообще процессы vlc.exe (для честного сообщения «чужой не тронут»).
    private bool AnyVlcProcessExists()
    {
        try { return Process.GetProcessesByName("vlc").Length > 0; }
        catch { return false; }
    }

    /// <summary>Подтверждение сторожа, что VLM отвечает: свежий watchdog.status с vlm=ok.</summary>
    public bool IsVlmConfirmed()
    {
        try
        {
            if (!File.Exists(StatusFile)) return false;
            WallStatusFile st;
            if (!WallStatusFile.TryParse(File.ReadAllText(StatusFile), out st)) return false;
            return st.VlmOk && st.IsFresh(DateTime.UtcNow, StatusTimeoutSeconds);
        }
        catch { return false; }
    }

    /// <summary>Сколько камер сторож видит играющими (0, если подтверждения нет).</summary>
    public int PlayingChannels()
    {
        try
        {
            WallStatusFile st;
            if (File.Exists(StatusFile) &&
                WallStatusFile.TryParse(File.ReadAllText(StatusFile), out st) &&
                st.IsFresh(DateTime.UtcNow, StatusTimeoutSeconds))
                return st.Playing;
        }
        catch { }
        return 0;
    }

    private string _lastKillError = "";

    /// <summary>Что помешало завершить свой VLC (пусто, если завершился штатно).</summary>
    public string LastKillError { get { return _lastKillError; } }

    /// <summary>
    /// FUP3-01: завершение только собственного VLC (PID + время старта) с подтверждением.
    /// PID-файл удаляется лишь после фактического выхода процесса — иначе приложение
    /// теряет возможность остановить его повторно и может объявить ложный успех.
    /// </summary>
    public async Task<bool> KillOwnVlcAsync(int timeoutMs = 6000)
    {
        Process p;
        int pid;
        if (!TryGetOwnVlc(out pid, out p)) return true;      // своего VLC нет — и удалять нечего

        _lastKillError = "";
        try { p.Kill(true); }
        catch (Exception e) { _lastKillError = "не удалось завершить PID " + pid + ": " + e.Message; }

        var sw = Stopwatch.StartNew();
        bool gone = HasProcessExited(p);
        while (!gone && sw.ElapsedMilliseconds < timeoutMs)
        {
            await Task.Delay(150);
            gone = HasProcessExited(p);
        }
        try { p.Dispose(); } catch { }

        if (gone)
        {
            try { if (File.Exists(PidFile)) File.Delete(PidFile); } catch { }
        }
        else if (_lastKillError.Length == 0)
        {
            _lastKillError = "VLC (PID " + pid + ") не завершился за " + Math.Max(1, timeoutMs / 1000) + " с";
        }
        return gone;
    }

    /// <summary>Завершился ли процесс. Объект привязан к конкретному экземпляру, подмена PID не страшна.</summary>
    private static bool HasProcessExited(Process p)
    {
        try { return p.HasExited; }
        catch { return true; }   // процесса уже нет
    }
}

/// <summary>Состояние стены, как его видит приложение.</summary>
public enum WallState
{
    /// <summary>Сторожа и VLC нет.</summary>
    Stopped,
    /// <summary>Сторож работает, VLC ещё не поднялся (запуск или перезапуск VLC).</summary>
    Starting,
    /// <summary>Сторож и VLC есть, но HTTP-интерфейс не отвечает — восстановление невозможно.</summary>
    GuardOnly,
    /// <summary>VLC остался от прошлого запуска без сторожа — нажать «Остановить».</summary>
    VlcWithoutGuard,
    /// <summary>Сторож, свой VLC и HTTP отвечают.</summary>
    Running
}

/// <summary>
/// Результат остановки: отдельно «команда отправлена и обработана» и
/// «компоненты действительно завершились» — UI не должен показывать успех,
/// если в памяти остались свой VLC или сторож.
/// </summary>
public sealed class StopResult
{
    /// <summary>Наши компоненты (свой VLC и сторож) остановлены.</summary>
    public bool Stopped { get; set; }

    /// <summary>Свой (запущенный сторожем) VLC ещё работает.</summary>
    public bool OwnVlcRunning { get; set; }

    /// <summary>Сторож ещё работает (свежий heartbeat или занятый лок-порт).</summary>
    public bool GuardRunning { get; set; }

    /// <summary>В системе есть посторонний vlc.exe — мы его не завершали.</summary>
    public bool ForeignVlcSeen { get; set; }

    /// <summary>Готовая строка для пользователя: что именно осталось.</summary>
    public string Note { get; set; } = "";
}
