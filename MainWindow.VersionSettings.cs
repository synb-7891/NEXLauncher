using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NEXLauncher.Services;

namespace NEXLauncher;

/// <summary>版本设置页：个性化（图标/名称/描述/收藏）、快捷文件夹、内存、Java 运行环境、高级管理。</summary>
public partial class MainWindow
{
    private string _versionSettingsTarget = "";
    private ComboBox? _vsetVersionBox;
    private TextBox? _vsetNameBox;
    private TextBox? _vsetDescBox;
    private TextBox? _vsetMaxRamBox;
    private TextBox? _vsetMinRamBox;
    private ComboBox? _vsetJavaBox;
    private Border? _vsetIconHost;
    private TextBlock? _vsetStatus;

    private VersionProfileItem ProfileOf(string versionId)
    {
        if (!_settings.VersionProfiles.TryGetValue(versionId, out var p))
        {
            p = new VersionProfileItem();
            _settings.VersionProfiles[versionId] = p;
        }
        return p;
    }

    private string VersionDir(string versionId) => Path.Combine(_gameDirectory, "versions", versionId);

    private UIElement BuildVersionSettingsPage()
    {
        var root = new StackPanel();
        root.Children.Add(Label("实例", 11, UiPalette.Accent));

        var headRow = new DockPanel { LastChildFill = false };
        var back = SmallButton("← 返回版本列表");
        back.HorizontalAlignment = HorizontalAlignment.Left;
        back.Margin = new Thickness(0, 0, 10, 0);
        back.Click += (_, _) => ShowPage("游戏版本", true);
        headRow.Children.Add(back);
        headRow.Children.Add(Heading("版本设置"));
        root.Children.Add(headRow);
        root.Children.Add(Body("为单个版本配置显示名称、图标、内存与 Java；这些设置只影响这个版本，不改动其它实例。"));

        var versions = _minecraftService.GetLocalInstalledVersions();
        if (versions.Count == 0)
        {
            root.Children.Add(Body("还没有已安装的版本。请先在「游戏版本」页导入本机版本。"));
            return root;
        }
        if (string.IsNullOrEmpty(_versionSettingsTarget) || !versions.Contains(_versionSettingsTarget))
            _versionSettingsTarget = versions[0];
        var prof = ProfileOf(_versionSettingsTarget);

        // ---- 选择版本 ----
        var pickCard = Card();
        var pickRow = new StackPanel { Orientation = Orientation.Horizontal };
        pickRow.Children.Add(Label("当前版本", 13, UiPalette.TextDim, FontWeights.SemiBold));
        _vsetVersionBox = new ComboBox
        {
            Width = 320,
            Margin = new Thickness(12, 0, 10, 0),
            ItemsSource = versions,
            SelectedItem = _versionSettingsTarget
        };
        _vsetVersionBox.SelectionChanged += (_, _) =>
        {
            if (_vsetVersionBox.SelectedItem is string s)
            {
                _versionSettingsTarget = s;
                ShowPage("版本设置", true);
            }
        };
        pickRow.Children.Add(_vsetVersionBox);
        var openVer = SmallButton("打开版本文件夹");
        openVer.Click += (_, _) => OpenPath(VersionDir(_versionSettingsTarget));
        pickRow.Children.Add(openVer);
        pickCard.Children.Add(pickRow);

        _vsetStatus = new TextBlock
        {
            FontSize = 12,
            Foreground = Brush(UiPalette.Accent),
            Margin = new Thickness(0, 8, 0, 0),
            TextWrapping = TextWrapping.Wrap
        };
        pickCard.Children.Add(_vsetStatus);
        root.Children.Add(pickCard);

        // ---- 个性化 ----
        var look = Card();
        look.Margin = new Thickness(0, 14, 0, 0);
        look.Children.Add(Label("个性化", 15, UiPalette.Text, FontWeights.SemiBold));
        look.Children.Add(Body("自定义这个版本在启动器里显示的样子，方便区分整合包与不同存档环境。"));

        var lookGrid = new Grid();
        lookGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        lookGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        _vsetIconHost = new Border
        {
            Width = 84, Height = 84,
            CornerRadius = new CornerRadius(12),
            Background = Brush(UiPalette.Bg),
            BorderBrush = Brush(UiPalette.Border),
            BorderThickness = new Thickness(1),
            Cursor = System.Windows.Input.Cursors.Hand,
            Margin = new Thickness(0, 0, 16, 0),
            ToolTip = "点击更换版本图标（PNG / ICO）"
        };
        _vsetIconHost.MouseLeftButtonUp += (_, _) => PickVersionIcon();
        RefreshVersionIcon(prof);
        Grid.SetColumn(_vsetIconHost, 0);
        lookGrid.Children.Add(_vsetIconHost);

        var fields = new StackPanel();
        fields.Children.Add(Label("显示名称", 11, UiPalette.Muted));
        _vsetNameBox = new TextBox { Text = prof.DisplayName, Padding = new Thickness(8, 6, 8, 6), FontSize = 12 };
        fields.Children.Add(_vsetNameBox);
        fields.Children.Add(Label("版本描述", 11, UiPalette.Muted));
        _vsetDescBox = new TextBox
        {
            Text = prof.Description,
            Padding = new Thickness(8, 6, 8, 6),
            FontSize = 12,
            AcceptsReturn = true,
            MinHeight = 54,
            TextWrapping = TextWrapping.Wrap
        };
        fields.Children.Add(_vsetDescBox);

        var favRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        var favBtn = SmallButton(prof.Favorite ? "★ 已收藏（点击取消）" : "☆ 收藏此版本");
        if (prof.Favorite) favBtn.Foreground = Brush(UiPalette.Accent);
        favBtn.Click += (_, _) =>
        {
            var p = ProfileOf(_versionSettingsTarget);
            p.Favorite = !p.Favorite;
            _settings.Save();
            ShowPage("版本设置", true);
        };
        favRow.Children.Add(favBtn);
        var saveLook = SmallButton("保存设置");
        saveLook.Background = Brush(UiPalette.Accent);
        saveLook.Foreground = Brush(UiPalette.Bg);
        saveLook.Click += (_, _) => SaveVersionProfile();
        favRow.Children.Add(saveLook);
        fields.Children.Add(favRow);

        Grid.SetColumn(fields, 1);
        lookGrid.Children.Add(fields);
        look.Children.Add(lookGrid);
        root.Children.Add(look);

        // ---- 快捷方式 ----
        var shortcuts = Card();
        shortcuts.Margin = new Thickness(0, 14, 0, 0);
        shortcuts.Children.Add(Label("快捷方式", 15, UiPalette.Text, FontWeights.SemiBold));
        var scRow = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        void AddShortcut(string text, string dir)
        {
            var b = SmallButton(text);
            b.Margin = new Thickness(0, 0, 8, 8);
            b.Click += (_, _) =>
            {
                Directory.CreateDirectory(dir);
                OpenPath(dir);
            };
            scRow.Children.Add(b);
        }
        AddShortcut("版本文件夹", VersionDir(_versionSettingsTarget));
        AddShortcut("存档文件夹", _settings.VersionIsolation
            ? Path.Combine(VersionDir(_versionSettingsTarget), "saves")
            : Path.Combine(_gameDirectory, "saves"));
        AddShortcut("截图文件夹", _settings.VersionIsolation
            ? Path.Combine(VersionDir(_versionSettingsTarget), "screenshots")
            : Path.Combine(_gameDirectory, "screenshots"));
        AddShortcut("日志文件夹", _settings.VersionIsolation
            ? Path.Combine(VersionDir(_versionSettingsTarget), "logs")
            : Path.Combine(_gameDirectory, "logs"));
        shortcuts.Children.Add(scRow);
        root.Children.Add(shortcuts);

        // ---- 内存 ----
        var ram = Card();
        ram.Margin = new Thickness(0, 14, 0, 0);
        ram.Children.Add(Label("内存分配", 15, UiPalette.Text, FontWeights.SemiBold));
        ram.Children.Add(Body($"该版本单独分配的内存；留空或填 0 表示跟随全局设置（当前全局：最大 {_settings.MaxRamMb} MB / 最小 {_settings.MinRamMb} MB）。"));
        var ramRow = new WrapPanel();
        ramRow.Children.Add(Label("最大内存 (MB)", 11, UiPalette.Muted));
        _vsetMaxRamBox = new TextBox
        {
            Width = 120,
            Text = prof.MaxRamMb > 0 ? prof.MaxRamMb.ToString() : "",
            Padding = new Thickness(8, 6, 8, 6),
            Margin = new Thickness(0, 0, 18, 0)
        };
        ramRow.Children.Add(_vsetMaxRamBox);
        ramRow.Children.Add(Label("最小内存 (MB)", 11, UiPalette.Muted));
        _vsetMinRamBox = new TextBox
        {
            Width = 120,
            Text = prof.MinRamMb > 0 ? prof.MinRamMb.ToString() : "",
            Padding = new Thickness(8, 6, 8, 6)
        };
        ramRow.Children.Add(_vsetMinRamBox);
        var ramSave = SmallButton("保存内存");
        ramSave.Margin = new Thickness(16, 0, 0, 0);
        ramSave.Click += (_, _) => SaveVersionProfile();
        ramRow.Children.Add(ramSave);
        ram.Children.Add(ramRow);
        root.Children.Add(ram);

        // ---- 运行环境 ----
        var java = Card();
        java.Margin = new Thickness(0, 14, 0, 0);
        java.Children.Add(Label("运行环境", 15, UiPalette.Text, FontWeights.SemiBold));
        java.Children.Add(Body("为该版本指定专用 Java；不选则使用设置页里的全局 Java（推荐 Java 17+ 玩 1.17 以上版本）。"));
        var javaRow = new WrapPanel();
        _vsetJavaBox = new ComboBox { Width = 420, Padding = new Thickness(8, 6, 8, 6) };
        _vsetJavaBox.Items.Add("（跟随全局设置）");
        var runtimes = new JavaService().Detect(_settings.JavaPath);
        foreach (var rt in runtimes) _vsetJavaBox.Items.Add(rt.JavaPath);
        _vsetJavaBox.SelectedIndex = 0;
        if (!string.IsNullOrWhiteSpace(prof.JavaPath))
        {
            var idx = _vsetJavaBox.Items.IndexOf(prof.JavaPath);
            if (idx >= 0) _vsetJavaBox.SelectedIndex = idx;
        }
        javaRow.Children.Add(_vsetJavaBox);
        var javaBrowse = SmallButton("浏览…");
        javaBrowse.Margin = new Thickness(8, 0, 0, 0);
        javaBrowse.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Java 可执行文件 (java.exe)|java.exe|所有文件|*.*" };
            if (dlg.ShowDialog(this) == true)
            {
                if (!_vsetJavaBox.Items.Contains(dlg.FileName)) _vsetJavaBox.Items.Add(dlg.FileName);
                _vsetJavaBox.SelectedItem = dlg.FileName;
            }
        };
        javaRow.Children.Add(javaBrowse);
        var javaSave = SmallButton("保存运行环境");
        javaSave.Margin = new Thickness(8, 0, 0, 0);
        javaSave.Click += (_, _) => SaveVersionProfile();
        javaRow.Children.Add(javaSave);
        java.Children.Add(javaRow);
        java.Children.Add(new TextBlock
        {
            Text = runtimes.Count == 0
                ? "未检测到 Java：可在左侧「Java」页扫描、下载或手动导入。"
                : $"已检测到 {runtimes.Count} 个 Java 运行时：" + string.Join("、", runtimes.Take(4).Select(r => "Java " + r.Major)),
            FontSize = 11,
            Foreground = Brush(UiPalette.Muted),
            Margin = new Thickness(0, 8, 0, 0),
            TextWrapping = TextWrapping.Wrap
        });
        root.Children.Add(java);

