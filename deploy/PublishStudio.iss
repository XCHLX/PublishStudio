; PublishStudio 安装包脚本（Inno Setup 6）
;
; 输入：dist\PublishStudio.exe（自包含单文件）+ dist\使用说明.txt
; 输出：release\PublishStudio-<版本>-setup.exe
;
; 本地编译（需先装 Inno Setup 6）：
;   ISCC.exe /DMyAppVersion=0.04 deploy\PublishStudio.iss
; GitHub Actions 用构建机预装的 ISCC.exe，版本号由工作流传入。

#define MyAppName "PublishStudio 发布工具"
#define MyAppPublisher "陈"
#define MyAppExeName "PublishStudio.exe"

#ifndef MyAppVersion
  #define MyAppVersion "0.04"
#endif

[Setup]
; 升级识别靠这个 GUID，首次定了就别再改，否则旧版卸载不掉会装成两个。
AppId={{8B4F2C7A-3D91-4E6F-A2C5-7D8E9F0A1B34}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\PublishStudio
DefaultGroupName={#MyAppName}
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName} {#MyAppVersion}
OutputDir=..\release
OutputBaseFilename=PublishStudio-{#MyAppVersion}-setup
SetupIconFile=..\app.ico
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
WizardStyle=modern

; GitHub 的 windows runner 预装的 Inno Setup 不含中文语言包，硬引用会编译失败。
; 本机装了完整版就用中文界面，CI 上找不到就退回英文默认，两边都能编译。
; CompilerPath 是 ISPP 预定义变量，指向编译器目录，不必猜 Program Files 路径。
#ifexist CompilerPath + "\Languages\ChineseSimplified.isl"
[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
#else
[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
#endif

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加任务："

[Files]
Source: "..\dist\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion
; 用通配符匹配说明文件，避免脚本里出现中文路径——英文系统的 ANSI 代码页读不了中文文件名。
Source: "..\dist\*.txt"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\PublishStudio 发布工具"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\使用说明"; Filename: "{app}\使用说明.txt"
Name: "{group}\卸载 PublishStudio"; Filename: "{uninstallexe}"
Name: "{autodesktop}\PublishStudio 发布工具"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "立即运行 PublishStudio"; Flags: nowait postinstall skipifsilent
