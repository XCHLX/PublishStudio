#Requires -Version 5.1
<#
    PublishStudio 一键发版脚本

    把「改版本号 → 提交 → 打 tag → 推送」合成一条命令。
    好处是 tag 指向的那次提交里，csproj 就已经是目标版本——本地 F5 启动、
    CI 构建出来的包、Release 页上的版本号三者一致，不会再出现「本地显示 V0.4.0、
    发布出来是 V0.0.6」。

    用法：
        powershell -ExecutionPolicy Bypass -File .\release.ps1 -Version 0.0.7
        powershell -ExecutionPolicy Bypass -File .\release.ps1 -Version 0.0.7 -SkipPush
        powershell -ExecutionPolicy Bypass -File .\release.ps1 -Version 0.0.7 -Message "feat: update checker"

    提交说明默认是 "chore: release v<版本>"。也可以用 -Message 写中文，
    git 里存的是 UTF-8、GitHub 上显示正常，只是本机控制台回显可能会乱码。

    做的事：
        1. 校验版本格式（三段式、不带 v 前缀）和 tag 是否已存在（本地 + 远端）
        2. 要求工作区干净——版本号改动单独成一次提交，别和业务改动混在一起
        3. 改 PublishStudio.csproj 的 Version / FileVersion / InformationalVersion
        4. git commit → git tag v<版本> → git push 分支和 tag
        5. tag 推上去后 GitHub Actions 自动构建三个包并创建 Release

    不想推送（先本地看看）就加 -SkipPush，之后手动执行：
        git push origin <分支>
        git push origin v<版本>
#>

param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version,

    [string]$Message = "",

    [switch]$SkipPush
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $root "PublishStudio.csproj"
$tag = "v$Version"

if (-not $Message) { $Message = "chore: release $tag" }

# 统一跑 git：把 stderr 一起收进字符串，失败时抛出可读的报错。
function Invoke-Git {
    param(
        [Parameter(Mandatory = $true)][string[]]$Arguments
    )

    # git 会把进度写到 stderr（比如 push），在 Stop 模式下 2>&1 会被当成异常抛出来。
    # 这里局部改成 Continue，并把 ErrorRecord 拍平成纯文本，避免日志里混进 PowerShell 的位置信息。
    $ErrorActionPreference = 'Continue'
    $output = (& git @Arguments 2>&1 | ForEach-Object {
        if ($_ -is [System.Management.Automation.ErrorRecord]) { $_.Exception.Message } else { $_ }
    } | Out-String).Trim()

    if ($LASTEXITCODE -ne 0) {
        throw "git $($Arguments -join ' ') 失败（退出码 $LASTEXITCODE）：`n$output"
    }

    return $output
}

Push-Location $root

try {
    # ---------- 0. 环境与状态检查 ----------
    if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
        throw "找不到 git。请安装 Git，或把 git.exe 所在目录加进 PATH 后重试。"
    }

    if (-not (Test-Path $project)) {
        throw "找不到 PublishStudio.csproj：$project"
    }

    # 远端与当前分支：推送时要用
    $remote = Invoke-Git @('remote')
    if (-not $remote) {
        throw "当前仓库没有配置远程（remote），先在项目里 git remote add origin <地址>。"
    }

    $branch = Invoke-Git @('rev-parse', '--abbrev-ref', 'HEAD')
    if ($branch -eq 'HEAD') {
        throw "当前处于游离 HEAD 状态，先切回分支（例如 git switch main）再发版。"
    }

    # 工作区必须干净：版本号改动要单独成为一次提交
    $dirty = Invoke-Git @('status', '--porcelain')
    if ($dirty) {
        throw "工作区有未提交的改动，先提交或撤销再发版：`n$dirty"
    }

    # tag 不能重名，本地和远端都要查
    if (Invoke-Git @('tag', '--list', $tag)) {
        throw "本地已存在 tag $tag。换个版本号，或先删除旧 tag（git tag -d $tag）。"
    }

    if (Invoke-Git @('ls-remote', '--tags', 'origin', "refs/tags/$tag")) {
        throw "远端已存在 tag $tag。换个版本号，或先在 GitHub 上删掉它和对应的 Release。"
    }

    # ---------- 1. 改 csproj 三行 ----------
    $before = [System.IO.File]::ReadAllText($project)

    foreach ($name in @('Version', 'FileVersion', 'InformationalVersion')) {
        $count = ([regex]::Matches($before, "<$name>")).Count
        if ($count -ne 1) {
            throw "csproj 里 <$name> 出现 $count 次（预期 1 次），格式可能被改过，请手动检查。"
        }
    }

    $after = [regex]::Replace($before, '<Version>[^<]*</Version>', "<Version>$Version</Version>")
    $after = [regex]::Replace($after, '<FileVersion>[^<]*</FileVersion>', "<FileVersion>$Version.0</FileVersion>")
    $after = [regex]::Replace($after, '<InformationalVersion>[^<]*</InformationalVersion>', "<InformationalVersion>$Version</InformationalVersion>")

    if ($after -eq $before) {
        Write-Host "[skip] csproj 已经是 $Version，不用改。" -ForegroundColor Yellow
    }
    else {
        # UTF-8 无 BOM，与仓库现有文件一致；换行由原文带过来，这里不重新拼。
        [System.IO.File]::WriteAllText($project, $after, (New-Object System.Text.UTF8Encoding($false)))
        Write-Host "[ok] csproj 版本号改为 $Version" -ForegroundColor Green
    }

    # ---------- 2. 提交并打 tag ----------
    Invoke-Git @('add', 'PublishStudio.csproj') | Out-Null
    $commit = Invoke-Git @('commit', '-m', $Message)
    Write-Host $commit

    Invoke-Git @('tag', $tag) | Out-Null
    Write-Host "[ok] 已打 tag $tag（指向 $branch 当前提交）" -ForegroundColor Green

    # ---------- 3. 推送 ----------
    if ($SkipPush) {
        Write-Host "[skip] 未推送。确认无误后手动执行：" -ForegroundColor Yellow
        Write-Host "       git push origin $branch"
        Write-Host "       git push origin $tag"
        return
    }

    Invoke-Git @('push', 'origin', $branch) | Write-Host
    Invoke-Git @('push', 'origin', $tag) | Write-Host

    Write-Host "[ok] 已推送 $branch 和 $tag，GitHub Actions 会自动构建三个包。" -ForegroundColor Green
    Write-Host "     进度：https://github.com/XCHLX/PublishStudio/actions"
}
finally {
    Pop-Location
}
