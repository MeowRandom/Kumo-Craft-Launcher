# Kumo Craft Launcher / 库莫

[English](README.md) | [简体中文](README.zh-CN.md)

Kumo 是一个使用 WPF 和 .NET 开发的 Windows Minecraft Java 版启动器。

当前稳定版本：`1.1`

## 功能

- 安装 Mojang 正式版、快照版和历史版本
- 自动检测并下载合适的 Java 运行时
- 支持 Fabric、Quilt、Forge、NeoForge、OptiFine 和 OptiFabric
- 每个版本可独立隔离存档、Mod 和配置
- 支持 Microsoft 正版账号和离线账号登录
- 使用 Minecraft 皮肤头像，并为离线账号提供 Steve 和 Alex 默认头像
- 缓存 3D 皮肤和披风预览
- 从 Modrinth 和 CurseForge 获取包含封面图和中文说明的 Mod
- 安装 Mod 时自动处理前置依赖
- 支持导入 PCL2、Modrinth 和 CurseForge 整合包
- 支持 Windows 原生滚轮速度和平滑滚动动画
- 提供浅色、深色和跟随系统三种主题
- 自动保存设置
- 全局游戏设置，并可向所有版本同步完整按键配置

## 运行

发布版本是无需安装运行时的 Windows x64 单文件程序。

1. 解压发布 ZIP。
2. 运行 `Kumo Craft Launcher.exe`。
3. 不需要管理员权限。

系统要求：

- Windows 10 或更高版本，64 位
- Microsoft Edge WebView2 Runtime
- 下载版本、Mod 和登录账号时需要网络连接

当前 Windows 和 Microsoft Edge 通常已经包含 WebView2。如果 3D 皮肤预览
无法加载，请从 [Microsoft](https://developer.microsoft.com/microsoft-edge/webview2/)
安装 WebView2 Evergreen Runtime。

启动器会使用自身所在目录中的 `.minecraft` 文件夹：

```text
<启动器所在目录>\.minecraft
```

如果启动器本身已经在 `.minecraft` 文件夹中，启动器会直接使用该文件夹。
你也可以在设置中选择其他父目录，Kumo 会在其中创建并使用 `.minecraft`
文件夹。设置会自动保存。

## 开发

从源码构建需要 .NET 9 SDK，运行冒烟测试需要 Java 17 或更高版本。

```powershell
.\build.ps1
```

构建脚本会生成独立的单文件程序：

```text
publish\Kumo Craft Launcher.exe
```

普通开发构建：

```powershell
dotnet restore .\Kumo.csproj --configfile .\NuGet.Config
dotnet build .\Kumo.csproj -c Release --no-restore
```

## 测试

```powershell
dotnet run --project .\tests\Kumo.SmokeTests\Kumo.SmokeTests.csproj `
  -c Release --configfile .\tests\Kumo.SmokeTests\NuGet.Config
```

## 账号与隐私

登录 Minecraft Java 正版账号需要完成 Microsoft 和 Xbox Live 验证。
玩家外观数据会缓存在本地，因此打开自定义界面时不需要等待网络请求。

Microsoft 刷新令牌使用 Windows DPAPI 加密。短期有效的 Minecraft
访问令牌只保存在内存中，不会以明文写入设置文件。

## 数据来源

- Mojang 版本信息、资源、Java 运行时和玩家配置
- 用于兼容 Minecraft 资源的 BMCLAPI 镜像
- Modrinth 项目与版本 API
- 通过公开 CFWidget API 获取的 CurseForge 信息
- MC百科公开页面中的 Mod 中文名称和说明

下载文件时，只要来源提供哈希值，Kumo 就会进行完整性校验。

## 许可证

MIT，详见 [LICENSE](LICENSE) 和 [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt)。

Kumo 与 Mojang Studios、Microsoft、Modrinth、CurseForge 或 MC百科
没有隶属关系。Minecraft 是 Mojang Studios 的商标。
