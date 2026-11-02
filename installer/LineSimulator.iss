; Installer Inno Setup per Industrial Line Simulator
; Uso: ISCC.exe /DAppVersion=0.3.0 /DSourceDir=<cartella publish> /O<cartella output> LineSimulator.iss

#ifndef AppVersion
  #define AppVersion "0.3.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\publish\IndustrialLineSimulator"
#endif

[Setup]
AppId={{6C1E0B7A-3F2D-4C59-9A8E-1B2C3D4E5F60}
AppName=Industrial Line Simulator
AppVersion={#AppVersion}
AppPublisher=Industrial Line Simulator
DefaultDirName={autopf}\IndustrialLineSimulator
DefaultGroupName=Industrial Line Simulator
OutputBaseFilename=IndustrialLineSimulator-Setup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequiredOverridesAllowed=dialog
WizardStyle=modern

[Languages]
Name: "italian"; MessagesFile: "compiler:Languages\Italian.isl"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion

[Icons]
Name: "{group}\Industrial Line Simulator"; Filename: "{app}\IndustrialLineSimulator.exe"
Name: "{autodesktop}\Industrial Line Simulator"; Filename: "{app}\IndustrialLineSimulator.exe"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Crea un'icona sul desktop"; GroupDescription: "Icone aggiuntive:"

[Run]
Filename: "{app}\IndustrialLineSimulator.exe"; Description: "Avvia Industrial Line Simulator"; Flags: nowait postinstall skipifsilent
