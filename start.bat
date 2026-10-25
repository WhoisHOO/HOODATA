@echo off
rem HOODATA: double-click to start. It opens the dashboard in your browser and keeps collecting while its window
rem is open. PowerShell, WPF and SQLite are built into Windows 10/11, so nothing needs installing.
start "" powershell.exe -NoProfile -ExecutionPolicy Bypass -STA -WindowStyle Hidden -File "%~dp0app\HOODATA.ps1" %*
