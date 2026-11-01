# NEX Launcher

轻量的 Minecraft 第三方启动器（WPF / .NET 8），支持微软正版登录、离线登录、外置登录（Yggdrasil / authlib-injector）、
多账号管理、3D 皮肤预览、PCL2 风格的资源下载（整合包 / 模组 / 资源包 / 光影包 / 数据包）、联机隧道与工具箱。

## 下载

| 版本 | 文件 | 大小 | 说明 |
| --- | --- | --- | --- |
| 公共版（推荐） | [NEXLauncher-0.4.0-full.zip](https://github.com/synb-7891/NEXLauncher/releases/download/v0.4.0/NEXLauncher-0.4.0-full.zip) | 约 68 MB | 自包含版，解压即用，无需安装 .NET 运行时 |
| 精简版 | [NEXLauncher-0.4.0.zip](https://github.com/synb-7891/NEXLauncher/releases/download/v0.4.0/NEXLauncher-0.4.0.zip) | 约 1.5 MB | 需要电脑已安装 .NET 8 桌面运行时 |

全部版本见 [Releases](https://github.com/synb-7891/NEXLauncher/releases)。

## 0.4.0 说明

- **已移除在线下载游戏版本功能**，改为「导入本机版本」：自动扫描官方启动器 / PCL2 / HMCL 的 `.minecraft` 目录，
  一键导入已有版本，并识别 Fabric / Forge / NeoForge / Quilt / OptiFine。
- 账号管理：多账号互不覆盖、外置登录、皮肤库与 3D 皮肤预览。
- 资源下载按 PCL2 规格重做，走 MCIM 镜像加速。
- 需要 Windows 10 及以上（x64）。

## 更新源

`manifest.json` 为启动器自动更新清单，格式：

```json
{ "version": "0.4.0", "url": "更新包直链", "notes": "更新内容", "publishedAt": "2026-11-01" }
```

## 许可

见 [LICENSE.txt](LICENSE.txt)。本项目与 Mojang / Microsoft 无关。
