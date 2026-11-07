# LYJ - 一键打包脚本
# 作用：编译 DLL → 收集部署文件 → 生成内嵌数据 payload.zip → 编译安装程序 → 输出到 打包输出\dist
# 产物命名：LYJ安装程序_V<大版本>.<小版本>.<yyMMdd-HHmm>.exe（当前 V1.0 + 打包时间）
# 用法：在项目根目录执行  .\打包输出\build_release.ps1

param(
    [string]$Major = "1",                    # 大版本号
    [string]$Minor = "0",                    # 小版本号
    [string]$SourceDeploy = ""               # 当前已调好的部署目录（含 startup/application/config）；留空则自动找
)

$ErrorActionPreference = "Stop"
# 本脚本位于 打包输出\ 下，项目根是它的上一级（$PSScriptRoot 始终是绝对路径）
$root = Split-Path -Parent $PSScriptRoot
$outDir = Join-Path $root "打包输出"
$build = Join-Path $outDir "build"
$stage = Join-Path $build "stage"
$dist = Join-Path $outDir "dist"

# ---- 版本号：V<大版本>.<小版本>.<打包时间>（年月日 6 位 + 时分）----
# 例：V1.0.261105-1431；同一版本多次打包靠时间后缀区分，不会互相覆盖。
$now = Get-Date
$stamp = $now.ToString("yyMMdd-HHmm")
$version = "{0}.{1}.{2}" -f $Major, $Minor, $stamp
# 程序集/文件版本号每段必须 <= 65534，所以用“2000 年起的第几天 + 当天第几秒/2”（经典做法），
# 版本字符串本身（V1.0.261105-1431）走 InformationalVersion，窗口标题/卸载信息读的就是它。
$fileVersion = "{0}.{1}.{2}.{3}" -f $Major, $Minor,
    [int]($now.Date - [datetime]"2000-01-01").TotalDays,
    [int](($now - $now.Date).TotalSeconds / 2)

# 部署源（可选）：只有想把当前机器上已跑通的额外文件一并带走时才需要。
# 标准件（菜单/功能区/图标/模板）全部来自 资源\，脚本不再依赖任何已部署目录，
# 免得不小心把旧版本（如 NX工具箱）的 DLL/菜单混进安装包。
if (-not $SourceDeploy -and $env:LYJ_HOME -and (Test-Path $env:LYJ_HOME)) {
    $SourceDeploy = $env:LYJ_HOME
}
if ($SourceDeploy) { Write-Host "部署源：$SourceDeploy（仅作模板来源兜底）" -ForegroundColor DarkGray }

Write-Host "== 1/5 编译 LYJ.dll ==" -ForegroundColor Cyan
& dotnet build (Join-Path $root "源码\LYJ\LYJ.csproj") -c Release -v q | Select-String -Pattern "error|已成功生成|个错误" | ForEach-Object { $_.Line }
if ($LASTEXITCODE -ne 0) { throw "LYJ.dll 编译失败（退出码 $LASTEXITCODE）" }
$dll = Join-Path $root "源码\LYJ\bin\Release\LYJ.dll"
if (-not (Test-Path $dll)) { throw "编译失败：找不到 $dll" }

Write-Host "== 2/5 收集部署文件 ==" -ForegroundColor Cyan
if (Test-Path $stage) { [System.IO.Directory]::Delete($stage, $true) }   # 用 .NET 递归删除，避免交互式确认
New-Item -ItemType Directory -Path $stage | Out-Null
New-Item -ItemType Directory -Path (Join-Path $stage "startup") | Out-Null
New-Item -ItemType Directory -Path (Join-Path $stage "application") | Out-Null
New-Item -ItemType Directory -Path (Join-Path $stage "config") | Out-Null

# startup 只放固定件：资源\菜单功能区 的 5 个标准件 + 新编译的 LYJ.dll（下面统一拷入）。
# 不再从部署目录复制 startup\*——那会把旧版本的 DLL/菜单（如 NXTools.*）一起带进安装包。

# 功能区选项卡与固定组的标准件统一放 资源\菜单功能区，保证装完第一次启动 NX 就能看到：
#   rbn_lyj.rtb              "LYJ"选项卡（引用下面三个组）
#   rbn_lyj_import.grb       图档导入组（选项卡的必需内容，缺了 NX 会报"工具条数据不充分"）
#   rbn_lyj_export.grb       图档导出组
#   rbn_lyj_cam.grb          加工组（加工助手按钮，原「NX-编程」选项卡已并入 LYJ）
#   lyj.men                  主菜单（LYJ 级联 + 各按钮，无刷新模板项）
# DLL 每次启动会按同样内容重写这些文件，两者不会跑偏。
$ribbonDir = Join-Path $root "资源\菜单功能区"
foreach ($std in @("rbn_lyj.rtb", "rbn_lyj_import.grb", "rbn_lyj_export.grb",
                   "rbn_lyj_cam.grb", "lyj.men")) {
    $p = Join-Path $ribbonDir $std
    if (-not (Test-Path $p)) { throw "缺少 资源\菜单功能区\$std" }
    Copy-Item $p (Join-Path $stage "startup") -Force
}

