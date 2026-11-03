using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NEXLauncher.Services;

namespace NEXLauncher;

/// <summary>模组管理页：按版本扫描 mods，支持搜索、状态/加载器筛选、启用禁用、删除、检查更新、导出整合包。</summary>
public partial class MainWindow
{
    private readonly List<LocalModInfo> _modsCache = [];
    private string _modsVersion = "";
    private string _modsStatusFilter = "全部状态";
    private string _modsLoaderFilter = "全部加载器";
    private string _modsQuery = "";
    private StackPanel? _modsListPanel;
    private TextBlock? _modsStatusText;
    private ComboBox? _modsVersionBox;

    /// <summary>版本隔离时 mods 在 versions/&lt;id&gt;/mods，否则在根目录 mods。</summary>
    private string ModsDirFor(string versionId)
    {
        var isolated = Path.Combine(_gameDirectory, "versions", versionId, "mods");
        if (!_settings.VersionIsolation) return Path.Combine(_gameDirectory, "mods");
        // 隔离目录不存在但根目录有 mods：沿用根目录，避免用户看不到已有模组
        if (!Directory.Exists(isolated) && Directory.Exists(Path.Combine(_gameDirectory, "mods"))
            && !Directory.Exists(Path.Combine(_gameDirectory, "versions", versionId)))
            return Path.Combine(_gameDirectory, "mods");
        return isolated;
    }

    private UIElement BuildModsPage()
    {
        var root = new StackPanel();
        root.Children.Add(Label("实例", 11, UiPalette.Accent));
        root.Children.Add(Heading("模组管理"));
        root.Children.Add(Body("扫描当前版本的 mods 文件夹，启用/禁用、删除、检查更新，或把整套模组导出成整合包分享给朋友。"));

        var versions = _minecraftService.GetLocalInstalledVersions();
        if (versions.Count == 0)
        {
            root.Children.Add(Body("还没有已安装的版本，请先在「游戏版本」页导入本机版本。"));
            return root;
        }
        if (string.IsNullOrEmpty(_modsVersion) || !versions.Contains(_modsVersion))
            _modsVersion = versions.Contains(_settings.LastQuickVersion) ? _settings.LastQuickVersion : versions[0];

        // ---- 工具条 ----
        var bar = Card();
        var row = new WrapPanel();
        row.Children.Add(Label("版本", 11, UiPalette.Muted));
        _modsVersionBox = new ComboBox { Width = 220, Padding = new Thickness(8, 6, 8, 6), Margin = new Thickness(0, 0, 12, 8) };
        _modsVersionBox.ItemsSource = versions;
        _modsVersionBox.SelectedItem = _modsVersion;
        _modsVersionBox.SelectionChanged += (_, _) =>
        {
            if (_modsVersionBox.SelectedItem is string v)
            {
                _modsVersion = v;
                RefreshModsList();
            }
        };
        row.Children.Add(_modsVersionBox);

        var search = new TextBox
        {
            Width = 200,
            Padding = new Thickness(8, 6, 8, 6),
            Margin = new Thickness(0, 0, 12, 8),
            FontSize = 12
        };
        search.TextChanged += (_, _) =>
        {
            _modsQuery = search.Text.Trim();
            RefreshModsList();
        };
        // 占位提示
        var ph = new TextBlock
        {
            Text = "搜索模组 / 文件名",
            FontSize = 12,
            Foreground = Brush(UiPalette.Muted),
            Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false
        };
        var searchHost = new Grid { Margin = new Thickness(0, 0, 12, 8) };
        searchHost.Children.Add(search);
        searchHost.Children.Add(ph);
        search.TextChanged += (_, _) => ph.Visibility = string.IsNullOrEmpty(search.Text) ? Visibility.Visible : Visibility.Collapsed;
        row.Children.Add(searchHost);

        var statusBox = new ComboBox { Width = 120, Padding = new Thickness(8, 6, 8, 6), Margin = new Thickness(0, 0, 10, 8) };
        statusBox.ItemsSource = new[] { "全部状态", "仅启用", "仅禁用" };
        statusBox.SelectedIndex = 0;
        statusBox.SelectionChanged += (_, _) =>
        {
            _modsStatusFilter = statusBox.SelectedItem as string ?? "全部状态";
            RefreshModsList();
        };
        row.Children.Add(statusBox);

        var loaderBox = new ComboBox { Width = 130, Padding = new Thickness(8, 6, 8, 6), Margin = new Thickness(0, 0, 10, 8) };
        loaderBox.ItemsSource = new[] { "全部加载器", "Fabric", "Forge", "NeoForge", "Quilt", "未知" };
        loaderBox.SelectedIndex = 0;
        loaderBox.SelectionChanged += (_, _) =>
        {
            _modsLoaderFilter = loaderBox.SelectedItem as string ?? "全部加载器";
            RefreshModsList();
        };
        row.Children.Add(loaderBox);

        var scanBtn = SmallButton("重新扫描");
        scanBtn.Margin = new Thickness(0, 0, 8, 8);
        scanBtn.Click += (_, _) => { _modsCache.Clear(); RefreshModsList(true); };
        row.Children.Add(scanBtn);

        var folderBtn = SmallButton("打开 mods 文件夹");
        folderBtn.Margin = new Thickness(0, 0, 8, 8);
        folderBtn.Click += (_, _) => OpenPath(ModsDirFor(_modsVersion));
        row.Children.Add(folderBtn);

        var exportBtn = SmallButton("导出整合包");
        exportBtn.Margin = new Thickness(0, 0, 8, 8);
        exportBtn.Click += async (_, _) => await ExportModpackAsync();
        row.Children.Add(exportBtn);

        var updateBtn = SmallButton("检查更新");
        updateBtn.Margin = new Thickness(0, 0, 8, 8);
        updateBtn.Background = Brush(UiPalette.Accent);
        updateBtn.Foreground = Brush(UiPalette.Bg);
        updateBtn.Click += async (_, _) => await CheckModUpdatesAsync();
        row.Children.Add(updateBtn);

        bar.Children.Add(row);

        _modsStatusText = new TextBlock
        {
            FontSize = 11,
            Foreground = Brush(UiPalette.Muted),
            Margin = new Thickness(0, 2, 0, 0),
            TextWrapping = TextWrapping.Wrap
        };
        bar.Children.Add(_modsStatusText);
        root.Children.Add(bar);

        var listCard = Card();
        listCard.Margin = new Thickness(0, 14, 0, 0);
        _modsListPanel = new StackPanel();
        listCard.Children.Add(_modsListPanel);
        root.Children.Add(listCard);

        RefreshModsList();
        return root;
    }

