@echo off
chcp 65001 > nul
setlocal enabledelayedexpansion

echo 抖音直播推流码获取工具 - 编译脚本
echo ==============================

:: 检查是否安装了 .NET SDK
where dotnet >nul 2>nul
if %errorlevel% neq 0 (
    echo 错误: 未找到 .NET SDK，请先安装 .NET SDK 9.0
    echo 下载地址: https://dotnet.microsoft.com/download/dotnet/9.0
    pause
    exit /b 1
)

:: 检查 .NET SDK 版本
dotnet --version | findstr /r "9\.[0-9]*\.[0-9]*" >nul
if %errorlevel% neq 0 (
    echo 警告: 未检测到 .NET 9.0，当前版本:
    dotnet --version
    echo.
    set /p choice="是否继续编译？(Y/N): "
    if /i "!choice!" neq "Y" exit /b 1
)

echo.
echo 开始清理旧文件...
if exist "bin" rd /s /q "bin"
if exist "obj" rd /s /q "obj"

echo 开始还原包...
dotnet restore
if %errorlevel% neq 0 (
    echo 错误: 包还原失败
    pause
    exit /b 1
)

echo 开始编译...
dotnet publish -c Release -r win-x64 --self-contained false
if %errorlevel% neq 0 (
    echo 错误: 编译失败
    pause
    exit /b 1
)

echo.
echo 编译成功！
echo 输出目录: bin\Release\net9.0\win-x64\publish\

:: 复制配置文件
echo 复制配置文件...
if exist "config.json" (
    copy /y "config.json" "bin\Release\net9.0-windows\win-x64\" >nul
    echo - 已复制 config.json
)

if exist "config.ini" (
    copy /y "config.ini" "bin\Release\net9.0-windows\win-x64\" >nul
    echo - 已复制 config.ini
) else (
    echo [License]> "bin\Release\net9.0-windows\win-x64\config.ini"
    echo AuthUrl=http://127.0.0.1:3000/api/v1/app/key?mode=Ui5OjtoI02MY8g2-kIaL>> "bin\Release\net9.0-windows\win-x64\config.ini"
    echo - 已创建默认 config.ini
)

if not exist "bin\Release\net9.0-windows\win-x64\config.json" (
    echo {> "bin\Release\net9.0-windows\win-x64\config.json"
    echo     "obs_path": "C:\\Program Files\\obs-studio\\bin\\64bit\\obs64.exe",>> "bin\Release\net9.0-windows\win-x64\config.json"
    echo     "obs_config_path": "C:\\Users\\tool\\AppData\\Roaming\\obs-studio\\basic\\profiles\\未命名\\service.json">> "bin\Release\net9.0-windows\win-x64\config.json"
    echo }>> "bin\Release\net9.0-windows\win-x64\config.json"
    echo - 已创建默认 config.json
)

echo.
set /p run="是否立即运行程序？(Y/N): "
if /i "%run%"=="Y" (
    echo 正在启动程序...
    cd bin\Release\net9.0-windows\win-x64
    start DouyinRtmp.exe
)

echo.
pause 