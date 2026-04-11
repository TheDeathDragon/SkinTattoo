@echo off
dotnet clean -c Release >nul 2>&1
dotnet build -c Release
if %errorlevel% neq 0 (
    echo Build failed!
    pause
    exit /b 1
)
echo.
echo Output: SkinTattoo\SkinTattoo\bin\Release\win-x64\
pause
