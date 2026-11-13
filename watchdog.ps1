# watchdog.ps1 - сторож VLC-мозаики. PowerShell 5.1, без внешних зависимостей.
#
# Что делает:
#   1. При обрыве RTSP (камера/сеть/кабель) VLC 3 уничтожает инстанс потока
#      и больше не подключается. Сторож видит это через HTTP-интерфейс VLC
#      (/requests/vlm.xml) и делает stop+play только этому каналу.
#   2. Если VLC упал или завис (не отвечает HTTP) - перезапускает его.
#   3. Живые каналы не трогает: никаких периодических restart'ов.
#
# Запускается из video_start.vbs (без окна) или video_start.bat (с окном и логом).

param(
    [int]$WallPort = 0,    # локальный HTTP-порт VLC стены (0 = из Mosaic.conf или 4212)
    [int]$GuardPort = 0,   # локальный порт сторожа (0 = из Mosaic.conf или 42130)
    [switch]$AudioFromWall # звук берём из потока стены (одна камера), без отдельного VLC
)

$ErrorActionPreference = 'Continue'

$BASE = $PSScriptRoot
if (-not $BASE) { $BASE = Split-Path -Parent $MyInvocation.MyCommand.Definition }

$VLC_EXE   = $env:MOSAIC_VLC
if (-not $VLC_EXE) { $VLC_EXE = Join-Path $BASE 'vlc.exe' }   # уточняется в Resolve-VlcPath
$VLM_FILE  = $env:MOSAIC_VLM
if (-not $VLM_FILE) { $VLM_FILE = Join-Path $BASE 'Mosaic.vlm' }
$LOG_FILE  = Join-Path $BASE 'mosaic_watchdog.log'
$STOP_FLAG = Join-Path $BASE 'stop.flag'
$HEARTBEAT_FILE = Join-Path $BASE 'watchdog.heartbeat'
$WALL_STATUS_FILE = Join-Path $BASE 'watchdog.status'

$HTTP_PORT = 4212
# пароль HTTP-интерфейса VLC уникален для каждого запуска и не попадает в журнал
$HTTP_PASS = ([guid]::NewGuid().ToString('N')).Substring(0, 16)
$script:AuthHeader = 'Basic ' + [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes(':' + $HTTP_PASS))

$POLL              = 5.0   # период опроса состояния, сек
$BAD_POLLS         = 2     # сколько опросов подряд "плохо" -> действуем (10 c)
$STARTUP_GRACE     = 25    # первые секунды после старта VLC не трогаем каналы
$BACKOFF           = @(5, 10, 15)   # пауза между попытками по каналу, сек
$HANG_POLLS        = 8     # нет ответа HTTP ~40 c -> считаем, что VLC завис
$VLC_RESTART_DELAY = 5
$KILL_WAIT_MS = 8000      # сколько ждать завершения процесса после Kill
$KILL_RETRY_DELAY = 3     # пауза между попытками принудительного завершения
$LOCK_PORT         = 42130 # защита от второго экземпляра сторожа (переопределяется параметром)

# порты из параметров запуска: приложение передаёт их при старте сторожа
if ($WallPort -ge 1024 -and $WallPort -le 65535) { $HTTP_PORT = $WallPort }
if ($GuardPort -ge 1024 -and $GuardPort -le 65535) { $LOCK_PORT = $GuardPort }
# звук из потока стены: приложение включает это, когда на полотне одна камера.
# Тогда VLM выводит поток напрямую (видео и звук в одном RTSP-сеансе) — иначе
# камеры с лимитом одного подключения отдают звук второму процессу пустым.
$script:AUDIO_FROM_WALL = [bool]$AudioFromWall
$PROBE_OK_INTERVAL   = 5    # опрос камеры, когда она не отвечает по сети
$PROBE_SLOW_INTERVAL = 60   # порт открыт, но RTSP молчит
$PROBE_AUTH_INTERVAL = 120  # 401/403: не чаще раза в 2 минуты, иначе камера банит IP

# ------------------------- журнал -------------------------

function Write-Log([string]$msg) {
    $line = '{0}  {1}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $msg
    try {
        $fi = Get-Item -LiteralPath $LOG_FILE -ErrorAction SilentlyContinue
        if ($fi -and $fi.Length -gt 2MB) {
            Move-Item -LiteralPath $LOG_FILE -Destination ($LOG_FILE + '.old') -Force -ErrorAction SilentlyContinue
        }
    } catch { }
    try { [System.IO.File]::AppendAllText($LOG_FILE, $line + "`r`n", (New-Object System.Text.UTF8Encoding($false))) } catch { }
    try { Write-Host $line } catch { }
}

# ------------------------- VLC HTTP -------------------------

function Get-UrlOn([int]$port, [string]$authHeader, [string]$path, [int]$timeoutMs) {
    if (-not $timeoutMs) { $timeoutMs = 6000 }
    $url = 'http://127.0.0.1:{0}{1}' -f $port, $path
    $req = [System.Net.HttpWebRequest]::Create($url)
    $req.Timeout = $timeoutMs
    $req.ReadWriteTimeout = $timeoutMs
    $req.KeepAlive = $false
    # заголовок шлём сразу: HTTP-интерфейс VLC не отдаёт нормальный 401-challenge,
    # и .NET с Credentials/PreAuthenticate на нём спотыкается
    $req.Headers.Add('Authorization', $authHeader)
    $resp = $req.GetResponse()
    $sr = $null
    try {
        $sr = New-Object System.IO.StreamReader($resp.GetResponseStream())
        return $sr.ReadToEnd()
    } finally {
        if ($sr) { $sr.Close() }
        $resp.Close()
    }
}

function Get-Url([string]$path, [int]$timeoutMs) {
    return Get-UrlOn $HTTP_PORT $script:AuthHeader $path $timeoutMs
}

function Get-States {
    # { имя_канала = 'playing' | 'stopped' | $null } ; $null = инстанса нет
    $xml = [xml](Get-Url -path '/requests/vlm.xml' -timeoutMs 6000)
    $out = @{}
    foreach ($b in $xml.vlm.broadcast) {
        $st = $null
        if ($b.instances -and $b.instances.instance) {
            $inst = @($b.instances.instance)[0]
            if ($inst -and $inst.state) { $st = [string]$inst.state }
        }
        $out[[string]$b.name] = $st
    }
    return $out
}

function Send-VlmCommand([string]$cmd) {
    $enc = [System.Uri]::EscapeDataString($cmd)
    return Get-Url -path ('/requests/vlm_cmd.xml?command=' + $enc) -timeoutMs 6000
}

# ------------------------- конфиг каналов -------------------------

function New-ChannelEntry([string]$name, [string]$url) {
    $port = 554
    $hostName = ''
    try {
        $u = [uri]$url
        $hostName = $u.Host
        if ($u.Port -gt 0) { $port = $u.Port }
    } catch { }
    return [pscustomobject]@{
        Name = $name; Url = $url; HostName = $hostName; Port = $port
        Fails = 0; NextTry = 0.0; NextProbe = 0.0; Attempts = 0; DownSince = 0.0; Probe = 'ok'
    }
}

