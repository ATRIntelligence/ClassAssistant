@echo off
rem =====================================================
rem  课堂助手 - 一键编译（不需要安装任何开发环境）
rem  使用 Windows 自带的 .NET Framework C# 编译器
rem =====================================================
setlocal
cd /d "%~dp0"

set "FWDIR=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319"
if not exist "%FWDIR%\csc.exe" set "FWDIR=%WINDIR%\Microsoft.NET\Framework\v4.0.30319"
if not exist "%FWDIR%\csc.exe" (
    echo [错误] 未找到编译器 csc.exe，系统可能不是 Windows 10 / Windows 11。
    pause
    exit /b 1
)

echo 正在编译 ClassroomAssistant.exe ...
"%FWDIR%\csc.exe" /nologo /target:winexe /platform:anycpu /optimize+ ^
    /out:ClassroomAssistant.exe ^
    /lib:"%FWDIR%" /lib:"%FWDIR%\WPF" ^
    /r:System.dll /r:System.Core.dll /r:System.Web.Extensions.dll ^
    /r:System.Drawing.dll /r:System.Windows.Forms.dll ^
    /r:WindowsBase.dll /r:PresentationCore.dll /r:PresentationFramework.dll /r:System.Xaml.dll ^
    ClassroomAssistant.cs

if errorlevel 1 (
    echo.
    echo [失败] 编译出错，请把上面显示的错误信息反馈给作者。
    pause
    exit /b 1
)

echo.
echo [成功] 已生成 ClassroomAssistant.exe
echo        双击 run.bat 或 ClassroomAssistant.exe 即可启动。
endlocal