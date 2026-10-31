@echo off
rem ============================================================
rem  Pi.NET - run the CLI from this repository (development)
rem
rem  Usage:
rem    run.bat                    start interactive mode
rem    run.bat --help             show CLI help
rem    run.bat -p "prompt"        one-shot print mode
rem    run.bat --agent "prompt"   one-shot agent mode
rem
rem  All arguments are forwarded to the Pi.NET CLI. The script
rem  changes to the repository root, so settings and sessions
rem  resolve there.
rem
rem  Fast path: pinet.exe starts in ~0.1 s, while even an
rem  incremental `dotnet build` takes ~1.5 s (and several seconds
rem  on cold MSBuild / file-indexing machines). So the script only
rem  builds when a source or project file is newer than the
rem  built executable (checked by scripts\pinet-uptodate.ps1).
rem  Set PNET_FORCE_BUILD=1 to force a full build. To rebuild
rem  manually: dotnet build src\PiNet.Cli\PiNet.Cli.csproj
rem
rem  The build runs with plain output (-tl:off disables the
rem  MSBuild terminal logger, whose live render clears the screen)
rem  and then the built executable is launched directly.
rem
rem  Tip: for interactive mode, run this script from an already
rem  open terminal window (cmd/PowerShell). A window created just
rem  for the script goes through the Windows default-terminal
rem  handoff, which on some machines can leave the console hidden
rem  or the process suspended ("black"/unusable window).
rem ============================================================
setlocal
pushd "%~dp0"

set "PNET_PROJ=src\PiNet.Cli\PiNet.Cli.csproj"
set "PNET_EXE=src\PiNet.Cli\bin\Debug\net10.0\pinet.exe"

rem Warn if a previous instance is still running: it keeps
rem bin\...\pinet.exe locked and can make the build fail.
tasklist /FI "IMAGENAME eq pinet.exe" 2>nul | find /I "pinet.exe" >nul
if not errorlevel 1 (
    echo [run.bat] Warning: another pinet.exe process is still running.
    echo [run.bat] If the build fails because the executable is locked:
    echo [run.bat]     taskkill /IM pinet.exe /F
    echo.
)

rem Skip the build when the executable is already up to date.
rem pinet-uptodate.ps1 exits 0 = up to date, otherwise (stale or
rem missing binary) rebuild. PNET_FORCE_BUILD=1 always rebuilds.
set "PNET_SKIPBUILD=0"
if not defined PNET_FORCE_BUILD (
    powershell -NoProfile -ExecutionPolicy Bypass -File scripts\pinet-uptodate.ps1 "%PNET_EXE%" >nul 2>nul
    if not errorlevel 1 set "PNET_SKIPBUILD=1"
)

if "%PNET_SKIPBUILD%"=="1" (
    echo [run.bat] pinet.exe is up to date - skipping build ^(set PNET_FORCE_BUILD=1 to rebuild^).
) else (
    dotnet build "%PNET_PROJ%" -v:m -nologo -tl:off
    if errorlevel 1 (
        echo.
        echo [run.bat] Build failed; see the errors above.
        pause
        popd
        exit /b 1
    )
)

if "%~1"=="" (
    "%PNET_EXE%" -i
) else (
    "%PNET_EXE%" %*
)
set "EXITCODE=%ERRORLEVEL%"
popd

if not "%EXITCODE%"=="0" (
    echo.
    echo [run.bat] PiNet exited with code %EXITCODE%.
    pause
)
exit /b %EXITCODE%
