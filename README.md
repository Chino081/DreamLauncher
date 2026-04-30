# DreamLauncher

DreamLauncher 是一个面向 Minecraft 服务器的专属启动器。首发目标是 Windows WPF，核心逻辑独立在 `DreamLauncher.Core`，同时提供 `DreamLauncher.Avalonia` 版本，方便后续扩展到 macOS / Linux。

启动器本体不内置大型客户端资源。客户端包、Java Runtime、公告、更新清单等都从远程配置或 CDN 获取；下载后会做 SHA256 校验，解压时会防 Zip Slip 路径穿越。

## 当前状态

- Windows WPF：主版本，已接入登录、客户端下载/更新、Java 管理、游戏启动、资源管理、设置、自动更新。
- Avalonia：跨平台版本，已复用 Core 逻辑，并补齐启动、下载、设置、资源管理等主要页面。
- Core：负责远程配置、下载、解压、校验、Java 管理、账号管理、Minecraft 启动参数生成、资源包/光影包/Mod 管理。
- Models：共享数据模型。

当前 Microsoft 登录使用设备码流程，固定 Client ID：

```text
00000000402b5328
```

## 项目结构

```text
DreamtcTamracLauncherNew/
  DreamLauncher.slnx
  README.md
  local-cdn/
    clients.json
    java-runtimes.json
    announcement.json
    launcher-update.json
  docs/
    samples/
  DreamLauncher/
    DreamLauncher.Models/
    DreamLauncher.Core/
    DreamLauncher.Windows/
      Accounts/
      Assets/
      Dialogs/
      Security/
      ViewModels/
    DreamLauncher.Avalonia/
      Accounts/
      Assets/
      Dialogs/
      Security/
      ViewModels/
```

## 数据目录

普通配置和运行数据保存在程序运行目录下：

```text
程序目录/
  DreamLauncher/
    config.json
    clients/
    runtime/
      java/
    cache/
      downloads/
        packs/
        java/
      images/
    logs/
  .minecraft/
```

账号资料单独保存在：

```text
%AppData%\.DreamtcLauncher\
  accounts.json
```

Windows 版敏感令牌优先写入 Windows Credential Manager。日志会对 token 等敏感信息脱敏。

## 固定远程配置

当前启动器会强制使用以下配置地址：

```text
https://raw.giteeusercontent.com/Chino7/DreamLauncher/raw/master/local-cdn/clients.json
https://raw.giteeusercontent.com/Chino7/DreamLauncher/raw/master/local-cdn/java-runtimes.json
https://raw.giteeusercontent.com/Chino7/DreamLauncher/raw/master/local-cdn/announcement.json
https://raw.giteeusercontent.com/Chino7/DreamLauncher/raw/master/local-cdn/launcher-update.json
```

即使本地 `config.json` 写了其他地址，读取或保存配置时也会自动修正回上面的固定地址。

## 主要功能

### 账号

- Microsoft 正版登录，使用系统浏览器/设备码流程。
- 不保存 Microsoft 密码。
- 多账号保存、切换、删除。
- 当前账号状态展示。
- token 过期后自动刷新；失效时提示重新登录。
- 登录选择弹窗保留第三方验证、离线验证入口，目前作为预留入口显示。

### 客户端

- 从 `clients.json` 读取可用客户端。
- 支持未安装、已就绪、需要更新、下载中、解压中、校验失败、Java 缺失、启动失败等状态。
- 完整客户端压缩包下载。
- 下载进度、速度、大小、剩余时间展示。
- 下载完成后 SHA256 校验。
- 校验失败会删除损坏缓存并允许重试。
- 解压时只合并压缩包中 `.minecraft` 的内容，不删除原本 `.minecraft`。
- 下载/解压完成后清理缓存文件。

### Java

Java 启动优先级：

1. 当前客户端手动指定的 Java 路径
2. 启动器私有 Java Runtime
3. 系统已安装 Java

支持能力：

