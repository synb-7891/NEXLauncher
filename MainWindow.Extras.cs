using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NEXLauncher.Services;

namespace NEXLauncher;

/// <summary>Java 管理、百宝箱（内存优化 / 清理垃圾 / 直链下载）、基岩版、正版披风。</summary>
public partial class MainWindow
{
    private static readonly HttpClient _fileHttp = new() { Timeout = TimeSpan.FromMinutes(30) };

    #region Java 管理
    private StackPanel? _javaPageListPanel;
    private TextBlock? _javaPageStatus;

    private UIElement BuildJavaPage()
    {
        var root = new StackPanel();
        root.Children.Add(Label("运行环境", 11, UiPalette.Accent));
        root.Children.Add(Heading("Java"));
        root.Children.Add(Body("启动游戏需要 Java 运行时。1.17 及以上建议 Java 17/21，1.16 及更早用 Java 8。这里可以扫描、下载、导入或移除运行时。"));

        var bar = Card();
        var row = new WrapPanel();
        var scanBtn = SmallButton("重新扫描");
        scanBtn.Margin = new Thickness(0, 0, 8, 8);
        scanBtn.Click += (_, _) => RefreshJavaList();
        row.Children.Add(scanBtn);

        var importBtn = SmallButton("手动导入…");
        importBtn.Margin = new Thickness(0, 0, 8, 8);
        importBtn.Click += (_, _) => ImportJavaManually();
        row.Children.Add(importBtn);

        foreach (var major in new[] { 8, 17, 21 })
        {
            var b = SmallButton($"下载 Java {major}");
            b.Margin = new Thickness(0, 0, 8, 8);
            b.Click += async (_, _) => await DownloadJavaAsync(major, b);
            row.Children.Add(b);
        }
        bar.Children.Add(row);

        _javaPageStatus = new TextBlock
        {
            FontSize = 11,
            Foreground = Brush(UiPalette.Muted),
            TextWrapping = TextWrapping.Wrap
        };
        bar.Children.Add(_javaPageStatus);
        root.Children.Add(bar);

        var listCard = Card();
        listCard.Margin = new Thickness(0, 14, 0, 0);
        _javaPageListPanel = new StackPanel();
        listCard.Children.Add(_javaPageListPanel);
        root.Children.Add(listCard);

        RefreshJavaList();
        return root;
    }

