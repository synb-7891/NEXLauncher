# NEX Launcher 伴侣 App（安卓 / iOS）

界面与 Windows 版完全同一份（`www/`），用 Capacitor 打包成原生 App。

## 今天就能用的方式：PWA（免打包）

`www/` 已是标准 PWA（manifest + Service Worker + 图标齐全）。把它托管到任意
https 静态空间（或 GitHub Pages），手机浏览器打开后：

- **安卓 Chrome**：菜单 → 「添加到主屏幕」→ 获得带图标的全屏 App
- **iOS Safari**：分享 → 「添加到主屏幕」

无需安卓 Studio、无需签名，断网也能打开界面（SW 缓存）。

## 正式 APK 打包步骤

前置：JDK 17 + Android Studio（含 SDK）。

```bash
cd mobile
npm install
npx cap add android          # 生成 android/ 原生工程（仅首次）
npx cap sync                 # 每次更新 www/ 后同步
npx cap open android         # 在 Android Studio 里 Run 或 Build APK
```

产物：`android/app/build/outputs/apk/debug/app-debug.apk`（调试包）
正式上架用 Android Studio → Generate Signed Bundle/APK。

## 当前 App 内可用的功能（浏览器模式）

皮肤站（9 个皮肤穿戴）、资源下载（Modrinth 实时搜索）、百宝箱、安全防护页展示、
个性化（白底主题/背景图/深浅切换）、联系我们、开始页面自由拖拽。

## 路线图（做成真「伴侣」）

1. PC 端启动器加一个局域网 HTTP API（配对码绑定）
2. App 远程：查看电脑端启动状态 / 推送下载任务（模组、整合包）到电脑
3. 账号同步、QQ 频道反馈直达

> 注意：安卓无法运行 Java 版 Minecraft（需桌面 Java），
> 手机端定位是「伴侣 App + 基岩版快捷入口」。
