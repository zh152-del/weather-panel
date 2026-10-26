@echo off
chcp 936 >nul
title WeatherPanel 打包安装程序
cd /d "%~dp0"

where dotnet >nul 2>nul
if errorlevel 1 ( echo [错误] 找不到 dotnet，请先安装 .NET 8 SDK & pause & exit /b 1 )
where wix >nul 2>nul
if errorlevel 1 ( echo [错误] 找不到 wix，请执行: dotnet tool install -g wix & pause & exit /b 1 )

echo ==========================================
echo   WeatherPanel  一键打包成安装程序
echo ==========================================
echo.
echo [1/4] 发布程序（自带运行时，对方电脑不用装 .NET）...
if exist dist rmdir /s /q dist
dotnet publish ..\src -c Release -r win-x64 --self-contained true -p:PublishReadyToRun=true -o dist --nologo -v m
if errorlevel 1 goto fail

echo.
echo [2/4] 组装安装内容...
if exist payload rmdir /s /q payload
robocopy dist payload /E /NFL /NDL /NJH /NJS >nul
if errorlevel 8 goto fail
copy /y README.txt payload\README.txt >nul

echo [3/4] 同步源码副本 + 生成安装清单...
robocopy ..\src payload\source *.cs *.xaml *.csproj *.png /E /XD bin obj dist publish /NFL /NDL /NJH /NJS >nul
if errorlevel 8 goto fail
python gen_wxs.py
if errorlevel 1 goto fail

echo [4/5] 生成 MSI 安装包...
if not exist 输出 mkdir 输出
wix build Package.wxs -ext WixToolset.UI.wixext -o "输出\WeatherPanel-Setup-1.0.0.msi"
if errorlevel 1 goto fail

echo [5/5] 生成 EXE 安装包（带图标，Inno Setup）...
set "ISCC=%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe"
if not exist "%ISCC%" set "ISCC=C://Program Files (x86)\Inno Setup 6\ISCC.exe"
if not exist "%ISCC%" (
    echo [警告] 没找到 Inno Setup，跳过 exe 安装包，只保留 msi
) else (
    "%ISCC%" /Q app.iss
    if errorlevel 1 goto fail
)

echo.
echo 完成：
echo   输出\WeatherPanel-Setup-1.0.0.exe   ^(推荐分享这个，带图标，双击即装^)
echo   输出\WeatherPanel-Setup-1.0.0.msi   ^(备选，标准 MSI^)
echo 都是用户级安装，不需要管理员权限
echo.
pause
exit /b 0

:fail
echo.
echo [错误] 打包失败，请看上面的报错信息。
pause
exit /b 1
