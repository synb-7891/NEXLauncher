using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using NEXLauncher.Services;

namespace NEXLauncher;

/// <summary>
/// 「工具」页：Java 运行环境管理、存档备份/恢复、模组启用禁用。
/// 拆成独立 partial 文件，避免主窗口文件继续膨胀。
/// </summary>
public partial class MainWindow
{
    private readonly JavaService _javaService = new();
    private readonly SaveManagerService _saveService = new();

    private StackPanel? _javaListPanel;
    private TextBlock? _javaStatus;
    private StackPanel? _worldsPanel;
    private StackPanel? _backupsPanel;
    private TextBlock? _savesStatus;
    private StackPanel? _modsManagePanel;
    private TextBlock? _modsManageStatus;

    private UIElement BuildToolsPage()
    {
        var root = new StackPanel();
        root.Children.Add(Label("工具", 11, UiPalette.Accent));
        root.Children.Add(Heading("工具箱"));
        root.Children.Add(Body("常用操作一屏直达：游戏目录、缓存清理、日志与崩溃报告、Java、存档、模组、资源包与光影包。"));

        root.Children.Add(BuildToolboxTiles());
        root.Children.Add(BuildLogsCard());
        root.Children.Add(BuildJavaCard());
        root.Children.Add(BuildSavesCard());
        root.Children.Add(BuildModsManageCard());
        root.Children.Add(BuildResourcePacksCard());
        root.Children.Add(BuildResourceNavCard());

        RefreshWorlds();
        RefreshBackups();
        RefreshModsManage();
        RefreshLogsStatus();
        RefreshPackStatus();
        _ = RefreshJavaAsync();
        return root;
    }

    // ---------------- PCL2 风格快捷操作卡 ----------------

    private UIElement BuildToolboxTiles()
    {
        var grid = new UniformGrid { Columns = 4, Margin = new Thickness(0, 14, 0, 0) };
        grid.Children.Add(ToolboxTile("\uE8B7", "游戏目录", "打开 .minecraft", () => OpenGameDirectory()));
        grid.Children.Add(ToolboxTile("\uE74D", "清理缓存", "日志与崩溃报告", () => CleanCaches()));
        grid.Children.Add(ToolboxTile("\uE8A5", "运行日志", "查看最新日志", () => OpenLatestLog()));
        grid.Children.Add(ToolboxTile("\uE7BA", "崩溃报告", "定位启动失败原因", () => OpenCrashReports()));
        return grid;
    }

