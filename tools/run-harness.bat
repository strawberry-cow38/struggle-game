@echo off
REM Runs the scripted harness scenario in windowed mode so screenshots
REM land alongside the JSON log + final report. Output dir is printed
REM on stdout (defaults to %APPDATA%\Godot\app_userdata\Struggle Game\harness\<stamp>\).
REM
REM Usage:
REM   tools\run-harness.bat                     - default scenario, windowed
REM   tools\run-harness.bat quick               - short scenario
REM   tools\run-harness.bat stress              - rings-of-blueprints stress
REM   tools\run-harness.bat default headless    - no rendering, no screenshots
REM
REM Add a 3rd arg `out=<abs path>` to redirect the output dir, eg:
REM   tools\run-harness.bat default windowed out=C:\harness-out\run1
REM
SETLOCAL
SET SCENARIO=%1
IF "%SCENARIO%"=="" SET SCENARIO=default
SET MODE=%2
IF "%MODE%"=="" SET MODE=windowed
SET OUTARG=
IF NOT "%3"=="" SET OUTARG=--harness-out=%~3

SET GODOT_EXE=Godot_v4.6.2-stable_mono_win64.exe
WHERE %GODOT_EXE% >NUL 2>NUL
IF ERRORLEVEL 1 SET GODOT_EXE=godot.exe

PUSHD %~dp0\..
IF "%MODE%"=="headless" (
    %GODOT_EXE% --path . --harness=%SCENARIO% %OUTARG% --headless
) ELSE (
    %GODOT_EXE% --path . --harness=%SCENARIO% %OUTARG%
)
POPD
