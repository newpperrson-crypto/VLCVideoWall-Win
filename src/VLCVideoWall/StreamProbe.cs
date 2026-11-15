namespace VLCVideoWall;

/// <summary>
/// Проверка звуковой дорожки в локальной копии потока (коллектор отдаёт MPEG-TS по HTTP).
///
/// Камеру при этом трогать не нужно: разбирается тот самый поток, который уже играет стена,
/// поэтому проверка не зависит ни от авторизации камеры (Digest без qop, userinfo в адресе),
/// ни от того, отдаёт ли она SDP. Достаточно прочитать несколько десятков транспортных
/// пакетов и разобрать PAT/PMT.
/// </summary>
public static class StreamProbe
{
    private const int PacketSize = 188;
    private const byte SyncByte = 0x47;
    private const int ChunkSize = 64 * 1024;
    private const int MaxBytes = 3 * 1024 * 1024;  // у HEVC-камер PAT встречается раз в ~180 КБ
    private const int DefaultTimeoutMs = 6000;

    /// <summary>Типы дорожек, которые считаются видео (всё остальное — звук или служебное).</summary>
    private static readonly System.Collections.Generic.HashSet<byte> VideoTypes =
        new() { 0x01, 0x02, 0x10, 0x1b, 0x24, 0x42, 0xea };

    /// <summary>
    /// Есть ли в потоке на локальном порту звуковая дорожка.
    /// Absent — поток прочитан, в PMT только видео; Unknown — прочитать не удалось.
    /// </summary>
    public static AudioTrackState HasAudioTrack(int port, int timeoutMs = DefaultTimeoutMs)
    {
        if (port < 1024 || port > 65535) return AudioTrackState.Unknown;
        if (timeoutMs <= 0) timeoutMs = DefaultTimeoutMs;

        System.Net.Sockets.TcpClient client = null;
        try
        {
            client = new System.Net.Sockets.TcpClient();
            var connect = client.BeginConnect("127.0.0.1", port, null, null);
            if (!connect.AsyncWaitHandle.WaitOne(Math.Min(timeoutMs, 3000))) return AudioTrackState.Unknown;
            client.EndConnect(connect);
            client.ReceiveTimeout = Math.Min(timeoutMs, 4000);

            using var stream = client.GetStream();
            const string crlf = "\r\n";
            var request = System.Text.Encoding.ASCII.GetBytes(
                "GET / HTTP/1.0" + crlf + "Host: 127.0.0.1" + crlf + "User-Agent: VLCVideoWall" + crlf + crlf);
stream.Write(request, 0, request.Length);
            stream.Flush();

            var buffer = new byte[MaxBytes];
            int total = 0;
            int bodyStart = -1;
            var started = DateTime.UtcNow;

            while (total < MaxBytes)
            {
                var left = timeoutMs - (int)(DateTime.UtcNow - started).TotalMilliseconds;
                if (left <= 0) break;

                int read;
                try { read = stream.Read(buffer, total, Math.Min(ChunkSize, MaxBytes - total)); }
                catch { break; }
                if (read <= 0) break;
                total += read;

                if (bodyStart < 0)
                {
                    bodyStart = IndexOfHeaderEnd(buffer, total);
                    if (bodyStart < 0) continue;
                }

                int bodyLength = total - bodyStart;
                if (bodyLength < PacketSize * 3) continue;

                // разбираем нарастающий буфер: таблицы в потоке идут редко и неравномерно
                var body = new byte[bodyLength];
                Array.Copy(buffer, bodyStart, body, 0, bodyLength);
                var verdict = Analyze(body);
                if (verdict != AudioTrackState.Unknown) return verdict;
            }
            return AudioTrackState.Unknown;
        }
        catch
        {
            return AudioTrackState.Unknown;
        }
        finally
        {
            try { client?.Close(); } catch { }
        }
    }

