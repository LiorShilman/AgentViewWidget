@echo off
rem Agent Live Widget — start server (minimized) + widget
setlocal

set ROOT=%~dp0

rem Start the state server if not already running (it exits quietly if the port is taken)
start "Agent Widget Server" /min cmd /c "cd /d %ROOT%server && node index.js"

rem Give the server a moment to bind
timeout /t 1 /nobreak >nul

set EXE=%ROOT%widget\bin\Release\net10.0-windows\AgentLiveWidget.exe
if not exist "%EXE%" (
  echo Widget binary not found. Building...
  dotnet build "%ROOT%widget\AgentLiveWidget.csproj" -c Release
)

start "" "%EXE%"
endlocal
