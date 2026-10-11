@echo off
rem Builds ZawHTTP (Release) and runs it. Extra arguments are passed to the app, e.g.:
rem   run.bat --no-browser --port 5480
setlocal
cd /d "%~dp0"

where dotnet >nul 2>nul
if errorlevel 1 (
    echo The .NET SDK was not found. Install .NET 10 from https://dotnet.microsoft.com/download
    pause
    exit /b 1
)

echo Building ZawHTTP...
dotnet build src\ZawHTTP\ZawHTTP.csproj -c Release -nologo -v q
if errorlevel 1 (
    echo.
    echo Build failed.
    pause
    exit /b 1
)

dotnet src\ZawHTTP\bin\Release\net10.0\ZawHTTP.dll %*
if errorlevel 1 pause