function Read-ChannelsFromVlm([string]$path) {
    # Резервный режим: каналы берём прямо из готового .vlm (любой broadcast с rtsp://)
    $list = @()
    if (-not (Test-Path -LiteralPath $path)) { return $list }
    $text = Get-Content -LiteralPath $path -Raw -Encoding UTF8
    if ([string]::IsNullOrWhiteSpace($text)) { return $list }
    $re = '(?m)^\s*setup\s+(\S+)\s+input\s+"?(rtsp://[^"\s]+)'
    foreach ($m in [regex]::Matches($text, $re)) {
        $list += New-ChannelEntry $m.Groups[1].Value $m.Groups[2].Value.Trim()
    }
    return $list
}

function Read-MosaicConf([string]$path) {
    # Основной режим: Mosaic.conf = единственное место, где живут камеры, сетка и полотно
    $layout = '2x2'
    $canvasW = 1920
    $canvasH = 1080
    $windowed = $false
    $portWall = 0
    $portGuard = 0
    $cams = @()
    foreach ($line in (Get-Content -LiteralPath $path -Encoding UTF8)) {
        $t = $line.Trim()
        if (-not $t) { continue }
        if ($t.StartsWith('#')) { continue }
        if ($t -match '^layout\s*=\s*(\d+)\s*[xхX×]\s*(\d+)') {
            $layout = '{0}x{1}' -f $Matches[1], $Matches[2]
        } elseif ($t -match '^canvas\s*=\s*(\d+)\s*[xхX×]\s*(\d+)') {
            $canvasW = [int]$Matches[1]
            $canvasH = [int]$Matches[2]
        } elseif ($t -match '^port_wall\s*=\s*(\d+)') {
            $portWall = [int]$Matches[1]
        } elseif ($t -match '^port_guard\s*=\s*(\d+)') {
            $portGuard = [int]$Matches[1]
        } elseif ($t -match '^windowed\s*=\s*(\S+)') {
            # стена в окне размером с полотно; по умолчанию выключено
            $windowed = @('yes', 'on', 'true', '1', 'да') -contains $Matches[1].ToLowerInvariant().Trim('"')
        } elseif ($t -match '^cam\s+(\S+)\s+(\S+)') {
            $cams += New-ChannelEntry $Matches[1] $Matches[2]
        }
    }
    if ($canvasW -lt 640) { $canvasW = 640 }
    if ($canvasW -gt 3840) { $canvasW = 3840 }
    if ($canvasH -lt 480) { $canvasH = 480 }
    if ($canvasH -gt 2160) { $canvasH = 2160 }

    # стандартные полотна имеют свой файл, любое другое — один общий bg_custom.jpg
    $std = @('1280x720', '1920x1080', '2560x1440', '3840x2160', '1280x1024')
    $key = '{0}x{1}' -f $canvasW, $canvasH
    if ($std -contains $key) { $bgFile = 'bg_' + $key + '.jpg' } else { $bgFile = 'bg_custom.jpg' }

    return [pscustomobject]@{
        Layout = $layout; Cams = @($cams)
        CanvasW = $canvasW; CanvasH = $canvasH; BgFile = $bgFile
        Windowed = $windowed
        PortWall = $portWall; PortGuard = $portGuard
    }
}

function Write-VlmText([string]$path, [string]$text) {
    # перезаписываем файл только при фактическом изменении
    $old = $null
    if (Test-Path -LiteralPath $path) {
        try { $old = Get-Content -LiteralPath $path -Raw -Encoding UTF8 } catch { }
    }
    if ($old -ne $text) {
        [System.IO.File]::WriteAllText($path, $text, (New-Object System.Text.UTF8Encoding($false)))
    }
}

function Write-MosaicVlm([string]$path, $conf) {
    # Собирает Mosaic.active.vlm под нужную сетку: cols x rows из Mosaic.conf
    $parts = $conf.Layout -split 'x'
    $cols = [int]$parts[0]
    $rows = [int]$parts[1]
    if ($cols -lt 1) { $cols = 1 }
    if ($rows -lt 1) { $rows = 1 }
    $max = [math]::Min($cols * $rows, 16)
    $use = @($conf.Cams | Select-Object -First $max)
    $cw = $conf.CanvasW
    $chh = $conf.CanvasH
    $tw = [math]::Floor($cw / $cols / 2) * 2
    $th = [math]::Floor($chh / $rows / 2) * 2
    [void](Ensure-Background -file $conf.BgFile -w $cw -h $chh)

    # Звук из потока стены: на полотне одна камера — выводим её напрямую, без мозаики
    # и без «no-audio». Видео и звук идут из одного RTSP-сеанса, поэтому камеры с
    # лимитом одного подключения тоже дают звук (второй сеанс они не отдают).
    if ($script:AUDIO_FROM_WALL -and $use.Count -eq 1 -and ($cols * $rows) -le 1) {
        $d = New-Object System.Text.StringBuilder
        [void]$d.AppendLine('del all')
        [void]$d.AppendLine('')
        [void]$d.AppendLine('# собрано автоматически из Mosaic.conf: одна камера, звук из того же потока')
        [void]$d.AppendLine('')
        [void]$d.AppendLine(('new {0} broadcast enabled' -f $use[0].Name))
        [void]$d.AppendLine(('setup {0} input "{1}"' -f $use[0].Name, $use[0].Url))
        [void]$d.AppendLine(('setup {0} option rtsp-tcp' -f $use[0].Name))
        [void]$d.AppendLine(('setup {0} option network-caching=1500' -f $use[0].Name))
        [void]$d.AppendLine(('setup {0} option sout-keep' -f $use[0].Name))
        [void]$d.AppendLine(('setup {0} output #display' -f $use[0].Name))
        [void]$d.AppendLine('')
        [void]$d.AppendLine(('control {0} play' -f $use[0].Name))
        [void]$d.AppendLine('')
        Write-VlmText -path $path -text $d.ToString()
        return $use
    }

    $sb = New-Object System.Text.StringBuilder
    [void]$sb.AppendLine('del all')
    [void]$sb.AppendLine((''))
    [void]$sb.AppendLine(('# собрано автоматически из Mosaic.conf: сетка {0}x{1}, окон {2}, камер {3}' -f $cols, $rows, $max, $use.Count))
    foreach ($c in $use) {
        [void]$sb.AppendLine('')
        [void]$sb.AppendLine(('new {0} broadcast enabled' -f $c.Name))
        [void]$sb.AppendLine(('setup {0} input "{1}"' -f $c.Name, $c.Url))
        [void]$sb.AppendLine(('setup {0} option rtsp-tcp' -f $c.Name))
        [void]$sb.AppendLine(('setup {0} option network-caching=1500' -f $c.Name))
        [void]$sb.AppendLine(('setup {0} option sout-keep' -f $c.Name))
        [void]$sb.AppendLine(('setup {0} option no-audio' -f $c.Name))
        [void]$sb.AppendLine(('setup {0} output #mosaic-bridge{{id={0},width={1},height={2}}}' -f $c.Name, $tw, $th))
    }
    $order = ($use | ForEach-Object { $_.Name }) -join ','
    [void]$sb.AppendLine('')
    [void]$sb.AppendLine('new bg broadcast enabled')
    [void]$sb.AppendLine(('setup bg input "{0}"' -f $conf.BgFile))
    [void]$sb.AppendLine('setup bg option image-duration=-1')
    [void]$sb.AppendLine(('setup bg output #transcode{{vcodec=mp4v,vb=8000,acodec=none,sfilter=mosaic{{alpha=255,width={0},height={1},cols={2},rows={3},position=1,order="{4}",keep-aspect-ratio=enabled,keep-picture=1}}}}:bridge-in:display' -f $cw, $chh, $cols, $rows, $order))
    [void]$sb.AppendLine('')
    [void]$sb.AppendLine('control bg play')
    foreach ($c in $use) { [void]$sb.AppendLine(('control {0} play' -f $c.Name)) }
    [void]$sb.AppendLine('')

    $text = $sb.ToString()
    Write-VlmText -path $path -text $text
    return $use
}

