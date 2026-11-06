@echo off
setlocal
pushd "%~dp0"
if errorlevel 1 exit /b 1
if not exist bin mkdir bin
set "TOOLBOX_CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%TOOLBOX_CSC%" set "TOOLBOX_CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
"%TOOLBOX_CSC%" /nologo /utf8output /codepage:65001 /target:winexe /optimize+ /out:bin\Toolbox.exe /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Net.Http.dll /reference:System.Web.Extensions.dll NetSpeed.cs IpInfo.cs LanScan.cs SerialAssistant.cs ToolShell.cs Communication.cs SocketSession.cs SocketPage.cs SocketTests.cs AdbClient.cs AdbWorkspace.cs
set "TOOLBOX_BUILD_RESULT=%ERRORLEVEL%"
if "%TOOLBOX_BUILD_RESULT%"=="0" echo Build succeeded: bin\Toolbox.exe
popd
exit /b %TOOLBOX_BUILD_RESULT%
