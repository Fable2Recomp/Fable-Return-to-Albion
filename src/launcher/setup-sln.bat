@echo off
setlocal

set SCRIPT_DIR=%~dp0
cd /d "%SCRIPT_DIR%"

set BUILD_DIR=%SCRIPT_DIR%build\vs2022

cmake -S "%SCRIPT_DIR%" -B "%BUILD_DIR%" -G "Visual Studio 17 2022" -A x64
if errorlevel 1 goto :eof

echo Generated Visual Studio solution in %BUILD_DIR%
pause
