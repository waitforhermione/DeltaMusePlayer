<#
.SYNOPSIS
    把 DeltaMusePlayer 仓库内容推送到 GitHub，并创建 v0.2.0 release。

.DESCRIPTION
    这台机器**连不上 github.com:443**（git push / ls-remote 均 RPC failed），
    但 api.github.com 可达。因此本脚本不使用 git push，而是走 Git Database + Releases REST API：

      1. POST /git/blobs          逐个文件上传（base64 content）
      2. POST /git/trees          组装树（含子目录与可执行位）
      3. POST /git/commits        创建唯一一个提交
      4. PATCH /git/refs/heads/main  让 main 指向该提交
      5. POST /releases           创建 v0.2.0 release
      6. POST uploads.github.com   上传 exe / LICENSE / THIRD_PARTY_NOTICES.md 资产

    这样仓库里的源码树与本地冻结快照逐字节一致，且有真实的提交历史（一个提交）。

.NOTES
    token 从环境变量 GH_TOKEN 读取，**不**写进本文件、不写进日志。
    本文件必须保存为带 BOM 的 UTF-8（含中文），否则 Windows PowerShell 5.1 会按 ANSI 解码成乱码。
#>
[CmdletBinding()]
param(
    [string]$Owner = 'waitforhermione',
    [string]$Repo  = 'DeltaMusePlayer',
    [string]$Tag   = 'v0.2.0',
    [string]$Title = 'DeltaMuse Player v0.2.0',
    [string]$AuthorName  = 'waitforhermione',
    [string]$AuthorEmail = '56627460+waitforhermione@users.noreply.github.com',
    [string[]]$CommitMessageLines = @(
        'DeltaMuse Player v0.2.0 (M2): Windows 用户态真实键鼠输入',
        '',
        'M1: MIDI → InstrumentProfile → 音高映射 → 预编译 PlaybackPlan → 预览后端',
        'M2: WindowsInputBackend（SendInput + 扫描码模式）+ 安全闸门 + 时序诊断',
        '',
        '安全设计：默认 Preview；Real Input 需一次确认框；倒计时 0/1/2/3/5/10s（默认 3）',
        '且不改动 MIDI 时间轴；REAL INPUT ACTIVE 横幅；F12 / Stop 紧急停止（无低层全局钩子）；',
        'held-state 记账只释放本程序按过的输入；异常路径 best-effort ReleaseAll；全程 asInvoker。',
        '',
        '本版修复：',
        ' * 倒计时误用「停止按钮可用性」作为取消信号，导致倒计时第一秒自我中止、派发 0 条。',
        ' * 时序诊断把倒计时预滚当成抖动，均值/p95/最大被抬到 3000ms 量级。',
        '   现按「整体偏移 = 误差最小值」与「抖动 = 扣偏后残差」分开报告。',
        '',
        '测试：177/177 通过。真实输入尚未完成 Notepad / 鼠标 / 游戏内人工验收（环境无可靠焦点控制）。'
    ),
    [switch]$SkipRelease,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
$apiBase  = 'https://api.github.com'
$uploadBase = 'https://uploads.github.com'

$token = $env:GH_TOKEN
if (-not $token) { throw '环境变量 GH_TOKEN 未设置。请在调用前设置，不要把它写进任何文件。' }

$CommitMessage = ($CommitMessageLines -join "`n")

$headers = @{
    Authorization          = "Bearer $token"
    Accept                 = 'application/vnd.github+json'
    'User-Agent'           = 'DeltaMusePlayer-release-script'
    'X-GitHub-Api-Version' = '2022-11-28'
}

function Invoke-Gh {
    param(
        [Parameter(Mandatory)][string]$Method,
        [Parameter(Mandatory)][string]$Uri,
        [object]$Body,
        [hashtable]$ExtraHeaders,
        [string]$ContentType = 'application/json'
    )
    $h = @{} + $headers
    if ($ExtraHeaders) { foreach ($k in $ExtraHeaders.Keys) { $h[$k] = $ExtraHeaders[$k] } }
    $args = @{ Method = $Method; Uri = $Uri; Headers = $h; TimeoutSec = 300 }
    if ($null -ne $Body) {
        if ($ContentType -eq 'application/json') {
            $args['Body'] = ($Body | ConvertTo-Json -Depth 12 -Compress)
            $args['ContentType'] = 'application/json; charset=utf-8'
        }
        else {
            $args['Body'] = $Body
            $args['ContentType'] = $ContentType
        }
    }
    return Invoke-RestMethod @args
}

# ---------------------------------------------------------------- 收集文件

$excludePattern = '\\(bin|obj|artifacts|TestResults|\.vs|\.git)\\'
$files = Get-ChildItem $repoRoot -Recurse -File -Force |
    Where-Object { $_.FullName -notmatch $excludePattern } |
    Sort-Object FullName

if ($files.Count -eq 0) { throw "没有找到要发布的文件：$repoRoot" }
Write-Host "==> 冻结快照：$($files.Count) 个文件，$([math]::Round((($files | Measure-Object Length -Sum).Sum / 1MB), 2)) MB"

# ---------------------------------------------------------------- 前置检查

Write-Host '==> 校验 token 与仓库'
$user = Invoke-Gh -Method GET -Uri "$apiBase/user"
Write-Host "    登录身份: $($user.login)"
$repoInfo = Invoke-Gh -Method GET -Uri "$apiBase/repos/$Owner/$Repo"
Write-Host "    目标仓库: $($repoInfo.full_name) (private=$($repoInfo.private), default=$($repoInfo.default_branch))"
if (-not $repoInfo.permissions.push) { throw "token 对该仓库没有 push 权限。" }

if ($DryRun) {
    Write-Host '==> DryRun：只列出将要上传的文件'
    $files | ForEach-Object { Write-Host ("    " + $_.FullName.Substring($repoRoot.Length + 1)) }
    return
}

# ---------------------------------------------------------------- 1) blobs

Write-Host '==> 上传 blobs'
$treeItems = New-Object System.Collections.Generic.List[object]
$manifest  = New-Object System.Collections.Generic.List[object]
$i = 0
foreach ($f in $files) {
    $i++
    $rel = $f.FullName.Substring($repoRoot.Length + 1).Replace('\', '/')
    $bytes = [System.IO.File]::ReadAllBytes($f.FullName)
    $sha256 = (Get-FileHash -LiteralPath $f.FullName -Algorithm SHA256).Hash
    $b64 = [Convert]::ToBase64String($bytes)

    $blob = Invoke-Gh -Method POST -Uri "$apiBase/repos/$Owner/$Repo/git/blobs" -Body @{
        content  = $b64
        encoding = 'base64'
    }

    $item = [ordered]@{ path = $rel; mode = '100644'; type = 'blob'; sha = $blob.sha }
    # app.manifest 与脚本保持普通文件位；exe 资产单独走 release，不进源码树。
    $treeItems.Add([pscustomobject]$item)
    $manifest.Add([pscustomobject]@{
        path   = $rel
        bytes  = $bytes.Length
        sha256 = $sha256
        blob   = $blob.sha
    })
    if ($i % 10 -eq 0 -or $i -eq $files.Count) { Write-Host "    $i / $($files.Count)" }
}

# ---------------------------------------------------------------- 2) tree

Write-Host '==> 组装 tree'
# 注意：这里必须用 .ToArray()，不能用 @($treeItems)。
# 对泛型 List[object] 用 @() 会抛 "Argument types do not match"（ArgumentException），
# 而 .ToArray() 稳定产出 object[]，JSON 序列化才正确。
$tree = Invoke-Gh -Method POST -Uri "$apiBase/repos/$Owner/$Repo/git/trees" -Body @{
    tree = $treeItems.ToArray()
}
Write-Host "    tree: $($tree.sha)  (entries=$($tree.tree.Count))"

# ---------------------------------------------------------------- 3) commit

Write-Host '==> 创建 commit'
# 空仓库必须先由 Contents API 建出第一个提交（blobs/trees API 在空仓库上返回 409
# "Git Repository is empty."）。因此这里要先取当前 main 作为父提交 ——
# 否则我们的提交没有父提交，PATCH refs 会因为 "Update is not a fast forward" 失败。
$parentSha = $null
try {
    $cur = Invoke-Gh -Method GET -Uri "$apiBase/repos/$Owner/$Repo/git/ref/heads/main"
    $parentSha = $cur.object.sha
}
catch {
    $parentSha = $null
}

$commitBody = [ordered]@{
    message   = $CommitMessage
    tree      = $tree.sha
    author    = @{ name = $AuthorName; email = $AuthorEmail; date = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ') }
    committer = @{ name = $AuthorName; email = $AuthorEmail; date = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ') }
}
if ($parentSha) { $commitBody['parents'] = @($parentSha) }

$commit = Invoke-Gh -Method POST -Uri "$apiBase/repos/$Owner/$Repo/git/commits" -Body $commitBody
Write-Host "    commit: $($commit.sha)  (parent: $(if ($parentSha) { $parentSha } else { 'none' }))"

# ---------------------------------------------------------------- 4) ref

Write-Host '==> 更新 refs/heads/main'
if ($parentSha) {
    $null = Invoke-Gh -Method PATCH -Uri "$apiBase/repos/$Owner/$Repo/git/refs/heads/main" -Body @{
        sha   = $commit.sha
        force = $true
    }
    Write-Host "    main: $parentSha -> $($commit.sha)"
}
else {
    $null = Invoke-Gh -Method POST -Uri "$apiBase/repos/$Owner/$Repo/git/refs" -Body @{
        ref = 'refs/heads/main'
        sha = $commit.sha
    }
    Write-Host '    新建分支 main'
}

# ---------------------------------------------------------------- 5) release

if ($SkipRelease) {
    Write-Host '==> 跳过 release（-SkipRelease）'
}
else {
    Write-Host "==> 创建 release $Tag"
    $notesPath = Join-Path $repoRoot 'docs\RELEASE_NOTES_v0.2.0.md'
    if (-not (Test-Path $notesPath)) { throw "找不到发布说明：$notesPath" }
    $notes = [System.IO.File]::ReadAllText($notesPath, [System.Text.Encoding]::UTF8)

    $release = Invoke-Gh -Method POST -Uri "$apiBase/repos/$Owner/$Repo/releases" -Body @{
        tag_name         = $Tag
        target_commitish = 'main'
        name             = $Title
        body             = $notes
        draft            = $false
        prerelease       = $false
    }
    Write-Host "    release: $($release.html_url)"

    # ------------------------------------------------------------ 6) assets

    $uploadUrl = $release.upload_url -replace '\{.*$', ''
    $assets = @(
        @{ path = Join-Path $repoRoot 'artifacts\win-x64\DeltaMusePlayer.exe'; name = 'DeltaMusePlayer.exe'; type = 'application/vnd.microsoft.portable-executable' },
        @{ path = Join-Path $repoRoot 'LICENSE';                                name = 'LICENSE';               type = 'text/plain' },
        @{ path = Join-Path $repoRoot 'THIRD_PARTY_NOTICES.md';                 name = 'THIRD_PARTY_NOTICES.md'; type = 'text/markdown' }
    )

    foreach ($a in $assets) {
        if (-not (Test-Path $a.path)) { throw "找不到资产：$($a.path)" }
        $len = (Get-Item -LiteralPath $a.path).Length
        Write-Host "    上传 $($a.name) ($([math]::Round($len / 1MB, 2)) MB)"
        $res = Invoke-Gh -Method POST -Uri "$uploadUrl`?name=$($a.name)" -Body ([System.IO.File]::ReadAllBytes($a.path)) -ContentType $a.type
        Write-Host "      -> $($res.browser_download_url)  size=$($res.size)"
    }
}

# ---------------------------------------------------------------- 7) 本地清单

$manifestPath = Join-Path $repoRoot 'artifacts\release-manifest.json'
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $manifestPath) | Out-Null
$manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $manifestPath -Encoding UTF8
Write-Host "==> 文件清单（含每个 blob 的 sha256）已写入 $manifestPath"

Write-Host ''
Write-Host '==> 完成'
Write-Host "    仓库:   https://github.com/$Owner/$Repo"
Write-Host "    提交:   $($commit.sha)"
Write-Host "    发布:   https://github.com/$Owner/$Repo/releases/tag/$Tag"
