@echo off
set RUNTIME=win-x64
set SCRIPT_DIR=%~dp0
powershell -ExecutionPolicy Bypass -File "%SCRIPT_DIR%publish-framework-dependent.ps1" -Runtime %RUNTIME% %*
if %errorlevel% neq 0 (
  echo Publish failed.
  exit /b %errorlevel%
)
echo.
echo Output artifacts in dist\ folder.
