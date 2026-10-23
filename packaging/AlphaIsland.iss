#ifndef AppVersion
  #define AppVersion "0.1.155"
#endif
#ifndef PublishDir
  #error PublishDir must identify the clean self-contained publish directory.
#endif
#ifndef OutputDir
  #define OutputDir "..\bin\Installers"
#endif
#ifndef InstallerId
  #define InstallerId "{B9EAA54C-314A-4A45-B10F-46937497020A}"
#endif
#ifndef AppDataRoot
  #define AppDataRoot "{localappdata}\alpha-island"
#endif
#ifndef StartupValue
  #define StartupValue "AlphaIsland"
#endif
#ifndef HookDirectory
  #define HookDirectory ""
#endif
#ifndef DesktopRoot
  #define DesktopRoot "{userdesktop}"
#endif
#ifndef MenuRoot
  #define MenuRoot "{userprograms}\Alpha Island"
#endif

[Setup]
AppId={{#InstallerId}
AppName=Alpha Island
AppVersion={#AppVersion}
AppPublisher=Alpha Island
AppCopyright=© Alpha Island
DefaultDirName={localappdata}\Programs\Alpha Island
DisableDirPage=no
UsePreviousAppDir=yes
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
OutputDir={#OutputDir}
OutputBaseFilename=AlphaIsland-{#AppVersion}-win-x64-Setup
SetupIconFile=..\Assets\alpha-island.ico
UninstallDisplayIcon={app}\icons\alpha-island-{#AppVersion}.ico
UninstallDisplayName=Alpha Island
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
CloseApplicationsFilter=alpha-island.exe
SetupLogging=yes
VersionInfoVersion={#AppVersion}.0

[Languages]
Name: "chinesesimplified"; MessagesFile: "ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "在桌面创建快捷方式"; GroupDescription: "快捷方式："; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "installer-maintenance.ps1"; DestDir: "{app}\installer"; Flags: ignoreversion
Source: "shortcut-icons.ps1"; DestDir: "{app}\installer"; Flags: ignoreversion

[Icons]
Name: "{#MenuRoot}\Alpha Island"; Filename: "{app}\alpha-island.exe"; WorkingDir: "{app}"; IconFilename: "{app}\icons\alpha-island-{#AppVersion}.ico"
Name: "{#MenuRoot}\卸载 Alpha Island"; Filename: "{uninstallexe}"
Name: "{#DesktopRoot}\Alpha Island"; Filename: "{app}\alpha-island.exe"; WorkingDir: "{app}"; IconFilename: "{app}\icons\alpha-island-{#AppVersion}.ico"; Tasks: desktopicon

[Run]
Filename: "{app}\alpha-island.exe"; Description: "启动 Alpha Island"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: dirifempty; Name: "{#MenuRoot}"

[Code]
var
  PurgeUserData: Boolean;

procedure RegisterPreviousData(PreviousDataKey: Integer);
begin
  SetPreviousData(PreviousDataKey, 'InstallDirectory', ExpandConstant('{app}'));
  SetPreviousData(PreviousDataKey, 'Version', '{#AppVersion}');
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  OldDirectory, Directory, DataDirectory: String;
begin
  Result := True;
  if CurPageID <> wpSelectDir then Exit;
  Directory := AddBackslash(ExpandFileName(WizardDirValue));
  DataDirectory := AddBackslash(ExpandConstant('{#AppDataRoot}'));
  if (Length(Directory) > 220) or (Length(Directory) < 5) or
    (Pos(Lowercase(DataDirectory), Lowercase(Directory)) = 1) or
    (Pos(Lowercase(Directory), Lowercase(DataDirectory)) = 1) then begin
    MsgBox('请选择路径较短的独立程序文件夹；不能安装到磁盘根目录或用户数据目录。', mbError, MB_OK);
    Result := False; Exit;
  end;
  OldDirectory := GetPreviousData('InstallDirectory', '');
  if (OldDirectory <> '') and (CompareText(AddBackslash(OldDirectory), Directory) <> 0) then begin
    MsgBox('覆盖更新请沿用原安装路径。若要移动到其他盘，请先卸载并选择保留数据，再重新安装。', mbInformation, MB_OK);
    Result := False;
  end;
end;

function Maintenance(const Mode: String; const Purge: Boolean): Boolean;
var
  Args: String;
  ResultCode: Integer;
begin
  Args := '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + ExpandConstant('{app}\installer\installer-maintenance.ps1') +
    '" -Mode ' + Mode + ' -InstallDirectory "' + ExpandConstant('{app}') + '" -DataRoot "' +
    ExpandConstant('{#AppDataRoot}') + '" -RunValue "{#StartupValue}"';
  if Purge then Args := Args + ' -PurgeData';
  if '{#HookDirectory}' <> '' then Args := Args + ' -HookDirectory "{#HookDirectory}"';
  Result := Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'), Args, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    if not Maintenance('Install', False) then
      SuppressibleMsgBox('程序已安装，但插件或原自启动项未能更新。可在设置中检查，安装日志已保留。', mbError, MB_OK, IDOK);
end;

function InitializeUninstall(): Boolean;
var
  Choice: Integer;
begin
  if UninstallSilent then PurgeUserData := ExpandConstant('{param:PURGEDATA|0}') = '1'
  else begin
    Choice := MsgBox('卸载 Alpha Island 时，是否同时删除全部用户数据？' + #13#10 + #13#10 +
      '“是”：删除配置、加密密钥、聊天与剪贴板历史、插件、缓存和本程序的 AI 审批接入。' + #13#10 +
      '“否”：保留数据，方便以后重新安装。' + #13#10 + '“取消”：退出卸载。', mbConfirmation, MB_YESNOCANCEL or MB_DEFBUTTON2);
    if Choice = IDCANCEL then begin Result := False; Exit; end;
    PurgeUserData := Choice = IDYES;
  end;
  Result := Maintenance('ValidateRemoval', PurgeUserData);
  if not Result then MsgBox('卸载检查未通过。数据目录中可能存在链接，或接入配置不可读取。程序和数据均已保留，请检查后重试。', mbError, MB_OK);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then begin
    if not Maintenance('Uninstall', PurgeUserData) then
      RaiseException('无法清理启动项或用户数据，卸载已停止。请关闭正在运行的 Alpha Island 后重试。');
  end;
end;
