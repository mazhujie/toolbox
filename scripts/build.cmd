@echo off
setlocal
pushd "%~dp0.."
if errorlevel 1 exit /b 1
if not exist bin mkdir bin
call scripts\compile.cmd /target:winexe /main:Program /optimize+ /out:bin\Toolbox.exe /recurse:src\*.cs
set "TOOLBOX_BUILD_RESULT=%ERRORLEVEL%"
if "%TOOLBOX_BUILD_RESULT%"=="0" echo Build succeeded: bin\Toolbox.exe
popd
exit /b %TOOLBOX_BUILD_RESULT%
