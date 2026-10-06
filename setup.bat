@echo off
setlocal
cd /d "%~dp0"
if defined ISCC goto compiler_found
for /f "delims=" %%I in ('where ISCC.exe 2^>nul') do set "ISCC=%%I"
if defined ISCC goto compiler_found
for %%V in (7 6) do (
    if exist "%ProgramFiles%\Inno Setup %%V\ISCC.exe" set "ISCC=%ProgramFiles%\Inno Setup %%V\ISCC.exe"
    if exist "%ProgramFiles(x86)%\Inno Setup %%V\ISCC.exe" set "ISCC=%ProgramFiles(x86)%\Inno Setup %%V\ISCC.exe"
    if exist "%LOCALAPPDATA%\Programs\Inno Setup %%V\ISCC.exe" set "ISCC=%LOCALAPPDATA%\Programs\Inno Setup %%V\ISCC.exe"
)
if not defined ISCC (
    echo Install Inno Setup 6.2 or newer, or set ISCC to the full path of ISCC.exe.
    exit /b 1
)
:compiler_found
if not exist "%ISCC%" (
    echo ISCC.exe not found: "%ISCC%"
    exit /b 1
)
call publish.bat
if errorlevel 1 exit /b 1
"%ISCC%" installer\XamppRecovery.iss
if errorlevel 1 exit /b 1
exit /b 0
