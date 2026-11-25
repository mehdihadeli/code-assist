@echo off
setlocal

set "PROJECT=%~dp0src\CodeAssist.Cli\CodeAssist.Cli.csproj"

dotnet run --project "%PROJECT%" --no-restore -v:q -- %*
exit /b %ERRORLEVEL%