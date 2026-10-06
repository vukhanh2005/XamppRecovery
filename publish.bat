@echo off
setlocal
cd /d "%~dp0"
call build.bat
if errorlevel 1 exit /b 1
dotnet publish XamppRecoveryTool/XamppRecoveryTool.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o release
if errorlevel 1 exit /b 1
echo Published: %~dp0release\XamppMySqlRecoveryTool.exe
exit /b 0
