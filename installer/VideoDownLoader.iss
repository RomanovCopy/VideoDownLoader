#ifndef PublishDir
  #define PublishDir "..\artifacts\publish"
#endif

#ifndef InstallerOutputDir
  #define InstallerOutputDir "..\artifacts\installer"
#endif

[Setup]
AppId={{2E6F6CD4-F4D8-4A0A-B426-9E15D4584C66}
AppName=VideoDownLoader
AppVersion=1.0.0
AppPublisher=RomanovCopy
AppPublisherURL=https://github.com/RomanovCopy/VideoDownLoader
AppSupportURL=https://github.com/RomanovCopy/VideoDownLoader/issues
DefaultDirName={localappdata}\Programs\VideoDownLoader
DefaultGroupName=VideoDownLoader
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#InstallerOutputDir}
OutputBaseFilename=VideoDownLoader-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\VideoDownLoader.exe
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[Tasks]
Name: "desktopicon"; Description: "Создать ярлык на рабочем столе"; GroupDescription: "Дополнительные ярлыки:"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\VideoDownLoader"; Filename: "{app}\VideoDownLoader.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\VideoDownLoader"; Filename: "{app}\VideoDownLoader.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\VideoDownLoader.exe"; Description: "Запустить VideoDownLoader"; Flags: nowait postinstall skipifsilent
Filename: "{app}\VideoDownLoader.exe"; Flags: nowait skipifnotsilent
