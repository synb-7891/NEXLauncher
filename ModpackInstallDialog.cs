using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NEXLauncher.Services;

namespace NEXLauncher;

/// <summary>
/// 整合包安装向导：展示包信息、选择基底版本、选择是否备份现有 mods、执行安装并显示进度。
/// 完整版可在缺少对应游戏版本时自动下载安装；公共版不会下载游戏版本，只安装整合包内容。
/// </summary>
public sealed class ModpackInstallDialog : Window
{
    private readonly ModpackInfo _info;
    private readonly string _gameDir;
    private readonly Func<string, string, string, Task<string?>>? _ensureBaseAsync;
    private readonly bool _allowDownload;

    private readonly ComboBox _baseBox = new();
    private readonly CheckBox _backupBox = new();
    private readonly CheckBox _autoBaseBox = new();
    private readonly TextBlock _status = new();
    private readonly ProgressBar _progress = new();
    private readonly Button _installBtn = new();
    private readonly Button _cancelBtn = new();
    private CancellationTokenSource? _cts;
    private bool _busy;

    public ModpackRecord? Result { get; private set; }

    public ModpackInstallDialog(ModpackInfo info, string gameDir, IReadOnlyList<string> installedVersions,
        Func<string, string, string, Task<string?>>? ensureBaseAsync, bool allowDownload)
    {
        _info = info;
        _gameDir = gameDir;
        _ensureBaseAsync = ensureBaseAsync;
        _allowDownload = allowDownload;

        Title = "安装整合包 — NEX";
        Width = 640;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Background = UiBrush(UiPalette.Bg);
        Foreground = UiBrush(UiPalette.Text);
        WindowStyle = WindowStyle.ToolWindow;

        var root = new StackPanel { Margin = new Thickness(22, 18, 22, 18) };
        Content = root;

        root.Children.Add(new TextBlock
        {
            Text = info.Name,
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            Foreground = UiBrush(UiPalette.Text),
            TextWrapping = TextWrapping.Wrap
        });
        root.Children.Add(new TextBlock
        {
            Text = $"版本 {info.Version} · {FormatName(info.Format)}",
            FontSize = 12,
            Foreground = UiBrush(UiPalette.Muted),
            Margin = new Thickness(0, 4, 0, 0)
        });

        // 信息行
        var grid = new Grid { Margin = new Thickness(0, 16, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        AddInfo(grid, 0, 0, "游戏版本", string.IsNullOrEmpty(info.McVersion) ? "未声明" : info.McVersion);
        AddInfo(grid, 0, 1, "模组加载器", string.IsNullOrEmpty(info.LoaderId)
            ? "原版 / 未声明"
            : info.LoaderId + (string.IsNullOrEmpty(info.LoaderVersion) ? "" : " " + info.LoaderVersion));
        AddInfo(grid, 1, 0, "待下载文件", $"{info.Files.Count} 个 · {FormatBytes(info.TotalBytes)}");
        AddInfo(grid, 1, 1, "覆盖文件（overrides）", $"{info.OverrideEntries} 个");
        root.Children.Add(grid);

        // 目标目录
        root.Children.Add(SectionLabel("安装到"));
        root.Children.Add(new TextBlock
        {
            Text = gameDir,
            FontSize = 11,
            FontFamily = new FontFamily("Consolas"),
            Foreground = UiBrush(UiPalette.TextDim),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0)
        });

        // 基底版本
        root.Children.Add(SectionLabel("基底游戏版本（选你实际要启动的版本）"));
        _baseBox.FontSize = 12;
        _baseBox.Height = 32;
        _baseBox.Background = UiBrush(UiPalette.Bg);
        _baseBox.Foreground = UiBrush(UiPalette.Text);
        _baseBox.BorderBrush = UiBrush(UiPalette.Border);
        var items = new List<string> { "（不指定）" };
        items.AddRange(installedVersions);
        _baseBox.ItemsSource = items;
        _baseBox.SelectedItem = PickDefault(installedVersions);
        root.Children.Add(_baseBox);

        // 选项
        _backupBox.Content = "安装前备份现有 mods 文件夹（推荐，避免与旧模组混在一起）";
        _backupBox.IsChecked = true;
        _backupBox.FontSize = 12;
        _backupBox.Foreground = UiBrush(UiPalette.TextDim);
        _backupBox.Margin = new Thickness(0, 14, 0, 0);
        root.Children.Add(_backupBox);

        if (_allowDownload)
        {
            _autoBaseBox.Content = string.IsNullOrEmpty(info.McVersion)
                ? "缺少依赖时自动下载对应游戏版本（仅完整版）"
                : $"缺少 Minecraft {info.McVersion} 时自动下载安装（仅完整版）";
            _autoBaseBox.IsChecked = true;
            _autoBaseBox.FontSize = 12;
            _autoBaseBox.Foreground = UiBrush(UiPalette.TextDim);
            _autoBaseBox.Margin = new Thickness(0, 8, 0, 0);
            root.Children.Add(_autoBaseBox);
        }
        else
        {
            root.Children.Add(new TextBlock
            {
                Text = "公共版不下载游戏版本：如果本机还没有对应的游戏版本，请先用其它启动器下载，"
                     + "再用「导入本机版本」导入，然后安装整合包。",
                FontSize = 11,
                Foreground = UiBrush(UiPalette.Muted),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 10, 0, 0)
            });
        }

        if (info.MissingDownloadCount > 0)
        {
            root.Children.Add(new TextBlock
            {
                Text = $"注意：有 {info.MissingDownloadCount} 个文件没有下载地址（CurseForge 整合包需要作者授权），"
                     + "安装后请到整合包页面手动补齐这些模组。",
                FontSize = 11,
                Foreground = UiBrush("#F6A82A"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 10, 0, 0)
            });
        }

        // 进度
        _progress.Height = 6;
        _progress.Margin = new Thickness(0, 18, 0, 0);
        _progress.Visibility = Visibility.Collapsed;
        _progress.Background = UiBrush(UiPalette.SurfaceAlt);
        _progress.Foreground = UiBrush(UiPalette.Accent);
        _progress.BorderBrush = UiBrush(UiPalette.Border);
        root.Children.Add(_progress);

        _status.Text = "";
        _status.FontSize = 12;
        _status.Foreground = UiBrush(UiPalette.Muted);
        _status.TextWrapping = TextWrapping.Wrap;
        _status.Margin = new Thickness(0, 8, 0, 0);
        root.Children.Add(_status);

        // 按钮
        var btnRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0)
        };
        _cancelBtn.Content = "取消";
        _cancelBtn.Padding = new Thickness(18, 8, 18, 8);
        _cancelBtn.Background = UiBrush(UiPalette.SurfaceAlt);
        _cancelBtn.Foreground = UiBrush(UiPalette.TextDim);
        _cancelBtn.BorderThickness = new Thickness(0);
        _cancelBtn.Cursor = System.Windows.Input.Cursors.Hand;
        _cancelBtn.Margin = new Thickness(0, 0, 8, 0);
        _cancelBtn.Click += (_, _) =>
        {
            if (_busy) { _cts?.Cancel(); _status.Text = "正在取消…"; }
            else Close();
        };
        btnRow.Children.Add(_cancelBtn);

        _installBtn.Content = "开始安装";
        _installBtn.Padding = new Thickness(20, 8, 20, 8);
        _installBtn.Background = UiBrush(UiPalette.Accent);
        _installBtn.Foreground = UiBrush(UiPalette.Bg);
        _installBtn.BorderThickness = new Thickness(0);
        _installBtn.FontWeight = FontWeights.SemiBold;
        _installBtn.Cursor = System.Windows.Input.Cursors.Hand;
        _installBtn.Click += async (_, _) => await InstallAsync();
        btnRow.Children.Add(_installBtn);
        root.Children.Add(btnRow);
    }

    private string PickDefault(IReadOnlyList<string> installed)
    {
        if (!string.IsNullOrEmpty(_info.McVersion))
        {
            var exact = installed.FirstOrDefault(v => v.Equals(_info.McVersion, StringComparison.OrdinalIgnoreCase));
            if (exact != null) return exact;
            var contains = installed.FirstOrDefault(v => v.Contains(_info.McVersion, StringComparison.OrdinalIgnoreCase));
            if (contains != null) return contains;
        }
        return installed.Count > 0 ? installed[0] : "（不指定）";
    }

    private static void AddInfo(Grid grid, int row, int col, string title, string value)
    {
        if (grid.RowDefinitions.Count <= row) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var sp = new StackPanel { Margin = new Thickness(0, 0, 12, 10) };
        sp.Children.Add(new TextBlock
        {
            Text = title, FontSize = 11, Foreground = UiBrush(UiPalette.Muted)
        });
        sp.Children.Add(new TextBlock
        {
            Text = value, FontSize = 13, Foreground = UiBrush(UiPalette.Text), TextWrapping = TextWrapping.Wrap
        });
        Grid.SetRow(sp, row);
        Grid.SetColumn(sp, col);
        grid.Children.Add(sp);
    }

    private static TextBlock SectionLabel(string text) => new()
    {
        Text = text,
        FontSize = 11,
        Foreground = UiBrush(UiPalette.Accent),
        Margin = new Thickness(0, 16, 0, 4)
    };

    private async Task InstallAsync()
    {
        if (_busy) return;
        _busy = true;
        _installBtn.IsEnabled = false;
        _cancelBtn.Content = "取消安装";
        _progress.Visibility = Visibility.Visible;
        _progress.Value = 0;
        _cts = new CancellationTokenSource();

        var progress = new Progress<(string Message, double Progress)>(p =>
        {
            _status.Text = p.Message;
            _progress.Value = Math.Clamp(p.Progress, 0, 1) * 100;
        });

        try
        {
            // 1) 基底版本（完整版可在缺少时自动下载安装）
            var baseVersion = _baseBox.SelectedItem as string;
            var needBase = string.IsNullOrEmpty(baseVersion) || baseVersion == "（不指定）"
                           || (!string.IsNullOrEmpty(_info.McVersion)
                               && !string.Equals(baseVersion, _info.McVersion, StringComparison.OrdinalIgnoreCase)
                               && !baseVersion.Contains(_info.McVersion, StringComparison.OrdinalIgnoreCase));

            if (needBase && _allowDownload && _autoBaseBox.IsChecked == true && _ensureBaseAsync != null
                && !string.IsNullOrEmpty(_info.McVersion))
            {
                _status.Text = "正在准备游戏版本（可能需要下载）…";
                var resolved = await _ensureBaseAsync(_info.McVersion, _info.LoaderId, _info.LoaderVersion);
                if (!string.IsNullOrEmpty(resolved)) baseVersion = resolved;
            }

            // 2) 安装
            var record = await ModpackService.InstallAsync(_info, _gameDir, _backupBox.IsChecked == true,
                progress, _cts.Token);

            Result = record;
            _status.Text = "安装完成。";
            _progress.Value = 100;
            _installBtn.Content = "完成";
            _installBtn.IsEnabled = true;
            _installBtn.Click += (_, _) => { DialogResult = true; };

            MessageBox.Show(this,
                $"整合包「{record.Name}」安装完成。\n\n"
                + $"安装文件：{record.Files.Count} 个\n"
                + $"基底版本：{(string.IsNullOrEmpty(baseVersion) || baseVersion == "（不指定）" ? "未指定" : baseVersion)}\n"
                + (record.BackupDir != null ? $"原 mods 已备份到：\n{record.BackupDir}\n" : "")
                + "\n现在可以到「游戏版本」页启动对应版本开始玩。",
                "NEX", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            _status.Text = "已取消安装（已下载的文件保留在游戏目录）。";
        }
        catch (Exception ex)
        {
            _status.Text = "安装失败：" + ex.Message;
            MessageBox.Show(this, "安装失败：\n" + ex.Message, "NEX", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _busy = false;
            _installBtn.IsEnabled = true;
            _cancelBtn.Content = "关闭";
            _progress.Visibility = Visibility.Visible;
        }
    }

    private static string FormatName(string format) => format switch
    {
        "mrpack" => "Modrinth 整合包（.mrpack）",
        "curseforge" => "CurseForge 整合包（.zip）",
        _ => "整合包（.zip）"
    };

    private static string FormatBytes(long bytes) => bytes switch
    {
        > 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024 / 1024:F2} GB",
        > 1024L * 1024 => $"{bytes / 1024.0 / 1024:F1} MB",
        > 1024 => $"{bytes / 1024.0:F0} KB",
        _ => $"{bytes} B"
    };

    private static SolidColorBrush UiBrush(string hex) =>
        new((Color)ColorConverter.ConvertFromString(hex));
}