    private void RefreshJavaList()
    {
        if (_javaPageListPanel == null) return;
        _javaPageListPanel.Children.Clear();
        var runtimes = new JavaService().Detect(_settings.JavaPath);
        if (runtimes.Count == 0)
        {
            _javaPageListPanel.Children.Add(new TextBlock
            {
                Text = "未扫描到 Java。可点上方「下载 Java 17」自动安装，或用「手动导入」指定 java.exe。",
                FontSize = 12,
                Foreground = Brush(UiPalette.Muted),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(2, 4, 2, 4)
            });
        }

        foreach (var rt in runtimes)
        {
            var row = new Border
            {
                Background = Brush(UiPalette.SurfaceAlt),
                CornerRadius = new CornerRadius(9),
                Padding = new Thickness(12, 9, 12, 9),
                Margin = new Thickness(0, 0, 0, 6)
            };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var info = new StackPanel();
            var isCurrent = string.Equals(rt.JavaPath, _settings.JavaPath, StringComparison.OrdinalIgnoreCase);
            info.Children.Add(new TextBlock
            {
                Text = rt.Title + (isCurrent ? "　（当前使用）" : ""),
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brush(isCurrent ? UiPalette.Accent : UiPalette.Text)
            });
            info.Children.Add(new TextBlock
            {
                Text = rt.JavaPath,
                FontSize = 11,
                Foreground = Brush(UiPalette.Muted),
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            grid.Children.Add(info);

            var btns = new StackPanel { Orientation = Orientation.Horizontal };
            if (!isCurrent)
            {
                var use = SmallButton("设为默认");
                use.Margin = new Thickness(0, 0, 6, 0);
                use.Click += (_, _) =>
                {
                    _settings.JavaPath = rt.JavaPath;
                    _settings.Save();
                    RefreshJavaList();
                };
                btns.Children.Add(use);
            }
            var openHome = SmallButton("打开目录");
            openHome.Margin = new Thickness(0, 0, 6, 0);
            openHome.Click += (_, _) => OpenPath(rt.Home);
            btns.Children.Add(openHome);

            Grid.SetColumn(btns, 1);
            grid.Children.Add(btns);
            row.Child = grid;
            _javaPageListPanel.Children.Add(row);
        }

        if (_javaPageStatus != null)
            _javaPageStatus.Text = $"已识别 {runtimes.Count} 个运行时　内置目录：{JavaService.ManagedRoot}";
    }

    private void ImportJavaManually()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Java 可执行文件 (java.exe)|java.exe|所有文件|*.*" };
        if (dlg.ShowDialog(this) != true) return;
        _settings.JavaPath = dlg.FileName;
        _settings.Save();
        RefreshJavaList();
    }

    private async Task DownloadJavaAsync(int major, Button trigger)
    {
        try
        {
            trigger.IsEnabled = false;
            trigger.Content = $"下载 Java {major}…";
            var path = await new JavaService().InstallAsync(major, new Progress<string>(msg =>
            {
                trigger.Content = msg;
            }));
            _settings.JavaPath = path;
            _settings.Save();
            RefreshJavaList();
            MessageBox.Show(this, "Java 已安装并设为默认：\n" + path, "NEX", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "下载 Java 失败：" + ex.Message, "NEX", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            trigger.IsEnabled = true;
            trigger.Content = $"下载 Java {major}";
        }
    }
    #endregion

    #region 百宝箱
    private TextBlock? _memStatus;
    private TextBlock? _cleanStatus;
    private TextBox? _directUrlBox;
    private TextBox? _directDirBox;
    private TextBox? _directNameBox;
    private TextBlock? _directStatus;
    private ProgressBar? _directBar;

    [DllImport("psapi.dll")]
    private static extern bool EmptyWorkingSet(IntPtr hProcess);

    [DllImport("kernel32.dll")]
    private static extern IntPtr OpenProcess(int access, bool inherit, int pid);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    private static ulong AvailablePhysicalMb()
    {
        var st = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        return GlobalMemoryStatusEx(ref st) ? st.ullAvailPhys / 1024 / 1024 : 0;
    }

    private UIElement BuildTreasurePage()
    {
        var root = new StackPanel();
        root.Children.Add(Label("百宝箱", 11, UiPalette.Accent));
        root.Children.Add(Heading("百宝箱"));
        root.Children.Add(Body("一些顺手的小工具：整理内存、清理游戏垃圾、用启动器下载任意直链文件。"));

        // ---- 内存优化 ----
        var mem = Card();
        mem.Children.Add(Label("内存优化", 15, UiPalette.Text, FontWeights.SemiBold));
        mem.Children.Add(Body("整理各进程（含启动器与游戏）的内存工作集，把暂时不用的物理内存换出，降低任务管理器里的占用。不会影响运行中的程序。"));
        _memStatus = new TextBlock
        {
            Text = $"当前可用内存：{AvailablePhysicalMb()} MB",
            FontSize = 12,
            Foreground = Brush(UiPalette.Muted),
            Margin = new Thickness(0, 4, 0, 0)
        };
        mem.Children.Add(_memStatus);
        var memBtn = ActionButton("立即优化", (_, _) => OptimizeMemory());
        mem.Children.Add(memBtn);
        root.Children.Add(mem);

        // ---- 清理垃圾 ----
        var clean = Card();
        clean.Margin = new Thickness(0, 14, 0, 0);
        clean.Children.Add(Label("清理游戏垃圾", 15, UiPalette.Text, FontWeights.SemiBold));
        clean.Children.Add(Body("一键清理所有版本的 logs 与 crash-reports、临时文件。不会动存档、配置和模组。"));
        _cleanStatus = new TextBlock
        {
            FontSize = 12,
            Foreground = Brush(UiPalette.Muted),
            Margin = new Thickness(0, 4, 0, 0),
            TextWrapping = TextWrapping.Wrap
        };
        clean.Children.Add(_cleanStatus);
        var cleanBtn = ActionButton("一键清理", (_, _) => CleanGameJunk());
        clean.Children.Add(cleanBtn);
        root.Children.Add(clean);

        // ---- 直链下载 ----
        var dl = Card();
        dl.Margin = new Thickness(0, 14, 0, 0);
        dl.Children.Add(Label("下载任意文件", 15, UiPalette.Text, FontWeights.SemiBold));
        dl.Children.Add(Body("用启动器的下载引擎下载任意直链文件（网盘等需要登录/鉴权的链接会失败，属正常）。"));

        dl.Children.Add(Label("下载地址", 11, UiPalette.Muted));
        _directUrlBox = new TextBox { Padding = new Thickness(8, 6, 8, 6), FontSize = 12 };
        dl.Children.Add(_directUrlBox);

        var dirRow = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        dirRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        dirRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _directDirBox = new TextBox
        {
            Padding = new Thickness(8, 6, 8, 6),
            FontSize = 12,
            Text = string.IsNullOrWhiteSpace(_settings.DownloadDir)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads")
                : _settings.DownloadDir
        };
        dirRow.Children.Add(_directDirBox);
        var pickDir = SmallButton("选择文件夹");
        pickDir.Margin = new Thickness(8, 0, 0, 0);
        pickDir.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "选择保存位置" };
            if (dlg.ShowDialog(this) == true) _directDirBox.Text = dlg.FolderName;
        };
        Grid.SetColumn(pickDir, 1);
        dirRow.Children.Add(pickDir);
        dl.Children.Add(Label("保存到", 11, UiPalette.Muted));
        dl.Children.Add(dirRow);

        dl.Children.Add(Label("文件名（留空则自动从链接获取）", 11, UiPalette.Muted));
        _directNameBox = new TextBox { Padding = new Thickness(8, 6, 8, 6), FontSize = 12 };
        dl.Children.Add(_directNameBox);

        _directBar = new ProgressBar { Height = 6, Margin = new Thickness(0, 10, 0, 0), Visibility = Visibility.Collapsed };
        dl.Children.Add(_directBar);
        _directStatus = new TextBlock
        {
            FontSize = 11,
            Foreground = Brush(UiPalette.Muted),
            Margin = new Thickness(0, 6, 0, 0),
            TextWrapping = TextWrapping.Wrap
        };
        dl.Children.Add(_directStatus);

        var dlBtn = ActionButton("开始下载", async (_, _) => await DirectDownloadAsync());
        dl.Children.Add(dlBtn);
        root.Children.Add(dl);

        return root;
    }

