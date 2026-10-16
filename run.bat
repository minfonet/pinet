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
rem ============================================================
setlocal
pushd "%~dp0"

if "%~1"=="" (
    dotnet run --project "src\PiNet.Cli\PiNet.Cli.csproj" -- -i
) else (
    dotnet run --project "src\PiNet.Cli\PiNet.Cli.csproj" -- %*
)

set "EXITCODE=%ERRORLEVEL%"
popd
exit /b %EXITCODE%
