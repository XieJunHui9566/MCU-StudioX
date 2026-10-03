; AppId 是已安装产品的永久身份；发布新版本时禁止修改。
#ifndef AppVersion
  #error AppVersion is required
#endif
#ifndef PayloadDirectory
  #error PayloadDirectory is required
#endif
#ifndef AppFileVersion
  #define AppFileVersion AppVersion
#endif
#ifndef DistributionProfile
  #define DistributionProfile "full"
#endif
#if DistributionProfile == "base"
  #define DistributionProfile "light"
#endif
#if DistributionProfile != "full" && DistributionProfile != "light"
  #error Unsupported distribution profile
#endif
#if DistributionProfile == "light" && (DirExists(PayloadDirectory + "\runtime\toolsets") || DirExists(PayloadDirectory + "\runtime\hdl") || DirExists(PayloadDirectory + "\runtime\stc-isp"))
  #error Light payload must not contain development tools
#endif
#if DirExists(PayloadDirectory + "\runtime\toolsets") || DirExists(PayloadDirectory + "\runtime\hdl") || DirExists(PayloadDirectory + "\runtime\stc-isp")
  #ifndef DevelopmentComponentEntries
    #error Generate DevelopmentComponentEntries before compiling a payload containing toolsets
  #endif
#endif
#ifndef ProductId
  #define ProductId "{B8050FBC-2DE2-43F4-B839-F0E90522E671}"
#endif
#ifndef ProductMutex
  #define ProductMutex "MCUStudioX.Desktop.InstallLock"
#endif
#ifndef InstallerMutex
  #define InstallerMutex "MCUStudioX.Setup"
#endif

