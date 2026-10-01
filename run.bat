@echo off
rem  启动课堂助手（若还没有 exe，会先自动编译）
cd /d "%~dp0"
if not exist ClassroomAssistant.exe call build.bat
start "" ClassroomAssistant.exe