function Initialize-Scene {
    # Mosaic.conf -> Mosaic.active.vlm (обычный режим)
    if ($env:MOSAIC_VLM) {
        return [pscustomobject]@{ Path = $env:MOSAIC_VLM; Channels = @(Read-ChannelsFromVlm $env:MOSAIC_VLM); Layout = 'вручную'; Cols = 0; Rows = 0 }
    }
    $confPath = Join-Path $BASE 'Mosaic.conf'
    if (Test-Path -LiteralPath $confPath) {
        $conf = Read-MosaicConf $confPath
        $gen = Join-Path $BASE 'Mosaic.active.vlm'
        $chans = @(Write-MosaicVlm -path $gen -conf $conf)
        $p = $conf.Layout -split 'x'
        return [pscustomobject]@{
            Path = $gen; Channels = $chans; Layout = $conf.Layout
            Cols = [int]$p[0]; Rows = [int]$p[1]
            CanvasW = $conf.CanvasW; CanvasH = $conf.CanvasH; BgFile = $conf.BgFile
            Windowed = [bool]$conf.Windowed
        }
    }
    $vlm = Join-Path $BASE 'Mosaic.vlm'
}

# ------------------------- служебное -------------------------

function Write-VlcPid($proc) {
    # приложение должно завершать только свой VLC — передаём PID и время старта
    try {
        $p = Get-Process -Id $proc.Id -ErrorAction Stop
        $line = '{0} {1}' -f $p.Id, $p.StartTime.ToUniversalTime().Ticks
        Set-Content -LiteralPath (Join-Path $BASE 'vlc.pid') -Value $line -Encoding ASCII
    } catch {
        Write-Log ('не удалось записать vlc.pid: {0}' -f $_.Exception.Message)
    }
}

function Remove-VlcPid {
    try {
        $f = Join-Path $BASE 'vlc.pid'
        if (Test-Path -LiteralPath $f) { Remove-Item -LiteralPath $f -Force }
    } catch { }
}

function Drain-Guard($listener) {
    # подключения надо принимать и закрывать, иначе очередь слушателя копится
    if (-not $listener) { return }
    try {
        while ($listener.Pending()) {
            $c = $listener.AcceptTcpClient()
            try { $c.GetStream().WriteByte(1) } catch { }
            $c.Close()
        }
    } catch { }
}

# ------------------------- сеть -------------------------

# ---------- RTSP-проверка: абсолютный request URI, Basic и Digest ----------

function Get-Md5Hex([string]$text) {
    $md5 = [System.Security.Cryptography.MD5]::Create()
    try {
        return (($md5.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($text)) | ForEach-Object { $_.ToString('x2') }) -join '')
    } finally { $md5.Dispose() }
}

function New-OptionsRequest([string]$absUri, [string]$authHeader, [int]$cseq) {
    $t = "OPTIONS $absUri RTSP/1.0`r`nCSeq: $cseq`r`nUser-Agent: vlc-videowall-watchdog`r`n"
    if ($authHeader) { $t += "Authorization: $authHeader`r`n" }
    return $t + "`r`n"
}

function Invoke-RtspHead([System.Net.Sockets.TcpClient]$client, [string]$request, [int]$timeoutMs) {
    # читает заголовок ответа до пустой строки, не дольше таймаута
    $stream = $client.GetStream()
    $stream.ReadTimeout = $timeoutMs
    $stream.WriteTimeout = $timeoutMs
    $bytes = [System.Text.Encoding]::ASCII.GetBytes($request)
    $stream.Write($bytes, 0, $bytes.Length)
    $buf = New-Object byte[] 1024
    $sb = New-Object System.Text.StringBuilder
    $deadline = (Get-Date).AddMilliseconds($timeoutMs)
    while ((Get-Date) -lt $deadline) {
        try {
            $n = $stream.Read($buf, 0, $buf.Length)
            if ($n -le 0) { break }
            [void]$sb.Append([System.Text.Encoding]::ASCII.GetString($buf, 0, $n))
            if ($sb.ToString().Contains("`r`n`r`n")) { break }
        } catch { break }
    }
    return $sb.ToString()
}

function Get-DigestAuth([string]$challenge, [string]$absUri, [string]$userInfo) {
    $realm = ''; $nonce = ''; $qop = ''; $opaque = ''
    if ($challenge -match 'realm="([^"]*)"') { $realm = $Matches[1] }
    if ($challenge -match 'nonce="([^"]*)"') { $nonce = $Matches[1] }
    if ($challenge -match 'qop="([^"]*)"') { $qop = $Matches[1] }
    if ($challenge -match 'opaque="([^"]*)"') { $opaque = $Matches[1] }
    if (-not $nonce) { return $null }

    $user = $userInfo; $pass = ''
    $c = $userInfo.IndexOf(':')
    if ($c -ge 0) { $user = $userInfo.Substring(0, $c); $pass = $userInfo.Substring($c + 1) }

    $ha1 = Get-Md5Hex ('{0}:{1}:{2}' -f $user, $realm, $pass)
    $ha2 = Get-Md5Hex ('OPTIONS:{0}' -f $absUri)
    $nc = '00000001'
    $cnonce = ([guid]::NewGuid().ToString('N')).Substring(0, 8)
    if ($qop -and (($qop -split ',') -contains 'auth')) {
        $resp = Get-Md5Hex ('{0}:{1}:{2}:{3}:auth:{4}' -f $ha1, $nonce, $nc, $cnonce, $ha2)
        $h = 'Digest username="{0}", realm="{1}", nonce="{2}", uri="{3}", response="{4}", qop=auth, nc={5}, cnonce="{6}", algorithm=MD5' -f $user, $realm, $nonce, $absUri, $resp, $nc, $cnonce
    } else {
        $resp = Get-Md5Hex ('{0}:{1}:{2}' -f $ha1, $nonce, $ha2)
        $h = 'Digest username="{0}", realm="{1}", nonce="{2}", uri="{3}", response="{4}", algorithm=MD5' -f $user, $realm, $nonce, $absUri, $resp
    }
    if ($opaque) { $h += ', opaque="' + $opaque + '"' }
    return $h
}

function Connect-Rtsp([string]$hostName, [int]$port, [int]$timeoutMs) {
    $client = New-Object System.Net.Sockets.TcpClient
    try {
        $iar = $client.BeginConnect($hostName, $port, $null, $null)
        if (-not $iar.AsyncWaitHandle.WaitOne($timeoutMs)) { $client.Close(); return $null }
        try { $client.EndConnect($iar) } catch { $client.Close(); return $null }
        return $client
    } catch { try { $client.Close() } catch { }; return $null }
}

