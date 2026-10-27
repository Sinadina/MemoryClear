@echo off
rem 编译 MemoryClear.exe —— 只需要 Windows 自带的 .NET Framework 编译器，无需 VS / .NET SDK
rem 注意：必须 cd 到本目录用相对文件名，csc 对带空格的绝对路径会报“无法打开源文件”
setlocal
cd /d "%~dp0"
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
"%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ /win32manifest:app.manifest /out:MemoryClear.exe ^
  /reference:System.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Management.dll ^
  MemoryClear.cs
echo.
if errorlevel 1 (echo BUILD FAILED) else (echo BUILD OK: %~dp0MemoryClear.exe)
pause
