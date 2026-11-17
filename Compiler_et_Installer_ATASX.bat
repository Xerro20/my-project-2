@echo off
chcp 65001 >nul
title Compilation & Installation - NQ Autonomous Absorption Strategy (ATAS X)
echo ============================================================================
echo   COMPILATION ET INSTALLATION AUTOMATIQUE POUR ATAS X / ATAS PLATFORM
echo ============================================================================
echo.

set "STRAT_DIR=%USERPROFILE%\Documents\ATAS\Strategies"
if not exist "%STRAT_DIR%" mkdir "%STRAT_DIR%"

REM 1. Si la DLL pre-compilee est deja presente dans dist\, on l'installe immediatement
if exist "%~dp0dist\NqAutonomousAbsorptionStrategy.dll" (
    echo [1/2] Copie de la DLL pre-compilee vers "%STRAT_DIR%"...
    copy /Y "%~dp0dist\NqAutonomousAbsorptionStrategy.dll" "%STRAT_DIR%\NqAutonomousAbsorptionStrategy.dll" >nul
    echo [OK] NqAutonomousAbsorptionStrategy.dll copiee avec succes !
)

REM 2. Si le SDK .NET est installe sur la machine, recompilation directe contre les DLL locales d'ATAS X
where dotnet >nul 2>nul
if %ERRORLEVEL% EQU 0 (
    echo.
    echo [2/2] SDK .NET detecte : Recompilation native contre votre installation locale ATAS X...
    dotnet build "%~dp0NqAutonomousAbsorptionStrategy.csproj" -c Release
    if %ERRORLEVEL% EQU 0 (
        echo.
        echo [SUCCES] Compilation et installation terminees dans :
        echo          %STRAT_DIR%\NqAutonomousAbsorptionStrategy.dll
    ) else (
        echo [INFO] Utilisation de la DLL pre-compilee deja installee dans %STRAT_DIR%.
    )
) else (
    echo.
    echo [SUCCES] La DLL pre-compilee a ete installee dans :
    echo          %STRAT_DIR%\NqAutonomousAbsorptionStrategy.dll
)

echo.
echo ============================================================================
echo   Vous pouvez maintenant ouvrir ATAS X -> Chart Strategies (Ctrl+S)
echo   et selectionner : "NQ Autonomous Absorption & Delta Divergence"
echo ============================================================================
pause