function Test-StreamProbe([string]$hostName, [int]$port, [string]$url, [int]$timeoutMs) {
    # 'ok'   — RTSP отвечает (сервер жив)
    # 'auth' — 401/403 после Basic и Digest: логин или пароль не приняты
    # 'tcp'  — порт открыт, но RTSP молчит (камера заблокировала клиента)
    # 'dead' — сеть или камера недоступны
    if (-not $timeoutMs) { $timeoutMs = 2500 }
    if (-not $hostName) { return 'dead' }

    $target = '/'
    $userInfo = ''
    try {
        $u = [uri]$url
        if ($u.PathAndQuery) { $target = $u.PathAndQuery }
        if ($u.UserInfo) { $userInfo = [Uri]::UnescapeDataString($u.UserInfo) }
    } catch { }

    # RFC 2326: request URI абсолютный, учётные данные передаются только в заголовке
    $absUri = 'rtsp://{0}:{1}{2}' -f $hostName, $port, $target
    $basic = $null
    if ($userInfo) {
        $basic = 'Basic ' + [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($userInfo))
    }

    $client = Connect-Rtsp $hostName $port $timeoutMs
    if (-not $client) { return 'dead' }

    try {
        $head = Invoke-RtspHead $client (New-OptionsRequest $absUri $basic 1) $timeoutMs

        if ($head -match '(?m)^RTSP/\S+\s+401' -and $head -match '(?im)^WWW-Authenticate:\s*Digest' -and $userInfo) {
            $chal = ($head -split "`r`n" | Where-Object { $_ -match '(?i)^WWW-Authenticate:\s*Digest' } | Select-Object -First 1)
            $digest = Get-DigestAuth $chal $absUri $userInfo
            if ($digest) {
                try { $client.Close() } catch { }
                $client = Connect-Rtsp $hostName $port $timeoutMs
                if (-not $client) { return 'dead' }
                $head = Invoke-RtspHead $client (New-OptionsRequest $absUri $digest 2) $timeoutMs
            }
        }
        try { $client.Close() } catch { }

        if ($head -match '(?m)^RTSP/\S+\s+(\d{3})') {
            $code = [int]$Matches[1]
            if ($code -eq 401 -or $code -eq 403) { return 'auth' }
            return 'ok'
        }
        return 'tcp'
    } catch {
        try { $client.Close() } catch { }
        return 'tcp'
    }
}

# ------------------------- VLC процесс -------------------------

function Get-LaunchDir {
    # HTTP-интерфейс VLC написан на Lua и не грузится, если путь содержит
    # не-ASCII символы (папка вида "Видеостена"). Тогда запускаем VLC через
    # ASCII-джункшен в %TEMP%: для VLC путь становится латинским.
    if ($BASE -match '^[\x20-\x7E]*$') { return $BASE }

    $link = Join-Path $env:TEMP 'vlc-mosaic-link'
    $need = $true
    if (Test-Path -LiteralPath $link) {
        try {
            $item = Get-Item -LiteralPath $link -Force
            if ($item.LinkType -eq 'Junction' -and @($item.Target)[0] -eq $BASE) { $need = $false }
        } catch { }
        if ($need) { cmd /c rmdir "$link" | Out-Null }
    }
    if ($need) {
        cmd /c mklink /J "$link" "$BASE" | Out-Null
        if (-not (Test-Path -LiteralPath $link)) {
            Write-Log ('НЕ УДАЛОСЬ создать ASCII-ссылки на папку: {0}. Переименуйте папку латиницей, иначе HTTP-интерфейс VLC не поднимется.' -f $link)
            return $BASE
        }
        Write-Log ('не-ASCII путь: работаю через {0}' -f $link)
    }
    return $link
}


function Get-OwnVlcFromPid {
    # свой прежний VLC по vlc.pid (PID + время старта), чужой процесс не трогаем
    try {
        $f = Join-Path $BASE 'vlc.pid'
        if (-not (Test-Path -LiteralPath $f)) { return $null }
        $parts = (Get-Content -LiteralPath $f -Raw).Trim() -split '\s+'
        if ($parts.Count -lt 2) { return $null }
        $pid0 = [int]$parts[0]
        $ticks = [long]$parts[1]
        $p = Get-Process -Id $pid0 -ErrorAction Stop
        if ($p.ProcessName -ne 'vlc') { return $null }
        $diff = [Math]::Abs($p.StartTime.ToUniversalTime().Ticks - $ticks)
        if ($diff -gt 2 * [TimeSpan]::TicksPerSecond) { return $null }
        return $p
    } catch { return $null }
}

function Start-MosaicVlc {
    # windowed = yes в Mosaic.conf: стена в окне, размер задаётся после старта
    # (Set-WallWindowSize доводит его до разрешения полотна)
    $full = '--fullscreen'
    # windowed: обычное окно с заголовком — за него окно можно перетаскивать
    if ($script:WINDOWED) { $full = '--no-fullscreen' }
    # VLC переразбивает свою командную строку по пробелам и ломает путь в --vlm-conf,
    # поэтому передаём имя файла относительным, а рабочую папку задаём процессу.
    $vlmName = Split-Path -Leaf $VLM_FILE
    $launch  = Get-LaunchDir
    $argStr = '-I dummy {0} --no-video-title-show --vlm-conf "{1}" --extraintf http --http-host 127.0.0.1 --http-port {2} --http-password {3} --verbose 1' -f $full, $vlmName, $HTTP_PORT, $HTTP_PASS
    Write-Log ('запуск VLC: {0} {1}' -f $script:VLC_EXE, $argStr.Replace($HTTP_PASS, '<скрыт>'))
    Write-Log ('рабочая папка VLC (для Mosaic.active.vlm): {0}' -f $launch)
    return Start-Process -FilePath $script:VLC_EXE -ArgumentList $argStr -WorkingDirectory $launch -PassThru
}

