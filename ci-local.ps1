#Requires -Version 5.1
<#
    Локальная проверка (CI) проекта VLCVideoWall.

    Повторяет все шаги, которые раньше выполнялись в GitHub Actions:
    проверку комплекта файлов, проверку состава публикации, сборку, тесты,
    публикацию и упаковку готового архива.

    Ничего не отправляется в сеть: ни артефактов, ни кэшей, ни минут Actions.
    Результат складывается в папку artifacts\ рядом с репозиторием.

    Примеры:
        powershell -NoProfile -ExecutionPolicy Bypass -File ci-local.ps1
        powershell -NoProfile -ExecutionPolicy Bypass -File ci-local.ps1 -SkipTests
#>
[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
Set-Location -LiteralPath $root

$script:step = 0
$script:total = 7
if ($SkipTests) { $script:total = 6 }
$started = Get-Date

function Write-Step([string]$Title) {
    $script:step++
    Write-Host ''
    Write-Host ('[{0}/{1}] {2}' -f $script:step, $script:total, $Title) -ForegroundColor Cyan
}

function Write-Ok([string]$Text) {
    Write-Host ('   ok  ' + $Text) -ForegroundColor DarkGreen
}

function Fail([string]$Message) {
    Write-Host ''
    Write-Host ('ОШИБКА: ' + $Message) -ForegroundColor Red
    exit 1
}

function Invoke-Native([string]$Exe, [string[]]$Arguments) {
    & $Exe @Arguments
    if ($LASTEXITCODE -ne 0) {
        Fail ($Exe + ' завершился с кодом ' + $LASTEXITCODE)
    }
}

Write-Host '=============================================' -ForegroundColor DarkGray
Write-Host ' VLCVideoWall: локальная проверка' -ForegroundColor White
Write-Host (' конфигурация: ' + $Configuration) -ForegroundColor DarkGray
Write-Host '=============================================' -ForegroundColor DarkGray

# ---------------------------------------------------------------- 1. окружение
Write-Step 'окружение'

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Fail 'не найден dotnet SDK: установите .NET 10 SDK и повторите запуск'
}
$dotnetVersion = (& dotnet --version).Trim()
if ($LASTEXITCODE -ne 0) { Fail 'dotnet --version не выполнился' }
Write-Ok ('dotnet SDK ' + $dotnetVersion)

$commit = ''
if (Get-Command git -ErrorAction SilentlyContinue) {
    $commit = (& git rev-parse --short HEAD 2>$null)
    if ($commit) { Write-Ok ('git ' + $commit.Trim()) }
}

# ------------------------------------------------------- 2. обязательные файлы
Write-Step 'обязательные файлы'

$required = @(
    'README.md',
    'CHANGELOG.md',
    'LICENSE',
    'watchdog.ps1',
    'config/Mosaic.conf.example',
    'config/README.txt',
    'docs/configuration.md',
    'docs/troubleshooting.md'
)
$missing = @($required | Where-Object { -not (Test-Path -LiteralPath $_ -PathType Leaf) })
if ($missing.Count -gt 0) {
    Fail ('нет обязательных файлов: ' + ($missing -join ', '))
}
Write-Ok ('файлов на месте: ' + $required.Count)

# --------------------------------------------------- 3. состав публикации
Write-Step 'состав публикации'

if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
    Fail 'без git нельзя проверить состав публикации'
}
$tracked = @(& git ls-files)
if ($LASTEXITCODE -ne 0) { Fail 'git ls-files не выполнился' }

# В публикации допустимы только пользовательские документы из списка.
# Любой новый файл документации требует явного разрешения владельца проекта.
$allowedDocs = @(
    'README.md',
    'CHANGELOG.md',
    'LICENSE',
    'docs/configuration.md',
    'docs/troubleshooting.md',
    'config/README.txt'
)
$docs = @($tracked | Where-Object { $_ -match '\.(md|markdown|txt)$' })
$extraDocs = @($docs | Where-Object { $allowedDocs -notcontains $_ })
if ($extraDocs.Count -gt 0) {
    Fail ('в публикацию добавлены документы вне списка: ' + ($extraDocs -join ', '))
}
Write-Ok ('документов в публикации: ' + $docs.Count + ', лишних нет')

$service = @($tracked | Where-Object { $_ -match '^(audio-temp/|\.env|.*\.xspf$|Mosaic\.conf$)' })
if ($service.Count -gt 0) {
    Fail ('в индексе есть служебные файлы: ' + ($service -join ', '))
}
Write-Ok 'служебных файлов и адресов камер в индексе нет'

