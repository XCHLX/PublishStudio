#Requires -Version 5.1
<#
    PublishStudio 一键发布脚本

    三种分发形态（-Flavor）：
      SelfContained       自包含单文件 + zip（默认），目标机免装 .NET，约 60 MB
      FrameworkDependent  框架依赖单文件 + zip，目标机需装 .NET 8 Desktop Runtime，约 2 MB
      All                 两种一起构建

    用法：
      powershell -ExecutionPolicy Bypass -File .\build-release.ps1
      powershell -ExecutionPolicy Bypass -File .\build-release.ps1 -Flavor All
      powershell -ExecutionPolicy Bypass -File .\build-release.ps1 -Flavor FrameworkDependent
      powershell -ExecutionPolicy Bypass -File .\build-release.ps1 -Version 0.05
      powershell -ExecutionPolicy Bypass -File .\build-release.ps1 -NoZip

    产物：
      dist\PublishStudio.exe             自包含单文件
      dist-fd\PublishStudio.exe          框架依赖单文件
      dist-fd\WebView2Loader.dll         框架依赖模式需随 exe 一起发
      release\PublishStudio-<版本>-win-x64.zip       自包含 zip
      release\PublishStudio-<版本>-win-x64-fd.zip    框架依赖 zip

    版本号默认从 csproj 的 <InformationalVersion> 读，和「帮助 → 关于」显示的一致。
#>

param(
    [string]$Version = "",
    [string]$Runtime = "win-x64",
    [ValidateSet("SelfContained", "FrameworkDependent", "All")]
    [string]$Flavor = "SelfContained",
    [switch]$NoZip
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $root "PublishStudio.csproj"
$dist = Join-Path $root "dist"
$distFd = Join-Path $root "dist-fd"
$release = Join-Path $root "release"
$quickstart = Join-Path $root "deploy\使用说明.txt"

if (-not (Test-Path $project)) {
    throw "找不到 PublishStudio.csproj：$project"
}

# ---------- 版本号 ----------
if ([string]::IsNullOrWhiteSpace($Version)) {
    $csproj = Get-Content $project -Raw
    if ($csproj -match '<InformationalVersion>([^<]+)</InformationalVersion>') {
        $Version = $Matches[1].Trim()
    } else {
        $Version = "0.0.0"
    }
}

$doSc = $Flavor -eq "SelfContained" -or $Flavor -eq "All"
$doFd = $Flavor -eq "FrameworkDependent" -or $Flavor -eq "All"

Write-Host ""
Write-Host "== PublishStudio release ==" -ForegroundColor Cyan
Write-Host "  version : $Version"
Write-Host "  runtime : $Runtime"
Write-Host "  flavor  : $Flavor"
Write-Host ""

function Clear-OutputDir([string]$path) {
    if (Test-Path $path) {
        Write-Host "[clean] $path"
        Remove-Item $path -Recurse -Force
    }
}

function Copy-QuickStart([string]$targetDir) {
    if (Test-Path $quickstart) {
        Copy-Item $quickstart (Join-Path $targetDir "使用说明.txt") -Force
        Write-Host "[copy] 使用说明.txt -> $targetDir"
    } else {
        Write-Host "[warn] 没找到 deploy\使用说明.txt，跳过" -ForegroundColor Yellow
    }
}

function New-Zip([string]$sourceDir, [string]$zipPath) {
    if ($NoZip) {
        Write-Host "[skip] -NoZip，不压缩"
        return
    }
    if (-not (Test-Path $release)) {
        New-Item -ItemType Directory -Path $release | Out-Null
    }
    Write-Host "[zip] $zipPath"
    Compress-Archive -Path (Join-Path $sourceDir "*") -DestinationPath $zipPath -CompressionLevel Optimal -Force
    $zipSize = [math]::Round((Get-Item $zipPath).Length / 1MB, 1)
    Write-Host "[ok] zip $zipSize MB" -ForegroundColor Green
}

# ---------- ① 自包含单文件 ----------
if ($doSc) {
    Clear-OutputDir $dist
    Write-Host "[build] dotnet publish (self-contained, single file) ..."
    & dotnet publish $project `
        -c Release `
        -r $Runtime `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true `
        -p:DebugType=none `
        -p:InformationalVersion=$Version `
        --nologo `
        -o $dist

    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish (self-contained) 失败，退出码 $LASTEXITCODE"
    }

    $exe = Join-Path $dist "PublishStudio.exe"
    if (-not (Test-Path $exe)) {
        throw "发布失败：$exe 不存在"
    }

    $loader = Join-Path $dist "WebView2Loader.dll"
    if (Test-Path $loader) {
        Write-Host "[check] WebView2Loader.dll 在 exe 旁边，zip 会带上它" -ForegroundColor Yellow
    }

    Copy-QuickStart $dist
    $size = [math]::Round((Get-Item $exe).Length / 1MB, 1)
    Write-Host "[ok] $exe ($size MB)"

    New-Zip $dist (Join-Path $release "PublishStudio-$Version-$Runtime.zip")
}

# ---------- ② 框架依赖单文件 ----------
if ($doFd) {
    Clear-OutputDir $distFd
    Write-Host "[build] dotnet publish (framework-dependent, single file) ..."
    & dotnet publish $project `
        -c Release `
        -r $Runtime `
        --self-contained false `
        -p:PublishSingleFile=true `
        -p:DebugType=none `
        -p:InformationalVersion=$Version `
        --nologo `
        -o $distFd

    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish (framework-dependent) 失败，退出码 $LASTEXITCODE"
    }

    $exeFd = Join-Path $distFd "PublishStudio.exe"
    if (-not (Test-Path $exeFd)) {
        throw "发布失败：$exeFd 不存在"
    }

    # 框架依赖的单文件模式不会把 WebView2Loader.dll 塞进 exe，必须随包一起发。
    $loaderFd = Join-Path $distFd "WebView2Loader.dll"
    if (Test-Path $loaderFd) {
        Write-Host "[check] WebView2Loader.dll 在 exe 旁边，zip 会带上它" -ForegroundColor Yellow
    } else {
        Write-Host "[warn] 没找到 WebView2Loader.dll：框架依赖版必须带上它，否则「打开发布记录」会失败" -ForegroundColor Yellow
    }

    Copy-QuickStart $distFd
    $sizeFd = [math]::Round((Get-Item $exeFd).Length / 1MB, 1)
    Write-Host "[ok] $exeFd ($sizeFd MB)"

    New-Zip $distFd (Join-Path $release "PublishStudio-$Version-$Runtime-fd.zip")
}

Write-Host ""
Write-Host "完成。产物目录：" -ForegroundColor Green
if ($doSc) { Write-Host "  $dist" }
if ($doFd) { Write-Host "  $distFd" }
if (-not $NoZip) { Write-Host "  $release" }
