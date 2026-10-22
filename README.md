# BlockDock

Windows 上的 Minecraft Java 版与基岩版启动器，使用 C#、WPF 和 .NET 8。

## 功能

- 微软正版登录、玩家头像与可旋转的皮肤预览。
- Java 实例与 Fabric / Forge / NeoForge 安装、Java 自动检测、自动或手动内存分配。
- Modrinth 模组、光影、材质包和整合包搜索，按版本匹配安装。
- 基岩版签名包安装、启动前自动切换版本，独立存档与内容目录、备份和恢复。
- 下载队列、取消、并发控制、速度与保存位置。
- 内置默认背景，自定义图片、视频和本机 Wallpaper Engine 图片 / 视频壁纸。

## 构建

需要 Windows 和 [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)。在仓库根目录运行：

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

输出位于 `artifacts/app/BlockDock.exe`。脚本通过 NuGet 还原依赖，并校验、提取固定版本的 BedrockLauncher 解析组件，同时准备其对应源码和许可证；不执行上游启动器。缓存位于 `.cache/`。`-SkipBedrock` 可只构建界面与 Java 功能。

只编译项目：

```powershell
dotnet build src/BlockDock.csproj -c Release
```

回归检查使用独立测试目录，不操作真实游戏存档：

```powershell
dotnet run --project tests-v2/Checks.csproj -c Release -- --release-checks
dotnet run --project tests-v2/Checks.csproj -c Release -- --player-launch-checks
dotnet run --project tests-v2/Checks.csproj -c Release -- --isolation-checks
```

基岩版由 Windows 管理程序文件，同一频道一次启用一个版本。独立实例保存各自游戏数据；系统签名、依赖和游戏许可检查仍生效。

## 下载源

仓库不包含游戏文件及预设游戏本体下载源。首次下载时，在设置中填写自己的 Java、UWP 或 GDK 版本清单地址；模组等资源使用 Modrinth。

也可导入以下格式的 JSON 配置，地址应替换成自己的来源：

```json
{
  "schemaVersion": 1,
  "name": "我的下载源",
  "rules": [],
  "javaManifestUrl": "https://example.invalid/java-versions.json",
  "bedrockUwpUrl": "",
  "bedrockGdkUrl": ""
}
```

Java 清单使用标准版本清单结构。可选 `rules` 项以 `host` 和 `baseUrl` 改写游戏文件地址，保留相对路径；认证地址不接受改写。本机来源配置、账号、游戏数据和构建产物不提交到 Git。

## 许可

源码采用 GPL-3.0-only，见 [LICENSE.txt](LICENSE.txt)。第三方组件及素材来源见 [NOTICE.md](NOTICE.md) 和 [licenses/](licenses/)。

非 Minecraft 官方产品，未经 Mojang 或 Microsoft 批准，与其无关联。
