@echo off
chcp 65001 >nul
cd /d "%~dp0"

where dotnet >nul 2>nul
if errorlevel 1 (
  echo Не найден .NET SDK. Установите его командой:
  echo     winget install Microsoft.DotNet.SDK.10 --source winget
  echo или скачайте с https://dotnet.microsoft.com/download/dotnet/10.0
  pause
  exit /b 1
)

dotnet publish CountdownSaver.csproj -c Release -o publish
if errorlevel 1 (
  echo Сборка не удалась.
  pause
  exit /b 1
)

copy /y publish\CountdownSaver.exe CountdownSaver.scr >nul
echo.
echo Готово: %~dp0CountdownSaver.scr
echo Нажмите на файл правой кнопкой мыши и выберите «Установить».
pause
