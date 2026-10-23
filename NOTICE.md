# Third-party notices

BlockDock source is provided under GPL-3.0-only; see LICENSE.txt. NOT AN OFFICIAL MINECRAFT PRODUCT. NOT APPROVED BY OR ASSOCIATED WITH MOJANG OR MICROSOFT.

## BedrockLauncher

Copyright CarJem Generations and contributors. [BedrockLauncher](https://github.com/BedrockLauncher/BedrockLauncher), GPL-3.0. The build retrieves the unmodified UpdateProcessor and its managed support libraries from release `2026.10.19.270`, archive SHA256 `d23a5e778f1250afd258da4e03d1617fe6e7b435133101f9bfd522e06b666097`. No upstream launcher executable or UI is used.

Corresponding source commit: `09282ecb8f47262af519df1ce1e50dba970e016c`; localization submodule commit: `0f909b21c1229728f7c858a3bb475743b77a2e8c`. The build places these source archives and their licenses alongside the generated application. Extract localization into `modules/BedrockLauncher.Localization` when building upstream; see its `docs/COMPILING.md`.

Per-version data redirection follows the upstream PackageHandler, GdkSaveDataRedirector and AppContainerSid approach. BlockDock adds its own verified backups, per-version directories, managed-link journals, rollback, Windows storage path handling and launch UI. Integration is distributed under GPL-3.0-only.

## CmlLib

Copyright AlphaBs and contributors. CmlLib.Core 4.1.0 and Commons 4.0.0 are MIT. Forge/NeoForge installer sources are vendored from [CmlLib.Core.Installer.Forge](https://github.com/CmlLib/CmlLib.Core.Installer.Forge), MIT, with original headers. BlockDock changes: .NET 8 targeting, removal of Fody packaging and advertising navigation, internal installer project integration.

## Other dependencies

[Cubiomes](https://github.com/Cubitect/cubiomes), Copyright Cubitect and contributors, MIT. Vendored commit `e61f90580cbdd883214a8054670dacae655e59c0`; original sources and license are in `src/SeedMapNative/cubiomes/`. BlockDock adds a native C bridge and C# UI; the engine predicts supported Java biomes and structures. The Windows native build uses Zig 0.13.0 and MinGW runtime support; see `licenses/MinGW-runtime-COPYING.txt`.

The read-only Bedrock world parser was implemented with reference to Google LevelDB format documentation and [Mojang creator-tools](https://github.com/Mojang/creator-tools) commit `a8f2cd2fc7aa7e7e075dbf54a4597319aa8b71e1` (MIT). See `licenses/Mojang-Creator-Tools-LICENSE.txt`. No game database engine or game assets are bundled for map rendering.

HtmlAgilityPack, SharpZipLib, LZMA SDK, Newtonsoft.Json, Semver, .NET support assemblies and WebView2 retain their respective licenses in `licenses/`. JemExtensions is covered by the matching BedrockLauncher source. WebView2 browser runtime is not bundled.

## Images and services

The launcher mark and default background were supplied by the project maintainer. The application uses the supplied default PNG without modifying it. Minecraft, loader and modpack icons and player skins are read from local installations or their services at runtime; they are not included in the repository.

Minecraft account and profile services, FabricMC, Forge, NeoForge and Modrinth retain their own names and terms. Authentication is independent of user-configured game download sources. This repository includes only the publisher's public application ID, with no client secret or user credentials.
