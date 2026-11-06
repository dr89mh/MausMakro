@echo off
rem Baut MausMakro.exe mit dem C#-Compiler, der bei Windows (.NET Framework 4.8) dabei ist.
cd /d "%~dp0"
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /codepage:65001 /target:winexe /optimize+ ^
  /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll /r:System.Core.dll ^
  /out:MausMakro.exe MausMakro.cs
if errorlevel 1 pause