        // ---- 高级管理 ----
        var adv = Card();
        adv.Margin = new Thickness(0, 14, 0, 0);
        adv.Children.Add(Label("高级管理", 15, UiPalette.Text, FontWeights.SemiBold));
        var advRow = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        var fixBtn = SmallButton("补全游戏文件");
        fixBtn.ToolTip = "校验并补齐这个版本缺失的库文件、资源与客户端";
        fixBtn.Click += async (_, _) =>
        {
            if (_vsetStatus != null) _vsetStatus.Text = "正在校验并补全文件…";
            try
            {
                await _minecraftService.InstallAsync(_versionSettingsTarget);
                if (_vsetStatus != null) _vsetStatus.Text = "文件补全完成。";
            }
            catch (Exception ex)
            {
                if (_vsetStatus != null) _vsetStatus.Text = "补全失败：" + ex.Message;
            }
        };
        advRow.Children.Add(fixBtn);

        var scriptBtn = SmallButton("导出启动脚本");
        scriptBtn.ToolTip = "生成 .bat，可直接双击启动这个版本（不经过启动器）";
        scriptBtn.Click += async (_, _) => await ExportLaunchScriptAsync();
        advRow.Children.Add(scriptBtn);

        var delBtn = SmallButton("删除版本");
        delBtn.Foreground = Brush("#F87171");
        delBtn.Click += (_, _) => DeleteVersionFromSettings();
        advRow.Children.Add(delBtn);
        adv.Children.Add(advRow);
        root.Children.Add(adv);

