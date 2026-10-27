@echo off
rem Build MemoryClear.exe using the C# compiler that ships with .NET Framework.
rem No Visual Studio or .NET SDK required (only Windows + .NET Framework 4.x).
rem Note: csc must be invoked with relative file names from this directory;
rem passing an absolute path containing spaces fails with "cannot open source file".
setlocal
cd /d "%~dp0"
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (
  echo csc.exe not found - is .NET Framework 4.x installed?
  pause
  exit /b 1
)
"%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ /win32manifest:app.manifest /out:MemoryClear.exe ^
  /reference:System.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Management.dll ^
  MemoryClear.cs
echo.
if errorlevel 1 (echo BUILD FAILED) else (echo BUILD OK: %~dp0MemoryClear.exe)
pause
