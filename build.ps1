#requires -Version 5.1
<#
  发布脚本：全量测试 → 单文件发布 → 打交付物 zip → 打开真 zip 核对条目 → 交付一致性核对。

  两条与"同学拿到的那份东西"直接相关的规矩：
  ① 每一步 dotnet 之后都查 $LASTEXITCODE。dotnet 是原生命令行程序，$ErrorActionPreference
     管不住它的非零退出码 —— 不查的话测试失败照样往下发布，绿色进度条能把人骗过去。
  ② 这里**不跑** `ZutWifi.exe --selftest`。那一步会真向门户提交一次登录，而打包机上既没配置
     账号也不在校园网里，看到的只会是"①② 报问题、⑥⑦ 跳过、退出码 1"：什么也没测到，
     还平白多一个"发布脚本自己给自己发红"的来源。自检是同学在自己机器上做的事，见 使用说明.txt。
     交付物本身对不对，由 Task19DeliveryTests 那组离线用例核对（最后一行）。
#>
$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot

$Published = 'publish/ZutWifi.exe'
# zip 的内容就是这三样，一个不多一个不少：Task19DeliveryTests 会把这份名单逐字钉住。
$Payload = @('dist/ZutWifi.exe', 'dist/使用说明.txt', 'dist/自检.bat')

# ① 之前先把上一次的产物清掉。那一步里有一条用例是"zip 里的那两份文字 == 磁盘上的这一份"，
# 留着旧 zip 就等于拿旧说明给新说明作证：改过 dist/ 里的文字时它必然对不上（假红），
# 而一旦有人为了让它绿而去放宽断言，它就永久变成假绿。先删掉，让闸门只认真产物。
Remove-Item -LiteralPath 'dist/ZutWifi.zip', 'dist/ZutWifi.exe' -Force -ErrorAction SilentlyContinue

# ── ① 全量测试 ──
dotnet test -c Release --nologo
if ($LASTEXITCODE) { throw "测试未通过（exit $LASTEXITCODE），不发布" }

# ── ② 单文件 self-contained 发布 ──
# 发布属性写在命令行上而不是 csproj 里：这一轮的改动面只有 dist/ 与 build.ps1（src/ 下另有其在途改动）。
# 将来把它们挪进 <PropertyGroup> 时，两边只留一处，别让两份真值各自漂移。
dotnet publish src/ZutWifi -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true -p:DebugType=none -p:DebugSymbols=false `
    -o publish
if ($LASTEXITCODE) { throw "dotnet publish 失败（exit $LASTEXITCODE），不打包" }
if (-not (Test-Path -LiteralPath $Published)) {
    throw "publish 目录里没有 $Published：单文件发布根本没产出 exe，别看进度条，看上面那段输出"
}

# ── ③ 交付物：exe + 两份说明，打进 dist/ZutWifi.zip ──
New-Item -ItemType Directory -Force dist | Out-Null
Copy-Item -LiteralPath $Published -Destination dist/ -Force
foreach ($p in $Payload) {
    # 缺哪个就在这一步立刻说，而不是让 Compress-Archive 打出一个少了说明页的 zip。
    if (-not (Test-Path -LiteralPath $p)) { throw "交付物缺文件：$p（zip 里必须正好是这三样）" }
}
Compress-Archive -Path $Payload -DestinationPath dist/ZutWifi.zip -Force
if (-not (Test-Path -LiteralPath 'dist/ZutWifi.zip')) { throw 'dist/ZutWifi.zip 没生成' }

# ── ④ 把真产物打开来看一眼 ──
# 这一步不能省：-Force 保证 zip 一定生成，所以"打包成功"这件事本身没有信息量，
# 量在条目名单上 —— 少一页、多一页、套了一层目录，都只有打开 zip 才看得见。
Add-Type -AssemblyName System.IO.Compression.FileSystem
$entries = @()
$archive = [System.IO.Compression.ZipFile]::OpenRead((Resolve-Path 'dist/ZutWifi.zip'))
try { $entries = @($archive.Entries | ForEach-Object { $_.FullName }) } finally { $archive.Dispose() }
if ($entries.Count -ne 3) { throw "zip 里应该是 3 个条目，实际 $($entries.Count) 个：$($entries -join '、')" }
foreach ($e in $entries) {
    $leaf = [System.IO.Path]::GetFileName($e)
    # 名字对不上、或者多套了一层目录（PowerShell 的 -Path 指到目录时会这样），都算打错了包。
    if (-not (@('ZutWifi.exe', '使用说明.txt', '自检.bat') -contains $leaf) -or ($e -ne $leaf)) {
        throw "zip 里有意外成员：$e"
    }
}
# 主名按后缀判，不按"包含"判：ZutWifi.exe.bak 那种东西不该被当成通过了。
if (-not ($entries | Where-Object { $_.EndsWith('ZutWifi.exe') })) { throw 'zip 里没有以 ZutWifi.exe 结尾的主程序条目' }

# ── ⑤ 按真产物再核对一遍交付物与说明（离线：不联网、不动门户）──
dotnet test -c Release --nologo --filter 'FullyQualifiedName~Task19DeliveryTests'
if ($LASTEXITCODE) { throw "交付一致性核对未通过（exit $LASTEXITCODE）：说明或 zip 名单与代码对不上了" }

$exeMb = (Get-Item -LiteralPath 'dist/ZutWifi.exe').Length / 1MB
"{0:N1} MB  exe / {1:N1} MB  zip" -f $exeMb, ((Get-Item -LiteralPath 'dist/ZutWifi.zip').Length / 1MB)
if ($exeMb -lt 40 -or $exeMb -gt 80) {
    Write-Warning "exe 体积 $([math]::Round($exeMb,1)) MB 落在预期的 40–80 MB 之外：确认一下 self-contained / 压缩参数有没有丢"
}
