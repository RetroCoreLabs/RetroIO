@echo off
REM TNFS Stress Test Launcher
REM Builds and runs the TNFS performance testing tool

echo.
echo ===============================================================================
echo                        TNFS STRESS TEST LAUNCHER
echo ===============================================================================
echo.

REM Get the directory where this script is located
set SCRIPT_DIR=%~dp0

REM Build the project
echo Building TnfsStressTest...
dotnet build "%SCRIPT_DIR%TnfsStressTest.csproj" --configuration Release --verbosity quiet

if %ERRORLEVEL% NEQ 0 (
    echo.
    echo ERROR: Build failed!
    echo.
    pause
    exit /b %ERRORLEVEL%
)

echo Build successful!
echo.

REM Run the stress test
dotnet run --project "%SCRIPT_DIR%TnfsStressTest.csproj" --configuration Release --no-build

echo.
echo Stress test completed.
pause