    private void OptimizeMemory()
    {
        var before = AvailablePhysicalMb();
        var done = 0;
        foreach (var p in Process.GetProcesses())
        {
            IntPtr h = IntPtr.Zero;
            try
            {
                h = OpenProcess(0x0100 | 0x0400, false, p.Id); // PROCESS_SET_QUOTA | PROCESS_QUERY_INFORMATION
                if (h == IntPtr.Zero) continue;
                if (EmptyWorkingSet(h)) done++;
            }
            catch { }
            finally
            {
                if (h != IntPtr.Zero) CloseHandle(h);
                p.Dispose();
            }
        }
        var after = AvailablePhysicalMb();
        if (_memStatus != null)
            _memStatus.Text = $"已整理 {done} 个进程，可用内存 {before} MB → {after} MB（释放约 {Math.Max(0, (long)after - (long)before)} MB）";
    }

    private void CleanGameJunk()
    {
        long totalBytes = 0;
        var files = 0;
        var dirs = new List<string>();
        var versionsRoot = Path.Combine(_gameDirectory, "versions");
        if (Directory.Exists(versionsRoot))
            foreach (var d in Directory.GetDirectories(versionsRoot)) dirs.Add(d);
        dirs.Add(_gameDirectory);

        foreach (var root in dirs)
        {
            foreach (var sub in new[] { "logs", "crash-reports" })
            {
                var path = Path.Combine(root, sub);
                if (!Directory.Exists(path)) continue;
                foreach (var f in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        var info = new FileInfo(f);
                        totalBytes += info.Length;
                        info.Delete();
                        files++;
                    }
                    catch { }
                }
            }
        }

