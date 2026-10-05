<#
.SYNOPSIS
    Публикует SoundMeeter одним файлом и упаковывает публикацию в SoundMeeter.zip.

.DESCRIPTION
    Релизный сценарий целиком: тесты → публикация профилем portable → проверка
    результата → zip. Каждый шаг важен сам по себе, и два из них раньше приходилось
    делать вручную, где легко ошибиться.

      * Тесты идут перед публикацией: обновление у пользователя уже умеет ставить
        новую версию, и собрать её, не прогнав тесты, — значит отдать пользователю
        заведомый брак.
      * Каталог публикации очищается ПЕРЕД публикацией. dotnet publish файлы, которые
        больше не производит, не удаляет, а zip собирается из того, что лежит в
        каталоге: без очистки в архив попадал бы мусор прошлой сборки (старые dll,
        pdb, прошлая версия exe).
      * Результат проверяется до архива: нет SoundMeeter.exe — падать сразу, а не
        выпускать архив, который UpdateApplier потом отвергнет.
      * Сжимает zip системным tar (bsdtar в Windows 10+), а не Compress-Archive:
        exe уже сжат внутри бандла, второй проход Deflate даёт почти ничего и
        занимает минуту на файле такого размера. Нет tar — откат на Compress-Archive.

.EXAMPLE
    .\publish.ps1
    Тесты, публикация Release и artifacts\SoundMeeter.zip.

.EXAMPLE
    .\publish.ps1 -SkipTests -OutputPath D:\rel
    Без тестов, архив в D:\rel\SoundMeeter.zip.