function Set-WallWindowSize($proc, [int]$w, [int]$h) {
    # Окно стены в режиме windowed.
    #   * приоритет 1:1, но помещается ДАЖЕ ВНЕШНЕЕ окно: из рабочей области
    #     вычитаются измеренные рамки и заголовок;
    #   * если полотно больше доступного клиента — уменьшение с сохранением
    #     пропорций (картинка масштабируется целиком, ничего не обрезается);
    #   * внешний прямоугольник целиком внутри рабочей области монитора
    #     (учитываются панель задач и ненулевое/отрицательное начало координат);
    #   * сдвиг 10% рабочей области, если есть свободное место;
    #   * DPI: per-monitor v2, если ОС поддерживает, иначе system-aware.
    if (-not $proc) { return $false }
    try {
        $p = Get-Process -Id $proc.Id -ErrorAction Stop
        $hwnd = $p.MainWindowHandle
        if ($hwnd -eq 0) { return $false }

        if (-not ('VideoWall.Native' -as [type])) {
            Add-Type -Namespace VideoWall -Name Native -MemberDefinition @'
[DllImport("user32.dll", SetLastError = true)]
public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
[DllImport("user32.dll")]
public static extern bool SetProcessDPIAware();
[DllImport("user32.dll")]
public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
[DllImport("user32.dll")]
public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
[DllImport("user32.dll")]
public static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);
[DllImport("user32.dll")]
public static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);
[DllImport("user32.dll")]
public static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint dwFlags);
[DllImport("user32.dll")]
public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);
[DllImport("user32.dll")]
public static extern uint GetDpiForWindow(IntPtr hWnd);
public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
public struct POINT { public int X; public int Y; }
public struct MONITORINFO {
    public int cbSize;
    public RECT rcMonitor;
    public RECT rcWork;
    public uint dwFlags;
}
'@
        }

        # Per-monitor v2 нужен, чтобы на мониторах с разным DPI координаты не
        # пересчитывались относительно основного экрана. Если ОС/процесс не даёт
        # включить v2 — падаем на system-aware, это лучше, чем DPI-unaware.
        $awareness = 'system'
        if ([VideoWall.Native]::SetProcessDpiAwarenessContext([IntPtr](-4))) { $awareness = 'per-monitor v2' }
        elseif ([VideoWall.Native]::SetProcessDPIAware()) { $awareness = 'system (v2 недоступен)' }

        $outer  = New-Object VideoWall.Native+RECT
        $client = New-Object VideoWall.Native+RECT
        [void][VideoWall.Native]::GetWindowRect($hwnd, [ref]$outer)
        [void][VideoWall.Native]::GetClientRect($hwnd, [ref]$client)

        $origin = New-Object VideoWall.Native+POINT
        [void][VideoWall.Native]::ClientToScreen($hwnd, [ref]$origin)

        # рамки и заголовок измеряем по краям отдельно
        $borderL = [Math]::Max(0, $origin.X - $outer.Left)
        $borderT = [Math]::Max(0, $origin.Y - $outer.Top)
        $borderR = [Math]::Max(0, $outer.Right - ($origin.X + $client.Right))
        $borderB = [Math]::Max(0, $outer.Bottom - ($origin.Y + $client.Bottom))
        $extraW = $borderL + $borderR
        $extraH = $borderT + $borderB

        $mon = [VideoWall.Native]::MonitorFromWindow($hwnd, 2)   # MONITOR_DEFAULTTONEAREST
        $mi = New-Object VideoWall.Native+MONITORINFO
        $mi.cbSize = [System.Runtime.InteropServices.Marshal]::SizeOf($mi)
        if (-not [VideoWall.Native]::GetMonitorInfo($mon, [ref]$mi)) {
            Write-Log 'не удалось получить рабочую область монитора — размер окна не меняю'
            return $false
        }
        $workL = $mi.rcWork.Left
        $workT = $mi.rcWork.Top
        $workR = $mi.rcWork.Right
        $workB = $mi.rcWork.Bottom
        $workW = $workR - $workL
        $workH = $workB - $workT

        # сколько остаётся на клиентскую область, если ВСЁ окно должно поместиться.
        # 2 px запаса: при изменении размера Windows иногда возвращает внешний
        # прямоугольник на 1 px больше запрошенного (рамка округляется)
        $availW = [Math]::Max(320, $workW - $extraW - 2)
        $availH = [Math]::Max(240, $workH - $extraH - 2)

        function Get-Fit {
            param([int]$canvasW, [int]$canvasH, [int]$availW, [int]$availH)
            $k = [Math]::Min(1.0, [Math]::Min($availW / $canvasW, $availH / $canvasH))
            return [pscustomobject]@{
                K = $k
                ClientW = [Math]::Max(320, [int][Math]::Floor($canvasW * $k))
                ClientH = [Math]::Max(240, [int][Math]::Floor($canvasH * $k))
            }
        }

        $fit = Get-Fit -canvasW $w -canvasH $h -availW $availW -availH $availH
        $k = $fit.K
        $clientW = $fit.ClientW
        $clientH = $fit.ClientH

        for ($pass = 1; $pass -le 3; $pass++) {
            $outerW = $clientW + $extraW
            $outerH = $clientH + $extraH

            # запрашиваем ровно по полотну: если место есть — остаётся 1:1
            $reqW = $outerW
            $reqH = $outerH

            # сдвиг 10% рабочей области — только если окно со сдвигом помещается целиком
            $maxOffsetX = [Math]::Max(0, $workW - $reqW)
            $maxOffsetY = [Math]::Max(0, $workH - $reqH)
            $outerX = $workL + [Math]::Min([int]($workW * 0.1), $maxOffsetX)
            $outerY = $workT + [Math]::Min([int]($workH * 0.1), $maxOffsetY)

            if (-not [VideoWall.Native]::SetWindowPos($hwnd, [IntPtr]::Zero, $outerX, $outerY, $reqW, $reqH, 0x0004)) {
                Write-Log 'не удалось изменить размер окна VLC (SetWindowPos вернул false)'
                return $false
            }

            # контроль: внешнее окно должно целиком лежать в рабочей области
            $check = New-Object VideoWall.Native+RECT
            [void][VideoWall.Native]::GetWindowRect($hwnd, [ref]$check)
            $overX = [Math]::Max(0, $check.Right - $workR) + [Math]::Max(0, $workL - $check.Left)
            $overY = [Math]::Max(0, $check.Bottom - $workB) + [Math]::Max(0, $workT - $check.Top)
            if ($overX -eq 0 -and $overY -eq 0) { break }

            # не поместилось — уменьшаем клиент на величину выхода (+1 px на округление рамки)
            $clientW = [Math]::Max(320, $clientW - $overX - 1)
            $clientH = [Math]::Max(240, $clientH - $overY - 1)
            $k = [Math]::Min($clientW / $w, $clientH / $h)
        }

        # пишем в журнал ФАКТИЧЕСКИЕ размеры, а не запрошенные
        $final = New-Object VideoWall.Native+RECT
        $finalClient = New-Object VideoWall.Native+RECT
        [void][VideoWall.Native]::GetWindowRect($hwnd, [ref]$final)
        [void][VideoWall.Native]::GetClientRect($hwnd, [ref]$finalClient)
        $dpi = 96
        try { $dpi = [int][VideoWall.Native]::GetDpiForWindow($hwnd) } catch { }
        Write-Log ('стена в окне: полотно {0}x{1}; клиент {2}x{3}; окно {4}x{5} в {6},{7} (правый нижний {8},{9}); рабочая область {10},{11}..{12},{13}; масштаб {14}%; DPI окна {15}%; режим {16}' -f `
            $w, $h, $finalClient.Right, $finalClient.Bottom, ($final.Right - $final.Left), ($final.Bottom - $final.Top), `
            $final.Left, $final.Top, $final.Right, $final.Bottom, `
            $workL, $workT, $workR, $workB, [int][Math]::Round(($finalClient.Right / $w) * 100), [int]($dpi / 96 * 100), $awareness)
        if ($k -lt 0.999) {
            Write-Log ('масштабирование: полотно {0}x{1} не помещается с рамкой в рабочую область {2}x{3} (доступно клиенту {4}x{5}), коэффициент {6:N3}' -f `
                $w, $h, $workW, $workH, $availW, $availH, $k)
        }
        return $true
    } catch {
        Write-Log ('не удалось задать размер окна: {0}' -f $_.Exception.Message)
        return $false
    }
}

