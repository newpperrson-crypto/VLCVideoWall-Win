using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace VLCVideoWall;

/// <summary>Состояние звуковой дорожки камеры.</summary>
public enum AudioTrackState
{
    Unknown,    // ещё не проверяли
    Present,    // в RTSP-потоке есть звук
    Absent      // только видео
}

/// <summary>Состояние звука канала.</summary>
public enum AudioChannelState
{
    Off,         // звук камеры выключен
    Starting,    // процесс запускается
    Playing,     // процесс работает
    Recovering,  // процесс упал, идёт восстановление с задержкой
    Failed       // восстановление исчерпало попытки
}

/// <summary>
/// Звук стены. VLC собирает мозаику только из видео (у mosaic-bridge нет аудиопараметров),
/// поэтому каждая включённая камера получает собственный процесс VLC без видео.
///
/// Движок работает только при явном разрешении: ActivateRuntime() вызывает приложение
/// после успешного старта стены, DeactivateRuntime() — до остановки. Пока разрешения нет,
/// ни Tick(), ни StartEnabled(), ни асинхронные продолжения не запускают процессы, поэтому
/// после остановки стены звук не поднимается сам.
///
/// Порты назначает единственный метод AssignPortsLocked(): порт = BasePort + номер окна.
/// </summary>
public sealed class AudioController : IDisposable
{
    private sealed class Channel
    {
        public string Name = "";
        public string Url = "";
        public int Order;              // номер окна в текущем списке камер
        public int Port;
        public string Password = "";
        public Process Proc;
        public bool On;                // пользователь включил звук этой камеры
        public bool Starting;          // процесс запускается прямо сейчас
        public bool StopUnconfirmed;   // не удалось подтвердить завершение процесса
        public bool VolumeDirty;
        public int Fails;              // подряд неудачных запусков (сбрасывается только по наработке)
        public DateTime NextTry = DateTime.MinValue;
        public DateTime StartedAt = DateTime.MinValue;
        public AudioTrackState Track = AudioTrackState.Unknown;
        public AudioChannelState State = AudioChannelState.Off;
    }

    private const int PortSpan = 16;               // порты BasePort … BasePort+15
    private static readonly int[] BackoffSeconds = { 2, 5, 15, 45, 120 };
    private const int MaxFails = 5;

    /// <summary>Сколько процесс должен проработать, чтобы считать запуск успешным.</summary>
    private const int StableSeconds = 20;

