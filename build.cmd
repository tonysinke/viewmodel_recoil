@echo off
setlocal
set CONFIG=%~1
if "%CONFIG%"=="" set CONFIG=Release
dotnet build "%~dp0src\VMRecoil.csproj" -c %CONFIG%
if errorlevel 1 (
  echo.
  echo BUILD FAILED
  exit /b 1
)
echo.
echo OK: %~dp0src\bin\%CONFIG%\VMRecoil.dll
