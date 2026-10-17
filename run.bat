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
rem  The script builds once with plain output (-tl:off disables the
rem  MSBuild terminal logger, whose live render clears the screen)
rem  and then launches the built executable directly.
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

dotnet build "%PNET_PROJ%" -v:m -nologo -tl:off
if errorlevel 1 (
    echo.
    echo [run.bat] Build failed; see the errors above.
    pause
    popd
    exit /b 1
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
