@echo off
setlocal
pushd "%~dp0.."
if errorlevel 1 exit /b 1
if not exist bin mkdir bin
set "TOOLBOX_CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%TOOLBOX_CSC%" set "TOOLBOX_CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist bin\adb-fixture mkdir bin\adb-fixture
"%TOOLBOX_CSC%" /nologo /utf8output /codepage:65001 /target:exe /out:bin\adb-fixture\adb.exe tests\FakeAdb.cs
if errorlevel 1 (popd & exit /b 1)
"%TOOLBOX_CSC%" /nologo /utf8output /codepage:65001 /target:winexe /main:ToolboxUiTests /out:bin\UiTests.exe /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Net.Http.dll /reference:System.Web.Extensions.dll NetSpeed.cs IpInfo.cs LanScan.cs SerialAssistant.cs ToolShell.cs Communication.cs SocketSession.cs SocketPage.cs SocketTests.cs AdbClient.cs AdbWorkspace.cs AdbBrowser.cs PacketCapture.cs PacketDecoder.cs CapturePage.cs tests\UiTests.cs tests\AdbTests.cs tests\CaptureTests.cs
if errorlevel 1 (popd & exit /b 1)
start /wait "" bin\UiTests.exe
set "TOOLBOX_TEST_RESULT=%ERRORLEVEL%"
type bin\test-results\ui-test.txt
popd
exit /b %TOOLBOX_TEST_RESULT%
