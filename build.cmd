@echo off
rem Builds Zipcap.exe with the C# compiler that ships with Windows (no installs).
cd /d "%~dp0"
set FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319
"%FW%\csc.exe" /nologo /target:winexe /optimize+ /codepage:65001 ^
  /out:Zipcap.exe /win32icon:zipcap.ico ^
  /r:"%FW%\WPF\PresentationFramework.dll" /r:"%FW%\WPF\PresentationCore.dll" /r:"%FW%\WPF\WindowsBase.dll" ^
  /r:System.Xaml.dll /r:System.Net.Http.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll ^
  /r:System.Security.dll ^
  Zipcap.cs
