@echo off
setlocal
set "TOOLBOX_CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%TOOLBOX_CSC%" set "TOOLBOX_CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
"%TOOLBOX_CSC%" @scripts\compiler.rsp %*
exit /b %ERRORLEVEL%
