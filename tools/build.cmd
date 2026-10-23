@echo off
rem Build dist\monitor-power.exe with the C# compiler that ships with .NET Framework.
rem No NuGet, no Visual Studio, no SDK.
setlocal
set ROOT=%~dp0..
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" (
  echo csc.exe not found: %CSC%
  exit /b 1
)
if not exist "%ROOT%\dist" mkdir "%ROOT%\dist"

rem /target:winexe  : double-click opens the GUI, CLI mode attaches to the parent console
rem /codepage:65001 : the source is UTF-8, keep the Chinese UI strings intact
"%CSC%" /nologo /target:winexe /optimize+ /codepage:65001 ^
  /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll ^
  /out:"%ROOT%\dist\monitor-power.exe" "%ROOT%\src\*.cs"
if errorlevel 1 (
  echo BUILD FAILED
  exit /b 1
)
echo BUILD OK
exit /b 0