    /// <summary>Позиция первого байта тела ответа (после пустой строки заголовков); -1 — заголовки ещё не пришли.</summary>
    private static int IndexOfHeaderEnd(byte[] buffer, int length)
    {
        for (int i = 0; i + 3 < length; i++)
        {
            if (buffer[i] == 0x0d && buffer[i + 1] == 0x0a && buffer[i + 2] == 0x0d && buffer[i + 3] == 0x0a)
                return i + 4;
        }
        return -1;
    }

    /// <summary>Разбор готового куска транспортного потока (без сети — так его можно проверять тестами).</summary>
    internal static AudioTrackState Analyze(byte[] buffer)
    {
        if (buffer == null || buffer.Length < PacketSize) return AudioTrackState.Unknown;

        var pmtPids = ReadPatPids(buffer);
        if (pmtPids.Count == 0) return AudioTrackState.Unknown;

        bool sawPmt = false;
        foreach (var pid in pmtPids)
        {
            var streams = ReadPmtStreams(buffer, pid);
            if (streams == null) continue;
            sawPmt = true;
            foreach (var type in streams)
            {
                if (!VideoTypes.Contains(type)) return AudioTrackState.Present;
            }
        }
        return sawPmt ? AudioTrackState.Absent : AudioTrackState.Unknown;
    }

    /// <summary>Прочитать начало потока: столько, сколько успеет за отведённое время.</summary>
    private static byte[] ReadBytes(int port, int timeoutMs)
    {
        using var client = new System.Net.Sockets.TcpClient();
        var connect = client.BeginConnect("127.0.0.1", port, null, null);
        if (!connect.AsyncWaitHandle.WaitOne(Math.Min(timeoutMs, 3000))) return null;
        client.EndConnect(connect);
        client.ReceiveTimeout = Math.Min(timeoutMs, 4000);

        using var stream = client.GetStream();
        var path = "/" ;   // VLC отдаёт поток по любому пути
        var request = "GET " + path + " HTTP/1.0\r\nHost: 127.0.0.1\r\nUser-Agent: VLCVideoWall\r\n\r\n";
        var req = System.Text.Encoding.ASCII.GetBytes(request);
        stream.Write(req, 0, req.Length);
        stream.Flush();

        var buffer = new byte[MaxBytes];
        int total = 0;
        var started = DateTime.UtcNow;
        bool headersDone = false;
        int bodyStart = 0;
        while (total < MaxBytes)
        {
            if ((DateTime.UtcNow - started).TotalMilliseconds > timeoutMs) break;
            int read;
            try { read = stream.Read(buffer, total, Math.Min(16 * 1024, MaxBytes - total)); }
            catch { break; }
            if (read <= 0) break;
            total += read;
            if (!headersDone)
            {
                // тело ответа начинается после пустой строки; заголовки могут прийти не сразу
                var text = System.Text.Encoding.ASCII.GetString(buffer, 0, total);
                int idx = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                if (idx >= 0) { bodyStart = idx + 4; headersDone = true; }
            }
            if (headersDone && total - bodyStart > 32 * 1024)
            {
                // PMT обычно приходит в первых килобайтах, но повторяется — читаем ещё немного
                var found = ReadPatPids(new ArraySegment<byte>(buffer, bodyStart, total - bodyStart).ToArray());
                if (found.Count > 0) break;
            }
        }
        if (!headersDone) return null;
        var body = new byte[total - bodyStart];
        Array.Copy(buffer, bodyStart, body, 0, body.Length);
        return body;
    }

    /// <summary>
    /// Начало таблицы внутри пакета: пропускает заголовок, поле адаптации (оно сдвигает
    /// полезную нагрузку) и pointer_field. -1 — таблица начинается в другом пакете.
    /// </summary>
    private static int PayloadStart(byte[] data, int packet)
    {
        int end = packet + PacketSize;
        int p = packet + 4;
        if ((data[packet + 3] & 0x20) != 0)          // есть поле адаптации
        {
            p += 1 + data[p];
        }
        if (p + 1 >= end) return -1;
        p += 1 + data[p];                            // pointer_field
        return p + 12 <= end ? p : -1;
    }