# 图标：资源\按钮图标 下 9 张固定按钮图标（IconGen 生成，60x60 BMP）。
# 加工策略图标（cam_op_* / cam_grp_*）与 config\BMP 素材不打包——首次启动按模板自动生成。
$iconDir = Join-Path $root "资源\按钮图标"
if (-not (Test-Path $iconDir)) { throw "缺少 资源\按钮图标：功能区按钮图标目录" }
Get-ChildItem $iconDir -Filter "lyj_*.bmp" -File |
    ForEach-Object { Copy-Item $_.FullName (Join-Path $stage "application") -Force }

# 加工模板：只放模板 prt 本身（不带图标素材、不带本机生成的时间戳/清单/图标记录）。
# 标准件是 资源\加工模板\测试.prt；没有时才回退到部署目录（$SourceDeploy 可空）。
$tpl = Join-Path $root "资源\加工模板\测试.prt"
if (-not (Test-Path $tpl) -and $SourceDeploy) {
    $tpl = Join-Path $SourceDeploy "config\测试.prt"
}
if (-not (Test-Path $tpl)) { throw "找不到加工模板：资源\加工模板\测试.prt 不存在" }
Copy-Item $tpl (Join-Path $stage "config") -Force
Write-Host "   加工模板：$tpl" -ForegroundColor DarkGray

# gen_cam_strategies.men 与 config 下的清单/时间戳/图标都由 DLL 首次启动按本机模板生成，不打包。
# 用新编译的 DLL 覆盖
Copy-Item $dll (Join-Path $stage "startup\LYJ.dll") -Force

$fileCount = (Get-ChildItem $stage -Recurse -File).Count
Write-Host "   已收集 $fileCount 个文件"

Write-Host "== 3/5 生成 payload.zip ==" -ForegroundColor Cyan
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zipPath = Join-Path $root "源码\安装器\payload.zip"
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
$zip = [System.IO.Compression.ZipFile]::Open($zipPath, 'Create')
try {
    foreach ($f in Get-ChildItem $stage -Recurse -File) {
        $rel = $f.FullName.Substring($stage.Length + 1).Replace('\', '/')
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $f.FullName, $rel, 'Optimal') | Out-Null
    }
} finally { $zip.Dispose() }
Write-Host ("   payload.zip = {0:N0} KB" -f ((Get-Item $zipPath).Length / 1KB))

Write-Host "== 4/5 编译安装程序 ==" -ForegroundColor Cyan
# 把本次版本号注入安装程序：窗口标题与卸载信息里显示 V1.0.261105-1431
& dotnet build (Join-Path $root "源码\安装器\LYJSetup.csproj") -c Release -v q `
    "/p:Version=$version" "/p:FileVersion=$fileVersion" "/p:AssemblyVersion=$fileVersion" `
    | Select-String -Pattern "error|已成功生成|个错误" | ForEach-Object { $_.Line }
if ($LASTEXITCODE -ne 0) { throw "安装程序编译失败（退出码 $LASTEXITCODE）" }
$setup = Join-Path $root "源码\安装器\bin\LYJ安装程序.exe"
if (-not (Test-Path $setup)) { throw "安装程序编译失败：找不到 $setup" }

Write-Host "== 5/5 输出到 dist ==" -ForegroundColor Cyan
if (-not (Test-Path $dist)) { New-Item -ItemType Directory -Path $dist | Out-Null }
# 产物命名规则：V<大版本>.<小版本>.<打包时间>，如 V1.0.261105-1431
# 同一天多次打包也不会互相覆盖，从文件名即可看出是哪一次出的包。
$out = Join-Path $dist ("LYJ安装程序_V{0}.exe" -f $version)
Copy-Item $setup $out -Force
$readme = Join-Path $root "文档\使用说明.txt"
if (Test-Path $readme) { Copy-Item $readme (Join-Path $dist "使用说明.txt") -Force }

Write-Host ""
Write-Host "完成！" -ForegroundColor Green
Write-Host "  安装包：$out" 
Write-Host ("  大小：{0:N1} MB，文件数：{1}" -f ((Get-Item $out).Length / 1MB), $fileCount)
