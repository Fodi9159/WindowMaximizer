@echo off
cd /d "%~dp0WindowStateKeeper"
dotnet build -c Release
if errorlevel 1 (
    echo.
    echo BUILD FAILED
    pause
    exit /b 1
)
echo.
echo Build OK: %~dp0WindowStateKeeper\bin\Release\net8.0-windows\WindowStateKeeper.exe
explorer "%~dp0WindowStateKeeper\bin\Release\net8.0-windows"