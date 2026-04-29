# DreamLauncher

DreamLauncher 是一个面向 Minecraft 服务器的专属启动器项目。首发平台为 Windows，使用 C#、.NET 10 和 WPF；同时已经加入 Avalonia 前端，用于后续扩展到 macOS / Linux。

项目目标是：启动器本体保持轻量，客户端资源、Java Runtime、公告和图片资源都从远程配置与 CDN 获取；核心逻辑尽量独立于具体 UI，方便同一套业务逻辑被 WPF 和 Avalonia 复用。

## 当前状态

- Windows WPF 版：主力界面，已接入账号、客户端、下载、Java、启动、设置等流程。
- Avalonia 版：跨平台界面已接入 Core，可运行和测试主要流程，视觉风格与 Windows 版保持一致。
- Core 层：负责远程配置、客户端下载/解压/校验、Java 管理、账号管理和 Minecraft 启动。
- Models 层：存放客户端、账号、Java、公告、配置等共享模型。

当前 Microsoft 登录使用设备码流程，Client ID 固定为：

```text
00000000402b5328
```

登录弹窗中第三方验证和离线验证入口目前保留展示，但暂不允许选择。

## 项目结构

```text
DreamLauncherNew/
  DreamLauncher.slnx
  README.md
  local-cdn/
    clients.json
    java-runtimes.json
    announcement.json
  docs/
    samples/
  DreamLauncher/
    DreamLauncher.Models/
    DreamLauncher.Core/
    DreamLauncher.Windows/
      Dialogs/
      Accounts/
      Security/
      ViewModels/
      Assets/
    DreamLauncher.Avalonia/
      Dialogs/
      Accounts/
      Security/
      ViewModels/
      Assets/
```

## 数据目录

普通配置和运行数据会放在程序运行目录下：

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

账号资料单独放在用户目录：

```text
%AppData%/.DreamtcLauncher/
  accounts.json
```

敏感令牌在 Windows 版优先保存到 Windows Credential Manager；日志会做敏感信息脱敏。

## 固定远程配置

启动器当前会强制使用以下 Gitee raw 地址：

```text
https://raw.giteeusercontent.com/Chino7/DreamLauncher/raw/master/local-cdn/clients.json
https://raw.giteeusercontent.com/Chino7/DreamLauncher/raw/master/local-cdn/java-runtimes.json
https://raw.giteeusercontent.com/Chino7/DreamLauncher/raw/master/local-cdn/announcement.json
```

即使本地 `config.json` 中写了其他地址，读取或保存配置时也会自动修正回上面的固定地址。

## 客户端包要求

客户端下载采用完整压缩包方案：

- `packUrl` 必须是 HTTPS。
- `packSha256` 必须填写真实 SHA256。
- 下载完成后会校验 SHA256。
- 校验失败会删除损坏缓存文件。
- 解压时会防 Zip Slip 路径穿越。
- 解压不会删除原有 `.minecraft`，而是把包内 `.minecraft` 的内容合并/覆盖到程序目录下的 `.minecraft`。
- 下载和解压缓存完成后会清理。

当前启动使用版本隔离目录：

```text
.minecraft/versions/<versionId>/
```

Forge / NeoForge / Fabric 等版本 JSON 中的 `arguments.jvm`、`arguments.game`、natives、classpath、module path 都由 Core 生成启动参数。

## Java 管理

启动器按当前客户端的 `javaVersion` 自动选择 Java：

1. 当前客户端手动指定的 Java 路径
2. 启动器私有 Java Runtime
3. 系统已安装 Java

Java Runtime 下载后只会解压到启动器私有目录，不写入 `Program Files`，不修改系统环境变量，不污染 `PATH`，不需要管理员权限。

Windows 和 Avalonia 设置页都支持：

- 自动检测全部 Java 路径
- 显示推荐 Java
- 手动添加 Java 可执行文件
- 每个客户端单独保存 Java 路径
- 最大内存自动推荐、常用档位、自定义填写

