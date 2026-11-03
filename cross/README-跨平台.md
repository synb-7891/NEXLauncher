# NEX Launcher 跨平台外壳（Mac / Linux / Windows）

用 **Photino**（原生 WebView 外壳：Win=WebView2 / macOS=WKWebView / Linux=WebKitGTK）
承载与 Windows 版**完全同一份**界面（`wwwroot/index.html`），消息协议一致（`{cmd:...}`）。

## 构建（本机已验证 0 错误）

```bash
cd cross/NEXLauncher.Cross
dotnet build -c Release

# 各平台发布示例：
dotnet publish -c Release -r linux-x64 --self-contained -o publish/linux
dotnet publish -c Release -r osx-arm64 --self-contained -o publish/mac   # Apple Silicon
dotnet publish -c Release -r osx-x64   --self-contained -o publish/mac-intel
dotnet publish -c Release -r win-x64   --self-contained -o publish/win
```

系统依赖：
- **Linux**：`libwebkit2gtk-4.1`（`sudo apt install libwebkit2gtk-4.1-dev`）
- **macOS**：无额外依赖（系统自带 WKWebView）

## 服务层移植现状

`D:\NEXLauncher\Services\` 共 21 个服务：

- **17 个纯 C#，直接复用**（CmlLib 启动核心、皮肤目录、事件日志等本身跨平台）
- **4 个需去 WPF 化**（替换 `MessageBox`/`Application.Current`/WPF 颜色即可）：
  - `UiPalette.cs` — 主题色板 → 改为纯数据类，颜色经 CSS 变量传给界面
  - `GuardDaemon.cs` — 守护进程 → 文件监控部分用 `System.IO` 已可跨平台，去掉 WPF 计时器
  - `DownloadManagerService.cs` / `UpdateService.cs` — 把 WPF Dispatcher 换成同步上下文

## 桥接

外壳已实现与 Windows 版相同的消息协议：
`open-url`（系统浏览器打开）已可用；`ready` / `refresh` / `launch` / `theme`
已留好接入点（见 `Program.cs` 注释），接上服务层后界面即显示真实数据。

界面里 `NEX_APP` 检测的是 WebView2；Photino 下界面以「浏览器模式」运行
（全部展示型功能可用），下一步在 `index.html` 桥接检测中加 Photino 的
`window.external` 通道即可打通真实功能。