function Stop-MosaicVlc($proc, [int]$attempts = 3) {
    # исправление/vlc.pid удаляем только после ПОДТВЕРЖДЁННОГО завершения.
    # Если VLC не умер (отказ в доступе, зависание) — файл остаётся, а вызывающий
    # код обязан считать остановку неудачной и НЕ запускать новый экземпляр.
    if (-not $proc) { return $true }

    for ($i = 1; $i -le $attempts; $i++) {
        $exited = $false
        try {
            if ($proc.HasExited) {
                $exited = $true
            } else {
                $proc.Kill()
                $exited = $proc.WaitForExit($KILL_WAIT_MS)
            }
        } catch {
            Write-Log ('не удалось остановить VLC (попытка {0} из {1}): {2}' -f $i, $attempts, $_.Exception.Message)
            try { $exited = $proc.HasExited } catch { $exited = $false }
        }

        if ($exited) {
            Remove-VlcPid
            return $true
        }
        if ($i -lt $attempts) {
            Write-Log ('VLC ещё работает, повторное завершение через {0} с' -f $KILL_RETRY_DELAY)
            # Stop-SleepChecked просыпается на stop.flag: остановку не затягиваем
            if (-not (Start-SleepChecked $KILL_RETRY_DELAY)) { return $false }
        }
    }

    Write-Log ('VLC не завершился после {0} попыток — vlc.pid сохранён, новый экземпляр не запускается' -f $attempts)
    return $false
}

function Write-Heartbeat {
    # приложение считает сторожа живым по свежести этого файла,
    # а не по факту подключения к порту (очередь слушателя может копиться).
    try {
        $nowTicks = [DateTime]::UtcNow.Ticks
        if ($script:LAST_HEARTBEAT -and ($nowTicks - $script:LAST_HEARTBEAT) -lt 8000000) { return }
        $script:LAST_HEARTBEAT = $nowTicks
        Set-Content -LiteralPath $HEARTBEAT_FILE -Value $nowTicks -Encoding ASCII
    } catch { }
}

function Write-WallStatus([string]$vlm, [int]$playing, [int]$total) {
    # графическое приложение не знает пароль HTTP-интерфейса VLC,
    # поэтому сторож публикует результат СВОЕГО обращения к VLM.
    # Открытый TCP-порт сам по себе здоровьем интерфейса не считается.
    try {
        $lines = @(
            [string][DateTime]::UtcNow.Ticks,
            ('vlm=' + $vlm),
            ('playing=' + $playing),
            ('total=' + $total)
        )
        Set-Content -LiteralPath $WALL_STATUS_FILE -Value $lines -Encoding ASCII
    } catch { }
}

function Remove-WallStatus {
    try {
        if (Test-Path -LiteralPath $WALL_STATUS_FILE) { Remove-Item -LiteralPath $WALL_STATUS_FILE -Force }
    } catch { }
}

function Wait-VlcHttp([int]$seconds) {
    # 'ok' — HTTP отвечает, 'stop' — просили остановиться, 'timeout' — не дождались
    # stop.flag проверяем внутри: иначе «Сохранить и перезапустить» висит до конца ожидания
    $end = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $end) {
        Drain-Guard $script:GUARD
        Write-Heartbeat
        if (Test-Path -LiteralPath $STOP_FLAG) { return 'stop' }
        try { Get-States | Out-Null; Write-WallStatus 'ok' 0 0; return 'ok' } catch { Start-Sleep -Milliseconds 1000 }
    }
    return 'timeout'
}

function Start-SleepChecked([int]$seconds) {
    # Пауза, которая просыпается на stop.flag. $false = просили остановиться
    $end = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $end) {
        Drain-Guard $script:GUARD
        Write-Heartbeat
        if (Test-Path -LiteralPath $STOP_FLAG) { return $false }
        Start-Sleep -Milliseconds 250
    }
    return $true
}

function Complete-Stop($proc, [string]$why) {
    # Единый выход по stop.flag: гасим VLC, снимаем флаг — приложение сразу видит остановку.
    # если VLC не завершился, это не скрываем: vlc.pid остаётся, приложение
    # увидит живой процесс и сообщит, что стена не остановлена.
    Write-Log $why
    $stopped = Stop-MosaicVlc $proc
    if (-not $stopped) {
        Write-Log 'ВНИМАНИЕ: VLC не удалось завершить — стена считается неостановленной, vlc.pid сохранён'
    }
    Remove-Item -LiteralPath $STOP_FLAG -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $HEARTBEAT_FILE -Force -ErrorAction SilentlyContinue
    Remove-WallStatus
    return 0
}

function Get-NowSeconds {
    return ([DateTime]::UtcNow.Ticks / 10000000.0)
}

# ------------------------- основной цикл -------------------------

