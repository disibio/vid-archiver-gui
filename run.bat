@echo off
rem Builds and runs Vid Archiver GUI. Pass "publish" to build a standalone exe into publish\win-x64 instead.
cd /d "%~dp0"

if /i "%~1"=="publish" (
    dotnet publish src\VidArchiverGui.App -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish\win-x64
    if errorlevel 1 goto failed
    echo.
    echo Built publish\win-x64\VidArchiverGui.exe
    goto :eof
)

dotnet build src\VidArchiverGui.App -c Release
if errorlevel 1 goto failed
echo Starting Vid Archiver GUI (this window closes when the app does)...
"src\VidArchiverGui.App\bin\Release\net10.0\VidArchiverGui.exe"
goto :eof

:failed
echo.
echo Build failed.
pause
exit /b 1