## 构建运行

需要安装 .NET 10 SDK。

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

如果直接运行已构建的 exe：

```text
DreamLauncher/DreamLauncher.Windows/bin/Debug/net10.0-windows/DreamLauncher.Windows.exe
DreamLauncher/DreamLauncher.Avalonia/bin/Debug/net10.0/DreamLauncher.Avalonia.exe
```

## 发布

框架依赖发布，体积较小：

```powershell
dotnet publish .\DreamLauncher\DreamLauncher.Windows\DreamLauncher.Windows.csproj -c Release -r win-x64 --self-contained false
```

后续如需自包含单文件版，可增加：

```powershell
/p:PublishSingleFile=true --self-contained true
```

Avalonia 后续可按目标平台分别发布，例如 `win-x64`、`linux-x64`、`osx-x64`、`osx-arm64`。

## 配置示例

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
      "minecraftVersion": "1.20.1-Forge",
      "loader": "forge",
      "loaderVersion": "47.4.20",
      "javaVersion": 17,
      "serverAddress": "mc.example.com",
      "defaultMemoryMb": 4096,
      "jvmArgs": "-XX:+UseG1GC",
      "gameArgs": "",
      "packUrl": "https://cdn.example.com/packs/survival-1.0.0.zip",
      "packSha256": "填写真实SHA256",
      "packSize": 2147483648,
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
      "version": 17,
      "name": "Zulu JDK 17",
      "windowsX64Url": "https://cdn.example.com/java/zulu17.zip",
      "windowsX64Sha256": "填写真实SHA256",
      "size": 200000000
    },
    {
      "version": 21,
      "name": "Zulu JDK 21",
      "windowsX64Url": "https://cdn.example.com/java/zulu21.zip",
      "windowsX64Sha256": "填写真实SHA256",
      "size": 210000000
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

## 客户端字段说明

| 字段 | 说明 |
| --- | --- |
| `id` | 客户端唯一 ID |
| `name` | 显示名称 |
| `description` | 客户端描述 |
| `version` | 客户端资源包版本 |
| `minecraftVersion` | Minecraft 版本或版本目录 ID |
| `loader` | 加载器类型，如 `vanilla`、`forge`、`fabric`、`quilt`、`neoforge` |
| `loaderVersion` | 加载器版本 |
| `javaVersion` | 需要的 Java 主版本 |
| `serverAddress` | 启动后自动连接的服务器地址 |
| `defaultMemoryMb` | 默认最大内存，单位 MB |
| `jvmArgs` | 附加 JVM 参数 |
| `gameArgs` | 附加游戏参数 |
| `packUrl` | 完整客户端压缩包地址 |
| `packSha256` | 压缩包 SHA256 |
| `packSize` | 压缩包大小，单位字节 |
| `installDir` | 客户端安装目录名 |
| `coverUrl` | 封面图地址，预留 |
| `iconUrl` | 图标地址，预留 |
| `enabled` | 是否启用 |

## 已实现能力

- Microsoft 正版登录设备码流程
- 多账号保存、切换和删除
- Windows Credential Manager 令牌存储
- 客户端列表、安装、更新、修复
- 完整包下载、SHA256 校验、解压合并
- Java 自动检测、私有 Java 下载和安装
- Forge / NeoForge / Fabric 版本启动参数生成
- Windows / macOS / Linux natives 按系统解压
- 启动后自动连接服务器
- 启动成功后自动关闭启动器
- 日志脱敏
- 文件 SHA256 和 Size 快速计算工具
- WPF 与 Avalonia 双前端

## 后续计划

- 启动器自动更新
- 文件级 manifest 增量更新
- 更多平台 Java Runtime 配置
- 服务器在线人数
- 资源包 / 光影包切换
- 崩溃日志分析
- 多语言
- 下载限速
- Mod 列表展示
