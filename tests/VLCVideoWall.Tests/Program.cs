using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace VLCVideoWall.Tests;

/// <summary>
/// Тесты без внешних пакетов: конфиг, разбор vlc.pid и RTSP-проверка
/// (абсолютный request URI, Basic, Digest с независимым пересчётом подписи).
/// Запуск: dotnet run --project tests/VLCVideoWall.Tests
/// </summary>
internal static class Program
{
    private static int _failed;

    private static void Check(bool ok, string what)
    {
        Console.WriteLine((ok ? "  ok   " : "  FAIL ") + what);
        if (!ok) _failed++;
    }

    private static int Main()
    {
        Console.WriteLine("Конфиг:");
        ConfigRoundTrip();
        LayoutAndCanvasLimits();

        Console.WriteLine("vlc.pid:");
        PidParsing();
        PidStartTimeMatch();
        FakePidFileRejected();

        Console.WriteLine("watchdog.status:");
        StatusFileParsing();

        Console.WriteLine("RTSP-проверка:");
        ProbeBasicOk();
        ProbeDigestQopAuth();
        ProbeDigestNoQop();
        ProbeDigestWrongPassword();
        ProbeDigestAndBasicTogether();
        ProbeQueryAndEncodedCredentials();
        ProbeRejectsRelativeUri();
        ProbeNoServer();

        Console.WriteLine(_failed == 0 ? "ВСЕ ТЕСТЫ ПРОЙДЕНЫ" : $"ПРОВАЛЕНО: {_failed}");
        return _failed == 0 ? 0 : 1;
    }

    // ============================ конфиг ============================