        if (_cleanStatus != null)
        {
            _cleanStatus.Text = files == 0
                ? "没有发现需要清理的游戏垃圾。"
                : $"已清理 {files} 个文件，释放约 {totalBytes / 1024.0 / 1024.0:0.#} MB。";
        }
    }

    private async Task DirectDownloadAsync()
    {
        var url = _directUrlBox?.Text.Trim() ?? "";
        var dir = _directDirBox?.Text.Trim() ?? "";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != "http" && uri.Scheme != "https"))
        {
            MessageBox.Show(this, "请输入有效的下载地址（http/https）。", "NEX", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (string.IsNullOrWhiteSpace(dir))
        {
            MessageBox.Show(this, "请选择保存位置。", "NEX", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var name = _directNameBox?.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            name = Path.GetFileName(uri.LocalPath);
            if (string.IsNullOrWhiteSpace(name)) name = "download.bin";
        }
        try
        {
            Directory.CreateDirectory(dir);
            var dest = Path.Combine(dir, name);
            if (_directBar != null) { _directBar.Visibility = Visibility.Visible; _directBar.Value = 0; }
            if (_directStatus != null) _directStatus.Text = "准备下载…";

            using var resp = await _fileHttp.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength ?? 0;
            await using var fs = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None);
            await using var stream = await resp.Content.ReadAsStreamAsync();
            var buffer = new byte[81920];
            long received = 0;
            int read;
            while ((read = await stream.ReadAsync(buffer)) > 0)
            {
                await fs.WriteAsync(buffer.AsMemory(0, read));
                received += read;
                if (total > 0 && _directBar != null)
                {
                    _directBar.Value = received * 100.0 / total;
                    if (_directStatus != null)
                        _directStatus.Text = $"{received / 1024.0 / 1024.0:0.#} MB / {total / 1024.0 / 1024.0:0.#} MB";
                }
            }
            if (_directStatus != null) _directStatus.Text = "下载完成：" + dest;
            if (_directBar != null) _directBar.Value = 100;
        }
        catch (Exception ex)
        {
            if (_directStatus != null) _directStatus.Text = "下载失败：" + ex.Message;
        }
    }
    #endregion

    #region 基岩版
    private static readonly string[] BedrockPackages =
    [
        "Microsoft.MinecraftUWP_8wekyb3d8bbwe",
        "Microsoft.MinecraftWindowsBeta_8wekyb3d8bbwe"
    ];

    private static bool BedrockInstalled(out string path)
    {
        path = "";
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        foreach (var pkg in BedrockPackages)
        {
            var dir = Path.Combine(local, "Packages", pkg);
            if (Directory.Exists(dir))
            {
                path = dir;
                return true;
            }
        }
        return false;
    }

    private UIElement BuildBedrockPage()
    {
        var root = new StackPanel();
        root.Children.Add(Label("Bedrock", 11, UiPalette.Accent));
        root.Children.Add(Heading("基岩版"));
        root.Children.Add(Body("启动本机已安装的基岩版（微软商店版），或把世界 / 资源包 / 皮肤直接交给游戏导入。基岩版的联机与成就需要在游戏内登录微软账号。"));

        var state = BedrockInstalled(out var pkgPath);

        var status = Card();
        status.Children.Add(Label("安装状态", 15, UiPalette.Text, FontWeights.SemiBold));
        status.Children.Add(new TextBlock
        {
            Text = state ? "已检测到本机安装的基岩版。" : "未检测到基岩版。可前往微软商店安装正式版或预览版。",
            FontSize = 13,
            Foreground = Brush(state ? UiPalette.Accent : UiPalette.MutedLight),
            Margin = new Thickness(0, 4, 0, 2),
            TextWrapping = TextWrapping.Wrap
        });
        if (state) status.Children.Add(Body("位置：" + pkgPath));

        var row = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        var launch = SmallButton("启动基岩版");
        launch.Margin = new Thickness(0, 0, 8, 8);
        launch.Background = Brush(UiPalette.Accent);
        launch.Foreground = Brush(UiPalette.Bg);
        launch.Click += (_, _) => LaunchBedrock();
        row.Children.Add(launch);

        var rescan = SmallButton("重新检测");
        rescan.Margin = new Thickness(0, 0, 8, 8);
        rescan.Click += (_, _) => ShowPage("基岩版", true);
        row.Children.Add(rescan);

        var store = SmallButton("打开微软商店");
        store.Margin = new Thickness(0, 0, 8, 8);
        store.Click += (_, _) => OpenUrl("ms-windows-store://pdp/?productid=9NBLGGH2JHXJ");
        row.Children.Add(store);

        var preview = SmallButton("预览版（Insider）");
        preview.Margin = new Thickness(0, 0, 8, 8);
        preview.Click += (_, _) => OpenUrl("ms-windows-store://search/?query=Minecraft%20Preview");
        row.Children.Add(preview);
        status.Children.Add(row);
        root.Children.Add(status);

        // 导入
        var importCard = Card();
        importCard.Margin = new Thickness(0, 14, 0, 0);
        importCard.Children.Add(Label("导入到基岩版", 15, UiPalette.Text, FontWeights.SemiBold));
        importCard.Children.Add(Body("支持世界（.mcworld）、整合资源（.mcaddon）、资源/行为包（.mcpack）、世界模板（.mctemplate）、皮肤与存档。选中后会自动拉起基岩版导入。"));
        var importRow = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        var impBtn = SmallButton("选择文件并导入…");
        impBtn.Click += (_, _) => ImportToBedrock();
        importRow.Children.Add(impBtn);
        var impFolder = SmallButton("打开基岩版目录");
        impFolder.Margin = new Thickness(8, 0, 0, 0);
        impFolder.Click += (_, _) =>
        {
            if (BedrockInstalled(out var p))
                OpenPath(Path.Combine(p, "LocalState", "games", "com.mojang"));
            else
                MessageBox.Show(this, "未检测到基岩版。", "NEX", MessageBoxButton.OK, MessageBoxImage.Information);
        };
        importRow.Children.Add(impFolder);
        importCard.Children.Add(importRow);
        root.Children.Add(importCard);

        var tip = Card();
        tip.Margin = new Thickness(0, 14, 0, 0);
        tip.Children.Add(Label("说明", 15, UiPalette.Text, FontWeights.SemiBold));
        tip.Children.Add(Body("基岩版以微软商店应用方式运行，启动器通过系统协议 minecraft:// 拉起本机已安装的游戏；离线账号也可以拉起游戏进行单机游玩。"));
        root.Children.Add(tip);

        return root;
    }

    private void LaunchBedrock()
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = "minecraft://", UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "启动失败：" + ex.Message + "\n\n请先在微软商店安装基岩版。", "NEX",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ImportToBedrock()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择要导入基岩版的文件",
            Filter = "基岩版文件 (*.mcworld;*.mcaddon;*.mcpack;*.mctemplate;*.png;*.zip)|*.mcworld;*.mcaddon;*.mcpack;*.mctemplate;*.png;*.zip|所有文件|*.*",
            Multiselect = true
        };
        if (dlg.ShowDialog(this) != true) return;
        var ok = 0;
        foreach (var f in dlg.FileNames)
        {
            try
            {
                Process.Start(new ProcessStartInfo { FileName = f, UseShellExecute = true });
                ok++;
            }
            catch { }
        }
        MessageBox.Show(this,
            ok == 0
                ? "导入失败：系统无法打开这些文件。\n\n请先安装并至少运行过一次基岩版。"
                : $"已把 {ok} 个文件交给基岩版导入，请在游戏内确认。\n\n若没有反应，请先手动运行一次游戏后再试。",
            "NEX", MessageBoxButton.OK, MessageBoxImage.Information);
    }
    #endregion

    #region 正版披风
    private WrapPanel? _capePanel;
    private TextBlock? _capeStatus;

    /// <summary>账号页的披风卡片：列出正版账号可用的披风，点击即穿戴。</summary>
    private UIElement BuildCapesCard()
    {
        var card = Card();
        card.Margin = new Thickness(0, 16, 0, 0);
        var inner = new StackPanel { Margin = new Thickness(16, 14, 16, 16) };
        card.Children.Add(inner);

        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(new TextBlock
        {
            Text = "\uE7B8",
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 14,
            Foreground = Brush(UiPalette.Accent),
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center
        });
        head.Children.Add(new TextBlock
        {
            Text = "披风",
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush(UiPalette.Text),
            VerticalAlignment = VerticalAlignment.Center
        });
        inner.Children.Add(head);
        inner.Children.Add(new TextBlock
        {
            Text = "正版账号可在这里查看并切换披风（由 minecraft.net 同步，进入游戏后生效）",
            FontSize = 11,
            Foreground = Brush(UiPalette.Muted),
            Margin = new Thickness(0, 4, 0, 10),
            TextWrapping = TextWrapping.Wrap
        });

        _capeStatus = new TextBlock
        {
            FontSize = 11,
            Foreground = Brush(UiPalette.Muted),
            Margin = new Thickness(0, 0, 0, 8),
            TextWrapping = TextWrapping.Wrap
        };
        inner.Children.Add(_capeStatus);

        _capePanel = new WrapPanel();
        inner.Children.Add(_capePanel);

        var refresh = SmallButton("刷新披风列表");
        refresh.Click += async (_, _) => await RefreshCapesAsync();
        inner.Children.Add(refresh);

        _ = RefreshCapesAsync();
        return card;
    }

    private async Task RefreshCapesAsync()
    {
        if (_capePanel == null) return;
        _capePanel.Children.Clear();
        if (_session == null || _settings.LastAccountType != "microsoft" || string.IsNullOrEmpty(_session.AccessToken))
        {
            _capeStatus!.Text = "只有正版（微软）账号可以查看与切换披风。";
            return;
        }
        _capeStatus!.Text = "正在获取披风列表…";
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get,
                "https://api.minecraftservices.com/minecraft/profile/capes");
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _session.AccessToken);
            using var resp = await _imgHttp.SendAsync(req);
            if (!resp.IsSuccessStatusCode)
            {
                _capeStatus.Text = $"获取披风失败（HTTP {(int)resp.StatusCode}），令牌可能已过期，请重新登录正版账号。";
                return;
            }
            using var doc = System.Text.Json.JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var capes = new List<(string Id, string Alias, string Url, bool Active)>();
            if (doc.RootElement.TryGetProperty("capes", out var arr))
            {
                foreach (var c in arr.EnumerateArray())
                {
                    capes.Add((
                        c.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "",
                        c.TryGetProperty("alias", out var al) ? al.GetString() ?? "Cape" : "Cape",
                        c.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "",
                        c.TryGetProperty("state", out var st) && st.GetString() == "ACTIVE"));
                }
            }
            if (capes.Count == 0)
            {
                _capeStatus.Text = "该账号没有可用披风（可在 minecraft.net 参与活动或购买获得）。";
                return;
            }
            _capeStatus.Text = $"共 {capes.Count} 个披风，点击卡片即可穿戴，点「关闭披风」可隐藏。";
            foreach (var cape in capes)
            {
                _capePanel.Children.Add(BuildCapeTile(cape.Id, cape.Alias, cape.Url, cape.Active));
            }
            // 关闭披风
            var off = SmallButton("关闭披风");
            off.Margin = new Thickness(0, 0, 8, 8);
            off.Click += async (_, _) => await SetCapeAsync(null);
            _capePanel.Children.Add(off);
        }
        catch (Exception ex)
        {
            _capeStatus.Text = "获取披风失败：" + ex.Message;
        }
    }

    private UIElement BuildCapeTile(string id, string alias, string url, bool active)
    {
        var border = new Border
        {
            Width = 118,
            Margin = new Thickness(0, 0, 10, 10),
            Padding = new Thickness(6),
            Background = Brush(UiPalette.SurfaceAlt),
            BorderBrush = Brush(active ? UiPalette.Accent : UiPalette.Border),
            BorderThickness = new Thickness(active ? 1.5 : 1),
            CornerRadius = new CornerRadius(8),
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = active ? $"{alias}（当前穿戴）" : $"点击穿戴 {alias}"
        };
        var col = new StackPanel();
        var imgHost = new Border
        {
            Height = 72,
            CornerRadius = new CornerRadius(6),
            Background = Brush(UiPalette.Bg),
            ClipToBounds = true
        };
        var img = new Image { Stretch = Stretch.Uniform };
        imgHost.Child = img;
        col.Children.Add(imgHost);
        col.Children.Add(new TextBlock
        {
            Text = alias,
            FontSize = 10,
            Foreground = Brush(UiPalette.Muted),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 4, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        border.Child = col;

        if (!string.IsNullOrEmpty(url))
            _ = Task.Run(async () =>
            {
                var bmp = await LoadRemoteBitmapAsync(url);
                if (bmp != null)
                    await Dispatcher.InvokeAsync(() => img.Source = bmp);
            });

        border.MouseLeftButtonUp += async (_, _) => await SetCapeAsync(id);
        return border;
    }

    private async Task SetCapeAsync(string? capeId)
    {
        if (_session == null) return;
        if (_capeStatus != null) _capeStatus.Text = capeId == null ? "正在关闭披风…" : "正在穿戴披风…";
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Put,
                "https://api.minecraftservices.com/minecraft/profile/capes/active");
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _session.AccessToken);
            if (capeId == null)
            {
                req.Method = HttpMethod.Delete;
            }
            else
            {
                req.Content = new StringContent(
                    System.Text.Json.JsonSerializer.Serialize(new { capeId }),
                    System.Text.Encoding.UTF8, "application/json");
            }
            using var resp = await _imgHttp.SendAsync(req);
            if (capeId == null ? resp.StatusCode == System.Net.HttpStatusCode.OK
                               : resp.IsSuccessStatusCode)
            {
                if (_capeStatus != null) _capeStatus.Text = capeId == null ? "披风已关闭。" : "披风已穿戴，进入游戏即可看到。";
                await RefreshCapesAsync();
            }
            else
            {
                if (_capeStatus != null) _capeStatus.Text = $"操作失败（HTTP {(int)resp.StatusCode}），请重新登录后再试。";
            }
        }
        catch (Exception ex)
        {
            if (_capeStatus != null) _capeStatus.Text = "操作失败：" + ex.Message;
        }
    }
    #endregion

    private void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true }); } catch { }
    }

    /// <summary>基岩版 / 商店链接可用性检查（内部使用，避免编译器警告）。</summary>
    private static bool HasBedrockPackage() => BedrockInstalled(out _);
}