    private void RefreshModsList(bool rescan = false)
    {
        if (_modsListPanel == null) return;
        var dir = ModsDirFor(_modsVersion);
        Directory.CreateDirectory(dir);

        if (rescan || _modsCache.Count == 0)
        {
            _modsCache.Clear();
            try { _modsCache.AddRange(_localModService.ScanMods(Path.GetDirectoryName(dir) == _gameDirectory
                ? _gameDirectory
                : Path.Combine(_gameDirectory, "versions", _modsVersion))); }
            catch (Exception ex)
            {
                if (_modsStatusText != null) _modsStatusText.Text = "扫描失败：" + ex.Message;
            }
        }

        var list = _modsCache.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(_modsQuery))
            list = list.Where(m => m.FileName.Contains(_modsQuery, StringComparison.OrdinalIgnoreCase)
                                   || m.ModId.Contains(_modsQuery, StringComparison.OrdinalIgnoreCase));
        if (_modsStatusFilter == "仅启用") list = list.Where(m => !m.FileName.EndsWith("（已禁用）"));
        if (_modsStatusFilter == "仅禁用") list = list.Where(m => m.FileName.EndsWith("（已禁用）"));
        if (_modsLoaderFilter != "全部加载器") list = list.Where(m => m.Loader == _modsLoaderFilter);

        var items = list.ToList();
        _modsListPanel.Children.Clear();

        if (items.Count == 0)
        {
            _modsListPanel.Children.Add(new TextBlock
            {
                Text = _modsCache.Count == 0
                    ? "这个版本还没有模组。去「资源下载」页搜索并下载模组，会自动放进 mods 文件夹。"
                    : "没有符合筛选条件的模组。",
                FontSize = 12,
                Foreground = Brush(UiPalette.Muted),
                Margin = new Thickness(4, 6, 4, 6),
                TextWrapping = TextWrapping.Wrap
            });
        }

        foreach (var m in items) _modsListPanel.Children.Add(BuildModRow(m, dir));

