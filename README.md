<p align="center">
  <img src="./assets/readme/hero.svg" width="100%" alt="PublishStudio：选中项目后一条流水线走完编译、打包、上传、登记，右侧为一次真实编译的终端输出">
</p>

# PublishStudio

**把编译、压包、上传 OSS、登记发布记录串成一条流水线 —— 选中项目点一次，剩下的它跑完。**

[![Release](https://img.shields.io/github/v/release/XCHLX/PublishStudio?label=release)](https://github.com/XCHLX/PublishStudio/releases)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](./LICENSE)
![.NET 8](https://img.shields.io/badge/.NET-8-512BD4)
![Platform](https://img.shields.io/badge/platform-Windows%2010%20%2F%2011-blue)

一个常驻托盘的 Windows 桌面工具，面向需要反复把 **.NET WebAPI / Node.js** 项目发到自建服务器的团队。手工发一次版的顺序是 `dotnet publish` → 找到产物 → 压 zip → 打开浏览器传 OSS → 回内部系统填发布记录，每步都得记住参数和路径，顺序错了、漏了一步，包就已经放到服务器上了。PublishStudio 把这套顺序固定进代码，选项目点一次就跑完，并且在编译开始前先把前置条件验掉。

[为什么用它](#为什么用它) · [功能](#功能) · [下载与安装](#下载与安装) · [快速开始](#快速开始) · [数据与日志](#数据与日志) · [本地打包](#本地打包)

---

## 为什么用它

| 以前 | 现在 |
|---|---|
| 记命令、找产物目录 | 项目配置存一次，之后点一次 |
| 手动压缩、手动改名 | 自动按 `{目录名}-{时间戳}.zip` 命名 |
| 手动传 OSS、手填回调 | 自动上传 + 自动登记发布记录 |
| 出问题看不出卡在哪 | 日志窗口实时滚出每一步，同时落盘 |

流水线的顺序**不可调整**，这是刻意的：发版这件事不该每次都靠人回忆。

## 功能

- 🚀 **一条流水线走完** —— 查发布信息、确认、编译、压包、后置命令、上传、回调、清理，八步固定顺序。
- 🧩 **多项目** —— 同一个项目同时只允许一个任务，不同项目可以并行；托盘按自定义分组排菜单。
- 🧰 **内嵌发布记录页** —— WebView2 窗口直接看当前项目的发布记录，不用切浏览器。
- 🔍 **日志实时可见并落盘** —— 界面上滚出来的每一行都同时写文件，事后能翻。

## 下载与安装

每个版本的 [Releases 页面](https://github.com/XCHLX/PublishStudio/releases) 提供三个包，按你的场景选一个：

| 包 | 适合谁 | 怎么用 | 前提 |
|---|---|---|---|
| `PublishStudio-<版本>-win-x64.zip` | 大多数人 | 解压，双击 `PublishStudio.exe` | 无，免装 .NET（约 60 MB） |
| `PublishStudio-<版本>-win-x64-fd.zip` | 在意体积 | 解压，双击 `PublishStudio.exe` | 需装 [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)（约 2 MB） |
| `PublishStudio-<版本>-setup.exe` | 想要开始菜单和卸载项 | 双击安装，一路下一步 | 无，内部自带运行时 |

三个包里都带同一份 `使用说明.txt`，开篇先让你认领是哪一种包，再分别讲自包含版、依赖环境版、安装版的运行步骤——所以三种包只需要维护这一个说明文件。

- **依赖环境版（`fd`）** 必须先装 .NET 8 **Desktop** Runtime（不是 Runtime、也不是 SDK），并且 `WebView2Loader.dll` 要和 exe 放在一起，删了「打开发布记录」会打不开。
- **安装版** 默认装到 `C:\Program Files\PublishStudio`（要 UAC 提权），建开始菜单组、桌面快捷方式和卸载项；升级直接跑新版 `setup.exe` 覆盖安装，`AppId` 相同会识别成升级，配置不丢。

三种包都要求系统有 WebView2 Runtime：Win11 自带，Win10 没装时「打开发布记录」窗口会提示安装。系统要求 64 位 Windows 10 / 11。

第一次运行若弹「Windows 已保护你的电脑」，点「更多信息 → 仍要运行」——exe 和 `setup.exe` 都没有代码签名，这是正常提示。

## 快速开始

1. 运行 `PublishStudio.exe` 
2. `Ctrl+N` 新建项目：选到含 `.csproj` / `package.json` 的那一层目录，类型选 `Api` 或 `Node.js`，填**内务府发布 ID**（发版才需要）。构建命令留空即用默认命令。
3. 菜单「设置 → 设置发布配置」填**接口地址 / 网页地址 / OSS Key / OSS 密钥**，再点右上角**登录**。
4. 选中项目右键「发布打包」。日志窗口会实时滚出编译、压缩、上传、回调的全过程。

只想在本地把项目跑起来调试，右键「运行」——它会在**系统控制台窗口**里执行 `dotnet watch run` / `npm run dev`，可以随手敲命令、`Ctrl+C` 停，关掉窗口进程就结束，不打包也不上传。

关闭主窗口不退出程序，只是缩到托盘；真正退出走托盘右键菜单。

## 一次「发布打包」做了什么

<p align="center">
  <img src="./assets/readme/pipeline.svg" width="100%" alt="发布打包的八个步骤：查发布信息、正式确认、编译、压成 zip、后置命令、上传 OSS、回调登记、清理产物">
</p>

 

 

<details>
<summary>一次真实运行长什么样（「仅编译」的日志原文）</summary>

```text
[2026-11-17 13:52:25] 编译 - full_nwf
------------------------------------------------------------
工作目录: D:\Projects\demo-api
执行命令: npm run build
------------------------------------------------------------
> vue-vadmin-js@1.0.0 build
> vite build && node ./src/utils/cp.js
vite v5.0.11 building for production...
✓ 3420 modules transformed.
...
压缩进度: 100%
[完成] 已生成压缩包: ...\build\full_nwf-20261117-135225\publish-zip\full_nwf-20261117-135225.zip  (1.0 MB)
[清理] 已删除 6 项编译产物，释放 3.1 MB，保留 publish-zip
[完成] 编译结束
产物位置: ...\build\full_nwf-20261117-135225\publish-zip
------------------------------------------------------------
[2026-11-17 13:52:45] 成功，用时 20 秒
```
</details>

## 界面与操作

### 项目列表

| 操作 | 行为 |
|---|---|
| 双击项目 | 按「设置 → 设置发布配置 → 双击项目列表时触发」执行：`运行`（默认）/ `发布` / `编译` |
| 选中右键 | 运行、发布、编译、查询发布信息、打开发布记录页面、git pull、VS Code、打开目录、复制路径、编辑…、移除 |


新建或编辑项目保存后会自动查一次发布信息；想手工刷新就对项目右键「查询发布信息」。

同一个项目同时只允许一个任务，不同项目可以并行。

    
## 项目类型与命令

| 类型 | 默认构建命令 | 产物目录留空时的推断值 |
|---|---|---|
| `Api` | `dotnet publish "<唯一的 csproj>" -c Release -r win-x64 --self-contained=true [-p:EnvironmentName=环境] -o "<临时目录>"` | `%TEMP%\PublishStudio\build\{目录名}-{时间戳}` |
| `Node.js` | `npm run build` | `<项目>\dist` |
| `Custom` | 无，必须自己填 | 无，必须自己填 |

新建项目时「产物目录」会**预填** `%TEMP%\PublishStudio\build\{folder}-{stamp}`，每个项目每次发布各落一个临时目录，包不会互相覆盖；清空这个框才回落到上表的推断值。

`.NET` 的产物目录直接传给 `dotnet publish -o`，填哪输出到哪。`Node.js` 的输出位置由项目自己的构建脚本决定，程序改不了 —— 所以填了「产物目录」时，会在构建成功后把 `dist` 的内容同步过去再打包，否则打出来是空包。

「产物目录」和「后置命令」都支持占位符：

| 占位符 | 含义 |
|---|---|
| `{work}` | 项目目录绝对路径 |
| `{output}` | 产物目录绝对路径（后置命令里用） |
| `{folder}` | 项目目录名 |
| `{stamp}` | 本次时间戳 `yyyyMMdd-HHmmss` |

默认按**自包含**发布（`-r win-x64 --self-contained=true`）：运行时一起打进产物，目标机**不用装 .NET**，直接跑 exe。代价是包大（几十 MB）、首次发布会联网拉一次 runtime pack、传 OSS 慢一些。

 
## 环境、Node 版本与后置命令

下拉框选 `Development` / `Production` / `Staging` 时做两件事：给 `dotnet publish` 追加 `-p:EnvironmentName=xxx`；发布完成后把产物里的 `appsettings.{环境}.json` 复制成 `appsettings.json`，删掉其余 `appsettings.*.json`。找不到对应文件只记一条提示，不中断。

### 指定 Node 版本

不同项目常要求不同 node 版本。「编辑项目」里类型选 `Node.js` 时，「环境」那一格会变成 **Node 版本**：下拉列出 nvm 里装好的版本（读 `NVM_HOME`），也能手填 `18` / `18.20` / `18.20.8`。

构建和「运行」时，程序只把那个版本目录插到**子进程** PATH 的最前面，**不调用 `nvm use`** —— 不改系统的全局 node、不需要管理员权限、多个项目并行发布也不会互相踩。留空就用系统 PATH 里的 node。

版本填错（nvm 里没有）会在编译开始前报错，并列出实际装了哪些版本；没装 nvm 时下拉只有「（系统默认）」。全局装的 `pnpm` / `yarn` 不在版本目录里，指定版本后调不到，项目命令保持 `npm run build` 即可。

后置命令一行一条，按顺序在项目目录执行，`#` 开头和空行跳过：
 

## 数据与日志

| 内容 | 位置 |
|---|---|
| 项目清单 | `%LOCALAPPDATA%\PublishStudio\projects.json` |
| 发布配置（含 OSS 凭据、登录账号） | `%LOCALAPPDATA%\PublishStudio\settings.json` |
| WebView2 缓存 | `%LOCALAPPDATA%\PublishStudio\WebView2` |
| 编译临时产物 | `%TEMP%\PublishStudio\build\{目录名}-{时间戳}` |
| 压缩包 | `<产物目录>\publish-zip\{目录名}-{时间戳}.zip` |
| 运行日志 | `%TEMP%\PublishStudio\Logs\{日期}_{时间}_{项目}_{动作}_{结果}.log` |

日志窗口滚出来的每一行都同时落盘，全部平铺在 `Logs` 下。运行期间叫 `_运行中.log`，结束时按结果改成 `_成功` 或 `_失败`（取消也算失败）。**所有日志一律只保留 1 天**，超期的在程序启动时删掉。临时目录不可写时不会影响任务，界面上照样看得到。

`build\` 下的历史目录不会无限堆积：「保留历史发布文件」可选 `不保留` / 1 / 3 / 7 / 15 / 30 天（默认 3 天），超期的在**程序启动时**和**每次发布成功后**自动删。选「不保留」只留最近 1 小时内创建的目录（刚发的包还在，别的项目正在编译的目录也不会被误删）。

`projects.json` 是普通 JSON，可以直接手改；改坏了程序会备份成 `.broken` 再空着启动，不会打不开。

> **注意**：`settings.json` 里的 OSS 密钥是**明文**。这台机器谁能读到这个文件，谁就能拿到发版权限。

菜单「帮助」里还有：打开配置文件夹、打开临时文件夹、打开日志文件夹、关于（版本 / 作者 / 运行环境 / 目录一览，带「复制信息」按钮）。

  

## 发版（维护 Release）

版本维护靠 **tag**，不用在 GitHub 网页上手点上传。流程固定为五步：

**1. 改版本号**：`PublishStudio.csproj` 里三行一起改，保持对应：

| 字段 | 示例 | 用途 |
|---|---|---|
| `<Version>` | `0.4.0` | 必须规范三段式，tag 和文件名用它 |
| `<FileVersion>` | `0.4.0.0` | Windows 文件版本，四段式 |
| `<InformationalVersion>` | `0.4.0` | 「帮助 → 关于」里显示的 `V0.4.0`；发布时脚本会用 tag 版本覆盖它，所以格式必须和 `<Version>` 一致 |

**2. 写版本说明**：新建 `docs/release-notes/v<版本>.md`（如 `v0.4.0.md`），写这次改了什么、下载哪个包、已知限制。这份文件会作为 Release 正文，GitHub 自动生成的 changelog 追加在它后面。**没写也能发**——会自动退回通用说明 `docs/release-notes/v.md`，其中的 `{{VERSION}}` 会被替换成实际版本号，包名不会写错；只有两份文件都不存在时工作流才失败。

**3. 提交并打 tag**：tag 名必须是 `v` + 三段式版本号（如 `v0.4.0`），工作流会校验格式。

```bash
git add .
git commit -m "chore: release v0.4.0"
git tag v0.4.0
git push origin main
git push origin v0.4.0
```

**4. 自动构建**：push tag 触发 `.github/workflows/release.yml`，它依次做：

- 检出代码、装 .NET 8 SDK、从 tag 解析版本号
- 读取 `docs/release-notes/v<版本>.md`；没有则用通用说明 `docs/release-notes/v.md`
- 跑 `build-release.ps1 -Flavor All` 出两个 zip
- 用构建机预装的 Inno Setup 编译 `setup.exe`
- 创建 Release，正文用版本说明，上传三个包

进度在仓库的 **Actions** 页看。失败通常是 tag 格式不对、两份版本说明都不存在、或 Inno Setup 没预装，日志里会写明。

**5. 核对**：到 Releases 页确认三个附件都在、说明正文正常、版本号对，再下载一个 zip 解压试跑。

改了内容但不想发版（比如补文档），只 commit 和 push，不要打 tag。一个 tag 对应一个 Release，重复 push 同一个 tag 不会覆盖旧 Release——要重发就删掉旧 tag 和 Release，改用新版本号（如 `v0.4.1`）。


## 参与贡献

Issue 和 PR 都欢迎。提 PR 前请先开 issue 说清楚要解决什么问题，避免白写。改动涉及发布流水线顺序的，请先讨论——那条顺序是刻意的。

## License

[MIT](./LICENSE) © 2026 陈
