@echo off
pwsh -NoProfile -ExecutionPolicy Bypass -File "%~dp0Build.ps1" %*
