using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace VLCVideoWall;

/// <summary>
/// Быстрая проверка RTSP-адреса: отвечает ли сервер и принимает ли учётные данные.
/// VW-04: request URI абсолютный (RFC 2326), логин и пароль идут только в заголовке.
/// VW-05: поддерживаются Basic и Digest, поэтому первый 401 с Digest-челленджем
/// не считается неверным паролем.
/// </summary>
public static class RtspProbe
{
    public static string Check(string url, int timeoutMs = 2500)
    {
        if (string.IsNullOrWhiteSpace(url)) return "адрес пустой";

        Uri u;
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out u) || u.Scheme != "rtsp")
            return "не похоже на rtsp:// адрес";

        int port = u.Port > 0 ? u.Port : 554;
        string path = string.IsNullOrEmpty(u.PathAndQuery) ? "/" : u.PathAndQuery;
        string absUri = "rtsp://" + u.Host + ":" + port + path;
        string userInfo = string.IsNullOrEmpty(u.UserInfo) ? null : Uri.UnescapeDataString(u.UserInfo);

        try
        {
            bool connected;
            string head = Exchange(u.Host, port, absUri,
                userInfo == null ? null : BasicHeader(userInfo), 1, timeoutMs, out connected);
            if (head == null) return connected ? "порт открыт, RTSP молчит" : "нет связи (таймаут)";

            int code = StatusCode(head);

            // Digest-сервер отвечает 401 с челленджем — это обычный этап согласования.
            // Сервер может прислать несколько WWW-Authenticate (Digest и Basic) —
            // берём именно Digest, иначе пароль уйдёт в открытом виде или проверка соврёт.
            if (code == 401 && userInfo != null)
            {
                string challenge = Headers(head, "WWW-Authenticate")
                    .FirstOrDefault(h => h.StartsWith("Digest", StringComparison.OrdinalIgnoreCase));
                if (challenge != null)
                {
                    string digest = DigestHeader(challenge, absUri, userInfo);
                    if (digest != null)
                    {
                        head = Exchange(u.Host, port, absUri, digest, 2, timeoutMs, out connected);
                        if (head == null) return connected ? "порт открыт, RTSP молчит" : "нет связи (таймаут)";
                        code = StatusCode(head);
                    }
                }
            }

            if (code == 401) return "401 — неверный логин/пароль";
            if (code == 403) return "403 — доступ запрещён";
            if (code > 0) return "отвечает: " + code;
            return "непонятный ответ";
        }
        catch (Exception ex) { return "ошибка: " + ex.Message; }
    }

    /// <summary>Отправляет OPTIONS и читает заголовок ответа. null = ответа нет.</summary>
    private static string Exchange(string host, int port, string absUri, string auth, int cseq,
                                   int timeoutMs, out bool connected)
    {
        connected = false;
        using (var client = new TcpClient())
        {
            var ar = client.BeginConnect(host, port, null, null);
            if (!ar.AsyncWaitHandle.WaitOne(timeoutMs)) return null;
            try { client.EndConnect(ar); } catch { return null; }
            connected = true;

            using (var stream = client.GetStream())
            {
                stream.ReadTimeout = timeoutMs;
                stream.WriteTimeout = timeoutMs;

                var req = new StringBuilder();
                req.Append("OPTIONS ").Append(absUri).Append(" RTSP/1.0\r\n");
                req.Append("CSeq: ").Append(cseq).Append("\r\n");
                req.Append("User-Agent: VLCVideoWall\r\n");
                if (auth != null) req.Append("Authorization: ").Append(auth).Append("\r\n");
                req.Append("\r\n");

                var bytes = Encoding.ASCII.GetBytes(req.ToString());
                stream.Write(bytes, 0, bytes.Length);

                var buf = new byte[1024];
                var head = new StringBuilder();
                var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
                while (DateTime.UtcNow < deadline)
                {
                    int n;
                    try { n = stream.Read(buf, 0, buf.Length); }
                    catch { break; }
                    if (n <= 0) break;
                    head.Append(Encoding.ASCII.GetString(buf, 0, n));
                    if (head.ToString().Contains("\r\n\r\n")) break;
                }

                var text = head.ToString();
                return text.Length == 0 ? null : text;
            }
        }
    }

    private static int StatusCode(string head)
    {
        if (!head.StartsWith("RTSP/", StringComparison.OrdinalIgnoreCase)) return 0;
        int sp = head.IndexOf(' ');
        if (sp < 0 || head.Length < sp + 4) return 0;
        int code;
        return int.TryParse(head.Substring(sp + 1, 3), out code) ? code : 0;
    }

    private static string Header(string head, string name)
    {
        foreach (var line in head.Split(new[] { "\r\n" }, StringSplitOptions.None))
        {
            if (line.StartsWith(name, StringComparison.OrdinalIgnoreCase))
                return line.Substring(name.Length).TrimStart(':', ' ').Trim();
        }
        return null;
    }

    /// <summary>Все значения заголовка: сервер может прислать несколько WWW-Authenticate.</summary>
    private static List<string> Headers(string head, string name)
    {
        var list = new List<string>();
        foreach (var line in head.Split(new[] { "\r\n" }, StringSplitOptions.None))
        {
            if (line.StartsWith(name, StringComparison.OrdinalIgnoreCase))
                list.Add(line.Substring(name.Length).TrimStart(':', ' ').Trim());
        }
        return list;
    }

    private static string BasicHeader(string userInfo)
    {
        return "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(userInfo));
    }

    private static string Md5Hex(string text)
    {
        using (var md5 = MD5.Create())
            return Convert.ToHexString(md5.ComputeHash(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }

    private static string DigestHeader(string challenge, string absUri, string userInfo)
    {
        string realm = Param(challenge, "realm");
        string nonce = Param(challenge, "nonce");
        if (string.IsNullOrEmpty(nonce)) return null;
        string qop = Param(challenge, "qop");
        string opaque = Param(challenge, "opaque");

        string user = userInfo, pass = "";
        int c = userInfo.IndexOf(':');
        if (c >= 0) { user = userInfo.Substring(0, c); pass = userInfo.Substring(c + 1); }

        string ha1 = Md5Hex(user + ":" + realm + ":" + pass);
        string ha2 = Md5Hex("OPTIONS:" + absUri);

        string result;
        bool withQop = qop != null && qop.Split(',').Any(q => q.Trim().Equals("auth", StringComparison.OrdinalIgnoreCase));
        if (withQop)
        {
            string cnonce = Guid.NewGuid().ToString("N").Substring(0, 8);
            string nc = "00000001";
            string resp = Md5Hex(ha1 + ":" + nonce + ":" + nc + ":" + cnonce + ":auth:" + ha2);
            result = string.Format(
                "Digest username=\"{0}\", realm=\"{1}\", nonce=\"{2}\", uri=\"{3}\", response=\"{4}\", qop=auth, nc={5}, cnonce=\"{6}\", algorithm=MD5",
                user, realm, nonce, absUri, resp, nc, cnonce);
        }
        else
        {
            string resp = Md5Hex(ha1 + ":" + nonce + ":" + ha2);
            result = string.Format(
                "Digest username=\"{0}\", realm=\"{1}\", nonce=\"{2}\", uri=\"{3}\", response=\"{4}\", algorithm=MD5",
                user, realm, nonce, absUri, resp);
        }

        if (!string.IsNullOrEmpty(opaque)) result += ", opaque=\"" + opaque + "\"";
        return result;
    }

    private static string Param(string challenge, string name)
    {
        var m = System.Text.RegularExpressions.Regex.Match(
            challenge, name + "=\"([^\"]*)\"", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }
}