- 根据客户端 `javaVersion` 自动选择 Java。
- 自动检测本机 Java，并显示全部可用路径。
- 支持手动添加 Java 可执行文件。
- 每个客户端独立保存 Java 路径。
- 支持最大内存自动推荐、常用档位、自定义填写。
- 私有 Java 只解压到启动器目录，不写系统目录、不改 PATH、不需要管理员权限。

### 游戏启动

- 根据版本 JSON 生成 Minecraft 启动参数。
- 支持 Forge / NeoForge / Fabric 等继承版本。
- 支持 `arguments.jvm`、`arguments.game`、classpath、module path。
- 使用 `javaw` 启动，避免弹出命令行窗口。
- 启动前检查账号、客户端、Java。
- 支持启动后自动连接服务器。
- 成功启动游戏后关闭启动器。
- Windows / Linux / macOS 会按当前系统和架构选择对应 natives，避免跨系统 natives 混用。

### 资源管理

Windows 与 Avalonia 都已接入资源页。

资源页包含三个子页签：

- Mod
- 资源包
- 光影包

支持能力：

- 展示当前客户端资源目录。
- 打开对应文件夹。
- 从文件安装。
- 拖拽安装。
- 刷新列表。
- Mod 支持 `.jar` 启用/停用，停用时重命名为 `.jar.disabled`。
- 资源包支持 `.zip` 或文件夹，启用状态写入 `options.txt`。
- 光影包支持 `.zip` 或文件夹，选择状态写入 `optionsshaders.txt`。

资源目录位于当前客户端版本隔离目录：

```text
.minecraft/versions/<versionId>/
  mods/
  resourcepacks/
  shaderpacks/
```

### 启动器自动更新

目前只做 Windows 版。

- 启动后读取固定 `launcher-update.json`。
- 远程版本高于当前版本时提示更新。
- 下载更新包后校验 SHA256。
- 校验通过后退出启动器、替换文件、重新打开。

### 工具

- SHA256 与 Size 快速计算工具。
- 下载重试次数设置。
- 下载限速字段预留。

## 配置文件示例

### clients.json

```json
{
  "launcherVersion": "0.1.0",
  "announcementUrl": "https://raw.giteeusercontent.com/Chino7/DreamLauncher/raw/master/local-cdn/announcement.json",
  "javaRuntimesUrl": "https://raw.giteeusercontent.com/Chino7/DreamLauncher/raw/master/local-cdn/java-runtimes.json",
  "clients": [
    {
      "id": "survival",
      "name": "生存服",
      "description": "主线生存服务器",
      "version": "1.0.0",
      "minecraftVersion": "26.1.2-Fabric_0.19.1",
      "loader": "fabric",
      "loaderVersion": "0.19.1",
      "javaVersion": 25,
      "serverAddress": "mc.example.com",
      "defaultMemoryMb": 4096,
      "jvmArgs": "-XX:+UseG1GC",
      "gameArgs": "",
      "packUrl": "https://cdn.example.com/packs/survival-1.0.0.zip",
      "packSha256": "填写真实 SHA256",
      "packSize": 514448849,
      "installDir": "survival",
      "coverUrl": "",
      "iconUrl": "",
      "enabled": true
    }
  ]
}
```

### java-runtimes.json

```json
{
  "runtimes": [
    {
      "version": 21,
      "name": "Zulu JDK 21",
      "windowsX64Url": "https://cdn.example.com/java/zulu21-win-x64.zip",
      "windowsX64Sha256": "填写真实 SHA256",
      "size": 211614661
    },
    {
      "version": 25,
      "name": "Zulu JDK 25",
      "windowsX64Url": "https://cdn.example.com/java/zulu25-win-x64.zip",
      "windowsX64Sha256": "填写真实 SHA256",
      "size": 230819454
    }
  ]
}
```

### announcement.json

```json
{
  "title": "服务器公告",
  "items": [
    {
      "title": "新周目开启",
      "content": "欢迎加入全新生存周目。",
      "date": "2026-04-29"
    }
  ]
}
```

### launcher-update.json

