@echo off
setlocal
set "TOOLBOX_SUITE=%~1"
if "%TOOLBOX_SUITE%"=="" set "TOOLBOX_SUITE=all"
if /i not "%TOOLBOX_SUITE%"=="all" if /i not "%TOOLBOX_SUITE%"=="core" if /i not "%TOOLBOX_SUITE%"=="ui" (
    echo Usage: tests\run.cmd [all^|core^|ui]
    exit /b 2
)
pushd "%~dp0.."
if errorlevel 1 exit /b 1
if not exist bin mkdir bin
set "TOOLBOX_TEST_SOURCES=/recurse:src\*.cs /recurse:tests\Core\*.cs tests\Fixtures\TestServer.cs tests\Fixtures\FakeSerialTransport.cs"
if /i "%TOOLBOX_SUITE%"=="ui" goto ui
call scripts\compile.cmd /target:exe /main:TestRunner /out:bin\CoreTests.exe %TOOLBOX_TEST_SOURCES% tests\TestRunner.cs
if errorlevel 1 goto done
bin\CoreTests.exe --self-test
if errorlevel 1 goto done
if /i "%TOOLBOX_SUITE%"=="core" goto done
:ui
if not exist bin\adb-fixture mkdir bin\adb-fixture
call scripts\compile.cmd /target:exe /out:bin\adb-fixture\adb.exe tests\Fixtures\FakeAdb.cs
if errorlevel 1 goto done
call scripts\compile.cmd /target:winexe /main:ToolboxUiTests /out:bin\UiTests.exe %TOOLBOX_TEST_SOURCES% /recurse:tests\UI\*.cs
if errorlevel 1 goto done
start /wait "" bin\UiTests.exe
set "TOOLBOX_TEST_RESULT=%ERRORLEVEL%"
if exist bin\test-results\ui-test.txt type bin\test-results\ui-test.txt
goto finish
:done
set "TOOLBOX_TEST_RESULT=%ERRORLEVEL%"
:finish
popd
exit /b %TOOLBOX_TEST_RESULT%
