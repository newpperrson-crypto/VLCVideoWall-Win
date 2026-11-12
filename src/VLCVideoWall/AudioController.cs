using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace VLCVideoWall;

/// <summary>Состояние звуковой дорожки камеры.</summary>
public enum AudioTrackState
{
    Unknown,    // ещё не проверяли
    Present,    // в RTSP-потоке есть звук
    Absent      // только видео
}

/// <summary>
/// Звук стены. VLC собирает мозаику только из видео (у mosaic-bridge нет аудиопараметров),
/// поэтому каждая включённая камера получает собственный процесс VLC без видео.
/// Процессами управляет приложение: так переключение мгновенное, а сторож не занят
/// ожиданием команд в своём цикле опроса.
/// </summary>
public sealed class AudioController : IDisposable
{
    private sealed class Channel
    {
        public string Name = "";
        public string Url = "";
        public int Port;
        public string Password = "";
        public Process Proc;
        public bool On;                 // пользователь включил звук этой камеры
        public bool Busy;               // идёт переключение
        public AudioTrackState Track = AudioTrackState.Unknown;
    }

    /// <summary>Первый порт звуковых процессов (задаётся в настройках портов).</summary>
    public int BasePort { get; set; } = 4213;
    private readonly Dictionary<string, Channel> _channels = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sync = new();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(2) };
    private VlcLocator _vlc = new();

    public bool Enabled { get; set; }          // общая поддержка звука
    public int Volume { get; set; } = 80;      // 0..100
    public string LastError { get; private set; } = "";

    public event Action Changed;               // что-то изменилось — перерисовать значки

    public void Dispose()
    {
        StopAll();
        _http.Dispose();
    }

    /// <summary>Актуальный список камер: имена, адреса и порядок окон.</summary>
    public void SetCameras(IEnumerable<Camera> cameras)
    {
        lock (_sync)
        {
            var list = cameras.Where(c => c.Enabled).ToList();
            var keep = new HashSet<string>(list.Select(c => c.Name), StringComparer.OrdinalIgnoreCase);

            foreach (var gone in _channels.Where(kv => !keep.Contains(kv.Key)).Select(kv => kv.Key).ToList())
            {
                KillChannel(_channels[gone]);
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
                        Port = BasePort + i,
                        Password = Guid.NewGuid().ToString("N").Substring(0, 16)
                    };
                    _channels[cam.Name] = ch;
                }
                ch.Url = cam.Url;
            }
        }
    }

    public bool IsOn(string name)
    {
        lock (_sync) { Channel ch; return _channels.TryGetValue(name, out ch) && ch.On; }
    }

    public bool IsBusy(string name)
    {
        lock (_sync) { Channel ch; return _channels.TryGetValue(name, out ch) && ch.Busy; }
    }

    public AudioTrackState TrackState(string name)
    {
        lock (_sync) { Channel ch; return _channels.TryGetValue(name, out ch) ? ch.Track : AudioTrackState.Unknown; }
    }

    /// <summary>Включённые камеры — для сохранения в настройках.</summary>
    public List<string> EnabledChannels()
    {
        lock (_sync) { return _channels.Values.Where(c => c.On).Select(c => c.Name).ToList(); }
    }

    /// <summary>Вернуть сохранённые состояния (до запуска стены они только помечают значки).</summary>
    public void RestoreStates(IEnumerable<string> enabled)
    {
        var set = new HashSet<string>(enabled ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        lock (_sync)
        {
            foreach (var ch in _channels.Values)
            {
                ch.On = set.Contains(ch.Name);
                ch.Busy = false;
            }
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
                var handler = Changed;
                if (handler != null) handler();
            });
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
            if (ch.Busy) return false;
            wantOn = !ch.On;
            ch.On = wantOn;
            ch.Busy = startProcess;
        }

        if (!startProcess)
        {
            var quiet = Changed;
            if (quiet != null) quiet();
            return true;
        }

        var raiser = Changed;
        if (raiser != null) raiser();

        Task.Run(() =>
        {
            if (wantOn) StartChannel(name); else StopChannel(name);
            lock (_sync)
            {
                Channel ch;
                if (_channels.TryGetValue(name, out ch)) ch.Busy = false;
            }
            var done = Changed;
            if (done != null) done();
        });
        return true;
    }

    /// <summary>Поднять процессы для включённых камер (вызывается при запуске стены).</summary>
    public void StartEnabled()
    {
        if (!Enabled) return;
        List<string> names;
        lock (_sync) names = _channels.Values.Where(c => c.On).Select(c => c.Name).ToList();
        foreach (var n in names) StartChannel(n);
    }

    private void StartChannel(string name)
    {
        Channel ch;
        lock (_sync)
        {
            if (!_channels.TryGetValue(name, out ch)) return;
            if (ch.Track == AudioTrackState.Absent) return;
            if (ch.Proc != null && !ch.Proc.HasExited) { ApplyVolume(ch); return; }
        }

        string exe;
        if (!VlcLocator.TryResolve(out exe))
        {
            LastError = "VLC не найден";
            return;
        }

        int vol = (int)(Math.Clamp(Volume, 0, 100) * 256 / 100.0);
        var args = new StringBuilder();
        args.Append("-I dummy --no-video --no-video-title-show --rtsp-tcp ");
        args.Append("--volume ").Append(vol).Append(' ');
        args.Append("--extraintf http --http-host 127.0.0.1 --http-port ").Append(ch.Port).Append(' ');
        args.Append("--http-password ").Append(ch.Password).Append(' ');
        args.Append('"').Append(ch.Url).Append('"');

        try
        {
            var psi = new ProcessStartInfo(exe, args.ToString())
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory
            };
            var proc = Process.Start(psi);
            lock (_sync) ch.Proc = proc;
        }
        catch (Exception e)
        {
            LastError = "не удалось запустить звук: " + e.Message;
        }
    }

    private void StopChannel(string name)
    {
        Channel ch;
        lock (_sync)
        {
            if (!_channels.TryGetValue(name, out ch)) return;
            KillChannel(ch);
        }
    }

    private void KillChannel(Channel ch)
    {
        try
        {
            if (ch.Proc != null && !ch.Proc.HasExited) ch.Proc.Kill();
        }
        catch { }
        try { if (ch.Proc != null) ch.Proc.Dispose(); } catch { }
        ch.Proc = null;
    }

    /// <summary>Громкость. Уже запущенные потоки получают новое значение сразу.</summary>
    public void ApplyVolumeAll()
    {
        List<Channel> list;
        lock (_sync) list = _channels.Values.ToList();
        foreach (var ch in list) ApplyVolume(ch);
    }

    private void ApplyVolume(Channel ch)
    {
        if (ch.Proc == null || ch.Proc.HasExited) return;
        int val = ch.On ? (int)(Math.Clamp(Volume, 0, 100) * 256 / 100.0) : 0;
        try
        {
            var url = "http://127.0.0.1:" + ch.Port + "/requests/status.xml?command=volume&val=" + val;
            using (var req = new HttpRequestMessage(HttpMethod.Get, url))
            {
                var auth = Convert.ToBase64String(Encoding.UTF8.GetBytes(":" + ch.Password));
                req.Headers.Authorization = new AuthenticationHeaderValue("Basic", auth);
                _http.SendAsync(req).GetAwaiter().GetResult().Dispose();
            }
        }
        catch { }
    }

    public void StopAll()
    {
        List<Channel> list;
        lock (_sync) list = _channels.Values.ToList();
        foreach (var ch in list)
        {
            lock (_sync) KillChannel(ch);
        }
    }

    /// <summary>Сколько камер сейчас реально звучит.</summary>
    public int PlayingCount()
    {
        lock (_sync)
            return _channels.Values.Count(c => c.Proc != null && !c.Proc.HasExited);
    }
}

/// <summary>Минимальный поиск vlc.exe: конфиг стены, потом типовые пути.</summary>
sealed class VlcLocator
{
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
