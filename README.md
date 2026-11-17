# NQ Autonomous Absorption & Delta Divergence — ATAS X

Stratégie quantitative Order Flow 100% autonome pour Nasdaq Futures (**NQ / MNQ**) sur **ATAS X**.

## Fichiers inclus
- `dist/NqAutonomousAbsorptionStrategy.dll` : Fichier binaire **compilé en Release** prêt à être copié dans le dossier `Strategies` d'ATAS X.
- `Compiler_et_Installer_ATASX.bat` : Script d'installation automatique Windows en 1 clic (copie la DLL dans `%USERPROFILE%\Documents\ATAS\Strategies` et recompile contre les DLL locales d'ATAS X si le SDK .NET est présent).
- `NqAutonomousAbsorptionStrategy.csproj` : Projet C# `.NET 8 / .NET 10` préconfiguré pour ATAS X (`C:\Program Files\ATAS X`) et ATAS Platform (`C:\Program Files (x86)\ATAS Platform`).
- `NqAutonomousAbsorptionStrategy.cs` : Code source C# complet commenté en français.

## Installation rapide dans ATAS X
1. Copiez **`dist/NqAutonomousAbsorptionStrategy.dll`** dans :
   ```text
   %USERPROFILE%\Documents\ATAS\Strategies\
   ```
   *(Ou double-cliquez simplement sur `Compiler_et_Installer_ATASX.bat` sous Windows).*
2. Ouvrez **ATAS X**, affichez un graphique Footprint / Cluster sur **NQ** ou **MNQ**, puis ouvrez **Chart Strategies** (`Ctrl + S`).
3. Sélectionnez **NQ Autonomous Absorption & Delta Divergence (Prop Firm Ready)**, associez votre compte de trading (Portfolio) et activez la stratégie.
