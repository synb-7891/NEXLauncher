using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using NEXLauncher.Services;

namespace NEXLauncher;

/// <summary>
/// 整合包安装/管理入口。完整版与公共版都具备：
/// - 从文件安装（.mrpack / CurseForge .zip / 普通整合 zip）
/// - 已安装整合包列表与一键卸载
/// 公共版不下载游戏版本，缺少基底版本时只提示，不联网安装（与「不提供在线下载游戏」保持一致）。
/// </summary>
public partial class MainWindow
{
    /// <summary>已安装整合包列表（用于版本页卡片与卸载）。</summary>
    private List<ModpackRecord> InstalledModpacks => ModpackService.ListInstalled(_gameDirectory);

    /// <summary>打开文件选择器并安装整合包。</summary>
    private async void PickAndInstallModpackAsync()
    {
        try
        {
            var modpackDir = Path.Combine(_gameDirectory, "modpacks");
            var dlg = new OpenFileDialog
            {
                Title = "选择整合包文件",
                Filter = "整合包 (*.mrpack;*.zip)|*.mrpack;*.zip|Modrinth 整合包 (*.mrpack)|*.mrpack|整合包压缩包 (*.zip)|*.zip|所有文件 (*.*)|*.*",
                InitialDirectory = Directory.Exists(modpackDir) ? modpackDir : _gameDirectory
            };
            if (dlg.ShowDialog(this) != true) return;
            await InstallModpackAsync(dlg.FileName);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "打开整合包失败：\n" + ex.Message, "NEX",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>安装指定整合包文件（下载完成后的「立即安装」也走这里）。</summary>
    private async Task InstallModpackAsync(string path)
    {
        if (!File.Exists(path))
        {
            MessageBox.Show(this, "找不到整合包文件：\n" + path, "NEX",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        ModpackInfo info;
        try
        {
            info = await ModpackService.InspectAsync(path);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "无法读取整合包，文件可能已损坏：\n" + ex.Message, "NEX",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

#if PUBLIC_EDITION
        const bool allowDownload = false;
#else
        const bool allowDownload = true;
#endif

        var dlg = new ModpackInstallDialog(
            info, _gameDirectory, _minecraftService.GetLocalInstalledVersions(),
            EnsureBaseVersionAsync, allowDownload)
        {
            Owner = this
        };
        dlg.ShowDialog();

        if (dlg.Result != null)
        {
            // 安装后刷新游戏版本页，让玩家直接看到新版本/新模组
            _ = LoadVersionsForCatalogAsync();
            RefreshVersionPageSafe();
        }
    }

    /// <summary>
    /// 完整版：整合包缺少基底版本时自动下载游戏版本 + 对应模组加载器。
    /// 公共版：直接返回 null（公共版不联网下载游戏版本）。
    /// </summary>
    private async Task<string?> EnsureBaseVersionAsync(string mcVersion, string loaderId, string loaderVersion)
    {
#if PUBLIC_EDITION
        await Task.CompletedTask;
        return null;
#else
        if (string.IsNullOrWhiteSpace(mcVersion)) return null;
        var installed = _minecraftService.GetLocalInstalledVersions();

        // 没有加载器：只要原版本体
        if (string.IsNullOrWhiteSpace(loaderId))
        {
            if (installed.Contains(mcVersion)) return mcVersion;
            await _minecraftService.InstallAsync(mcVersion);
            return mcVersion;
        }

        // 有加载器：先找现成的加载器版本
        var existing = installed.FirstOrDefault(v =>
            v.StartsWith(mcVersion + "-" + loaderId, StringComparison.OrdinalIgnoreCase)
            || v.Equals(loaderId, StringComparison.OrdinalIgnoreCase));
        if (existing != null) return existing;

        // 先装原版本体，再装加载器 profile
        if (!installed.Contains(mcVersion)) await _minecraftService.InstallAsync(mcVersion);

        var modloader = new ModloaderService();
        if (string.Equals(loaderId, "forge", StringComparison.OrdinalIgnoreCase))
        {
            var forgeVersion = loaderVersion;
            if (string.IsNullOrWhiteSpace(forgeVersion))
            {
                var info = await modloader.GetForgeLoaderInfoAsync(mcVersion);
                forgeVersion = info.Recommended ?? info.Latest ?? info.Versions.FirstOrDefault();
            }
            if (string.IsNullOrWhiteSpace(forgeVersion)) return mcVersion;
            return await modloader.InstallForgeAsync(_gameDirectory, mcVersion, forgeVersion);
        }

        var profileVersion = loaderVersion;
        if (string.IsNullOrWhiteSpace(profileVersion))
        {
            var list = loaderId.ToLowerInvariant() switch
            {
                "fabric" => await modloader.GetFabricLoaderVersionsAsync(mcVersion),
                "quilt" => await modloader.GetQuiltLoaderVersionsAsync(mcVersion),
                _ => new List<string>()
            };
            profileVersion = list.FirstOrDefault();
        }
        if (string.IsNullOrWhiteSpace(profileVersion)) return mcVersion;

        return await modloader.InstallLoaderProfileAsync(_gameDirectory, mcVersion, loaderId, profileVersion);
#endif
    }

    /// <summary>刷新版本页（若当前不在版本页则忽略异常，仅保证界面数据最新）。</summary>
    private void RefreshVersionPageSafe()
    {
        try { Dispatcher.Invoke(() => RefreshVersionList()); }
        catch { /* 页面尚未构建 */ }
    }

    /// <summary>整合包下载完成后的询问：立即安装 / 手动放入游戏 / 稍后。</summary>
    private async Task AskInstallDownloadedModpackAsync(string path)
    {
        var answer = MessageBox.Show(this,
            $"整合包已保存到：\n{path}\n\n"
            + "是否现在安装？\n"
            + "· 是：打开安装向导，装成一个独立游戏版本（不影响现有版本）\n"
            + "· 否：查看手动放入游戏的方法\n"
            + "· 取消：稍后再处理（文件已保留）",
            "安装整合包", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

        if (answer == MessageBoxResult.Yes)
        {
            await InstallModpackAsync(path);
            return;
        }
        if (answer == MessageBoxResult.No)
        {
            var manual = MessageBox.Show(this,
                "手动放入游戏的方法：\n\n"
                + "推荐用安装向导：「游戏版本」页右上角「整合包」→「安装整合包…」，选中这个文件即可一键装成一个独立版本。\n\n"
                + "纯手动的话：新建 versions\\<整合包名>\\ 目录，把 overrides（或 client-overrides）里的内容解压进去，"
                + "再把 modrinth.index.json 里 files 列出的模组下到该目录的 mods 文件夹。\n\n"
                + "是否打开整合包所在文件夹？",
                "手动安装整合包", MessageBoxButton.YesNo, MessageBoxImage.Information);
            if (manual == MessageBoxResult.Yes)
            {
                try
                {
                    var dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir)) System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\"");
                }
                catch { }
            }
        }
    }

    /// <summary>打开「整合包管理」窗口（已安装列表 + 卸载 + 从文件安装）。</summary>
    private void OpenModpackManager()
    {
        var win = new ModpackManagerWindow(_gameDirectory, async () =>
        {
            await Task.CompletedTask;
            RefreshVersionPageSafe();
        })
        {
            Owner = this
        };
        win.ShowDialog();
        RefreshVersionPageSafe();
    }

    /// <summary>版本页头部「整合包」按钮（完整版/公共版共用）。</summary>
    private Button ModpackButton()
    {
        var btn = new Button
        {
            Content = "整合包",
            Padding = new Thickness(14, 8, 14, 8),
            Background = Brush(UiPalette.SurfaceAlt),
            Foreground = Brush(UiPalette.TextDim),
            BorderThickness = new Thickness(0),
            Cursor = System.Windows.Input.Cursors.Hand,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 4, 8, 4),
            ToolTip = "安装 / 卸载整合包（支持 Modrinth .mrpack 与 CurseForge .zip）"
        };
        btn.Click += (_, _) => OpenModpackManager();
        return btn;
    }

    // ================= 自动化自检（NEX_MODPACK_E2E=1） =================
    // 输出：%TEMP%/nex_mp_e2e.json + nex_mp_versions.png / nex_mp_manager.png / nex_mp_dialog.png

    private async Task RunModpackE2EAsync()
    {
        var result = new Dictionary<string, object?>();
        try
        {
            await Task.Delay(2200);
            Exception? navError = null;
            await Dispatcher.InvokeAsync(() =>
            {
                try { ShowPage("游戏版本"); }
                catch (Exception ex) { navError = ex; }
            });
            result["navError"] = navError?.ToString();
            await Task.Delay(2500);

            result["currentPage"] = await Dispatcher.InvokeAsync(() => (string)CurrentPage);
            result["hostContentType"] = await Dispatcher.InvokeAsync(() => PageHost.Content?.GetType().Name);
            result["versionPageHasModpackButton"] = await Dispatcher.InvokeAsync(() =>
                FindVisualChildren<Button>(PageHost).Any(b => b.Content as string == "整合包"));
            await Dispatcher.InvokeAsync(() => SaveElementPng(PageHost, "nex_mp_versions.png"));

            // 1) 整合包管理窗口
            var mgr = new ModpackManagerWindow(_gameDirectory, () => Task.CompletedTask) { Owner = this };
            mgr.Show();
            await Task.Delay(1400);
            result["managerHasInstallButton"] = FindVisualChildren<Button>(mgr)
                .Any(b => (b.Content as string)?.Contains("安装整合包") == true);
            result["managerInstalledCount"] = ModpackService.ListInstalled(_gameDirectory).Count;
            SaveWindowPng(mgr, "nex_mp_manager.png");
            mgr.Close();

            // 2) 安装向导（用示例数据渲染，不真正安装）
            var demo = new ModpackInfo(
                Path.Combine(_gameDirectory, "modpacks", "demo.mrpack"), "mrpack",
                "Fabulously Optimized", "6.0.0", "1.21", "fabric", "0.15.11",
                [new ModpackFile("mods/fabric-api.jar", "https://example.com/x.jar", 2223 * 1024, null, null, "required")],
                27L * 1024 * 1024, 27, 0);
#if PUBLIC_EDITION
            const bool allowDownload = false;
#else
            const bool allowDownload = true;
#endif
            var dlg = new ModpackInstallDialog(demo, _gameDirectory,
                _minecraftService.GetLocalInstalledVersions(), EnsureBaseVersionAsync, allowDownload)
            { Owner = this };
            dlg.Show();
            await Task.Delay(1200);
            result["dialogHasInstallButton"] = FindVisualChildren<Button>(dlg)
                .Any(b => (b.Content as string) == "开始安装");
            SaveWindowPng(dlg, "nex_mp_dialog.png");
            dlg.Close();

            result["ok"] = true;
        }
        catch (Exception ex)
        {
            result["ok"] = false;
            result["error"] = ex.ToString();
        }

        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(Path.GetTempPath(), "nex_mp_e2e.json"),
                System.Text.Json.JsonSerializer.Serialize(result,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }

        await Dispatcher.InvokeAsync(Close);
    }

    private static void SaveWindowPng(Window window, string fileName)
    {
        try
        {
            window.UpdateLayout();
            if (window.Content is not FrameworkElement fe) return;
            var w = (int)Math.Ceiling(fe.ActualWidth);
            var h = (int)Math.Ceiling(fe.ActualHeight);
            if (w < 10 || h < 10) return;
            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
                w, h, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            rtb.Render(fe);
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
            using var fs = File.Create(Path.Combine(Path.GetTempPath(), fileName));
            enc.Save(fs);
        }
        catch { }
    }
}
