@echo off
echo Cleaning old build artifacts...
for /d /r . %%d in (bin,obj) do @if exist "%%d" rd /s /q "%%d"

echo Building HeadlessServer...
dotnet build -c Release

if %ERRORLEVEL% NEQ 0 (
    echo Build failed! Check errors above.
    pause
    exit /b %ERRORLEVEL%
)

echo Running bot with args: %*
dotnet run --no-build --configuration Release -- %*
pause