@echo off
rem DLSS5 Preset Switcher - ReShade add-on build
setlocal
if defined VCVARS goto toolchain_found
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if exist "%VSWHERE%" (
  for /f "usebackq delims=" %%I in (`"%VSWHERE%" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "VCVARS=%%I\VC\Auxiliary\Build\vcvars64.bat"
)
:toolchain_found
if not defined VCVARS (
  echo build.cmd: Visual Studio C++ tools not found; set VCVARS to vcvars64.bat
  exit /b 1
)
if not exist "%VCVARS%" (
  echo build.cmd: no vcvars64.bat at "%VCVARS%"
  exit /b 1
)
call "%VCVARS%" >nul 2>&1
if errorlevel 1 (
  echo build.cmd: failed to initialize "%VCVARS%"
  exit /b 1
)
cd /d "%~dp0"

rc /nologo version.rc
if errorlevel 1 exit /b 1

cl /nologo /W4 /O2 /MT /EHsc /std:c++17 /I. /Iimgui /Ireshade /LD ^
   dlss5-preset-switcher.cpp version.res ^
   /Fe:dlss5-preset-switcher.addon64 ^
   /link /DLL user32.lib version.lib
if errorlevel 1 exit /b 1

echo built: %~dp0dlss5-preset-switcher.addon64
endlocal
