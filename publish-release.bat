@echo off
rem ============================================================================
rem  XT1 Studio - Release publish (framework-dependent single file, win-x64)
rem
rem  Mirrors the Visual Studio publish profile "FolderProfile":
rem    Profile name            FolderProfile
rem    Configuration           Release | Any CPU
rem    Target framework        net8.0-windows
rem    Deployment mode         Framework-dependent
rem    Target runtime          win-x64
rem    Target location         bin\Release\net8.0-windows\publish\win-x64\
rem    Produce single file     ON
rem    Enable ReadyToRun       OFF
rem
rem  Usage:
rem    publish-release.bat                     publish normally
rem    publish-release.bat /clean              delete the output folder first
rem    publish-release.bat /bundle-nvtt        also ship nvtt30205.dll (~100 MB, NVIDIA's
rem                                            binary) so BC6H encoding works without a
rem                                            separate NVIDIA Texture Tools install
rem    publish-release.bat /bundle-natives     also bundle native DLLs into the
rem                                            single file (extracted to a temp
rem                                            dir on first run, so startup is
rem                                            slower - off by default)
rem    publish-release.bat /nopause            do not wait for a keypress at the end
rem
rem  Symbols (.pdb) are stripped from the publish output by the project, so the folder ships
rem  only the executable, the native glfw3.dll and the shell\ thumbnail host.
rem
rem  Switches can be combined, e.g.  publish-release.bat /clean /bundle-natives
rem ============================================================================

setlocal EnableExtensions

set "SCRIPT_DIR=%~dp0"
set "PROJECT=%SCRIPT_DIR%src\Xt1.App.Wpf\Xt1.App.Wpf.csproj"
set "OUTPUT=%SCRIPT_DIR%src\Xt1.App.Wpf\bin\Release\net8.0-windows\publish\win-x64"

set "CLEAN=0"
set "BUNDLE_NVTT=0"
set "BUNDLE_NATIVE_LIBS=0"
set "NOPAUSE=0"

:parse
if "%~1"=="" goto :parsed
if /I "%~1"=="/clean"          set "CLEAN=1"
if /I "%~1"=="/bundle-nvtt"     set "BUNDLE_NVTT=1"
if /I "%~1"=="/bundle-natives" set "BUNDLE_NATIVE_LIBS=1"
if /I "%~1"=="/nopause"        set "NOPAUSE=1"
if /I "%~1"=="-clean"          set "CLEAN=1"
if /I "%~1"=="-bundle-nvtt"     set "BUNDLE_NVTT=1"
if /I "%~1"=="-bundle-natives" set "BUNDLE_NATIVE_LIBS=1"
if /I "%~1"=="-nopause"        set "NOPAUSE=1"
shift
goto :parse
:parsed

where dotnet >nul 2>nul
if errorlevel 1 (
    echo [error] "dotnet" was not found on PATH. Install the .NET 8 SDK and try again.
    goto :fail
)

if not exist "%PROJECT%" (
    echo [error] Project not found: "%PROJECT%"
    goto :fail
)

if "%CLEAN%"=="1" (
    echo Cleaning "%OUTPUT%" ...
    if exist "%OUTPUT%" rmdir /s /q "%OUTPUT%"
)

rem ---- publish settings -------------------------------------------------------
set "ARGS=-c Release -f net8.0-windows -r win-x64"
set "ARGS=%ARGS% --self-contained false"
set "ARGS=%ARGS% -p:PublishSingleFile=true"
set "ARGS=%ARGS% -p:PublishReadyToRun=false"
set "ARGS=%ARGS% -p:PublishTrimmed=false"
set "ARGS=%ARGS% -p:SatelliteResourceLanguages=en"
if "%BUNDLE_NATIVE_LIBS%"=="1" set "ARGS=%ARGS% -p:IncludeNativeLibrariesForSelfExtract=true"
if "%BUNDLE_NVTT%"=="1" set "ARGS=%ARGS% -p:BundleNvtt=true"

echo ============================================================================
echo  Publishing XT1 Studio (Release, single file, framework-dependent, win-x64)
echo ============================================================================
echo  Project : %PROJECT%
echo  Output  : %OUTPUT%
echo  Command : dotnet publish %ARGS% -o "%OUTPUT%"
echo.

dotnet publish "%PROJECT%" %ARGS% -o "%OUTPUT%"
if errorlevel 1 (
    echo.
    echo [error] Publish failed.
    goto :fail
)

set "EXE=%OUTPUT%\XT1 Studio.exe"
if not exist "%EXE%" (
    echo.
    echo [error] Publish reported success but "%EXE%" is missing.
    goto :fail
)

echo.
echo ----------------------------------------------------------------------------
echo  Published files:
for /f "delims=" %%F in ('dir /b /a-d "%OUTPUT%" 2^>nul') do echo    %%F
echo ----------------------------------------------------------------------------
echo  Executable: "%EXE%"
echo.

rem A quick smoke test that the published build starts and finds its native tools.
echo Running published self-check...
"%EXE%" --selfcheck
if errorlevel 1 (
    echo.
    echo [warn] The published executable reported a self-check failure.
    goto :fail
)

echo.
echo [ok] Done.
goto :end

:fail
if "%NOPAUSE%"=="0" pause
endlocal
exit /b 1

:end
if "%NOPAUSE%"=="0" pause
endlocal
exit /b 0

