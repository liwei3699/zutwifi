@echo off
chcp 65001 >nul
cd /d "%~dp0"
REM =====================================================================
REM  这个脚本只做一件事：跑一次 ZutWifi.exe --selftest，然后把日志的位置说给你听。
REM  它会真的向校园网门户提交一次登录（除非门户已经说你在在线）；默认不发注销包。
REM  要连注销一起测：ZutWifi.exe --selftest --with-logout —— 那一步会真把当前会话踢下线。
REM  屏幕上看不见字是可能的（从资源管理器双击进来时根本没有控制台）：
REM  全过程一定写在日志里，以日志为准。
REM =====================================================================
if not exist "%~dp0ZutWifi.exe" (
  echo 找不到 %~dp0ZutWifi.exe —— 这个脚本必须和 ZutWifi.exe 放在同一个文件夹里。
  echo （这一条是脚本自己报的错，退出码 2，跟自检的结果无关。）
  pause
  exit /b 2
)
echo 正在自检：① 读设置 → ② 解密码 → ③ 读无线 → ④ 探测 → ⑥ 取内网IP → ⑦ 登录 → ⑧ 复检 → ⑨ 验证外网 → ⑩ 查日志落盘。
echo 要测到“登录”那一步，得先连上 zut-stu 并且在程序里保存过密码；缺条件的那几步会明写着“跳过”，那不是坏了。
echo.
start "" /b /wait "%~dp0ZutWifi.exe" --selftest
set RC=%ERRORLEVEL%
echo.
echo 自检结束，退出码 %RC%（0=整轮跑完且没问题，1=跑完了但发现问题，78=这一轮没测成或日志不完整）。
echo 把下面这两个文件发给帮忙看的人，它们在 %APPDATA%\ZutWifi\logs 下（各取日期最新的那份）：
echo   selftest-年月日-时分秒.log   这一次自检自己的记录，最后一行写着它的完整路径
echo   app-年月日.log               平时的运行日志，自检期间对门户的调用也落在里面
echo 这些文件里没有密码，可以放心发送。
echo 另一条路：打开主窗口点右下角“导出诊断包”，那份 zip 会把上面的日志一起打进去
echo （只有先跑过自检，包里才有 selftest 那几份）。
echo 日志目录打不开？在资源管理器地址栏里粘贴 %APPDATA%\ZutWifi\logs 然后回车。
pause