[Setup]
AppId={{#ProductId}
AppName=MCU StudioX
AppVersion={#AppVersion}
AppVerName=MCU StudioX {#AppVersion}
AppPublisher=MCU StudioX
VersionInfoVersion={#AppFileVersion}
VersionInfoProductVersion={#AppFileVersion}
VersionInfoProductTextVersion={#AppVersion}
VersionInfoDescription=MCU StudioX 安装程序
DefaultDirName={localappdata}\Programs\MCU StudioX
DefaultGroupName=MCU StudioX
PrivilegesRequired=lowest
SetupArchitecture=x64
ArchitecturesAllowed=x64os
MinVersion=10.0.19041
UsePreviousAppDir=yes
UsePreviousGroup=yes
UsePreviousTasks=yes
DisableDirPage=no
DisableProgramGroupPage=yes
AllowNoIcons=yes
UninstallDisplayName=MCU StudioX
UninstallDisplayIcon={app}\MCU StudioX.exe
; 旧安装日志曾登记工具文件；重新建立当前程序日志，使升级后卸载不会沿用旧记录删除独立组件。
UninstallLogMode=overwrite
SetupIconFile=..\..\src\StudioX.Desktop\Assets\StudioX.ico
AppMutex={#ProductMutex}
SetupMutex={#InstallerMutex}
CloseApplications=no
RestartApplications=no
AlwaysRestart=no
WizardStyle=modern
WizardSizePercent=110
WizardResizable=yes
Compression=lzma2/normal
SolidCompression=yes
LZMANumBlockThreads=4
LZMADictionarySize=32768
#ifdef ValidationNoCompression
; 完整 SDK 的未压缩数据超出单个 EXE 上限，仅隔离验收使用旁置数据文件。
DiskSpanning=yes
DiskSliceSize=max
#endif
#if DistributionProfile == "light"
OutputBaseFilename=MCU-StudioX-{#AppVersion}-win-x64-Light-Setup
#else
OutputBaseFilename=MCU-StudioX-{#AppVersion}-win-x64-Setup
#endif
#if DistributionProfile == "light"
InfoBeforeFile=install-info-light.txt
#else
InfoBeforeFile=install-info.txt
#endif

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; 只写入 IDE 发行目录；不包含用户的 AppData、背景图、工程或已安装器件包仓库。
Source: "{#PayloadDirectory}\*"; DestDir: "{app}"; Excludes: "\runtime\toolsets\*,\runtime\hdl\*,\runtime\stc-isp\*"; Flags: ignoreversion recursesubdirs createallsubdirs
; 快捷方式使用随版本变更的独立图标路径，避免 Explorer 复用旧 EXE 路径的图标缓存。
Source: "..\..\src\StudioX.Desktop\Assets\StudioX.ico"; DestDir: "{app}\icons"; DestName: "StudioX-{#AppVersion}.ico"; Flags: ignoreversion

[Icons]
Name: "{group}\MCU StudioX"; Filename: "{app}\MCU StudioX.exe"; WorkingDir: "{app}"; IconFilename: "{app}\icons\StudioX-{#AppVersion}.ico"; AppUserModelID: "MCUStudioX.Desktop"
Name: "{group}\器件包"; Filename: "{app}\device-packs"
Name: "{group}\使用与升级说明"; Filename: "{app}\使用说明.txt"
Name: "{group}\卸载 MCU StudioX"; Filename: "{uninstallexe}"
Name: "{autodesktop}\MCU StudioX"; Filename: "{app}\MCU StudioX.exe"; WorkingDir: "{app}"; IconFilename: "{app}\icons\StudioX-{#AppVersion}.ico"; Tasks: desktopicon; AppUserModelID: "MCUStudioX.Desktop"

[Run]
Filename: "{app}\MCU StudioX.exe"; Description: "{cm:LaunchProgram,MCU StudioX}"; Flags: nowait postinstall skipifsilent

[Code]
var
    PreservedDevelopmentDirectories: TStringList;

procedure SnapshotDevelopmentDirectories; forward;

function DirectoryAttributes(const Path: String): LongWord;
    external 'GetFileAttributesW@kernel32.dll stdcall';

procedure CheckDevelopmentDirectoryBoundary(Path: String);
var
    Parent, AppRoot: String;
    Attributes: LongWord;
begin
    AppRoot := ExpandFileName(ExpandConstant('{app}'));
    Path := ExpandFileName(Path);
    while Length(Path) >= Length(AppRoot) do
    begin
        Attributes := DirectoryAttributes(Path);
        if (Attributes <> $FFFFFFFF) and ((Attributes and $400) <> 0) then
            RaiseException('开发组件安装路径包含链接，请选择普通目录：' + Path);
        Parent := ExtractFileDir(Path);
        if Parent = Path then Exit;
        Path := Parent;
    end;
end;

procedure SnapshotDevelopmentDirectory(Relative: String);
var
    Path: String;
begin
    Path := ExpandConstant('{app}\') + Relative;
    CheckDevelopmentDirectoryBoundary(Path);
    if DirExists(Path) or FileExists(Path) then
    begin
        PreservedDevelopmentDirectories.Add(Relative);
        Log('Preserving existing development directory: ' + Relative);
    end;
end;

function ShouldInstallDevelopmentDirectory(Relative: String): Boolean;
begin
    Result := (PreservedDevelopmentDirectories = nil) or
        (PreservedDevelopmentDirectories.IndexOf(Relative) < 0);
end;

function TakeRevisionSuffix(var Version: String): String;
var
    Last: String;
begin
    Result := '';
    if Length(Version) = 0 then Exit;
    Last := Copy(Version, Length(Version), 1);
    if (Last >= 'A') and (Last <= 'Z') then
    begin
        Result := Last;
        Delete(Version, Length(Version), 1);
    end;
end;

function NextVersionPart(var Version: String): Integer;
var
    Separator: Integer;
    Part: String;
begin
    Separator := Pos('.', Version);
    if Separator = 0 then
    begin
        Part := Version;
        Version := '';
    end
    else
    begin
        Part := Copy(Version, 1, Separator - 1);
        Delete(Version, 1, Separator);
    end;
    Result := StrToIntDef(Part, 0);
end;

function CompareReleaseVersions(Left, Right: String): Integer;
var
    I, A, B: Integer;
    LeftSuffix, RightSuffix: String;
begin
    Result := 0;
    { 数字相同时按无后缀、A..Z 排序，避免把末段 4A 解析为零。 }
    LeftSuffix := TakeRevisionSuffix(Left);
    RightSuffix := TakeRevisionSuffix(Right);
    for I := 0 to 3 do
    begin
        A := NextVersionPart(Left);
        B := NextVersionPart(Right);
        if A > B then
        begin
            Result := 1;
            Exit;
        end;
        if A < B then
        begin
            Result := -1;
            Exit;
        end;
    end;
    Result := CompareText(LeftSuffix, RightSuffix);
end;

function InitializeSetup(): Boolean;
var
    InstalledVersion: String;
begin
    Result := True;
    if RegQueryStringValue(HKCU64,
        'Software\Microsoft\Windows\CurrentVersion\Uninstall\{#ProductId}_is1',
        'DisplayVersion', InstalledVersion) then
    begin
        Log('Existing MCU StudioX version: ' + InstalledVersion);
        if CompareReleaseVersions(InstalledVersion, '{#AppVersion}') > 0 then
        begin
            SuppressibleMsgBox('已安装较新版本 MCU StudioX ' + InstalledVersion +
                '，不能用 {#AppVersion} 覆盖。请使用相同版本或更新的安装包。' + #13#10 +
                'A newer MCU StudioX version is already installed. Downgrade is not allowed.', mbError, MB_OK, IDOK);
            Result := False;
        end;
    end;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
    Result := True;
    if CurPageID = wpSelectDir then
    begin
        { 升级始终使用注册的原目录；不允许一份卸载记录指向多套安装。 }
        if (GetPreviousData('InstallDirectory', '') <> '') and
            (CompareText(ExpandFileName(WizardDirValue), ExpandFileName(GetPreviousData('InstallDirectory', ''))) <> 0) then
        begin
            SuppressibleMsgBox('升级请使用原安装目录。若要迁移安装位置，请先卸载，个人数据和工程会保留。', mbError, MB_OK, IDOK);
            Result := False;
        end;
    end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
    Result := '';
    { 静默升级同样执行目录约束，不能靠 /DIR 绕过。 }
    if (GetPreviousData('InstallDirectory', '') <> '') and
        (CompareText(ExpandFileName(WizardDirValue), ExpandFileName(GetPreviousData('InstallDirectory', ''))) <> 0) then
        Result := '安装目录与原版本不同，请使用原目录升级，或先卸载再迁移。';
    if Result <> '' then Exit;
    try
        { 在任何文件复制前缓存整目录决定，不能因第一个文件创建目录而跳过后续文件。 }
        if PreservedDevelopmentDirectories = nil then
            PreservedDevelopmentDirectories := TStringList.Create;
        PreservedDevelopmentDirectories.Clear;
        CheckDevelopmentDirectoryBoundary(ExpandConstant('{app}\runtime'));
        SnapshotDevelopmentDirectories;
    except
        Result := GetExceptionMessage;
    end;
end;

procedure RegisterPreviousData(PreviousDataKey: Integer);
begin
    SetPreviousData(PreviousDataKey, 'InstallDirectory', ExpandFileName(WizardDirValue));
end;

// 不设置 UninstallDelete：卸载只删除安装器记录的文件，保留用户后来添加的数据。
#ifdef DevelopmentComponentEntries
  #include DevelopmentComponentEntries
#else
procedure SnapshotDevelopmentDirectories;
begin
end;
#endif