    private readonly Dictionary<string, Channel> _channels = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sync = new();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(2) };
    private readonly CancellationTokenSource _volumeCts = new();
    private readonly Task _volumeLoop;
    private int _basePort = 4213;
    private volatile bool _runtimeActive;
    private bool _disposed;
    private Task _stopTask = Task.CompletedTask;

    public AudioController()
    {
        _volumeLoop = Task.Run(() => VolumeLoopAsync(_volumeCts.Token));
    }

    public bool Enabled { get; set; }          // общая поддержка звука
    public int Volume { get; set; } = 80;      // 0..100
    public string LastError { get; private set; } = "";

    /// <summary>Разрешение работы движка: включается только при работающей стене.</summary>
    public bool RuntimeActive { get { return _runtimeActive; } }

    public static int PortSpanSize { get { return PortSpan; } }

    /// <summary>Первый порт звуковых процессов. Смена пересчитывает порты каналов.</summary>
    public int BasePort
    {
        get { lock (_sync) return _basePort; }
        set { SetBasePort(value); }
    }

    public event Action Changed;

    // ---------------------------------------------------------------- разрешение работы

    /// <summary>
    /// Разрешить работу звука: вызывается после успешного старта стены.
    /// Включённые пользователем камеры поднимаются один раз.
    /// </summary>
    public void ActivateRuntime()
    {
        if (_disposed || !Enabled) return;
        _runtimeActive = true;
        StartEnabled();
    }

    /// <summary>
    /// Запретить работу звука и погасить процессы. Вызывается до остановки стены,
    /// при неудачном старте, при выходе и в Dispose. Пользовательский выбор камер остаётся.
    /// </summary>
    public void DeactivateRuntime()
    {
        _runtimeActive = false;
        _stopTask = Task.Run(() => StopAllProcesses());
    }

    /// <summary>Дождаться фактического завершения процессов (после DeactivateRuntime).</summary>
    public Task WaitStoppedAsync(int timeoutMs = 6000)
    {
        var task = _stopTask;
        return Task.WhenAny(task, Task.Delay(timeoutMs));
    }

    public void Dispose()
    {
        _disposed = true;
        _runtimeActive = false;
        try { _volumeCts.Cancel(); } catch { }
        StopAllProcesses();
        try { _volumeCts.Dispose(); } catch { }
        try { _volumeLoop.Wait(800); } catch { }
        _http.Dispose();
        AudioPlaylist.DeleteAll();
    }

    // ---------------------------------------------------------------- камеры и порты

    /// <summary>
    /// Актуальный список камер. Пересчитывает порты по порядку окон и перезапускает
    /// живые каналы, у которых сменился порт (только при разрешённой работе движка).
    /// </summary>
    public void SetCameras(IEnumerable<Camera> cameras)
    {
        var list = cameras.Where(c => c.Enabled).ToList();
        List<Channel> restart = new List<Channel>();

        lock (_sync)
        {
            var keep = new HashSet<string>(list.Select(c => c.Name), StringComparer.OrdinalIgnoreCase);
            foreach (var gone in _channels.Where(kv => !keep.Contains(kv.Key)).Select(kv => kv.Key).ToList())
            {
                var ch = _channels[gone];
                KillChannelBlocking(ch);
                _channels.Remove(gone);
            }

            for (int i = 0; i < list.Count; i++)
            {
                var cam = list[i];
                Channel ch;
                if (!_channels.TryGetValue(cam.Name, out ch))
                {
                    ch = new Channel
                    {
                        Name = cam.Name,
                        Password = Guid.NewGuid().ToString("N").Substring(0, 16)
                    };
                    _channels[cam.Name] = ch;
                }
                ch.Order = i;
                ch.Url = cam.Url;
            }

            AssignPortsLocked(restart);
        }

        if (!_runtimeActive) return;
        foreach (var ch in restart) RestartChannelAsync(ch);
    }

    /// <summary>Сменить базовый порт: порты пересчитываются, живые каналы перезапускаются.</summary>
    public void SetBasePort(int port)
    {
        int clamped = Math.Clamp(port, 1024, 65535 - PortSpan);
        List<Channel> restart = new List<Channel>();
        lock (_sync)
        {
            if (_basePort == clamped) return;
            _basePort = clamped;
            AssignPortsLocked(restart);
        }
        if (!_runtimeActive) return;
        foreach (var ch in restart) RestartChannelAsync(ch);
    }

    /// <summary>Единственное место, где назначаются порты. Требует _sync.</summary>
    private void AssignPortsLocked(List<Channel> restart)
    {
        foreach (var ch in _channels.Values)
        {
            int newPort = _basePort + ch.Order;
            if (ch.Port == newPort) continue;
            ch.Port = newPort;
            if (ch.Proc != null) restart.Add(ch);
        }
    }

    public List<KeyValuePair<string, int>> PortMap()
    {
        lock (_sync)
        {
            return _channels.Values
                .OrderBy(c => c.Order)
                .Select(c => new KeyValuePair<string, int>(c.Name, c.Port))
                .ToList();
        }
    }

    /// <summary>Инвариант: порты уникальны и лежат в диапазоне звука.</summary>
    public bool PortsConsistent(out string problem)
    {
        problem = null;
        lock (_sync)
        {
            var ports = _channels.Values.Select(c => c.Port).ToList();
            if (ports.Distinct().Count() != ports.Count)
            {
                problem = "повторяющиеся порты звука";
                return false;
            }
            foreach (var ch in _channels.Values)
            {
                if (ch.Port < _basePort || ch.Port >= _basePort + PortSpan)
                {
                    problem = "порт " + ch.Port + " вне диапазона звука";
                    return false;
                }
            }
        }
        return true;
    }

    // ---------------------------------------------------------------- состояние каналов

    public bool IsOn(string name)
    {
        lock (_sync) { Channel ch; return _channels.TryGetValue(name, out ch) && ch.On; }
    }

    public bool IsBusy(string name)
    {
        lock (_sync)
        {
            Channel ch;
            if (!_channels.TryGetValue(name, out ch)) return false;
            return ch.Starting || ch.State == AudioChannelState.Starting || ch.State == AudioChannelState.Recovering;
        }
    }

    public AudioChannelState State(string name)
    {
        lock (_sync)
        {
            Channel ch;
            return _channels.TryGetValue(name, out ch) ? ch.State : AudioChannelState.Off;
        }
    }

    public AudioTrackState TrackState(string name)
    {
        lock (_sync) { Channel ch; return _channels.TryGetValue(name, out ch) ? ch.Track : AudioTrackState.Unknown; }
    }

    public List<string> EnabledChannels()
    {
        lock (_sync) { return _channels.Values.Where(c => c.On).Select(c => c.Name).ToList(); }
    }

    public int FailedCount()
    {
        lock (_sync) { return _channels.Values.Count(c => c.State == AudioChannelState.Failed); }
    }

    public void RestoreStates(IEnumerable<string> enabled)
    {
        var set = new HashSet<string>(enabled ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        lock (_sync)
        {
            foreach (var ch in _channels.Values)
            {
                ch.On = set.Contains(ch.Name);
                ch.Starting = false;
                ch.StopUnconfirmed = false;
                ch.Fails = 0;
                ch.NextTry = DateTime.MinValue;
                ch.State = AudioChannelState.Off;
            }
        }
    }

    /// <summary>Забыть результаты проверки дорожек (стена остановлена — данные устарели).</summary>
    public void ResetTracks()
    {
        lock (_sync)
        {
            foreach (var ch in _channels.Values) ch.Track = AudioTrackState.Unknown;
        }
    }

    /// <summary>Проверить наличие звуковой дорожки у всех камер (в фоне).</summary>
    public void ProbeTracks()
    {
        List<Channel> snapshot;
        lock (_sync) snapshot = _channels.Values.ToList();

        foreach (var ch in snapshot)
        {
            var url = ch.Url;
            var name = ch.Name;
            Task.Run(() =>
            {
                var state = RtspProbe.HasAudioTrack(url, 3000);
                lock (_sync)
                {
                    Channel target;
                    if (_channels.TryGetValue(name, out target)) target.Track = state;
                }
                RaiseChanged();
            });
        }
    }

    // ---------------------------------------------------------------- включение и выключение

    /// <summary>
    /// Переключить звук камеры. Возвращает false, если переключать нечего.
    /// <paramref name="startProcess"/> = false — только запомнить настройку (стена остановлена).
    /// </summary>
    public bool Toggle(string name, bool startProcess = true)
    {
        bool wantOn;
        lock (_sync)
        {
            Channel ch;
            if (!_channels.TryGetValue(name, out ch)) return false;
            if (startProcess && ch.Track == AudioTrackState.Absent) return false;
            if (ch.Starting || ch.State == AudioChannelState.Starting || ch.State == AudioChannelState.Recovering)
                return false;

            wantOn = !ch.On;
            ch.On = wantOn;
            ch.Fails = 0;
            ch.NextTry = DateTime.MinValue;
            ch.State = wantOn ? AudioChannelState.Starting : AudioChannelState.Off;
            if (startProcess && wantOn && _runtimeActive && Enabled) ch.Starting = true;
        }

        RaiseChanged();

        if (!startProcess) return true;
        if (!_runtimeActive || !Enabled) return true;      // стена стоит — только запомнили

        if (wantOn) Task.Run(() => StartChannel(name));
        else Task.Run(() => { StopChannel(name); RaiseChanged(); });
        return true;
    }

    /// <summary>Поднять процессы включённых камер (только при разрешённой работе движка).</summary>
    public void StartEnabled()
    {
        if (_disposed || !Enabled || !_runtimeActive) return;

        var start = new List<string>();
        lock (_sync)
        {
            foreach (var ch in _channels.Values)
            {
                if (!ch.On) continue;
                if (ch.StopUnconfirmed) continue;
                if (ch.Starting || ch.State == AudioChannelState.Playing) continue;
                if (ch.Proc != null && !SafeExited(ch.Proc)) continue;
                ch.Starting = true;
                ch.State = AudioChannelState.Starting;
                start.Add(ch.Name);
            }
        }
        foreach (var n in start) Task.Run(() => StartChannel(n));
    }

    /// <summary>
    /// Периодический контроль: поднимает каналы, которые должны играть, но процесс пропал.
    /// Счётчик неудач сбрасывается только после устойчивой работы, поэтому быстро падающий
    /// процесс не перезапускается бесконечно.
    /// </summary>
    public void Tick()
    {
        if (_disposed || !Enabled || !_runtimeActive) return;

        var start = new List<string>();
        bool changed = false;

        lock (_sync)
        {
            foreach (var ch in _channels.Values)
            {
                bool alive = ch.Proc != null && !SafeExited(ch.Proc);
                if (alive)
                {
                    if (ch.State != AudioChannelState.Playing && ch.On)
                    {
                        ch.State = AudioChannelState.Playing;
                        changed = true;
                    }
                    // устойчивая работа — только теперь сбрасываем счётчик неудач
                    if (ch.Fails > 0 && ch.StartedAt != DateTime.MinValue &&
                        (DateTime.UtcNow - ch.StartedAt).TotalSeconds >= StableSeconds)
                    {
                        ch.Fails = 0;
                        ch.NextTry = DateTime.MinValue;
                    }
                    continue;
                }

                if (!ch.On) continue;
                if (ch.Track == AudioTrackState.Absent) continue;
                if (ch.Starting || ch.StopUnconfirmed) continue;

                if (ch.Proc != null) { KillChannelBlocking(ch); ch.Proc = null; }

                if (ch.State == AudioChannelState.Playing || ch.State == AudioChannelState.Starting)
                {
                    ch.Fails++;
                    changed = true;
                    if (ch.Fails > MaxFails)
                    {
                        ch.State = AudioChannelState.Failed;
                        continue;
                    }
                    int delay = BackoffSeconds[Math.Min(ch.Fails - 1, BackoffSeconds.Length - 1)];
                    ch.NextTry = DateTime.UtcNow.AddSeconds(delay);
                    ch.State = AudioChannelState.Recovering;
                    continue;
                }

                if (ch.State == AudioChannelState.Failed) continue;
                if (DateTime.UtcNow < ch.NextTry) continue;

                ch.Starting = true;
                ch.State = AudioChannelState.Starting;
                changed = true;
                start.Add(ch.Name);
            }
        }

        if (changed) RaiseChanged();
        foreach (var n in start) Task.Run(() => StartChannel(n));
    }

    // ---------------------------------------------------------------- процессы

    private void StartChannel(string name)
    {
        Channel ch;
        int port;
        string url, password;

        lock (_sync)
        {
            if (!_channels.TryGetValue(name, out ch)) return;
            if (_disposed || !Enabled || !_runtimeActive || !ch.On || ch.StopUnconfirmed)
            {
                ch.Starting = false;
                ch.State = AudioChannelState.Off;
                return;
            }
            if (ch.Track == AudioTrackState.Absent)
            {
                ch.Starting = false;
                ch.State = AudioChannelState.Off;
                return;
            }
            if (ch.Proc != null && !SafeExited(ch.Proc))
            {
                ch.Starting = false;
                ch.State = AudioChannelState.Playing;
                ch.VolumeDirty = true;
                return;
            }

            port = ch.Port;
            url = ch.Url;
            password = ch.Password;
        }

        string exe;
        if (!VlcLocator.TryResolve(out exe))
        {
            FailStart(name, "VLC не найден");
            return;
        }

        // адрес камеры (с логином и паролем) кладём в файл плейлиста:
        // в командной строке процесса секретов не остаётся
        var playlist = AudioPlaylist.Write(name, url);
        if (playlist == null)
        {
            FailStart(name, "не удалось подготовить файл потока");
            return;
        }

        var args = new StringBuilder();
        args.Append("-I dummy --no-video --no-video-title-show --rtsp-tcp ");
        args.Append("--volume ").Append(VolumeToVlc(Volume)).Append(' ');
        args.Append("--extraintf http --http-host 127.0.0.1 --http-port ").Append(port).Append(' ');
        args.Append("--http-password ").Append(password).Append(' ');
        args.Append('"').Append(playlist).Append('"');

        // перед запуском повторно проверяем, что остановка не началась
        lock (_sync)
        {
            if (_disposed || !_runtimeActive || !_channels.ContainsKey(name) || !_channels[name].On)
            {
                if (_channels.ContainsKey(name)) _channels[name].Starting = false;
                AudioPlaylist.Delete(name);
                return;
            }
        }

        try
        {
            var psi = new ProcessStartInfo(exe, args.ToString())
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory
            };
            var proc = Process.Start(psi);

            lock (_sync)
            {
                if (!_channels.TryGetValue(name, out ch) || !_runtimeActive || _disposed || !ch.On)
                {
                    // остановка пришла, пока процесс создавался — гасим сразу
                    try { if (!SafeExited(proc)) proc.Kill(); } catch { }
                    try { proc.Dispose(); } catch { }
                    AudioPlaylist.Delete(name);
                    if (ch != null) ch.Starting = false;
                    return;
                }
                ch.Proc = proc;
                ch.Starting = false;
                ch.StartedAt = DateTime.UtcNow;
                ch.State = AudioChannelState.Playing;
                ch.VolumeDirty = true;
                // Fails НЕ сбрасываем: успешный старт процесса ещё не значит, что звук идёт
            }
            LastError = "";
            RaiseChanged();
        }
        catch (Exception e)
        {
            AudioPlaylist.Delete(name);
            FailStart(name, "не удалось запустить звук: " + e.Message);
        }
    }

    private void FailStart(string name, string error)
    {
        if (!string.IsNullOrEmpty(error)) LastError = error;
        lock (_sync)
        {
            Channel ch;
            if (!_channels.TryGetValue(name, out ch)) return;
            ch.Starting = false;
            ch.Fails++;
            if (ch.Fails > MaxFails)
            {
                ch.State = AudioChannelState.Failed;
            }
            else
            {
                int delay = BackoffSeconds[Math.Min(ch.Fails - 1, BackoffSeconds.Length - 1)];
                ch.NextTry = DateTime.UtcNow.AddSeconds(delay);
                ch.State = AudioChannelState.Recovering;
            }
        }
        RaiseChanged();
    }

    private void StopChannel(string name)
    {
        lock (_sync)
        {
            Channel ch;
            if (!_channels.TryGetValue(name, out ch)) return;
            KillChannelBlocking(ch);
            ch.Starting = false;
            if (!ch.StopUnconfirmed) ch.State = AudioChannelState.Off;
        }
        AudioPlaylist.Delete(name);
    }

    /// <summary>
    /// Смена порта/адреса у работающего канала: гасим старый, подтверждаем завершение,
    /// затем поднимаем новый. Если завершение не подтверждено, второй экземпляр не создаём.
    /// </summary>
    private void RestartChannelAsync(Channel ch)
    {
        string name = ch.Name;
        lock (_sync)
        {
            Channel current;
            if (!_channels.TryGetValue(name, out current) || !ReferenceEquals(current, ch)) return;
            ch.Starting = true;
            ch.State = AudioChannelState.Starting;
        }
        AudioPlaylist.Delete(name);
        RaiseChanged();

        Task.Run(() =>
        {
            bool stopped;
            lock (_sync)
            {
                Channel current;
                if (!_channels.TryGetValue(name, out current)) return;
                stopped = KillChannelBlocking(current);
                current.Starting = false;
                if (!stopped)
                {
                    current.State = AudioChannelState.Failed;
                    LastError = "не удалось завершить звук камеры " + name + " перед сменой порта";
                    return;
                }
                if (_disposed || !_runtimeActive || !Enabled || !current.On)
                {
                    current.State = AudioChannelState.Off;
                    return;
                }
                current.Fails = 0;
                current.NextTry = DateTime.MinValue;
            }
            RaiseChanged();
            if (stopped) StartChannel(name);
        });
    }

    /// <summary>
    /// Погасить процесс и подтвердить завершение. При неудаче процесс остаётся в учёте
    /// (ch.Proc) и помечается StopUnconfirmed — второй экземпляр на тот же порт не пойдёт.
    /// Возвращает true, если процесс завершён (или его не было).
    /// </summary>
    private bool KillChannelBlocking(Channel ch)
    {
        var proc = ch.Proc;
        ch.VolumeDirty = false;
        if (proc == null)
        {
            ch.StopUnconfirmed = false;
            return true;
        }

        bool exited;
        try
        {
            if (!SafeExited(proc))
            {
                proc.Kill();
                exited = proc.WaitForExit(3000);
            }
            else
            {
                exited = true;
            }
        }
        catch (Exception e)
        {
            LastError = "завершение звука: " + e.Message;
            exited = SafeExited(proc);
        }

        if (!exited)
        {
            ch.StopUnconfirmed = true;
            ch.State = AudioChannelState.Failed;
            return false;
        }

        try { proc.Dispose(); } catch { }
        ch.Proc = null;
        ch.StopUnconfirmed = false;
        ch.StartedAt = DateTime.MinValue;
        return true;
    }

    private void StopAllProcesses()
    {
        List<string> names;
        lock (_sync)
        {
            names = _channels.Values.Select(c => c.Name).ToList();
            foreach (var ch in _channels.Values)
            {
                // пользовательский выбор (On) сохраняется: остановка стены его не теряет
                KillChannelBlocking(ch);
                ch.Starting = false;
                ch.Fails = 0;
                ch.NextTry = DateTime.MinValue;
                if (!ch.StopUnconfirmed) ch.State = AudioChannelState.Off;
            }
        }
        AudioPlaylist.DeleteAll();
    }

    /// <summary>Совместимость: погасить всё и запретить работу движка.</summary>
    public void StopAll()
    {
        DeactivateRuntime();
    }

    public int PlayingCount()
    {
        lock (_sync) return _channels.Values.Count(c => c.Proc != null && !SafeExited(c.Proc));
    }

    private static bool SafeExited(Process p)
    {
        try { return p.HasExited; } catch { return true; }
    }

    // ---------------------------------------------------------------- громкость

    private static int VolumeToVlc(int percent)
    {
        return (int)(Math.Clamp(percent, 0, 100) * 256 / 100.0);
    }

    public void ApplyVolumeAll()
    {
        lock (_sync)
        {
            foreach (var ch in _channels.Values) ch.VolumeDirty = true;
        }
    }

    /// <summary>
    /// Отправка громкости в фоне: интерфейс не блокируется, очередь запросов не растёт —
    /// пока запрос в пути, новое значение только помечается и уходит следующим.
    /// </summary>
    private async Task VolumeLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var todo = new List<Tuple<Channel, int>>();
                lock (_sync)
                {
                    foreach (var ch in _channels.Values)
                    {
                        if (!ch.VolumeDirty) continue;
                        if (ch.Proc == null || SafeExited(ch.Proc)) { ch.VolumeDirty = false; continue; }
                        ch.VolumeDirty = false;
                        todo.Add(Tuple.Create(ch, VolumeToVlc(Volume)));
                    }
                }

                foreach (var item in todo)
                {
                    if (ct.IsCancellationRequested) break;
                    await SendVolumeAsync(item.Item1, item.Item2, ct).ConfigureAwait(false);
                }

                await Task.Delay(120, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch
            {
                try { await Task.Delay(300, ct).ConfigureAwait(false); } catch { break; }
            }
        }
    }

    private async Task SendVolumeAsync(Channel ch, int value, CancellationToken ct)
    {
        int port;
        string password;
        lock (_sync)
        {
            if (ch.Proc == null || SafeExited(ch.Proc)) return;
            port = ch.Port;
            password = ch.Password;
        }

        var url = "http://127.0.0.1:" + port + "/requests/status.xml?command=volume&val=" + value;
        try
        {
            using (var req = new HttpRequestMessage(HttpMethod.Get, url))
            {
                var auth = Convert.ToBase64String(Encoding.UTF8.GetBytes(":" + password));
                req.Headers.Authorization = new AuthenticationHeaderValue("Basic", auth);
                using (var resp = await _http.SendAsync(req, ct).ConfigureAwait(false))
                {
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            LastError = "громкость: " + e.Message;
        }
    }

    private void RaiseChanged()
    {
        var handler = Changed;
        if (handler == null) return;
        try { handler(); } catch { }
    }
}