        if (_modsStatusText != null)
        {
            var enabled = _modsCache.Count(x => !x.FileName.EndsWith("（已禁用）"));
            _modsStatusText.Text = $"共 {_modsCache.Count} 个模组（已启用 {enabled} / 已禁用 {_modsCache.Count - enabled}）　目录：{dir}";
        }
    }

    private UIElement BuildModRow(LocalModInfo mod, string modsDir)
    {
        var disabled = mod.FileName.EndsWith("（已禁用）");
        var border = new Border
        {
            Background = Brush(UiPalette.SurfaceAlt),
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(12, 9, 12, 9),
            Margin = new Thickness(0, 0, 0, 6)
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var left = new StackPanel();
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
        titleRow.Children.Add(new TextBlock
        {
            Text = "■",
            FontSize = 10,
            Foreground = Brush(disabled ? "#64748B" : UiPalette.Accent),
            Margin = new Thickness(0, 2, 8, 0)
        });
        titleRow.Children.Add(new TextBlock
        {
            Text = mod.FileName.Replace("（已禁用）", ""),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush(disabled ? "#94A3B8" : UiPalette.Text),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 520
        });
        left.Children.Add(titleRow);
        left.Children.Add(new TextBlock
        {
            Text = $"{mod.Loader}　ID: {mod.ModId}{(string.IsNullOrEmpty(mod.Version) ? "" : "　v" + mod.Version)}　{(disabled ? "已禁用" : "已启用")}",
            FontSize = 11,
            Foreground = Brush(UiPalette.Muted),
            Margin = new Thickness(18, 2, 0, 0)
        });
        grid.Children.Add(left);

        var btns = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var toggle = SmallButton(disabled ? "启用" : "禁用");
        toggle.Margin = new Thickness(0, 0, 6, 0);
        toggle.Click += (_, _) =>
        {
            try
            {
                if (disabled) _localModService.Enable(mod.FullPath);
                else _localModService.Disable(mod.FullPath);
                RefreshModsList(true);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "操作失败：" + ex.Message, "NEX", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        };
        btns.Children.Add(toggle);

        var del = SmallButton("删除");
        del.Foreground = Brush("#F87171");
        del.Click += (_, _) =>
        {
            if (MessageBox.Show(this, $"删除 {mod.FileName}？", "NEX", MessageBoxButton.YesNo, MessageBoxImage.Question)
                != MessageBoxResult.Yes) return;
            try
            {
                _localModService.Delete(mod.FullPath, Path.GetDirectoryName(modsDir) ?? _gameDirectory);
                RefreshModsList(true);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "删除失败：" + ex.Message, "NEX", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        };
        btns.Children.Add(del);

        Grid.SetColumn(btns, 1);
        grid.Children.Add(btns);
        border.Child = grid;
        return border;
    }

    /// <summary>导出整合包：mods + config + options.txt 打包成 zip。</summary>
    private async Task ExportModpackAsync()
    {
        var dir = ModsDirFor(_modsVersion);
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "整合包 (*.zip)|*.zip",
            FileName = $"{_modsVersion}-整合包.zip"
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            await Task.Run(() =>
            {
                using var zip = ZipFile.Open(dlg.FileName, ZipArchiveMode.Create);
                void AddDir(string path, string prefix)
                {
                    if (!Directory.Exists(path)) return;
                    foreach (var f in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                    {
                        var rel = Path.GetRelativePath(path, f);
                        zip.CreateEntryFromFile(f, Path.Combine(prefix, rel), CompressionLevel.Optimal);
                    }
                }
                AddDir(dir, "mods");
                var verDir = Path.Combine(_gameDirectory, "versions", _modsVersion);
                AddDir(Path.Combine(verDir, "config"), "config");
                AddDir(Path.Combine(_gameDirectory, "config"), "config");
                var opt = Path.Combine(verDir, "options.txt");
                if (!File.Exists(opt)) opt = Path.Combine(_gameDirectory, "options.txt");
                if (File.Exists(opt)) zip.CreateEntryFromFile(opt, "options.txt", CompressionLevel.Optimal);
            });
            MessageBox.Show(this, "整合包已导出：\n" + dlg.FileName, "NEX", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "导出失败：" + ex.Message, "NEX", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>检查更新：按模组 ID 在 Modrinth 查同名项目，列出可更新项，逐个更新。</summary>
    private async Task CheckModUpdatesAsync()
    {
        if (_modsCache.Count == 0)
        {
            MessageBox.Show(this, "当前版本没有模组。", "NEX", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dir = ModsDirFor(_modsVersion);
        var win = new Window
        {
            Title = "检查模组更新",
            Width = 720,
            Height = 520,
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Brush(UiPalette.Bg)
        };
        var host = new StackPanel { Margin = new Thickness(16) };
        var status = new TextBlock
        {
            Text = "正在比对 Modrinth…",
            FontSize = 12,
            Foreground = Brush(UiPalette.Muted),
            TextWrapping = TextWrapping.Wrap
        };
        host.Children.Add(new TextBlock
        {
            Text = $"模组更新检查（{_modsVersion}）",
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush(UiPalette.Text),
            Margin = new Thickness(0, 0, 0, 8)
        });
        host.Children.Add(status);
        var listHost = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        host.Children.Add(new ScrollViewer { Content = listHost, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Height = 380 });
        win.Content = host;
        win.Show();

        var updates = new List<(LocalModInfo Mod, ModrinthProject Project, ModrinthVersion Version)>();
        var checkedCount = 0;
        foreach (var m in _modsCache)
        {
            if (m.FileName.EndsWith("（已禁用）")) continue;
            try
            {
                var page = await _modrinthService.SearchPagedAsync(
                    m.ModId.Replace('-', ' ').Replace('_', ' '), "mod", null,
                    new[] { _modsVersion }, m.Loader == "未知" ? null : new[] { m.Loader.ToLowerInvariant() },
                    "relevance", 0, 5);
                var hit = page.Hits.FirstOrDefault();
                if (hit == null) continue;
                var versions = await _modrinthService.GetVersionsAsync(hit.Id);
                var latest = versions.FirstOrDefault(v =>
                    (v.GameVersions.Count == 0 || v.GameVersions.Contains(_modsVersion)) && v.Files.Count > 0);
                if (latest == null) continue;
                if (!string.IsNullOrEmpty(m.Version) &&
                    string.Equals(latest.VersionNumber, m.Version, StringComparison.OrdinalIgnoreCase)) continue;
                updates.Add((m, hit, latest));
                checkedCount++;
            }
            catch { /* 单个失败不影响整体 */ }
        }

        status.Text = updates.Count == 0
            ? $"已检查 {_modsCache.Count} 个模组，未发现可更新项（或无法在 Modrinth 匹配）。"
            : $"发现 {updates.Count} 个可更新模组（点击右侧按钮直接下载替换）。";

        foreach (var (mod, project, ver) in updates)
        {
            var row = new Border
            {
                Background = Brush(UiPalette.SurfaceAlt),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12, 8, 12, 8),
                Margin = new Thickness(0, 0, 0, 6)
            };
            var dp = new DockPanel();
            var btn = SmallButton("更新");
            btn.Background = Brush(UiPalette.Accent);
            btn.Foreground = Brush(UiPalette.Bg);
            var file = ver.PrimaryFile;
            btn.Click += async (_, _) =>
            {
                if (file == null) return;
                try
                {
                    btn.IsEnabled = false;
                    btn.Content = "下载中…";
                    var dest = Path.Combine(dir, file.FileName);
                    await _modrinthService.DownloadFileAsync(file.Url, dest);
                    // 删掉旧文件（同名不删，避免把新文件删掉）
                    if (!string.Equals(Path.GetFileName(mod.FullPath), file.FileName, StringComparison.OrdinalIgnoreCase)
                        && File.Exists(mod.FullPath))
                        File.Delete(mod.FullPath);
                    btn.Content = "已更新";
                    RefreshModsList(true);
                }
                catch (Exception ex)
                {
                    btn.Content = "失败";
                    btn.IsEnabled = true;
                    MessageBox.Show(this, "更新失败：" + ex.Message, "NEX", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            };
            DockPanel.SetDock(btn, Dock.Right);
            dp.Children.Add(btn);
            var info = new StackPanel();
            info.Children.Add(new TextBlock
            {
                Text = project.Title,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brush(UiPalette.Text)
            });
            info.Children.Add(new TextBlock
            {
                Text = $"{mod.FileName}  →  {ver.VersionNumber}（{ver.VersionType}）",
                FontSize = 11,
                Foreground = Brush(UiPalette.Muted),
                Margin = new Thickness(0, 2, 0, 0),
                TextWrapping = TextWrapping.Wrap
            });
            dp.Children.Add(info);
            row.Child = dp;
            listHost.Children.Add(row);
        }

        if (updates.Count == 0)
            listHost.Children.Add(new TextBlock
            {
                Text = $"已检查 {checkedCount} 个可匹配项目，全部为最新或无法匹配（部分闭源模组不在 Modrinth）。",
                FontSize = 12,
                Foreground = Brush(UiPalette.Muted),
                TextWrapping = TextWrapping.Wrap
            });
    }
}