    private static void ConfigRoundTrip()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vlcvideowall-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "Mosaic.conf");
        try
        {
            var cfg = new WallConfig { Layout = "2x2", CanvasW = 1920, CanvasH = 1080 };
            cfg.Cameras.Add(new Camera { Name = "ch1", Url = "rtsp://user:pass@192.0.2.10:554/a" });
            cfg.Cameras.Add(new Camera { Name = "ch2", Url = "rtsp://user:pass@192.0.2.11:554/b", Enabled = false });
            cfg.Cameras.Add(new Camera { Name = "ch3", Url = "rtsp://user:pass@192.0.2.12:554/c" });
            cfg.Windowed = true;            // стена в окне размером с полотно
            cfg.Save(path);

            var text = File.ReadAllText(path, Encoding.UTF8);
            Check(text.Contains("windowed = yes"), "windowed пишется в Mosaic.conf");
            Check(text.Contains("off cam ch2 "), "выключенная камера пишется как «off cam»");

            var back = WallConfig.Load(path);
            Check(back.Windowed, "windowed = yes читается обратно");
            Check(back.Cameras.Count == 3, "после сохранения и загрузки остаются все 3 камеры");
            Check(back.Cameras[0].Name == "ch1" && back.Cameras[1].Name == "ch2" && back.Cameras[2].Name == "ch3",
                "порядок камер сохраняется");
            Check(!back.Cameras[1].Enabled && back.Cameras[0].Enabled && back.Cameras[2].Enabled,
                "состояние «выключена» переживает сохранение");
            Check(back.CanvasW == 1920 && back.CanvasH == 1080, "полотно сохраняется");
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    private static void LayoutAndCanvasLimits()
    {
        var cfg = new WallConfig { Layout = "5x5" };
        cfg.ApplyLayout();
        Check(cfg.Cols * cfg.Rows <= 16, "сетка обрезается до 16 окон");

        var path = Path.Combine(Path.GetTempPath(), "vlcvideowall-canvas-" + Guid.NewGuid().ToString("N") + ".conf");
        var big = new WallConfig { CanvasW = 9999, CanvasH = 100 };
        big.Save(path);
        try { File.Delete(path); } catch { }
        Check(big.CanvasW == 3840, "ширина полотна ограничена 3840");
        Check(big.CanvasH == 480, "высота полотна ограничена 480");
    }

    // ============================ vlc.pid ============================

    private static void PidParsing()
    {
        int pid;
        long ticks;

        Check(VlcProcessInfo.TryParse("1234 638000000000000000", out pid, out ticks) && pid == 1234 && ticks > 0,
            "корректная строка разбирается");

        Check(!VlcProcessInfo.TryParse("", out pid, out ticks), "пустой файл — не наш процесс");
        Check(!VlcProcessInfo.TryParse("   \r\n ", out pid, out ticks), "пробелы — не наш процесс");
        Check(!VlcProcessInfo.TryParse("1234", out pid, out ticks), "только PID без времени старта — отказ");
        Check(!VlcProcessInfo.TryParse("abc def", out pid, out ticks), "мусор — отказ");
        Check(!VlcProcessInfo.TryParse("0 638000000000000000", out pid, out ticks), "PID 0 — отказ");
        Check(!VlcProcessInfo.TryParse("1234 0", out pid, out ticks), "нулевое время старта — отказ");
        Check(!VlcProcessInfo.TryParse("-5 -7", out pid, out ticks), "отрицательные значения — отказ");

        Check(VlcProcessInfo.TryParse("1234\t638000000000000000", out pid, out ticks) && pid == 1234,
            "табуляция как разделитель");
        Check(VlcProcessInfo.TryParse(" 1234  638000000000000000  лишнее ", out pid, out ticks) &&
              pid == 1234 && ticks == 638000000000000000,
            "лишние поля и пробелы не ломают разбор");
    }

    private static void PidStartTimeMatch()
    {
        var now = new DateTime(2026, 11, 11, 10, 0, 0, DateTimeKind.Utc);
        Check(VlcProcessInfo.MatchesStartTime(now.Ticks, now), "точное совпадение времени старта");
        Check(VlcProcessInfo.MatchesStartTime(now.Ticks - TimeSpan.TicksPerSecond, now),
            "допуск ±2 с для округлений");
        Check(!VlcProcessInfo.MatchesStartTime(now.Ticks - TimeSpan.FromSeconds(30).Ticks, now),
            "другое время старта (переиспользованный PID) — не наш процесс");
        Check(!VlcProcessInfo.MatchesStartTime(0, now), "нулевое ожидание — отказ");
    }

    /// <summary>
    /// FUP3-01: файл vlc.pid с чужим временем старта не должен превращать посторонний
    /// процесс в «свой» — иначе приложение завершит чужой vlc.exe.
    /// </summary>
    private static void FakePidFileRejected()
    {
        var me = Process.GetCurrentProcess();
        long realUtc = me.StartTime.ToUniversalTime().Ticks;

        Check(VlcProcessInfo.MatchesStartTime(realUtc, me.StartTime),
            "свой процесс узнаётся по реальному времени старта");

        int pid;
        long ticks;
        Check(VlcProcessInfo.TryParse(me.Id + " " + (realUtc + TimeSpan.TicksPerMinute), out pid, out ticks)
              && !VlcProcessInfo.MatchesStartTime(ticks, me.StartTime),
            "подделанное время старта (PID + 60 с) отклоняется");

        Check(VlcProcessInfo.TryParse(me.Id + " " + realUtc, out pid, out ticks)
              && VlcProcessInfo.MatchesStartTime(ticks, me.StartTime),
            "файл с настоящим временем старта принимается");
    }

    // ============================ watchdog.status ============================

    private static void StatusFileParsing()
    {
        var now = new DateTime(2026, 11, 11, 12, 0, 0, DateTimeKind.Utc);
        var text = now.Ticks + "\nvlm=ok\nplaying=3\ntotal=4\n";

        WallStatusFile st;
        Check(WallStatusFile.TryParse(text, out st), "корректный файл разбирается");
        Check(st != null && st.VlmOk && st.Playing == 3 && st.Total == 4, "поля прочитаны");
        Check(st != null && st.IsFresh(now.AddSeconds(5), 20), "запись 5 с назад — свежая");
        Check(st != null && !st.IsFresh(now.AddSeconds(30), 20), "запись 30 с назад — устарела");
        Check(st != null && !st.IsFresh(now.AddSeconds(-5), 20), "запись из будущего не считается свежей");

        Check(WallStatusFile.TryParse(now.Ticks + "\nvlm=fail\n", out st) && !st.VlmOk,
            "vlm=fail — подтверждения нет");
        Check(WallStatusFile.TryParse(now.Ticks + "\n", out st) && !st.VlmOk,
            "нет строки vlm — считаем, что подтверждения нет");
        Check(!WallStatusFile.TryParse("", out st), "пустой файл — отказ");
        Check(!WallStatusFile.TryParse("мусор\nvlm=ok", out st), "без тиков — отказ");
        Check(!WallStatusFile.TryParse("0\nvlm=ok", out st), "нулевые тики — отказ");
        Check(WallStatusFile.TryParse(now.Ticks + "\r\nVLM=OK\r\nplaying=x\r\ntotal=2\r\n", out st)
              && st.VlmOk && st.Playing == 0 && st.Total == 2,
            "CRLF, регистр ключа и мусор в числе не ломают разбор");
    }

    // ============================ RTSP ============================

    /// <summary>Одноразовый RTSP-сервер: отвечает обработчиком и запоминает запросы.</summary>
    private sealed class FakeRtsp : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly Thread _thread;
        public readonly List<string> Requests = new();
        public int Port { get; }

        public FakeRtsp(Func<string, string> handler, int connections = 3)
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _thread = new Thread(() =>
            {
                try
                {
                    for (int i = 0; i < connections; i++)
                    {
                        using var client = _listener.AcceptTcpClient();
                        using var stream = client.GetStream();
                        var sb = new StringBuilder();
                        var buf = new byte[2048];
                        stream.ReadTimeout = 2000;
                        while (!sb.ToString().Contains("\r\n\r\n"))
                        {
                            int n = stream.Read(buf, 0, buf.Length);
                            if (n <= 0) break;
                            sb.Append(Encoding.ASCII.GetString(buf, 0, n));
                        }
                        var req = sb.ToString();
                        lock (Requests) Requests.Add(req);
                        var resp = handler(req);
                        var bytes = Encoding.ASCII.GetBytes(resp);
                        stream.Write(bytes, 0, bytes.Length);
                    }
                }
                catch { }
                finally { try { _listener.Stop(); } catch { } }
            }) { IsBackground = true };
            _thread.Start();
        }

        public void Dispose() { try { _listener.Stop(); } catch { } }
    }

    private static string Md5(string s) =>
        Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();

    private static string HeaderLine(string request, string name)
    {
        foreach (var line in request.Split(new[] { "\r\n" }, StringSplitOptions.None))
            if (line.StartsWith(name + ":", StringComparison.OrdinalIgnoreCase))
                return line.Substring(name.Length + 1).Trim();
        return null;
    }

    /// <summary>Значение параметра из заголовка: и в кавычках, и без.</summary>
    private static string Param(string header, string name)
    {
        var m = System.Text.RegularExpressions.Regex.Match(header, name + "=\"([^\"]*)\"", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (m.Success) return m.Groups[1].Value;
        m = System.Text.RegularExpressions.Regex.Match(header, name + "=([^,\\s]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>
    /// Независимая проверка Digest-ответа: сервер сам считает ожидаемую подпись
    /// из пароля, realm, nonce, uri, qop/nc/cnonce и сравнивает с присланной.
    /// </summary>
    private static bool DigestValid(string request, string user, string pass, string realm, string nonce,
                                    string expectUri, bool expectQop, out string reason)
    {
        var auth = HeaderLine(request, "Authorization");
        if (auth == null || !auth.StartsWith("Digest", StringComparison.OrdinalIgnoreCase))
        {
            reason = "нет Authorization: Digest";
            return false;
        }

        string uri = Param(auth, "uri");
        string resp = Param(auth, "response");
        string qop = Param(auth, "qop");
        string nc = Param(auth, "nc");
        string cnonce = Param(auth, "cnonce");
        string algo = Param(auth, "algorithm") ?? "MD5";

        if (Param(auth, "username") != user) { reason = "username"; return false; }
        if (algo != "MD5") { reason = "algorithm " + algo; return false; }
        if (Param(auth, "realm") != realm) { reason = "realm"; return false; }
        if (Param(auth, "nonce") != nonce) { reason = "nonce"; return false; }
        if (uri != expectUri) { reason = "uri=" + uri; return false; }
        if (expectQop && qop != "auth") { reason = "qop=" + qop; return false; }
        if (expectQop && (nc == null || cnonce == null)) { reason = "no nc/cnonce"; return false; }

        var ha1 = Md5(user + ":" + realm + ":" + pass);
        var ha2 = Md5("OPTIONS:" + expectUri);
        var expected = expectQop
            ? Md5(ha1 + ":" + nonce + ":" + nc + ":" + cnonce + ":auth:" + ha2)
            : Md5(ha1 + ":" + nonce + ":" + ha2);

        if (expected != resp)
        {
            reason = "подпись не совпала";
            return false;
        }
        reason = "ok";
        return true;
    }

    private static void ProbeBasicOk()
    {
        using var srv = new FakeRtsp(_ => "RTSP/1.0 200 OK\r\nCSeq: 1\r\n\r\n");
        var result = RtspProbe.Check($"rtsp://user:pass@127.0.0.1:{srv.Port}/live/ch1", 1500);
        Thread.Sleep(200);

        Check(result == "отвечает: 200", "камера с 200 — «отвечает: 200» (получено: " + result + ")");
        var first = srv.Requests.Count > 0 ? srv.Requests[0] : "";
        Check(first.StartsWith("OPTIONS rtsp://127.0.0.1:" + srv.Port + "/live/ch1 RTSP/1.0"),
            "request URI абсолютный и без логина/пароля");
        Check(!first.Contains("user:pass"), "учётные данные не попадают в request URI");
        Check(first.Contains("Authorization: Basic "), "отправляется заголовок Basic");
    }

    private static void ProbeDigestQopAuth()
    {
        const string realm = "cam", nonce = "abc123", user = "admin", pass = "secret";
        using var srv = new FakeRtsp(req =>
        {
            var auth0 = HeaderLine(req, "Authorization");
            if (auth0 == null || !auth0.StartsWith("Digest", StringComparison.OrdinalIgnoreCase))
                return "RTSP/1.0 401 Unauthorized\r\nCSeq: 1\r\n" +
                       $"WWW-Authenticate: Digest realm=\"{realm}\", nonce=\"{nonce}\", qop=\"auth\"\r\n\r\n";

            return DigestValid(req, user, pass, realm, nonce, RequestUri(req), true, out var why)
                ? "RTSP/1.0 200 OK\r\nCSeq: 2\r\n\r\n"
                : "RTSP/1.0 401 Unauthorized\r\nCSeq: 2\r\n\r\n";
        }, 3);

        var result = RtspProbe.Check($"rtsp://{user}:{pass}@127.0.0.1:{srv.Port}/stream", 1500);
        Thread.Sleep(300);
        Check(result == "отвечает: 200", "Digest qop=auth с верным паролем принимается (получено: " + result + ")");
    }

    private static void ProbeDigestNoQop()
    {
        const string realm = "cam", nonce = "n-42", user = "op", pass = "pw";
        using var srv = new FakeRtsp(req =>
        {
            var auth0 = HeaderLine(req, "Authorization");
            if (auth0 == null || !auth0.StartsWith("Digest", StringComparison.OrdinalIgnoreCase))
                return "RTSP/1.0 401 Unauthorized\r\nCSeq: 1\r\n" +
                       $"WWW-Authenticate: Digest realm=\"{realm}\", nonce=\"{nonce}\"\r\n\r\n";

            return DigestValid(req, user, pass, realm, nonce, RequestUri(req), false, out _)
                ? "RTSP/1.0 200 OK\r\nCSeq: 2\r\n\r\n"
                : "RTSP/1.0 401 Unauthorized\r\nCSeq: 2\r\n\r\n";
        }, 3);

        var result = RtspProbe.Check($"rtsp://{user}:{pass}@127.0.0.1:{srv.Port}/stream", 1500);
        Thread.Sleep(300);
        Check(result == "отвечает: 200", "Digest без qop (устаревшая схема) принимается (получено: " + result + ")");
    }

    private static void ProbeDigestWrongPassword()
    {
        const string realm = "cam", nonce = "xyz", user = "admin";
        using var srv = new FakeRtsp(req =>
        {
            var auth0 = HeaderLine(req, "Authorization");
            if (auth0 == null || !auth0.StartsWith("Digest", StringComparison.OrdinalIgnoreCase))
                return "RTSP/1.0 401 Unauthorized\r\nCSeq: 1\r\n" +
                       $"WWW-Authenticate: Digest realm=\"{realm}\", nonce=\"{nonce}\", qop=\"auth\"\r\n\r\n";

            // сервер знает настоящий пароль «right», клиент пришёл с «wrong»
            return DigestValid(req, user, "right", realm, nonce, RequestUri(req), true, out _)
                ? "RTSP/1.0 200 OK\r\nCSeq: 2\r\n\r\n"
                : "RTSP/1.0 401 Unauthorized\r\nCSeq: 2\r\n\r\n";
        }, 3);

        var result = RtspProbe.Check($"rtsp://{user}:wrong@127.0.0.1:{srv.Port}/stream", 1500);
        Thread.Sleep(300);
        Check(result.StartsWith("401"), "неверный пароль при Digest даёт 401 (получено: " + result + ")");
        Check(srv.Requests.Count >= 2, "после челленджа отправлен второй запрос");
    }

    private static void ProbeDigestAndBasicTogether()
    {
        const string realm = "cam", nonce = "n1", user = "admin", pass = "secret";
        using var srv = new FakeRtsp(req =>
        {
            var auth0 = HeaderLine(req, "Authorization");
            if (auth0 == null || !auth0.StartsWith("Digest", StringComparison.OrdinalIgnoreCase))
                return "RTSP/1.0 401 Unauthorized\r\nCSeq: 1\r\n" +
                       "WWW-Authenticate: Basic realm=\"cam\"\r\n" +
                       $"WWW-Authenticate: Digest realm=\"{realm}\", nonce=\"{nonce}\", qop=\"auth\"\r\n\r\n";

            return DigestValid(req, user, pass, realm, nonce, RequestUri(req), true, out _)
                ? "RTSP/1.0 200 OK\r\nCSeq: 2\r\n\r\n"
                : "RTSP/1.0 401 Unauthorized\r\nCSeq: 2\r\n\r\n";
        }, 3);

        var result = RtspProbe.Check($"rtsp://{user}:{pass}@127.0.0.1:{srv.Port}/stream", 1500);
        Thread.Sleep(300);
        Check(result == "отвечает: 200", "из двух челленджей выбирается Digest (получено: " + result + ")");
        Check(srv.Requests.Count >= 2 && srv.Requests[1].Contains("Authorization: Digest"),
            "второй запрос уходит с Digest, а не с Basic");
    }

    private static void ProbeQueryAndEncodedCredentials()
    {
        // логин с @ и пароль с : — в URL они закодированы
        const string user = "us@er", pass = "pa:ss";
        var expectedUri = "/live/main?channel=1&subtype=0";
        using var srv = new FakeRtsp(req =>
        {
            var auth = HeaderLine(req, "Authorization");
            if (auth != null && auth.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
            {
                var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(auth.Substring(6)));
                if (decoded == user + ":" + pass) return "RTSP/1.0 200 OK\r\nCSeq: 1\r\n\r\n";
            }
            return "RTSP/1.0 401 Unauthorized\r\nCSeq: 1\r\nWWW-Authenticate: Basic realm=\"cam\"\r\n\r\n";
        }, 2);

        var url = $"rtsp://us%40er:pa%3Ass@127.0.0.1:{srv.Port}{expectedUri}";
        var result = RtspProbe.Check(url, 1500);
        Thread.Sleep(200);

        Check(result == "отвечает: 200", "percent-encoded учётные данные декодируются (получено: " + result + ")");
        var first = srv.Requests.Count > 0 ? srv.Requests[0] : "";
        Check(first.Contains("OPTIONS rtsp://127.0.0.1:" + srv.Port + expectedUri + " RTSP/1.0"),
            "query-строка сохраняется в абсолютном URI: " + first.Split('\r')[0]);
    }

    private static void ProbeRejectsRelativeUri()
    {
        using var srv = new FakeRtsp(_ => "RTSP/1.0 200 OK\r\nCSeq: 1\r\n\r\n");
        var result = RtspProbe.Check($"http://127.0.0.1:{srv.Port}/x", 800);
        Check(result.Contains("rtsp"), "не-RTSP адрес отклоняется (получено: " + result + ")");
    }

    private static void ProbeNoServer()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        var result = RtspProbe.Check($"rtsp://127.0.0.1:{port}/x", 800);
        Check(result.Contains("нет связи") || result.Contains("порт открыт"),
            "недоступный порт не вешает проверку (получено: " + result + ")");
    }

    /// <summary>Request-URI из строки запроса: по RFC он же идёт в digest-параметр uri.</summary>
    private static string RequestUri(string request)
    {
        var first = request.Split('\r')[0];
        var parts = first.Split(' ');
        return parts.Length >= 2 ? parts[1] : "";
    }
}
