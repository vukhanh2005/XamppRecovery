@echo off
setlocal
cd /d "%~dp0"
dotnet build XamppRecoveryTool.sln -c Release
if errorlevel 1 exit /b 1
dotnet run --project XamppRecoveryTool.Checks -c Release --no-build
if errorlevel 1 exit /b 1
exit /b 0
