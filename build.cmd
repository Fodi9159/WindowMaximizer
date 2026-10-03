@echo off
cd /d "%~dp0WindowMaximizer"
dotnet build -c Release
if errorlevel 1 (
    echo.
    echo BUILD FAILED
    pause
    exit /b 1
)
echo.
echo Build OK: %~dp0WindowMaximizer\bin\Release\net10.0-windows\WindowMaximizer.exe
explorer "%~dp0WindowMaximizer\bin\Release\net10.0-windows"