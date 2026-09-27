@echo off
rem Build VeritasInjector.exe with the in-box .NET Framework 4.8 compiler (no SDK needed).
cd /d "%~dp0"
if not exist dist mkdir dist
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:exe /platform:x64 /out:dist\VeritasInjector.exe /win32manifest:app.manifest /r:System.dll /r:System.Core.dll Program.cs
if errorlevel 1 (
  echo BUILD FAILED
  exit /b 1
)
if exist "..\Inject-Veritas.ps1" echo OK: dist\VeritasInjector.exe  ^(-- copy veritas.dll next to it --^)
