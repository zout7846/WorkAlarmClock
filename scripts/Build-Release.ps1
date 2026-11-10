[CmdletBinding()]
param([string]$InnoCompiler)
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Push-Location $repoRoot
try {
    [xml]$project = Get-Content 'WorkAlarmClock/WorkAlarmClock.csproj'
    $version = [string]$project.SelectSingleNode('/Project/PropertyGroup/Version').InnerText
    if ($version -notmatch '^\d+\.\d+\.\d+$') { throw '项目版本必须为 major.minor.patch。' }
    if (-not $InnoCompiler) {
        $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
        if ($command) { $InnoCompiler = $command.Source }
        else {
            $InnoCompiler = @(
                "${env:ProgramFiles(x86)}/Inno Setup 6/ISCC.exe",
                "$env:ProgramFiles/Inno Setup 7/ISCC.exe",
                "D:/Program Files (x86)/Inno Setup 6/ISCC.exe",
                "$repoRoot/artifacts/tools/inno/ISCC.exe"
            ) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
        }
    }
    if (-not $InnoCompiler -or -not (Test-Path -LiteralPath $InnoCompiler)) { throw '请安装 Inno Setup 6.7 或更新版本，或传入 -InnoCompiler 路径。' }
    $publishRoot = Join-Path $repoRoot 'artifacts/win-x64'
    $releaseRoot = Join-Path $repoRoot 'artifacts/release'
    # Only delete the fixed generated publish directory, never user-selected paths.
    if (Test-Path -LiteralPath $publishRoot) { Remove-Item -LiteralPath $publishRoot -Recurse -Force }
    New-Item -ItemType Directory -Path $publishRoot,$releaseRoot -Force | Out-Null
    dotnet publish WorkAlarmClock/WorkAlarmClock.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:DebugType=None -p:DebugSymbols=false -o $publishRoot
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish 失败。' }
    foreach ($name in @('WorkAlarmClock.exe','WorkAlarmClock.dll','zxing.dll','e_sqlite3.dll','coreclr.dll','PresentationFramework.dll')) {
        if (-not (Test-Path -LiteralPath (Join-Path $publishRoot $name))) { throw "发布依赖缺失：$name" }
    }
    & $InnoCompiler /Q "/DAppVersion=$version" 'packaging/WorkAlarmClock.iss'
    if ($LASTEXITCODE -ne 0) { throw '安装包编译失败。' }
    $zip = Join-Path $releaseRoot "WorkAlarmClock-$version-Portable-win-x64.zip"
    if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
    Compress-Archive -Path "$publishRoot/*" -DestinationPath $zip -CompressionLevel Optimal
    $installer = Join-Path $releaseRoot "WorkAlarmClock-$version-Setup-x64.exe"
    $checksum = @($installer,$zip) | ForEach-Object {
        $hash = Get-FileHash -LiteralPath $_ -Algorithm SHA256
        "$($hash.Hash.ToLowerInvariant())  $([IO.Path]::GetFileName($_))"
    }
    [IO.File]::WriteAllLines((Join-Path $releaseRoot 'SHA256SUMS.txt'), $checksum, [Text.UTF8Encoding]::new($false))
    Get-Item -LiteralPath $installer,$zip | Select-Object Name,Length
} finally { Pop-Location }