    /// <summary>
    /// Смещение начала пакета в буфере. Просто искать первый 0x47 нельзя: HTTP-поток может
    /// начаться с середины пакета, а 0x47 встречается и внутри полезной нагрузки. Берём
    /// позицию, где 0x47 стоит и через 188, и через 376 байт.
    /// </summary>
    internal static int SyncOffset(byte[] data, int limit = 0)
    {
        if (data == null) return -1;
        int max = limit > 0 ? Math.Min(limit, data.Length) : data.Length;
        for (int i = 0; i + 2 * PacketSize < max; i++)
        {
            if (data[i] != SyncByte) continue;
            if (data[i + PacketSize] != SyncByte) continue;
            if (data[i + 2 * PacketSize] != SyncByte) continue;
            return i;
        }
        return -1;
    }

    /// <summary>PID таблиц PMT из PAT (PID 0). Пусто — таблица не найдена.</summary>
    private static System.Collections.Generic.List<int> ReadPatPids(byte[] data)
    {
        var result = new System.Collections.Generic.List<int>();
        // пакеты идут строго по 188 байт: сначала выравниваемся по первому sync, а не ищем
        // 0x47 побайтно — иначе байт 0x47 внутри полезной нагрузки ломает разбор
        int align = SyncOffset(data);
        if (align < 0) return result;
        for (int i = align; i + PacketSize <= data.Length; i += PacketSize)
        {
            if (data[i] != SyncByte) continue;
            int pid = ((data[i + 1] & 0x1f) << 8) | data[i + 2];
            if (pid != 0) continue;
            if ((data[i + 1] & 0x40) == 0) continue;         // нужен начало секции
            int p = PayloadStart(data, i);
            if (p < 0) continue;
            if (data[p] != 0x00) continue;                    // table_id PAT
            int sectionLength = ((data[p + 1] & 0x0f) << 8) | data[p + 2];
            int end = Math.Min(p + 3 + sectionLength - 4, i + PacketSize);   // без CRC
            int q = p + 8;                                   // пропустить заголовок секции
            while (q + 4 <= end && q + 4 <= data.Length)
            {
                int program = (data[q] << 8) | data[q + 1];
                int mapPid = ((data[q + 2] & 0x1f) << 8) | data[q + 3];
                if (program != 0) result.Add(mapPid);          // 0 — network PID, пропускаем
                q += 4;
            }
            if (result.Count > 0) return result;
        }
        return result;
    }

    /// <summary>Типы дорожек из PMT указанного PID. null — таблица не найдена.</summary>
    private static System.Collections.Generic.List<byte> ReadPmtStreams(byte[] data, int pmtPid)
    {
        int align = SyncOffset(data);
        if (align < 0) return null;
        for (int i = align; i + PacketSize <= data.Length; i += PacketSize)
        {
            if (data[i] != SyncByte) continue;
            int pid = ((data[i + 1] & 0x1f) << 8) | data[i + 2];
            if (pid != pmtPid) continue;
            if ((data[i + 1] & 0x40) == 0) continue;
            int p = PayloadStart(data, i);
            if (p < 0) continue;
            if (data[p] != 0x02) continue;                    // table_id PMT
            int sectionLength = ((data[p + 1] & 0x0f) << 8) | data[p + 2];
            int end = Math.Min(p + 3 + sectionLength - 4, i + PacketSize);
            int infoLength = ((data[p + 10] & 0x0f) << 8) | data[p + 11];
            int q = p + 12 + infoLength;
            var types = new System.Collections.Generic.List<byte>();
            while (q + 5 <= end && q + 5 <= data.Length)
            {
                byte streamType = data[q];
                int esInfo = ((data[q + 3] & 0x0f) << 8) | data[q + 4];
                types.Add(streamType);
                q += 5 + esInfo;
            }
            return types;
        }
        return null;
    }
}