        return root;
    }

    private void RefreshVersionIcon(VersionProfileItem prof)
    {
        if (_vsetIconHost == null) return;
        _vsetIconHost.Child = null;
        if (!string.IsNullOrWhiteSpace(prof.IconPath) && File.Exists(prof.IconPath))
        {
            try
            {
                var img = new BitmapImage();
                img.BeginInit();
                img.CacheOption = BitmapCacheOption.OnLoad;
                img.UriSource = new Uri(prof.IconPath);
                img.EndInit();
                img.Freeze();
                _vsetIconHost.Child = new Image { Source = img, Stretch = Stretch.UniformToFill };
                return;
            }
            catch { }
        }
        _vsetIconHost.Child = new TextBlock
        {
            Text = "\uE7FC",
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 30,
            Foreground = Brush(UiPalette.Muted),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
    }

    private void PickVersionIcon()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择版本图标",
            Filter = "图片 (*.png;*.ico;*.jpg)|*.png;*.ico;*.jpg|所有文件|*.*"
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            Directory.CreateDirectory(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NEXLauncher", "icons"));
            var dest = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NEXLauncher", "icons",
                _versionSettingsTarget + Path.GetExtension(dlg.FileName));
            File.Copy(dlg.FileName, dest, true);
            var p = ProfileOf(_versionSettingsTarget);
            p.IconPath = dest;
            _settings.Save();
            RefreshVersionIcon(p);
            if (_vsetStatus != null) _vsetStatus.Text = "自定义图标已设置。";
        }
        catch (Exception ex)
        {
            if (_vsetStatus != null) _vsetStatus.Text = "设置图标失败：" + ex.Message;
        }
    }

    private void SaveVersionProfile()
    {
        var p = ProfileOf(_versionSettingsTarget);
        p.DisplayName = _vsetNameBox?.Text.Trim() ?? "";
        p.Description = _vsetDescBox?.Text.Trim() ?? "";
        p.MaxRamMb = int.TryParse(_vsetMaxRamBox?.Text.Trim(), out var mx) ? Math.Clamp(mx, 0, 32768) : 0;
        p.MinRamMb = int.TryParse(_vsetMinRamBox?.Text.Trim(), out var mn) ? Math.Clamp(mn, 0, 32768) : 0;
        p.JavaPath = _vsetJavaBox?.SelectedIndex > 0 ? (_vsetJavaBox.SelectedItem as string ?? "") : "";
        _settings.Save();
        if (_vsetStatus != null) _vsetStatus.Text = "版本设置已保存。";
    }

    private async Task ExportLaunchScriptAsync()
    {
        try
        {
            var session = _session;
            if (session == null)
            {
                MessageBox.Show(this, "请先登录一个账号，再导出启动脚本（脚本里包含账号令牌）。", "NEX",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "批处理 (*.bat)|*.bat",
                FileName = $"启动 {_versionSettingsTarget}.bat"
            };
            if (dlg.ShowDialog(this) != true) return;

            var cmd = await _minecraftService.BuildLaunchCommandAsync(_versionSettingsTarget, session, EffectiveSettings(_versionSettingsTarget));
            var content = "@echo off\r\nchcp 65001 >nul\r\ncd /d \"" + _gameDirectory + "\"\r\n" + cmd + "\r\n";
            await File.WriteAllTextAsync(dlg.FileName, content, System.Text.Encoding.UTF8);
            if (_vsetStatus != null) _vsetStatus.Text = "启动脚本已导出：" + dlg.FileName;
        }
        catch (Exception ex)
        {
            if (_vsetStatus != null) _vsetStatus.Text = "导出失败：" + ex.Message;
        }
    }

    /// <summary>把版本级覆盖合并进全局设置，得到本次启动实际使用的配置。</summary>
    private LauncherSettings EffectiveSettings(string versionId)
    {
        var s = _settings.Clone();
        if (_settings.VersionProfiles.TryGetValue(versionId, out var p))
        {
            if (p.MaxRamMb > 0) s.MaxRamMb = p.MaxRamMb;
            if (p.MinRamMb > 0) s.MinRamMb = p.MinRamMb;
            if (!string.IsNullOrWhiteSpace(p.JavaPath)) s.JavaPath = p.JavaPath;
        }
        return s;
    }

    private void DeleteVersionFromSettings()
    {
        if (MessageBox.Show(this,
                $"确认删除版本 “{_versionSettingsTarget}”？\n\n这会删除该版本的整个文件夹及其所有相关文件，删除后无法恢复。",
                "删除版本", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            _minecraftService.DeleteLocalVersion(_versionSettingsTarget);
            _settings.VersionProfiles.Remove(_versionSettingsTarget);
            _settings.Save();
            MessageBox.Show(this, $"已删除 {_versionSettingsTarget}。", "NEX", MessageBoxButton.OK, MessageBoxImage.Information);
            ShowPage("游戏版本", true);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "删除失败：" + ex.Message, "NEX", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OpenPath(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch { }
    }
}