function Invoke-Watchdog {
    $scene = Initialize-Scene
    $script:VLM_FILE = $scene.Path
    $channels = @($scene.Channels)
    if ($channels.Count -eq 0) {
        Write-Log 'В Mosaic.conf (или Mosaic.vlm) не найдено ни одного rtsp-канала - сторожу нечего делать'
        return 2
    }
    $names = ($channels | ForEach-Object { $_.Name }) -join ', '
    Write-Log ('=== сторож запущен, каналы: {0}' -f $names)
    Write-Log ('раскладка: {0}, полотно {1}x{2} (файл {3})' -f $scene.Layout, $scene.CanvasW, $scene.CanvasH, (Split-Path -Leaf $scene.Path))

    # режим окна: VLC запускается без --fullscreen, а размер окна доводится до полотна
    $script:WINDOWED = [bool]$scene.Windowed
    $script:CANVAS_W = [int]$scene.CanvasW
    $script:CANVAS_H = [int]$scene.CanvasH
    $script:WindowSized = $false
    if ($script:WINDOWED) {
        Write-Log ('стена в окне: размер окна будет {0}x{1}' -f $script:CANVAS_W, $script:CANVAS_H)
    }
    $addr = ($channels | ForEach-Object { '{0}={1}:{2}' -f $_.Name, $_.HostName, $_.Port }) -join '; '
    Write-Log ('адреса: {0}' -f $addr)
    if ($script:AUDIO_FROM_WALL -and $channels.Count -eq 1) {
        Write-Log 'звук: из потока стены (одна камера, отдельный процесс не нужен)'
    } elseif ($script:AUDIO_FROM_WALL) {
        Write-Log ('звук из потока стены невозможен: камер {0} — нужна одна, вывожу мозаику как обычно' -f $channels.Count)
    }

    $script:CAMERAS = @($channels)

    # порты: приоритет у параметров запуска, иначе из Mosaic.conf
    if (($WallPort -lt 1024 -or $WallPort -gt 65535) -and $scene.PortWall -ge 1024 -and $scene.PortWall -le 65535) {
        $script:HTTP_PORT = $scene.PortWall
        Write-Log ('порт стены из Mosaic.conf: {0}' -f $script:HTTP_PORT)
    }

    $proc = $null
    $graceUntil = 0.0
    $hang = 0
    $failStarts = 0

    # если прошлый VLC так и не был завершён, второй экземпляр не поднимаем —
    # иначе два процесса подерутся за порт 4212, а vlc.pid будет перезаписан
    $leftover = Get-OwnVlcFromPid
    if ($leftover) {
        Write-Log ('ОСТАНОВ: прошлый VLC (PID {0}) ещё работает — сначала завершите его. vlc.pid сохранён.' -f $leftover.Id)
        return 1
    }

    while ($true) {
        Drain-Guard $script:GUARD
        Write-Heartbeat
        if (Test-Path -LiteralPath $STOP_FLAG) {
            return (Complete-Stop $proc 'найден stop.flag - останавливаюсь')
        }


        # окно не нужного размера (VLC ещё не создал окно) — дотягиваем до полотна
        if ($script:WINDOWED -and -not $script:WindowSized -and $proc -and -not $proc.HasExited) {
            $script:WindowSized = Set-WallWindowSize $proc $script:CANVAS_W $script:CANVAS_H
        }

        # VLC не запущен / упал
        $needStart = $false
        if (-not $proc) { $needStart = $true }
        elseif ($proc.HasExited) {
            Write-Log ('VLC завершился (код {0}) - перезапускаю' -f $proc.ExitCode)
            if (-not (Start-SleepChecked $VLC_RESTART_DELAY)) {
                return (Complete-Stop $null 'найден stop.flag во время паузы перед перезапуском')
            }
            $needStart = $true
        }
        if ($needStart) {
            $proc = Start-MosaicVlc
            Write-VlcPid $proc
            foreach ($c in $channels) {
                $c.Fails = 0; $c.NextTry = 0.0; $c.NextProbe = 0.0; $c.Attempts = 0; $c.DownSince = 0.0; $c.Probe = 'ok'
            }
            $wait = Wait-VlcHttp 40
            if ($wait -eq 'stop') {
                return (Complete-Stop $proc 'найден stop.flag во время запуска VLC')
            }
            if ($wait -ne 'ok') {
                $failStarts++
                Write-Log ('HTTP-интерфейс VLC не поднялся (попытка {0} из 3)' -f $failStarts)
                # пока старый процесс жив, второй экземпляр не запускаем
                if (-not (Stop-MosaicVlc $proc)) {
                    Write-Log 'ОСТАНОВ: VLC не завершился — второй экземпляр не запускаю, vlc.pid сохранён'
                    return 1
                }
                $proc = $null
                if ($failStarts -ge 3) {
                    Write-Log 'ОСТАНОВ: VLC не отдаёт HTTP-интерфейс. Проверьте, не занят ли порт 4212 другой копией VLC, и права на папку.'
                    return 1
                }
                if (-not (Start-SleepChecked $VLC_RESTART_DELAY)) {
                    return (Complete-Stop $null 'найден stop.flag во время паузы перед повтором запуска')
                }
                continue
            }
            $failStarts = 0
            $graceUntil = (Get-NowSeconds) + $STARTUP_GRACE
            $hang = 0
            Write-Log 'VLC работает, HTTP-интерфейс отвечает'
            if ($script:WINDOWED) {
                # окно появляется не сразу: несколько попыток, не блокируя цикл
                $script:WindowSized = Set-WallWindowSize $proc $script:CANVAS_W $script:CANVAS_H
            }
            [void](Start-SleepChecked ([int][math]::Ceiling($POLL)))
            continue
        }

        # состояние каналов
        try {
            $states = Get-States
            $hang = 0
            # считаем только свои каналы: в vlm.xml есть ещё служебный broadcast bg
            $playing = 0
            foreach ($c in $channels) { if ($states[$c.Name] -eq 'playing') { $playing++ } }
            Write-WallStatus 'ok' $playing $channels.Count
        } catch {
            $hang++
            Write-WallStatus 'fail' 0 $channels.Count
            Write-Log ('HTTP-интерфейс VLC не отвечает ({0}/{1}): {2}' -f $hang, $HANG_POLLS, $_.Exception.Message)
            if ($hang -ge $HANG_POLLS) {
                Write-Log 'VLC завис - жёсткий перезапуск приложения'
                # перезапуск только после подтверждённого завершения
                if (-not (Stop-MosaicVlc $proc)) {
                    Write-Log 'ОСТАНОВ: зависший VLC не завершился — перезапуск отменён, vlc.pid сохранён'
                    return 1
                }
                $proc = $null
                $hang = 0
            } else {
                [void](Start-SleepChecked ([int][math]::Ceiling($POLL)))
            }
            continue
        }

        $now = Get-NowSeconds
        foreach ($c in $channels) {
            $state = $null
            if ($states.ContainsKey($c.Name)) { $state = $states[$c.Name] }

            if ($state -eq 'playing') {
                if ($c.DownSince -gt 0) {
                    Write-Log ('{0}: поток восстановлен (простой {1:N0} c)' -f $c.Name, ($now - $c.DownSince))
                }
                $c.Fails = 0; $c.NextTry = 0.0; $c.NextProbe = 0.0; $c.Attempts = 0; $c.DownSince = 0.0; $c.Probe = 'ok'
                continue
            }

            if ($c.DownSince -eq 0) {
                $c.DownSince = $now
                $shown = 'инстанса нет'
                if ($state) { $shown = $state }
                Write-Log ('{0}: поток пропал (состояние: {1})' -f $c.Name, $shown)
            }
            $c.Fails++

            if ($now -lt $graceUntil -or $c.Fails -lt $BAD_POLLS) { continue }

            # сетевой опрос камеры только если истёк интервал опроса.
            # Иначе неверный пароль превращается в подбор: OPTIONS каждые 5 секунд,
            # и камера блокирует IP клиента.
            if ($now -lt $c.NextProbe) { continue }

            # Опрос самого источника: мёртвую камеру VLC дёргать бессмысленно,
            # а как только связь вернулась - рестарт сразу, без ожидания backoff.
            $probe = Test-StreamProbe -hostName $c.HostName -port $c.Port -url $c.Url -timeoutMs 2500
            switch ($probe) {
                'dead'  { $c.NextProbe = $now + $PROBE_OK_INTERVAL }
                'tcp'   { $c.NextProbe = $now + $PROBE_SLOW_INTERVAL }
                'auth'  { $c.NextProbe = $now + $PROBE_AUTH_INTERVAL }
                default { $c.NextProbe = $now + $PROBE_OK_INTERVAL }
            }
            if ($probe -eq 'dead') {
                if ($c.Probe -ne 'dead') {
                    Write-Log ('{0}: источник {1}:{2} не отвечает - жду связи, VLC не трогаю' -f $c.Name, $c.HostName, $c.Port)
                }
                $c.Probe = 'dead'
                $c.NextTry = 0.0
                continue
            }
            if ($c.Probe -eq 'dead') {
                Write-Log ('{0}: связь с источником восстановилась - перезапускаю канал' -f $c.Name)
                $c.NextTry = 0.0
            }
            if ($probe -eq 'auth' -and $c.Probe -ne 'auth') {
                Write-Log ('{0}: камера отвечает 401 - проверьте логин/пароль в Mosaic.conf. Частые неудачные попытки камера может принять за подбор пароля и временно заблокировать IP.' -f $c.Name)
            }
            $c.Probe = $probe
            # 401/403: перезапуск VLC пароль не починит, только зря дёргаем канал
            if ($probe -eq 'auth') { continue }
            if ($now -lt $c.NextTry) { continue }

            Write-Log ('{0}: перезапуск канала (stop/play), попытка {1}' -f $c.Name, ($c.Attempts + 1))
            try {
                Send-VlmCommand ('control {0} stop' -f $c.Name) | Out-Null
                Start-Sleep -Milliseconds 800
                Send-VlmCommand ('control {0} play' -f $c.Name) | Out-Null
            } catch {
                Write-Log ('{0}: команда VLM не прошла: {1}' -f $c.Name, $_.Exception.Message)
            }
            $c.Attempts++
            $idx = $c.Attempts
            if ($idx -gt $BACKOFF.Count) { $idx = $BACKOFF.Count }
            $delay = $BACKOFF[$idx - 1]
            # порт открыт, но RTSP молчит: камера обычно в этом состоянии
            # блокирует клиента — не долбим её каждые 15 секунд
            if ($probe -eq 'tcp' -and $c.Attempts -ge 5) { $delay = 60 }
            if ($probe -eq 'auth' -and $delay -lt 120) { $delay = 120 }
            if ($probe -eq 'tcp' -and $c.Attempts -eq 3) {
                Write-Log ('{0}: порт открыт, но RTSP не отвечает - похоже, камера блокирует клиента; дальше повторяю реже' -f $c.Name)
            }
            $c.NextTry = (Get-NowSeconds) + $delay
        }

        [void](Start-SleepChecked ([int][math]::Ceiling($POLL)))
    }
}