```json
{
  "enabled": true,
  "version": "0.1.1",
  "title": "启动器更新",
  "notes": "修复问题并优化启动体验。",
  "mandatory": false,
  "windowsX64Url": "https://cdn.example.com/launcher/DreamLauncher-0.1.1-win-x64.zip",
  "windowsX64Sha256": "填写真实 SHA256",
  "size": 10485760
}
```

## clients.json 字段说明

| 字段 | 说明 |
| --- | --- |
| `id` | 客户端唯一 ID |
| `name` | 客户端显示名称 |
| `description` | 客户端描述 |
| `version` | 客户端资源包版本 |
| `minecraftVersion` | Minecraft 版本或版本目录 ID |
| `loader` | 加载器类型，例如 `vanilla`、`forge`、`fabric`、`quilt`、`neoforge` |
| `loaderVersion` | 加载器版本 |
| `javaVersion` | 需要的 Java 主版本 |
| `serverAddress` | 启动后自动连接的服务器地址 |
| `defaultMemoryMb` | 默认最大内存，单位 MB |
| `jvmArgs` | 额外 JVM 参数 |
| `gameArgs` | 额外游戏参数 |
| `packUrl` | 完整客户端压缩包地址，必须使用 HTTPS |
| `packSha256` | 压缩包 SHA256 |
| `packSize` | 压缩包大小，单位字节 |
| `installDir` | 客户端安装目录名 |
| `coverUrl` | 封面图地址，预留 |
| `iconUrl` | 图标地址，预留 |
| `enabled` | 是否启用 |

## 构建运行

需要 .NET 10 SDK。

构建全部项目：

```powershell
dotnet build .\DreamLauncher.slnx -c Debug
```

运行 Windows WPF 版：

```powershell
dotnet run --project .\DreamLauncher\DreamLauncher.Windows\DreamLauncher.Windows.csproj
```

运行 Avalonia 版：

```powershell
dotnet run --project .\DreamLauncher\DreamLauncher.Avalonia\DreamLauncher.Avalonia.csproj
```

直接运行已构建 exe：

```text
DreamLauncher/DreamLauncher.Windows/bin/Debug/net10.0-windows/DreamLauncher.Windows.exe
DreamLauncher/DreamLauncher.Avalonia/bin/Debug/net10.0/DreamLauncher.Avalonia.exe
```

## 发布

Windows 框架依赖发布：

```powershell
dotnet publish .\DreamLauncher\DreamLauncher.Windows\DreamLauncher.Windows.csproj -c Release -r win-x64 --self-contained false
```

Windows 自包含单文件发布：

```powershell
dotnet publish .\DreamLauncher\DreamLauncher.Windows\DreamLauncher.Windows.csproj -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true
```

Avalonia 可按目标平台发布，例如：

```powershell
dotnet publish .\DreamLauncher\DreamLauncher.Avalonia\DreamLauncher.Avalonia.csproj -c Release -r win-x64 --self-contained true
dotnet publish .\DreamLauncher\DreamLauncher.Avalonia\DreamLauncher.Avalonia.csproj -c Release -r linux-x64 --self-contained true
dotnet publish .\DreamLauncher\DreamLauncher.Avalonia\DreamLauncher.Avalonia.csproj -c Release -r osx-x64 --self-contained true
dotnet publish .\DreamLauncher\DreamLauncher.Avalonia\DreamLauncher.Avalonia.csproj -c Release -r osx-arm64 --self-contained true
```

## 安全约束

- 不保存 Microsoft 密码。
- token 不写日志。
- Windows 敏感令牌优先保存到 Windows Credential Manager。
- 下载地址必须使用 HTTPS。
- 客户端包和 Java 包必须校验 SHA256。
- 解压防 Zip Slip。
- Java 不静默安装到系统目录。
- 不修改系统 PATH。
- 删除或写入文件时限制在启动器管理目录内。

## 后续计划

- 文件级 manifest 增量更新。
- 更多平台 Java Runtime 配置。
- 服务器在线人数显示。
- 玩家皮肤 3D 预览。
- 崩溃日志分析。
- 多语言。
- 下载限速完善。
- P2P 或多源下载。
