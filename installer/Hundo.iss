; Per-user setup without admin rights, into %LocalAppData%\Programs; the version comes from ISCC /DAppVersion=X.Y.Z.
#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#define AppExe "Hundo.exe"
#define RunKey "Software\Microsoft\Windows\CurrentVersion\Run"
#define SideImages "side-202.png,side-404.png"
#define SmallImages "small-58.png,small-77.png,small-97.png,small-124.png"

[Setup]
; The same AppId on every release makes a new setup update the installed copy.
AppId={{39FB1325-FFAF-4F0B-B07A-70CD681B2423}
AppName=Hundo
AppVersion={#AppVersion}
AppPublisher=UsusCimex
AppPublisherURL=https://github.com/UsusCimex/Hundo
AppSupportURL=https://github.com/UsusCimex/Hundo/issues
AppUpdatesURL=https://github.com/UsusCimex/Hundo/releases
DefaultDirName={autopf}\Hundo
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
DisableProgramGroupPage=yes
DisableReadyPage=yes
ShowLanguageDialog=auto
WizardStyle=modern dynamic
WizardImageFile={#SideImages}
WizardImageFileDynamicDark={#SideImages}
WizardSmallImageFile={#SmallImages}
WizardSmallImageFileDynamicDark={#SmallImages}
SetupIconFile=..\src\AC.App\Assets\app.ico
UninstallDisplayName=Hundo
UninstallDisplayIcon={app}\{#AppExe}
OutputDir=..\out
OutputBaseFilename=Hundo-v{#AppVersion}-win-x64-setup
SolidCompression=yes

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "ru"; MessagesFile: "compiler:Languages\Russian.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\out\app\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion

; Achievement Companion was the app's first name: an update removes its exe and shortcuts.
[InstallDelete]
Type: files; Name: "{app}\AchievementCompanion.exe"
Type: files; Name: "{autoprograms}\Achievement Companion.lnk"
Type: files; Name: "{autodesktop}\Achievement Companion.lnk"

[Icons]
Name: "{autoprograms}\Hundo"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\Hundo"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,Hundo}"; Flags: nowait postinstall skipifsilent

[Code]
// "Start with Windows" leaves the app's value under the user's Run key: it goes with the app, unless it starts
// another copy.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Command: String;
begin
  if (CurUninstallStep = usUninstall) and RegQueryStringValue(HKCU, '{#RunKey}', 'Hundo', Command) and
     (Pos(Lowercase(ExpandConstant('"{app}\{#AppExe}"')), Lowercase(Command)) = 1) then
    RegDeleteValue(HKCU, '{#RunKey}', 'Hundo');
end;