    /// <summary>PCL2 风格的功能方块：图标 + 标题 + 说明，整块可点。</summary>
    private Button ToolboxTile(string icon, string title, string detail, Action onClick)
    {
        var btn = new Button
        {
            Margin = new Thickness(0, 0, 10, 10),
            Padding = new Thickness(14, 12, 14, 12),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Background = Brush(UiPalette.Surface),
            BorderBrush = Brush(UiPalette.Border),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand
        };
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = icon,
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 20,
            Foreground = Brush(UiPalette.Accent),
            Margin = new Thickness(0, 0, 0, 8)
        });
        panel.Children.Add(new TextBlock
        {
            Text = title, FontSize = 13, FontWeight = FontWeights.SemiBold,
            Foreground = Brush(UiPalette.Text)
        });
        panel.Children.Add(new TextBlock
        {
            Text = detail, FontSize = 11, Foreground = Brush(UiPalette.Muted),
            Margin = new Thickness(0, 2, 0, 0), TextWrapping = TextWrapping.Wrap
        });
        btn.Content = panel;
        btn.Click += (_, _) => onClick();
        return btn;
    }

    // ---------------- 日志与缓存 ----------------

    private TextBlock? _logsStatus;

    private UIElement BuildLogsCard()
    {
        var card = Card();
        card.Margin = new Thickness(0, 4, 0, 0);
        var inner = new StackPanel { Margin = new Thickness(20, 14, 20, 16) };
        inner.Children.Add(Label("日志与缓存", 13, UiPalette.Text, FontWeights.SemiBold));

        _logsStatus = new TextBlock
        {
            FontSize = 12, Foreground = Brush(UiPalette.Muted),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 8)
        };
        inner.Children.Add(_logsStatus);

        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        row.Children.Add(ToolBtn("刷新", true, (_, _) => RefreshLogsStatus()));
        row.Children.Add(ToolBtn("查看最新日志", false, (_, _) => OpenLatestLog()));
        row.Children.Add(ToolBtn("打开日志目录", false, (_, _) =>
        {
            var dir = Path.Combine(_gameDirectory, "logs");
            Directory.CreateDirectory(dir);
            OpenInExplorer(dir);
        }));
        row.Children.Add(ToolBtn("打开崩溃报告", false, (_, _) => OpenCrashReports()));
        row.Children.Add(ToolBtn("清理缓存", false, (_, _) => CleanCaches()));
        inner.Children.Add(row);

        inner.Children.Add(new TextBlock
        {
            Text = "清理缓存只会删除 logs 下的日志与 crash-reports 里的报告，不会动存档、模组和游戏文件。",
            FontSize = 11, Foreground = Brush(UiPalette.Muted), TextWrapping = TextWrapping.Wrap
        });

        card.Children.Add(inner);
        return card;
    }

    private void RefreshLogsStatus()
    {
        if (_logsStatus == null) return;
        var logsDir = Path.Combine(_gameDirectory, "logs");
        var crashDir = Path.Combine(_gameDirectory, "crash-reports");
        var (logCount, logSize) = FolderStats(logsDir, "*.log*");
        var (crashCount, crashSize) = FolderStats(crashDir, "*");
        _logsStatus.Text = $"日志 {logCount} 个（{FormatBytes(logSize)}） · 崩溃报告 {crashCount} 个（{FormatBytes(crashSize)}）";
    }

    private static (int Count, long Size) FolderStats(string dir, string pattern)
    {
        try
        {
            if (!Directory.Exists(dir)) return (0, 0);
            var files = Directory.GetFiles(dir, pattern, SearchOption.TopDirectoryOnly);
            long size = 0;
            foreach (var f in files)
            {
                try { size += new FileInfo(f).Length; } catch { }
            }
            return (files.Length, size);
        }
        catch { return (0, 0); }
    }

    private void OpenLatestLog()
    {
        var logsDir = Path.Combine(_gameDirectory, "logs");
        var latest = Path.Combine(logsDir, "latest.log");
        try
        {
            if (!File.Exists(latest))
            {
                MessageBox.Show(this, "还没有日志文件。启动一次游戏后会自动生成 logs\\latest.log。",
                    "NEX", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            Process.Start(new ProcessStartInfo(latest) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "打开日志失败：" + ex.Message, "NEX", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OpenCrashReports()
    {
        var dir = Path.Combine(_gameDirectory, "crash-reports");
        try
        {
            Directory.CreateDirectory(dir);
            OpenInExplorer(dir);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "打开崩溃报告目录失败：" + ex.Message, "NEX", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void CleanCaches()
    {
        var logsDir = Path.Combine(_gameDirectory, "logs");
        var crashDir = Path.Combine(_gameDirectory, "crash-reports");
        var (logCount, logSize) = FolderStats(logsDir, "*.log*");
        var (crashCount, crashSize) = FolderStats(crashDir, "*");
        if (logCount + crashCount == 0)
        {
            MessageBox.Show(this, "没有需要清理的日志或崩溃报告。", "NEX", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var confirm = MessageBox.Show(this,
            $"将删除以下缓存文件：\n\n· {logsDir} 下的 {logCount} 个日志文件（{FormatBytes(logSize)}）\n· {crashDir} 下的 {crashCount} 个崩溃报告（{FormatBytes(crashSize)}）\n\n存档、模组、资源包和游戏本体不会被删除。是否继续？",
            "清理缓存", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (confirm != MessageBoxResult.Yes) return;

        var deleted = 0;
        long freed = 0;
        foreach (var (dir, pattern) in new[] { (logsDir, "*.log*"), (crashDir, "*") })
        {
            try
            {
                if (!Directory.Exists(dir)) continue;
                foreach (var f in Directory.GetFiles(dir, pattern, SearchOption.TopDirectoryOnly))
                {
                    try
                    {
                        var size = new FileInfo(f).Length;
                        File.Delete(f);
                        deleted++;
                        freed += size;
                    }
                    catch { /* 被游戏占用就跳过 */ }
                }
            }
            catch { }
        }
        RefreshLogsStatus();
        MessageBox.Show(this, $"已清理 {deleted} 个文件，释放 {FormatBytes(freed)}。", "NEX",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // ---------------- 资源包 / 光影包 ----------------

    private TextBlock? _packStatus;

    private UIElement BuildResourcePacksCard()
    {
        var card = Card();
        card.Margin = new Thickness(0, 14, 0, 0);
        var inner = new StackPanel { Margin = new Thickness(20, 14, 20, 16) };
        inner.Children.Add(Label("资源包与光影包", 13, UiPalette.Text, FontWeights.SemiBold));

        _packStatus = new TextBlock
        {
            FontSize = 12, Foreground = Brush(UiPalette.Muted),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 8)
        };
        inner.Children.Add(_packStatus);

        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        row.Children.Add(ToolBtn("刷新", true, (_, _) => RefreshPackStatus()));
        row.Children.Add(ToolBtn("打开资源包目录", false, (_, _) => OpenPackDir("resourcepacks")));
        row.Children.Add(ToolBtn("打开光影包目录", false, (_, _) => OpenPackDir("shaderpacks")));
        row.Children.Add(ToolBtn("打开数据包目录", false, (_, _) => OpenPackDir("datapacks")));
        inner.Children.Add(row);

        card.Children.Add(inner);
        return card;
    }

    private void RefreshPackStatus()
    {
        if (_packStatus == null) return;
        var (rp, rpSize) = FolderStats(Path.Combine(_gameDirectory, "resourcepacks"), "*.*");
        var (sp, spSize) = FolderStats(Path.Combine(_gameDirectory, "shaderpacks"), "*.*");
        var (dp, _) = FolderStats(Path.Combine(_gameDirectory, "datapacks"), "*.*");
        _packStatus.Text = $"资源包 {rp} 个（{FormatBytes(rpSize)}） · 光影包 {sp} 个（{FormatBytes(spSize)}） · 数据包 {dp} 个";
    }

    private void OpenPackDir(string name)
    {
        var dir = Path.Combine(_gameDirectory, name);
        try
        {
            Directory.CreateDirectory(dir);
            OpenInExplorer(dir);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "打开目录失败：" + ex.Message, "NEX", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ---------------- Java ----------------

    private UIElement BuildJavaCard()
    {
        var card = Card();
        card.Margin = new Thickness(0, 18, 0, 0);
        var inner = new StackPanel { Margin = new Thickness(20, 14, 20, 16) };
        inner.Children.Add(Label("Java 运行环境", 13, UiPalette.Text, FontWeights.SemiBold));

        _javaStatus = new TextBlock
        {
            Text = "正在检测本机 Java...", FontSize = 12, Foreground = Brush(UiPalette.Muted),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 8)
        };
        inner.Children.Add(_javaStatus);

        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        row.Children.Add(ToolBtn("自动检测", true, async (_, _) => await RefreshJavaAsync()));
        row.Children.Add(ToolBtn("下载 Java 21", false, async (_, _) => await InstallJavaAsync(21)));
        row.Children.Add(ToolBtn("打开 Java 目录", false, (_, _) =>
        {
            Directory.CreateDirectory(JavaService.ManagedRoot);
            OpenInExplorer(JavaService.ManagedRoot);
        }));
        inner.Children.Add(row);

        _javaListPanel = new StackPanel();
        inner.Children.Add(_javaListPanel);

        card.Children.Add(inner);
        return card;
    }

    private async Task RefreshJavaAsync()
    {
        if (_javaStatus == null) return;
        _javaStatus.Text = "正在检测本机 Java...";
        if (_javaListPanel != null) _javaListPanel.Children.Clear();

        IReadOnlyList<JavaRuntime> list;
        try { list = await Task.Run(() => _javaService.Detect(_settings.JavaPath)); }
        catch (Exception ex)
        {
            _javaStatus.Text = "检测失败：" + ex.Message;
            return;
        }

        if (_javaListPanel == null) return;
        if (list.Count == 0)
        {
            _javaStatus.Text = "没有检测到已安装的 Java。点「下载 Java 21」自动装一个（约 45 MB）。";
            return;
        }

        var current = list.FirstOrDefault(j =>
            string.Equals(j.JavaPath, _settings.JavaPath, StringComparison.OrdinalIgnoreCase));
        _javaStatus.Text = current != null
            ? $"当前使用：{current.Title} · {current.Home}"
            : $"检测到 {list.Count} 个 Java，当前为「自动选择」（启动时由启动库挑选）。选择其中一个可固定使用。";

        foreach (var j in list.Take(8))
        {
            var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
            var isCurrent = string.Equals(j.JavaPath, _settings.JavaPath, StringComparison.OrdinalIgnoreCase);

            var useBtn = ToolBtn(isCurrent ? "使用中" : "使用", isCurrent, (_, _) =>
            {
                _settings.JavaPath = j.JavaPath;
                _settings.Save();
                if (_javaStatus != null) _javaStatus.Text = $"已切换为：{j.Title} · {j.Home}";
                if (_javaListPanel != null)
                {
                    // 重新渲染按钮状态
                    _ = RefreshJavaAsync();
                }
            });
            useBtn.Margin = new Thickness(8, 0, 0, 0);
            useBtn.Padding = new Thickness(12, 4, 12, 4);
            useBtn.IsEnabled = !isCurrent;
            DockPanel.SetDock(useBtn, Dock.Right);
            row.Children.Add(useBtn);

            var text = new StackPanel();
            text.Children.Add(new TextBlock
            {
                Text = j.Title, FontSize = 12.5, Foreground = Brush(UiPalette.Text),
                FontWeight = FontWeights.SemiBold
            });
            text.Children.Add(new TextBlock
            {
                Text = j.JavaPath, FontSize = 11, Foreground = Brush(UiPalette.Muted),
                TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = j.JavaPath
            });
            row.Children.Add(text);
            _javaListPanel.Children.Add(row);
        }
    }

    private async Task InstallJavaAsync(int major)
    {
        if (_javaStatus == null) return;
        _javaStatus.Text = $"准备下载 Java {major}...";
        try
        {
            var progress = new Progress<string>(s => { if (_javaStatus != null) _javaStatus.Text = s; });
            var javaw = await _javaService.InstallAsync(major, progress);
            _settings.JavaPath = javaw;
            _settings.Save();
            _javaStatus.Text = $"已安装并设为默认：{javaw}";
            await RefreshJavaAsync();
        }
        catch (Exception ex)
        {
            _javaStatus.Text = "下载失败：" + ex.Message + "\n可以先手动安装 Java 21，再用「浏览」指定路径。";
        }
    }

    // ---------------- 存档 ----------------

    private UIElement BuildSavesCard()
    {
        var card = Card();
        card.Margin = new Thickness(0, 14, 0, 0);
        var inner = new StackPanel { Margin = new Thickness(20, 14, 20, 16) };
        inner.Children.Add(Label("存档管理", 13, UiPalette.Text, FontWeights.SemiBold));

        _savesStatus = new TextBlock
        {
            FontSize = 12, Foreground = Brush(UiPalette.Muted),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 8)
        };
        inner.Children.Add(_savesStatus);

        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        row.Children.Add(ToolBtn("刷新", true, (_, _) => { RefreshWorlds(); RefreshBackups(); }));
        row.Children.Add(ToolBtn("打开存档目录", false, (_, _) =>
        {
            var dir = Path.Combine(_gameDirectory, "saves");
            Directory.CreateDirectory(dir);
            OpenInExplorer(dir);
        }));
        row.Children.Add(ToolBtn("备份全部存档", false, (_, _) => BackupAllWorlds()));
        inner.Children.Add(row);

        _worldsPanel = new StackPanel();
        inner.Children.Add(_worldsPanel);

        inner.Children.Add(new TextBlock
        {
            Text = "备份记录", FontSize = 12, FontWeight = FontWeights.SemiBold,
            Foreground = Brush(UiPalette.TextDim), Margin = new Thickness(0, 10, 0, 2)
        });
        _backupsPanel = new StackPanel();
        inner.Children.Add(_backupsPanel);

        card.Children.Add(inner);
        return card;
    }

    private void RefreshWorlds()
    {
        if (_worldsPanel == null) return;
        _worldsPanel.Children.Clear();

        IReadOnlyList<WorldInfo> worlds;
        try { worlds = _saveService.ListWorlds(_gameDirectory); }
        catch (Exception ex)
        {
            if (_savesStatus != null) _savesStatus.Text = "读取存档失败：" + ex.Message;
            return;
        }

        if (_savesStatus != null)
            _savesStatus.Text = worlds.Count == 0
                ? "还没有存档。进入游戏创建一个世界后，这里会列出来，可随时备份。"
                : $"共 {worlds.Count} 个存档（{Path.Combine(_gameDirectory, "saves")}）";

        foreach (var w in worlds.Take(20))
        {
            var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };

            var del = ToolBtn("删除", false, (_, _) =>
            {
                if (MessageBox.Show($"确定删除存档「{w.Name}」吗？此操作不可恢复。", "NEX",
                        MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
                try { _saveService.DeleteWorld(w.Path, _gameDirectory); RefreshWorlds(); }
                catch (Exception ex) { MessageBox.Show("删除失败：" + ex.Message, "NEX"); }
            });
            var backup = ToolBtn("备份", true, (_, _) => BackupWorld(w.Path));
            var launch = ToolBtn("启动", false, (_, _) => _ = LaunchWorldAsync(w));
            foreach (var b in new[] { del, backup, launch })
            {
                b.Padding = new Thickness(10, 3, 10, 3);
                b.Margin = new Thickness(6, 0, 0, 0);
                DockPanel.SetDock(b, Dock.Right);
                row.Children.Add(b);
            }

            var text = new StackPanel();
            text.Children.Add(new TextBlock
            {
                Text = w.Name, FontSize = 12.5, Foreground = Brush(UiPalette.Text),
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            text.Children.Add(new TextBlock
            {
                Text = $"{w.SizeText} · 最后修改 {w.LastWrite:yyyy-MM-dd HH:mm}",
                FontSize = 11, Foreground = Brush(UiPalette.Muted)
            });
            row.Children.Add(text);
            _worldsPanel.Children.Add(row);
        }
    }

    /// <summary>用当前侧栏选中的版本启动某个存档（1.20+ 使用 --quickPlaySingleplayer 直接进入世界）。</summary>
    private async Task LaunchWorldAsync(WorldInfo world)
    {
        var versionId = (SideVersionBox.SelectedItem as string) ?? (_quickVersionBox?.SelectedItem as string);
        if (string.IsNullOrEmpty(versionId))
        {
            MessageBox.Show("请先在侧栏底部选择要使用的游戏版本。", "NEX", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _session ??= MinecraftService.CreateOfflineSession(_settings.QuickOfflineName);
        try
        {
            if (_savesStatus != null) _savesStatus.Text = $"正在用版本 {versionId} 启动存档「{world.Name}」...";
            await _minecraftService.StartGameAsync(versionId, _session, _settings,
                $"--quickPlaySingleplayer \"{world.Name}\"");
            if (_savesStatus != null)
                _savesStatus.Text = $"已启动存档「{world.Name}」（1.20 及以上会直接进入该世界，旧版本将正常进入主菜单）。";
        }
        catch (Exception ex)
        {
            if (_savesStatus != null) _savesStatus.Text = "启动失败：" + ex.Message;
            MessageBox.Show("启动失败：" + ex.Message, "NEX", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BackupWorld(string worldPath)
    {
        try
        {
            var zip = _saveService.BackupWorld(worldPath, _gameDirectory);
            RefreshBackups();
            if (_savesStatus != null)
                _savesStatus.Text = $"已备份：{Path.GetFileName(zip)}（{new FileInfo(zip).Length / 1048576.0:0.#} MB）";
        }
        catch (Exception ex)
        {
            MessageBox.Show("备份失败：" + ex.Message, "NEX", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void BackupAllWorlds()
    {
        try
        {
            var worlds = _saveService.ListWorlds(_gameDirectory);
            if (worlds.Count == 0) { MessageBox.Show("没有可备份的存档。", "NEX"); return; }
            var done = 0;
            foreach (var w in worlds)
            {
                _saveService.BackupWorld(w.Path, _gameDirectory);
                done++;
            }
            RefreshBackups();
            if (_savesStatus != null) _savesStatus.Text = $"已备份 {done} 个存档到 nex-backups 目录。";
        }
        catch (Exception ex)
        {
            MessageBox.Show("备份失败：" + ex.Message, "NEX", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void RefreshBackups()
    {
        if (_backupsPanel == null) return;
        _backupsPanel.Children.Clear();

        IReadOnlyList<BackupInfo> backups;
        try { backups = _saveService.ListBackups(_gameDirectory); }
        catch { return; }

        if (backups.Count == 0)
        {
            _backupsPanel.Children.Add(new TextBlock
            {
                Text = "还没有备份。点存档右侧「备份」即可打包为 zip。",
                FontSize = 12, Foreground = Brush(UiPalette.Muted)
            });
            return;
        }

        foreach (var b in backups.Take(6))
        {
            var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };

            var del = ToolBtn("删除", false, (_, _) =>
            {
                if (MessageBox.Show($"删除备份「{Path.GetFileName(b.Path)}」？", "NEX",
                        MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
                try { _saveService.DeleteBackup(b.Path, _gameDirectory); RefreshBackups(); }
                catch (Exception ex) { MessageBox.Show("删除失败：" + ex.Message, "NEX"); }
            });
            var restore = ToolBtn("恢复", true, (_, _) =>
            {
                if (MessageBox.Show($"把备份「{b.WorldName}」恢复到存档目录？\n同名世界会另存为「-恢复-时间」，不会覆盖。", "NEX",
                        MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
                try
                {
                    var restored = _saveService.RestoreBackup(b.Path, _gameDirectory);
                    RefreshWorlds();
                    if (_savesStatus != null)
                        _savesStatus.Text = "已恢复到：" + Path.GetFileName(restored);
                }
                catch (Exception ex) { MessageBox.Show("恢复失败：" + ex.Message, "NEX"); }
            });
            foreach (var btn in new[] { del, restore })
            {
                btn.Padding = new Thickness(10, 3, 10, 3);
                btn.Margin = new Thickness(6, 0, 0, 0);
                DockPanel.SetDock(btn, Dock.Right);
                row.Children.Add(btn);
            }

            row.Children.Add(new TextBlock
            {
                Text = $"{b.WorldName} · {b.Created:MM-dd HH:mm} · {b.SizeText}",
                FontSize = 12, Foreground = Brush(UiPalette.TextDim),
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = b.Path, VerticalAlignment = VerticalAlignment.Center
            });
            _backupsPanel.Children.Add(row);
        }

        if (backups.Count > 6)
            _backupsPanel.Children.Add(new TextBlock
            {
                Text = $"…以及另外 {backups.Count - 6} 个备份（在 nex-backups 目录）",
                FontSize = 11, Foreground = Brush(UiPalette.Muted)
            });
    }

    // ---------------- 模组 ----------------

    private UIElement BuildModsManageCard()
    {
        var card = Card();
        card.Margin = new Thickness(0, 14, 0, 0);
        var inner = new StackPanel { Margin = new Thickness(20, 14, 20, 16) };
        inner.Children.Add(Label("模组管理", 13, UiPalette.Text, FontWeights.SemiBold));

        _modsManageStatus = new TextBlock
        {
            FontSize = 12, Foreground = Brush(UiPalette.Muted),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 8)
        };
        inner.Children.Add(_modsManageStatus);

        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        row.Children.Add(ToolBtn("刷新", true, (_, _) => RefreshModsManage()));
        row.Children.Add(ToolBtn("打开 mods 目录", false, (_, _) =>
        {
            var dir = Path.Combine(_gameDirectory, "mods");
            Directory.CreateDirectory(dir);
            OpenInExplorer(dir);
        }));
        inner.Children.Add(row);

        _modsManagePanel = new StackPanel();
        inner.Children.Add(_modsManagePanel);

        card.Children.Add(inner);
        return card;
    }

    private void RefreshModsManage()
    {
        if (_modsManagePanel == null) return;
        _modsManagePanel.Children.Clear();

        IReadOnlyList<LocalModInfo> mods;
        try { mods = _localModService.ScanMods(_gameDirectory); }
        catch (Exception ex)
        {
            if (_modsManageStatus != null) _modsManageStatus.Text = "扫描失败：" + ex.Message;
            return;
        }

        var disabled = mods.Count(m => m.FileName.EndsWith("（已禁用）", StringComparison.Ordinal));
        if (_modsManageStatus != null)
            _modsManageStatus.Text = mods.Count == 0
                ? "mods 目录为空。下载模组后可以在这里启用或禁用，禁用不会删除文件。"
                : $"共 {mods.Count} 个模组，启用 {mods.Count - disabled} 个，禁用 {disabled} 个。";

        foreach (var m in mods.Take(60))
        {
            var isDisabled = m.FileName.EndsWith("（已禁用）", StringComparison.Ordinal);
            var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };

            var del = ToolBtn("删除", false, (_, _) =>
            {
                if (MessageBox.Show($"删除模组文件「{Path.GetFileName(m.FullPath)}」？", "NEX",
                        MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
                try { _localModService.Delete(m.FullPath, _gameDirectory); RefreshModsManage(); }
                catch (Exception ex) { MessageBox.Show("删除失败：" + ex.Message, "NEX"); }
            });
            var toggle = ToolBtn(isDisabled ? "启用" : "禁用", !isDisabled, (_, _) =>
            {
                try
                {
                    if (isDisabled) _localModService.Enable(m.FullPath);
                    else _localModService.Disable(m.FullPath);
                    RefreshModsManage();
                }
                catch (Exception ex) { MessageBox.Show("操作失败：" + ex.Message, "NEX"); }
            });
            foreach (var b in new[] { del, toggle })
            {
                b.Padding = new Thickness(10, 3, 10, 3);
                b.Margin = new Thickness(6, 0, 0, 0);
                DockPanel.SetDock(b, Dock.Right);
                row.Children.Add(b);
            }

            var badge = LoaderBadge(isDisabled ? "已禁用" : m.Loader);
            DockPanel.SetDock(badge, Dock.Left);
            row.Children.Add(badge);
            row.Children.Add(new TextBlock
            {
                Text = string.IsNullOrEmpty(m.Version) ? m.FileName : $"{m.FileName}  ({m.Version})",
                FontSize = 12, Foreground = isDisabled ? Brush(UiPalette.Muted) : Brush(UiPalette.TextDim),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = m.FullPath
            });
            _modsManagePanel.Children.Add(row);
        }

        if (mods.Count > 60)
            _modsManagePanel.Children.Add(new TextBlock
            {
                Text = $"…以及另外 {mods.Count - 60} 个", FontSize = 11, Foreground = Brush(UiPalette.Muted)
            });
    }

    // ---------------- 通用 ----------------

    private static Button ToolBtn(string text, bool accent, RoutedEventHandler onClick)
    {
        var b = new Button
        {
            Content = text,
            Padding = new Thickness(14, 6, 14, 6),
            Margin = new Thickness(0, 0, 8, 0),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Background = accent ? Brush(UiPalette.Accent) : Brush(UiPalette.SurfaceAlt),
            Foreground = accent ? Brush(UiPalette.Bg) : Brush(UiPalette.TextDim),
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand
        };
        b.Click += onClick;
        return b;
    }

    private static void OpenInExplorer(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch { }
    }

    // ---------------- 资源导航（常用站点速查） ----------------

    /// <summary>资源导航卡：把常用站点按类别收在一屏，点开即用系统浏览器访问。</summary>
    private static UIElement BuildResourceNavCard()
    {
        var card = Card();
        card.Margin = new Thickness(0, 14, 0, 0);
        card.Children.Add(Label("资源导航", 13, UiPalette.Text, FontWeights.SemiBold));
        card.Children.Add(Body("常用站点一站式跳转：百科、模组平台、材质光影、地图、皮肤与服务器列表。"));

        var groups = new (string Title, (string Name, string Url)[] Links)[]
        {
            ("百科与攻略", new[]
            {
                ("Minecraft Wiki（中文）", "https://zh.minecraft.wiki/"),
                ("Minecraft 官网", "https://www.minecraft.net/zh-hans"),
                ("合成表查询", "https://minecraft.wiki/w/Crafting")
            }),
            ("模组与整合包", new[]
            {
                ("Modrinth", "https://modrinth.com/"),
                ("CurseForge", "https://www.curseforge.com/minecraft"),
                ("MCIM 镜像站", "https://mod.mcimirror.top/")
            }),
            ("材质 / 光影 / 地图", new[]
            {
                ("材质包下载", "https://modrinth.com/resourcepacks"),
                ("光影包下载", "https://modrinth.com/shaders"),
                ("地图与建筑", "https://www.planetminecraft.com/projects/")
            }),
            ("皮肤与社区", new[]
            {
                ("LittleSkin 皮肤站", "https://littleskin.cn/"),
                ("NameMC 皮肤查询", "https://namemc.com/"),
                ("苦力怕论坛", "https://klpbbs.com/")
            }),
            ("服务器", new[]
            {
                ("官方服务器列表", "https://minecraft.wiki/w/Server"),
                ("MC 服务器列表", "https://minecraftservers.org/")
            })
        };

        foreach (var (title, links) in groups)
        {
            card.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 12,
                Foreground = Brush(UiPalette.Muted),
                Margin = new Thickness(0, 10, 0, 4)
            });
            var row = new WrapPanel();
            foreach (var (name, url) in links)
            {
                var b = new Button
                {
                    Content = name,
                    Padding = new Thickness(12, 6, 12, 6),
                    Margin = new Thickness(0, 0, 8, 8),
                    FontSize = 12,
                    Background = Brush(UiPalette.SurfaceAlt),
                    Foreground = Brush(UiPalette.TextDim),
                    BorderThickness = new Thickness(0),
                    Cursor = Cursors.Hand,
                    ToolTip = url
                };
                b.Click += (_, _) =>
                {
                    try { Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true }); }
                    catch { }
                };
                row.Children.Add(b);
            }
            card.Children.Add(row);
        }

        return card;
    }
}
