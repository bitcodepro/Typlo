; Built from the already verified ZIP, never from a user installation.
[Setup]
AppId={{65B79E48-68D3-4619-B75D-BA7F7F7BE680}
AppName=Typlo
AppVersion={#AppVersion}
AppPublisher=bitcodepro
DefaultDirName={localappdata}\Programs\Typlo
DefaultGroupName=Typlo
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#OutputDirectory}
OutputBaseFilename=typlo-win-x64-setup
Compression=lzma2
SolidCompression=yes
UninstallDisplayIcon={app}\Typlo.exe
CloseApplications=no
RestartApplications=no
UsePreviousAppDir=no
DisableProgramGroupPage=yes
WizardStyle=modern

[Files]
Source: "{#PayloadDirectory}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{userprograms}\Typlo"; Filename: "{app}\Typlo.exe"; WorkingDir: "{app}"

[Code]
function InitializeSetup(): Boolean;
begin
  Result := not RegKeyExists(HKCU64, 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{65B79E48-68D3-4619-B75D-BA7F7F7BE680}_is1');
  if not Result then
    SuppressibleMsgBox('Typlo setup is already registered for this user. Use the built-in updater, or close Typlo and uninstall it before installing to a clean folder. Your data is preserved by uninstall.', mbError, MB_OK, IDOK);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if FileExists(ExpandConstant('{app}\Typlo.exe')) or
     FileExists(ExpandConstant('{app}\typlo-rust.exe')) or
     FileExists(ExpandConstant('{app}\typlo-settings.exe')) or
     FileExists(ExpandConstant('{app}\rust-current.txt')) or
     DirExists(ExpandConstant('{app}\versions')) then
    Result := 'An existing Typlo installation or updater state was found. Use Settings > Check for updates in Typlo, or choose a clean folder. Setup does not replace portable installations or reset updater state.';
end;
; No Run, InstallDelete or UninstallDelete: never start the app during setup,
; modify profiles, or delete data/versions created by the app on uninstall.
