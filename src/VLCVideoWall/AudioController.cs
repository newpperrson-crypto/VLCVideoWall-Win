using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
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
/// Процессами управляет приложение: так переключение мгновенное, а сторож не занят
/// ожиданием команд в своём цикле опроса.
///
/// Порты закрепляет только AssignPortsLocked(): порт = BasePort + номер окна камеры.
/// Поэтому после добавления, удаления или перестановки камер повторов быть не может,
/// а смена BasePort пересчитывает порты всем каналам.
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
        public bool VolumeDirty;       // нужно применить громкость
        public int Fails;              // подряд неудачных запусков
        public DateTime NextTry = DateTime.MinValue;
        public AudioTrackState Track = AudioTrackState.Unknown;
        public AudioChannelState State = AudioChannelState.Off;
    }

    private const int PortSpan = 16;               // порты BasePort … BasePort+15
    private static readonly int[] BackoffSeconds = { 2, 5, 15, 45, 120 };
    private const int MaxFails = 5;

    private readonly Dictionary<string, Channel> _channels = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sync = new();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(2) };
    private readonly CancellationTokenSource _volumeCts = new();
    private readonly Task _volumeLoop;
    private int _basePort = 4213;
    private bool _disposed;

    public AudioController()
    {
        _volumeLoop = Task.Run(() => VolumeLoopAsync(_volumeCts.Token));
    }

    public bool Enabled { get; set; }          // общая поддержка звука
    public int Volume { get; set; } = 80;      // 0..100
    public string LastError { get; private set; } = "";

    /// <summary>Сколько портов занимает звук.</summary>
    public static int PortSpanSize { get { return PortSpan; } }

    /// <summary>Первый порт звуковых процессов. Смена пересчитывает порты каналов.</summary>
    public int BasePort
    {
        get { lock (_sync) return _basePort; }
        set { SetBasePort(value); }
    }

    public event Action Changed;

    public void Dispose()
    {
        _disposed = true;
        lock (_sync)
        {
            foreach (var ch in _channels.Values) KillLocked(ch);
        }
        try { _volumeCts.Cancel(); } catch { }
        try { _volumeLoop.Wait(800); } catch { }
        try { _volumeCts.Dispose(); } catch { }
        _http.Dispose();
    }

    // ---------------------------------------------------------------- камеры и порты

    /// <summary>
    /// Актуальный список камер. Пересчитывает порты по текущему порядку окон и
    /// перезапускает те живые каналы, у которых сменился порт.
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
                KillLocked(ch);
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

        foreach (var ch in restart) RestartChannel(ch);
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
        foreach (var ch in restart) RestartChannel(ch);
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

    /// <summary>Карта «камера → порт» (для журнала и проверок).</summary>
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
            if (startProcess && ch.Track == AudioTrackState.Absent) return false;   // дорожки нет
            if (ch.Starting || ch.State == AudioChannelState.Starting || ch.State == AudioChannelState.Recovering)
                return false;

            wantOn = !ch.On;
            ch.On = wantOn;
            ch.Fails = 0;
            ch.NextTry = DateTime.MinValue;
            ch.State = wantOn ? AudioChannelState.Starting : AudioChannelState.Off;
            if (startProcess && wantOn) ch.Starting = true;
        }

        RaiseChanged();

        if (!startProcess) return true;

        if (wantOn) Task.Run(() => StartChannel(name));
        else Task.Run(() => { StopChannel(name); RaiseChanged(); });
        return true;
    }

    /// <summary>Поднять процессы для включённых камер (вызывается при запуске стены).</summary>
    public void StartEnabled()
    {
        if (!Enabled) return;
        var start = new List<string>();
        lock (_sync)
        {
            foreach (var ch in _channels.Values)
            {
                if (!ch.On) continue;
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
    /// Отличает намеренное выключение (On=false) от аварии, делает backoff с ограничением попыток.
    /// </summary>
    public void Tick()
    {
        if (_disposed) return;

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
                    continue;
                }

                if (!ch.On) continue;                          // выключено пользователем — не поднимаем
                if (!Enabled) continue;                        // звук выключен целиком
                if (ch.Track == AudioTrackState.Absent) continue;
                if (ch.Starting) continue;

                if (ch.Proc != null) { KillLocked(ch); ch.Proc = null; }

                if (ch.State == AudioChannelState.Playing || ch.State == AudioChannelState.Starting)
                {
                    // был живым и пропал — восстановление
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
            if (_disposed || !Enabled || !ch.On)
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
                if (!_channels.TryGetValue(name, out ch) || (!ch.On && !_disposed))
                {
                    try { proc.Kill(); } catch { }
                    try { proc.Dispose(); } catch { }
                    AudioPlaylist.Delete(name);
                    return;
                }
                ch.Proc = proc;
                ch.Starting = false;
                ch.Fails = 0;
                ch.NextTry = DateTime.MinValue;
                ch.State = AudioChannelState.Playing;
                ch.VolumeDirty = true;
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
            KillLocked(ch);
            ch.Starting = false;
            ch.State = AudioChannelState.Off;
        }
        AudioPlaylist.Delete(name);
    }

    /// <summary>Смена порта/адреса у работающего канала: гасим старый, поднимаем новый.</summary>
    private void RestartChannel(Channel ch)
    {
        string name = ch.Name;
        lock (_sync)
        {
            Channel current;
            if (!_channels.TryGetValue(name, out current) || !ReferenceEquals(current, ch)) return;
            ch.Starting = true;
            ch.State = AudioChannelState.Starting;
            KillLocked(ch);
        }
        AudioPlaylist.Delete(name);
        RaiseChanged();

        Task.Run(() =>
        {
            lock (_sync)
            {
                Channel current;
                if (!_channels.TryGetValue(name, out current)) return;
                current.Starting = false;
                if (_disposed || !Enabled || !current.On)
                {
                    current.State = AudioChannelState.Off;
                    return;
                }
            }
            StartChannel(name);
        });
    }

    private void KillLocked(Channel ch)
    {
        var proc = ch.Proc;
        ch.Proc = null;
        ch.VolumeDirty = false;
        if (proc == null) return;
        try
        {
            if (!SafeExited(proc))
            {
                proc.Kill();
                try { proc.WaitForExit(1500); } catch { }
            }
        }
        catch { }
        try { proc.Dispose(); } catch { }
    }

    public void StopAll()
    {
        var list = new List<Channel>();
        lock (_sync)
        {
            foreach (var ch in _channels.Values)
            {
                // настройка пользователя (On) сохраняется: остановка стены не должна её терять,
                // иначе после следующего запуска звук не поднимется
                KillLocked(ch);
                ch.Starting = false;
                ch.Fails = 0;
                ch.NextTry = DateTime.MinValue;
                ch.State = AudioChannelState.Off;
                list.Add(ch);
            }
        }
        AudioPlaylist.DeleteAll();
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

    /// <summary>Громкость: все запущенные потоки получат последнее значение.</summary>
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