#>
[CmdletBinding()]
param(
    # Куда складывать результат. Каталог очищается целиком, публикация уходит в
    # подкаталог publish, архив SoundMeeter.zip кладётся рядом с ним — не внутрь
    # публикации, иначе архив попадёт сам в себя.
    [string] $OutputPath = 'artifacts',

    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',

    # Имя архива. Имя exe внутри и имя архива — часть контракта релиза:
    # UpdateApplier ищет SoundMeeter.exe, автообновление качает zip-ассет релиза.
    [string] $ArchiveName = 'SoundMeeter.zip',

    # Пропустить тесты. Для сборки «на посмотреть» — можно, для релиза — нет.
    [switch] $SkipTests
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Проверка состава архива идёт через ZipFile, а в Windows PowerShell 5.1 сборка с
# ним не подгружена заранее. Файл сам сохранён в UTF-8 С BOM: без метки порядка
# байтов 5.1 читает .ps1 как ANSI и ломает парсер на кириллице.
Add-Type -AssemblyName System.IO.Compression.FileSystem

function Write-Step([string] $text) {
    Write-Host ''
    Write-Host "==> $text" -ForegroundColor Cyan
}

function Format-Size([long] $bytes) {
    $units = 'KB', 'MB', 'GB'
    $value = [double] $bytes
    $unit = -1
    do {
        $value /= 1024
        $unit++
    } while ($value -ge 1024 -and $unit -lt ($units.Count - 1))
    '{0:0.#} {1}' -f $value, $units[$unit]
}

# Корень репозитория — на уровень выше каталога скрипта, чтобы запуск из любого
# каталога давал одинаковый результат.
$repoRoot = Split-Path -Parent $PSCommandPath
$project = Join-Path $repoRoot 'src\SoundMeeter.App\SoundMeeter.App.csproj'

# Каталог артефактов и каталог публикации внутри него — разные вещи: архив
# кладётся рядом с публикацией, а не внутрь неё (иначе zip попадает сам в себя).
$artifactsDir = if ([System.IO.Path]::IsPathRooted($OutputPath)) {
    $OutputPath
} else {
    Join-Path $repoRoot $OutputPath
}
$publishDir = Join-Path $artifactsDir 'publish'
$archive = Join-Path $artifactsDir $ArchiveName

if (-not (Test-Path -LiteralPath $project)) {
    throw "Не найден проект: $project"
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'dotnet не найден в PATH: нужен .NET SDK (разрабатывалось на 10.0.401).'
}

Push-Location $repoRoot
try {
    if (-not $SkipTests) {
        Write-Step 'Тесты'
        dotnet test src\SoundMeeter.slnx -c $Configuration --nologo -v q
        if ($LASTEXITCODE -ne 0) { throw "Тесты упали (код $LASTEXITCODE) — публикация отменена." }
    } else {
        Write-Step 'Тесты пропущены (-SkipTests)'
    }

    # Очистка до публикации: publish не удаляет то, что больше не производит,
    # а именно старые файлы и есть содержимое будущего архива.
    Write-Step "Очищаю каталог артефактов: $artifactsDir"
    if (Test-Path -LiteralPath $artifactsDir) {
        Remove-Item -LiteralPath $artifactsDir -Recurse -Force
    }
    New-Item -ItemType Directory -Path $publishDir -Force | Out-Null

    Write-Step "Публикация ($Configuration, профиль portable) -> $publishDir"
    # -p:PublishProfile=portable: без него публикация молча собирает старый
    # многофайловый выход (SDK предупредит SM0001).
    dotnet publish $project -p:PublishProfile=portable -c $Configuration -o $publishDir --nologo -v m
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish упал (код $LASTEXITCODE)." }

    # Проверка результата до архива: UpdateApplier всё равно отвергнет пустой или
    # неполный архив, но узнать об этом дешевле здесь.
    $exe = Join-Path $publishDir 'SoundMeeter.exe'
    if (-not (Test-Path -LiteralPath $exe)) {
        throw "Публикация без SoundMeeter.exe — архив не собираю. Смотрите вывод dotnet publish."
    }

    $exeSize = (Get-Item -LiteralPath $exe).Length
    $files = @(Get-ChildItem -LiteralPath $publishDir -Recurse -File)
    Write-Step 'Состав публикации'
    $files | ForEach-Object {
        '    {0,10}  {1}' -f (Format-Size $_.Length), $_.FullName.Substring($publishDir.Length).TrimStart('\')
    }

    # Однофайловой контракт релиза: exe должен быть крупным (внутри рантайм),
    # а сборочных файлов рядом быть не должно.
    $assemblies = @($files | Where-Object { $_.Extension -in '.dll', '.pdb' })
    if ($assemblies.Count -gt 0) {
        throw "Рядом с exe остались сборочные файлы: $($assemblies.Name -join ', '). " +
        'Публикация должна быть одним файлом (профиль portable).'
    }
    if ($exeSize -lt 8MB) {
        throw "SoundMeeter.exe всего $(Format-Size $exeSize) — это не self-contained single-file."
    }

    Write-Step "Архив: $archive"
    $tar = Get-Command tar -ErrorAction SilentlyContinue
    if ($tar) {
        # tar на Windows (bsdtar) сам выбирает zip по расширению .zip. Перечисляем
        # содержимое каталога, а не «.»: с точкой в архив попадают записи вида
        # «./SoundMeeter.exe», и UpdateApplier не находит exe в корне архива.
        $items = @(Get-ChildItem -LiteralPath $publishDir | ForEach-Object { $_.Name })
        Push-Location $publishDir
        try {
            & $tar.Source -a -c -f $archive @items
            if ($LASTEXITCODE -ne 0) { throw "tar не собрал архив (код $LASTEXITCODE)." }
        } finally {
            Pop-Location
        }
    } else {
        # Запасной путь: Compress-Archive кладёт содержимое без ведущей точки,
        # но на файле в 70 МБ жмёт заметно дольше (внутри уже сжатый бандл).
        Compress-Archive -Path (Join-Path $publishDir '*') -DestinationPath $archive -Force
    }

    # Проверка архива: zip без exe UpdateApplier отвергнет, а запись вида
    # «./SoundMeeter.exe» он не найдёт в корне payload.
    $entries = [System.IO.Compression.ZipFile]::OpenRead($archive)
    try {
        # Записи каталогов (Resources/) в счёт файлов не идут: их видно и глазом,
        # а в сводке нужен состав файлов.
        $names = @($entries.Entries |
            Where-Object { $_.Name } |
            ForEach-Object { $_.FullName })
    } finally {
        $entries.Dispose()
    }

    if ($names -notcontains 'SoundMeeter.exe') {
        throw "В архиве нет SoundMeeter.exe в корне. Состав: $($names -join ', ')"
    }
    if ($names -contains $ArchiveName) {
        throw "В архив попал сам архив ($ArchiveName) — он не должен лежать рядом с публикацией."
    }

    $archiveItem = Get-Item -LiteralPath $archive
    $hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash

    Write-Step 'Готово'
    Write-Host "    архив     : $archive  ($(Format-Size $archiveItem.Length))"
    Write-Host "    файлов    : $($names.Count) ($($names -join ', '))"
    Write-Host "    sha256    : $hash"
    Write-Host ''
    Write-Host '    Установка вручную: распакуйте архив в отдельный каталог и запустите SoundMeeter.exe.'
    Write-Host '    Автообновление: прикрепите архив к GitHub Releases — сервис выбирает zip-ассет сам.'
} finally {
    Pop-Location
}