$personal = @()
$textFiles = @($tracked | Where-Object { $_ -match '\.(md|markdown|txt|ps1|cs|csproj|json|ya?ml|conf|example|manifest)$' })
foreach ($file in $textFiles) {
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { continue }
    $hit = Select-String -LiteralPath $file -Pattern '192\.168\.\d|10\.\d{1,3}\.\d{1,3}\.\d{1,3}|172\.(1[6-9]|2[0-9]|3[01])\.' -List -ErrorAction SilentlyContinue
    if ($hit) { $personal += $file }
}
if ($personal.Count -gt 0) {
    Fail ('в публикации есть приватные адреса: ' + ($personal -join ', '))
}
Write-Ok ('текстовых файлов проверено: ' + $textFiles.Count + ', приватных адресов нет')

# --------------------------------------------------------------- 4. сборка
Write-Step ('сборка ' + $Configuration)

Invoke-Native 'dotnet' @('build', 'src/VLCVideoWall/VLCVideoWall.csproj', '-c', $Configuration, '--nologo')
Write-Ok 'сборка прошла'

# ---------------------------------------------------------------- 5. тесты
if (-not $SkipTests) {
    Write-Step 'тесты'

    Invoke-Native 'dotnet' @('run', '--project', 'tests/VLCVideoWall.Tests/VLCVideoWall.Tests.csproj', '-c', $Configuration)
    Write-Ok 'тесты прошли'
}

# ------------------------------------------------- 6. публикация и комплект
Write-Step 'публикация одним файлом'

$artifacts = Join-Path $root 'artifacts'
$dist = Join-Path $artifacts 'dist'
$stage = Join-Path $artifacts 'stage'
if (-not (Test-Path -LiteralPath $artifacts)) {
    New-Item -ItemType Directory -Path $artifacts | Out-Null
}
if (Test-Path -LiteralPath $dist) { Remove-Item -LiteralPath $dist -Recurse -Force }
if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }

Invoke-Native 'dotnet' @('publish', 'src/VLCVideoWall/VLCVideoWall.csproj', '-c', $Configuration, '-o', $dist, '--nologo')

$exe = Join-Path $dist 'VLCVideoWall.exe'
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) {
    Fail 'после публикации нет dist\VLCVideoWall.exe'
}
$exeInfo = Get-Item -LiteralPath $exe
Write-Ok ('VLCVideoWall.exe ' + [math]::Round($exeInfo.Length / 1MB, 1) + ' МБ')

$bundle = @(
    @{ From = $exe;                     To = 'VLCVideoWall.exe' },
    @{ From = 'watchdog.ps1';           To = 'watchdog.ps1' },
    @{ From = 'LICENSE';                To = 'LICENSE' },
    @{ From = 'config/Mosaic.conf.example'; To = 'config/Mosaic.conf.example' },
    @{ From = 'config/README.txt';      To = 'config/README.txt' }
)
$missingBundle = @($bundle | Where-Object { -not (Test-Path -LiteralPath $_.From -PathType Leaf) })
if ($missingBundle.Count -gt 0) {
    Fail ('в комплекте не хватает: ' + (($missingBundle | ForEach-Object { $_.From }) -join ', '))
}
Write-Ok ('файлов в комплекте: ' + $bundle.Count)

# ------------------------------------------------------------ 7. упаковка
Write-Step 'упаковка архива'

New-Item -ItemType Directory -Path $stage | Out-Null
New-Item -ItemType Directory -Path (Join-Path $stage 'config') | Out-Null
foreach ($item in $bundle) {
    $target = Join-Path $stage $item.To
    $targetDir = Split-Path -Parent $target
    if (-not (Test-Path -LiteralPath $targetDir)) {
        New-Item -ItemType Directory -Path $targetDir | Out-Null
    }
    Copy-Item -LiteralPath $item.From -Destination $target -Force
}

$zip = Join-Path $artifacts 'VLCVideoWall-win-x64.zip'
if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip, [System.IO.Compression.CompressionLevel]::Optimal, $false)

$zipInfo = Get-Item -LiteralPath $zip
Write-Ok ('VLCVideoWall-win-x64.zip ' + [math]::Round($zipInfo.Length / 1MB, 1) + ' МБ')

Write-Host ''
Write-Host 'SHA256:' -ForegroundColor DarkGray
Get-FileHash -Algorithm SHA256 -LiteralPath $exe, $zip | ForEach-Object {
    Write-Host ('   ' + $_.Hash.ToLower() + '  ' + (Split-Path -Leaf $_.Path))
}

$elapsed = (Get-Date) - $started
Write-Host ''
Write-Host ('ВСЁ ЗЕЛЁНОЕ. Шагов: ' + $script:step + ', время: ' + [math]::Round($elapsed.TotalSeconds, 1) + ' с') -ForegroundColor Green
Write-Host ('готовый архив: ' + $zip) -ForegroundColor Green
exit 0
