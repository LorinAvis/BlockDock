# BlockDock

Windows 上的 Minecraft Java 版与基岩版启动器，使用 C#、WPF 和 .NET 8。

## 下载

前往 [Releases 下载 Windows x64 便携版](https://github.com/LorinAvis/BlockDock/releases)。选择 `BlockDock-版本号-Windows-x64.zip`，完整解压后运行 `BlockDock.exe`。下载包包含 .NET 运行时，无需安装 SDK；GitHub 自动生成的 Source code 压缩包用于源码构建。

## 功能

- 微软正版登录、玩家头像与可旋转的皮肤预览。
- Java 实例与 Fabric / Forge / NeoForge 安装、Java 自动检测、自动或手动内存分配。
- Modrinth 模组、光影、材质包和整合包搜索，按版本匹配安装。
- 基岩版签名包安装、启动前自动切换版本，独立存档与内容目录、备份和恢复。
- 下载队列、取消、并发控制、速度与保存位置。
- 启动时检查新版、设置内手动检查，支持正式版与预览版更新提醒。
- 按实例管理 Java / 基岩版世界，查看种子、备份、导出、导入和复制到其他同版实例。
- 查看本地已探索地图；内置离线 Java 生物群系与结构预测，包含末地要塞，支持点击坐标并复制、结构搜索与距离排序、缩放定位和 PNG 导出。
- 统一深色界面，首页快捷访问、分类设置和独立下载任务管理。
- 内置默认背景，自定义图片、视频和本机 Wallpaper Engine 图片 / 视频壁纸。

## 构建

需要 Windows 和 [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)。在仓库根目录运行：

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

输出位于 `artifacts/app/BlockDock.exe`。脚本通过 NuGet 还原依赖，校验并缓存 Zig 0.13.0 编译离线预测组件，再提取固定版本的 BedrockLauncher 解析组件及其源码和许可证。缓存位于 `.cache/`。`-SkipBedrock` 可跳过基岩版组件。

只编译项目：

```powershell
dotnet build src/BlockDock.csproj -c Release
```

首次直接编译项目时，先运行 `powershell -ExecutionPolicy Bypass -File .\build-seed-map.ps1`，以生成离线预测 DLL。

从干净的 Git 提交生成包含运行时、依赖及对应源码的发布 ZIP：

```powershell
powershell -ExecutionPolicy Bypass -File .\package-release.ps1
```

发布包和 SHA256 文件输出到 `artifacts/releases/`，不提交到 Git，上传至 GitHub Releases。

回归检查使用独立测试目录，不操作真实游戏存档：

```powershell
dotnet run --project tests-v2/Checks.csproj -c Release -- --release-checks
dotnet run --project tests-v2/Checks.csproj -c Release -- --update-checks
dotnet run --project tests-v2/Checks.csproj -c Release -- --player-launch-checks
dotnet run --project tests-v2/Checks.csproj -c Release -- --isolation-checks
dotnet run --project tests-v2/Checks.csproj -c Release -- --world-checks
```

基岩版由 Windows 管理程序文件，同一频道一次启用一个版本。独立实例保存各自游戏数据；系统签名、依赖和游戏许可检查仍生效。

## 地图与存档

本地地图读取存档中已有区块，显示地表方块；灰格表示未探索或无法读取的区域。支持常见 Java Anvil 与基岩版 LevelDB 存档，部分较新、旧式或模组格式可能无法解析。查看和备份前请退出正在使用该世界的游戏。

Java 地图可只读使用对应已安装版本的方块贴图，结合高度、水深和群系着色显示地形。草、花等小植物不遮挡地面，海带和水草按水面显示；树冠、建筑和农田保留。未找到贴图的方块及基岩版使用地表配色，暂不叠加资源包或显示实时生物位置。

离线预测使用 Cubiomes，提供 Java 1.7–1.21.3 的指定算法基准；具体补丁版本以界面列表为准。基岩版预测、快照和更新版本尚未适配。预测图展示生物群系及部分结构，不能还原玩家建筑；数据包、模组与跨版本生成的世界可能不同，结构位置也不保证实际生成。

末地要塞使用传送门框架标记，坐标指主世界中的要塞位置，不是传送门房间的精确位置。更改种子、维度或算法后，需要重新生成地图。

复制到其他实例会保留原世界，不会转换 Java 与基岩版格式。移入回收目录的世界可通过“导入文件夹”恢复。

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