# ------------------------- фон мозаики -------------------------

function Ensure-Background([string]$file, [int]$w, [int]$h) {
    # Полотно лежит в папке стены и грузится относительно рабочей папки VLC.
    # Нет файла или размер не совпадает — рисуем чёрное. Для своего разрешения файл
    # всегда один (bg_custom.jpg) и просто перезаписывается при смене размера.
    $bg = Join-Path $BASE $file
    $need = $true
    if (Test-Path -LiteralPath $bg) {
        try {
            Add-Type -AssemblyName System.Drawing -ErrorAction SilentlyContinue
            $img = [System.Drawing.Image]::FromFile($bg)
            if ($img.Width -eq $w -and $img.Height -eq $h) { $need = $false }
            $img.Dispose()
        } catch { $need = $true }
    }
    if ($need) {
        try {
            Add-Type -AssemblyName System.Drawing -ErrorAction SilentlyContinue
            $bmp = New-Object System.Drawing.Bitmap $w, $h
            $g = [System.Drawing.Graphics]::FromImage($bmp)
            $g.Clear([System.Drawing.Color]::Black)
            $g.Dispose()
            $bmp.Save($bg, [System.Drawing.Imaging.ImageFormat]::Jpeg)
            $bmp.Dispose()
            Write-Log ('полотно {0}x{1} -> {2}' -f $w, $h, $file)
        } catch {
            Write-Log ('не удалось создать полотно: {0}' -f $_.Exception.Message)
        }
    }
    return $bg
}

# ------------------------- журнал: папка и срок хранения -------------------------

function Initialize-Logging {
    # Папка и срок хранения задаются в Mosaic.conf (их пишет приложение VideoWall):
    #   logdir  = D:\Logs      (пусто/нет строки -> папка программы)
    #   logkeep = 30           (дней; 0 = не удалять)
    $dir = $BASE
    $keep = 30
    $confPath = Join-Path $BASE 'Mosaic.conf'
    if (Test-Path -LiteralPath $confPath) {
        foreach ($line in (Get-Content -LiteralPath $confPath -Encoding UTF8)) {
            $t = $line.Trim()
            if ($t -match '^logdir\s*=\s*(.+)$') {
                $v = $Matches[1].Trim().Trim('"')
                if ($v) { $dir = $v }
            } elseif ($t -match '^logkeep\s*=\s*(\d+)') {
                $keep = [int]$Matches[1]
            }
        }
    }
    if (-not (Test-Path -LiteralPath $dir)) {
        try { New-Item -ItemType Directory -Path $dir -Force | Out-Null } catch { $dir = $BASE }
    }
    $script:LOG_FILE = Join-Path $dir 'mosaic_watchdog.log'

    if ($keep -gt 0) {
        try {
            $limit = (Get-Date).AddDays(-$keep)
            Get-ChildItem -LiteralPath $dir -Filter 'mosaic_watchdog.log*' -File -ErrorAction SilentlyContinue |
                Where-Object { $_.LastWriteTime -lt $limit } |
                ForEach-Object { Remove-Item -LiteralPath $_.FullName -Force -ErrorAction SilentlyContinue }
        } catch { }
    }
}

# ------------------------- поиск vlc.exe -------------------------

function Resolve-VlcPath {
    # 1) vlcpath из Mosaic.conf (его пишет приложение)  2) vlc.exe рядом со стеной
    # 3) установленный VLC (реестр / Program Files)  4) 32-битная установка
    $fromConf = ''
    $confPath = Join-Path $BASE 'Mosaic.conf'
    if (Test-Path -LiteralPath $confPath) {
        foreach ($line in (Get-Content -LiteralPath $confPath -Encoding UTF8)) {
            $t = $line.Trim()
            if ($t -match '^vlcpath\s*=\s*(.+)$') { $fromConf = $Matches[1].Trim().Trim('"'); break }
        }
    }

    $cands = @($fromConf, (Join-Path $BASE 'vlc.exe'))
    try {
        $reg = (Get-ItemProperty 'HKLM:\SOFTWARE\VideoLAN\VLC' -ErrorAction SilentlyContinue).InstallDir
        if ($reg) { $cands += (Join-Path $reg 'vlc.exe') }
    } catch { }
    $cands += 'C:\Program Files\VideoLAN\VLC\vlc.exe'
    $cands += 'C:\Program Files (x86)\VideoLAN\VLC\vlc.exe'

    foreach ($c in $cands) {
        if ($c -and (Test-Path -LiteralPath $c -PathType Leaf)) {
            return (Resolve-Path -LiteralPath $c).Path
        }
    }
    return ''
}

# ------------------------- запуск -------------------------

Initialize-Logging

$script:VLC_EXE = Resolve-VlcPath
if (-not $script:VLC_EXE) {
    Write-Log 'ОСТАНОВ: не найден vlc.exe. Укажите путь к VLC в приложении (вкладка «Общие») или установите VLC.'
    exit 3
}
Write-Log ('VLC: {0}' -f $script:VLC_EXE)

$lock = $null
try {
    $lock = New-Object System.Net.Sockets.TcpListener([System.Net.IPAddress]::Loopback, $LOCK_PORT)
    $lock.Start()
    $script:GUARD = $lock
} catch {
    Write-Log ('сторож уже запущен (порт {0} занят) - выхожу' -f $LOCK_PORT)
    exit 0
}

try {
    $code = Invoke-Watchdog
} catch {
    Write-Log ('аварийное завершение: {0}' -f $_.Exception.Message)
    $code = 1
} finally {
    try { $lock.Stop() } catch { }
    Remove-Item -LiteralPath $HEARTBEAT_FILE -Force -ErrorAction SilentlyContinue
    Remove-WallStatus
}

exit $code
