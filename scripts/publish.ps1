<#
.SYNOPSIS
    构建 DeltaMusePlayer 的 Windows x64 自包含单文件发布包。

.DESCRIPTION
    产物：artifacts\win-x64\DeltaMusePlayer.exe（自包含、单文件、开裁剪），
    以及同目录下的 LICENSE / THIRD_PARTY_NOTICES.md（同时已嵌进 exe）。

    普通用户不需要装 .NET；这份 exe 可以直接拷走运行。

.PARAMETER Configuration
    默认 Release。

.PARAMETER NoTrim
    关掉裁剪，产出更大的 exe（排查裁剪相关问题时用）。

.EXAMPLE
    powershell -File scripts\publish.ps1
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [switch]$NoTrim
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'src\DeltaMusePlayer\DeltaMusePlayer.csproj'
$outDir = Join-Path $repoRoot 'artifacts\win-x64'

if (-not (Test-Path $project)) { throw "找不到项目文件：$project" }

Write-Host "==> 发布 $Configuration / win-x64 / self-contained / single-file"
if (Test-Path $outDir) { Remove-Item $outDir -Recurse -Force }

$publishArgs = @(
    'publish', $project,
    '-c', $Configuration,
    '-r', 'win-x64',
    '--self-contained', 'true',
    '-o', $outDir
)
if ($NoTrim) {
    Write-Host '    （--NoTrim：关掉裁剪）'
    $publishArgs += '-p:PublishTrimmed=false'
}

& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败，退出码 $LASTEXITCODE" }

$exe = Join-Path $outDir 'DeltaMusePlayer.exe'
if (-not (Test-Path $exe)) { throw "没有产出 $exe" }

Copy-Item (Join-Path $repoRoot 'LICENSE') $outDir -Force
Copy-Item (Join-Path $repoRoot 'THIRD_PARTY_NOTICES.md') $outDir -Force

$size = [math]::Round((Get-Item $exe).Length / 1MB, 1)
Write-Host ""
Write-Host "==> 完成"
Write-Host "    $exe ($size MB)"
Get-ChildItem $outDir | ForEach-Object { Write-Host ("    " + $_.Name) }
