# DreamLauncher

Minecraft 服务器专属启动器首版骨架，目标平台为 Windows，使用 C#、.NET 10、WPF。项目拆分为：

- `DreamLauncher.Models`：共享数据模型。
- `DreamLauncher.Core`：账号、下载、解压、Java、客户端、启动逻辑，不直接依赖 WPF。
- `DreamLauncher.Windows`：WPF 界面、系统浏览器、Windows Credential Manager 令牌存储。

## 已落地

- 远程 `clients.json`、`java-runtimes.json`、`announcement.json` 模型与拉取。
- `%AppData%/DreamLauncher/` 本地目录与 `config.json` 自动创建。
- 配置损坏时备份并重建。
- HTTPS 下载强制校验。
- SHA256 校验失败自动删除损坏文件。
- Zip Slip 路径穿越防护。
- 客户端完整包下载、解压到临时目录、校验后替换对应实例目录。
- Java 手动路径、私有 JRE、系统 Java 三层检测。
- Java Runtime 私有目录安装，不修改 PATH，不写系统目录。
- Microsoft OAuth + Xbox Live + XSTS + Minecraft Profile 流程骨架。
- 敏感令牌保存到 Windows Credential Manager。
- 启动日志令牌脱敏。
- WPF 主界面和设置窗口。

## 运行

```powershell
dotnet build .\DreamLauncher.slnx --disable-build-servers -m:1 /p:UseSharedCompilation=false
dotnet run --project .\DreamLauncher\DreamLauncher.Windows\DreamLauncher.Windows.csproj
```

首次启动后，在设置里填写 HTTPS 的 `clients.json` 地址，以及 Microsoft OAuth Client ID。

如果暂时没有 Azure 账号，可以在主界面点击“测试账号”创建离线测试账号。离线测试账号不需要 Microsoft OAuth Client ID，只适合开发测试或 `online-mode=false` 的服务器；正版服务器仍需要 Microsoft 登录。

Microsoft 正版登录使用设备码流程：启动器会打开浏览器并显示登录代码，网页登录完成后自动继续。

皮肤站登录支持 authlib-injector / Yggdrasil API。主界面点击“皮肤站”后填写 API Root、账号和密码；启动皮肤站账号前，需要在设置里填写本机 `authlib-injector.jar` 路径。

本地界面测试可以先填写：

```text
file:///F:/Chino/DreamtcTamracLauncherNew/local-cdn/clients.json
```

`local-cdn` 中的 pack/java 下载地址是占位 HTTPS 地址，只用于测试客户端列表、公告和设置读取；正式下载前需要替换为真实 CDN 地址和 SHA256。

## 发布

框架依赖发布：

```powershell
dotnet publish .\DreamLauncher\DreamLauncher.Windows\DreamLauncher.Windows.csproj -c Release -r win-x64 --self-contained false
```

后续自包含单文件版可在发布参数中启用 `PublishSingleFile` 与 `SelfContained`。
