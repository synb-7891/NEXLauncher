using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using NEXLauncher.Services;

namespace NEXLauncher;

/// <summary>导入本机已有游戏版本：扫描其它 .minecraft 目录，复制或链接到当前启动器。</summary>
public partial class ImportVersionDialog : Window
{
    private static readonly Brush TextBrush = new SolidColorBrush(Color.FromRgb(0xE6, 0xED, 0xF3));
    private static readonly Brush MutedBrush = new SolidColorBrush(Color.FromRgb(0x7D, 0x8B, 0x9E));
    private static readonly Brush AccentBrush = new SolidColorBrush(Color.FromRgb(0x5B, 0x9D, 0xFF));
    private static readonly Brush CardBrush = new SolidColorBrush(Color.FromRgb(0x14, 0x1C, 0x28));
    private static readonly Brush WarnBrush = new SolidColorBrush(Color.FromRgb(0xF0, 0xA0, 0x20));

    private readonly VersionImportService _service;
    private readonly List<(ImportableVersion Version, CheckBox Box)> _rows = [];
    private readonly List<string> _extraDirs = [];
    private bool _importing;

    /// <summary>成功导入的版本数量</summary>
    public int ImportedCount { get; private set; }
    /// <summary>逐个版本的失败 / 降级说明</summary>
    public List<string> Errors { get; } = [];

    public ImportVersionDialog(string targetGameDir)
    {
        InitializeComponent();
        _service = new VersionImportService(targetGameDir);
        TargetLabel.Text = $"当前启动器的游戏目录：{targetGameDir}" +
                           "　（会扫描官方启动器、PCL2 / HMCL 等常见位置的 .minecraft）";
        Loaded += async (_, _) => await ScanAsync();
    }

    #region 扫描与列表

    private async Task ScanAsync()
    {
        if (_importing) return;

        RescanBtn.IsEnabled = false;
        BrowseBtn.IsEnabled = false;
        ScanStatus.Text = "正在扫描本机 .minecraft 目录…";
        EmptyHint.Visibility = Visibility.Collapsed;
        ListPanel.Children.Clear();
        _rows.Clear();
        UpdateOkState();

        try
        {
            var dirs = await Task.Run(() => VersionImportService.DiscoverGameDirectories());
            foreach (var d in _extraDirs)
                if (!dirs.Contains(d, StringComparer.OrdinalIgnoreCase)) dirs.Insert(0, d);

            var target = await Task.Run(() => _service.ScanMany(dirs)
                .Where(v => !v.IsSameDirectory)
                .ToList());

            // 同一版本在多个目录中出现时只保留一份，避免导入时互相覆盖
            var unique = new List<ImportableVersion>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var v in target)
            {
                if (seen.Add(v.Id)) unique.Add(v);
            }

            foreach (var v in unique) AddRow(v);

            if (unique.Count == 0) EmptyHint.Visibility = Visibility.Visible;
            ScanStatus.Text = unique.Count == 0
                ? $"已扫描 {dirs.Count} 个目录，没有找到其它版本"
                : $"已扫描 {dirs.Count} 个目录，发现 {unique.Count} 个可导入版本";
        }
        catch (Exception ex)
        {
            ScanStatus.Text = "扫描失败：" + ex.Message;
        }
        finally
        {
            RescanBtn.IsEnabled = true;
            BrowseBtn.IsEnabled = true;
            UpdateOkState();
        }
    }

    private void AddRow(ImportableVersion v)
    {
        var card = new Border
        {
            Background = CardBrush,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 0, 0, 6)
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var check = new CheckBox
        {
            // 已存在的版本默认不勾选，勾选表示覆盖
            IsChecked = !v.ExistsInTarget,
            IsEnabled = !_importing,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
            ToolTip = v.ExistsInTarget ? "当前游戏目录已有同名版本，勾选并导入会覆盖它" : null
        };
        check.Checked += (_, _) => UpdateOkState();
        check.Unchecked += (_, _) => UpdateOkState();
        Grid.SetColumn(check, 0);
        grid.Children.Add(check);

        var info = new StackPanel();

        var line = new StackPanel { Orientation = Orientation.Horizontal };
        line.Children.Add(new TextBlock
        {
            Text = v.Id,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = TextBrush,
            VerticalAlignment = VerticalAlignment.Center
        });

        line.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x33, 0x5B, 0x9D, 0xFF)),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 1, 6, 1),
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = v.Loader, FontSize = 10, Foreground = AccentBrush }
        });

        if (!string.IsNullOrEmpty(v.BaseVersion) && !string.Equals(v.BaseVersion, v.Id, StringComparison.OrdinalIgnoreCase))
        {
            line.Children.Add(new TextBlock
            {
                Text = "基于 " + v.BaseVersion,
                FontSize = 11,
                Foreground = MutedBrush,
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
        }

        line.Children.Add(new TextBlock
        {
            Text = v.SizeText,
            FontSize = 11,
            Foreground = MutedBrush,
            Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        });
        info.Children.Add(line);

        info.Children.Add(new TextBlock
        {
            Text = v.SourceGameDir,
            FontSize = 11,
            Foreground = MutedBrush,
            Margin = new Thickness(0, 3, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = v.SourceGameDir
        });
        Grid.SetColumn(info, 1);
        grid.Children.Add(info);

        if (v.ExistsInTarget)
        {
            var badge = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0x33, 0xF0, 0xA0, 0x20)),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 2, 6, 2),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = "已存在", FontSize = 10, Foreground = WarnBrush }
            };
            Grid.SetColumn(badge, 2);
            grid.Children.Add(badge);
        }

        card.Child = grid;
        card.MouseLeftButtonUp += (_, _) =>
        {
            if (_importing || !check.IsEnabled) return;
            check.IsChecked = check.IsChecked != true;
        };

        ListPanel.Children.Add(card);
        _rows.Add((v, check));
    }

    #endregion

    #region 交互

    private async void Rescan_Click(object sender, RoutedEventArgs e) => await ScanAsync();

    private async void Browse_Click(object sender, RoutedEventArgs e)
    {
        if (_importing) return;

        var dlg = new OpenFolderDialog
        {
            Title = "选择 .minecraft 文件夹（或其中的 versions 文件夹）",
            Multiselect = false
        };
        if (dlg.ShowDialog(this) != true) return;

        var path = dlg.FolderName;
        try
        {
            // 允许选择 .minecraft 的父目录
            if (!Directory.Exists(Path.Combine(path, "versions")))
            {
                var sub = Path.Combine(path, ".minecraft");
                if (Directory.Exists(Path.Combine(sub, "versions"))) path = sub;
            }

            BrowseBtn.IsEnabled = false;
            ScanStatus.Text = "正在扫描所选目录…";
            var found = await Task.Run(() => _service.Scan(path));
            BrowseBtn.IsEnabled = true;

            if (found.Count == 0)
            {
                MessageBox.Show(this,
                    "该目录下没有找到可导入的版本。\n\n需要符合 versions\\<版本名>\\<版本名>.json 的目录结构。",
                    "没有找到版本", MessageBoxButton.OK, MessageBoxImage.Information);
                ScanStatus.Text = "所选目录没有版本";
                return;
            }

            if (!_extraDirs.Contains(path, StringComparer.OrdinalIgnoreCase)) _extraDirs.Add(path);

            var existing = new HashSet<string>(_rows.Select(r => r.Version.Id), StringComparer.OrdinalIgnoreCase);
            var added = 0;
            foreach (var v in found.Where(v => !v.IsSameDirectory))
            {
                if (!existing.Add(v.Id)) continue;
                AddRow(v);
                added++;
            }

            EmptyHint.Visibility = Visibility.Collapsed;
            ScanStatus.Text = added > 0 ? $"已从所选目录添加 {added} 个版本" : "所选目录的版本已在列表中";
            UpdateOkState();
        }
        catch (Exception ex)
        {
            BrowseBtn.IsEnabled = true;
            MessageBox.Show(this, "扫描失败：" + ex.Message, "NEX", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        if (_importing) return;
        // 全选不包含"已存在"的版本，避免误覆盖
        var selectable = _rows.Where(r => r.Box.IsEnabled && !r.Version.ExistsInTarget).ToList();
        var allChecked = selectable.Count > 0 && selectable.All(r => r.Box.IsChecked == true);
        foreach (var r in selectable) r.Box.IsChecked = !allChecked;
        SelectAllBtn.Content = allChecked ? "全选" : "全不选";
        UpdateOkState();
    }

    private void Mode_Changed(object sender, RoutedEventArgs e)
    {
        if (WithLibsBox == null) return;
        var copy = RadioCopy.IsChecked == true;
        WithLibsBox.IsEnabled = copy && !_importing;
        if (!copy) WithLibsBox.IsChecked = false;
    }

    private void UpdateOkState()
    {
        var count = _rows.Count(r => r.Box.IsChecked == true);
        OkBtn.Content = count > 0 ? $"导入所选（{count}）" : "导入所选";
        OkBtn.IsEnabled = count > 0 && !_importing;
    }

    private void SetBusy(bool busy)
    {
        RescanBtn.IsEnabled = !busy;
        BrowseBtn.IsEnabled = !busy;
        SelectAllBtn.IsEnabled = !busy;
        CancelBtn.IsEnabled = !busy;
        OkBtn.IsEnabled = !busy && _rows.Any(r => r.Box.IsChecked == true);
        RadioCopy.IsEnabled = !busy;
        RadioLink.IsEnabled = !busy;
        WithLibsBox.IsEnabled = !busy && RadioCopy.IsChecked == true;
        foreach (var r in _rows) r.Box.IsEnabled = !busy;
        Progress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (!busy) Progress.Value = 0;
    }

    private async void Ok_Click(object sender, RoutedEventArgs e)
    {
        var selected = _rows.Where(r => r.Box.IsChecked == true).Select(r => r.Version).ToList();
        if (selected.Count == 0) return;

        var overwrite = selected.Where(v => v.ExistsInTarget).Select(v => v.Id).ToList();
        if (overwrite.Count > 0)
        {
            var confirm = MessageBox.Show(this,
                "以下版本在当前游戏目录中已存在，导入会覆盖它们：\n\n"
                + string.Join("\n", overwrite.Take(10))
                + (overwrite.Count > 10 ? $"\n……共 {overwrite.Count} 个" : "")
                + "\n\n确定继续吗？",
                "覆盖已有版本", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (confirm != MessageBoxResult.Yes) return;
        }

        _importing = true;
        SetBusy(true);
        ImportedCount = 0;
        Errors.Clear();

        var mode = RadioLink.IsChecked == true ? VersionImportMode.Link : VersionImportMode.Copy;
        var withLibs = WithLibsBox.IsChecked == true && mode == VersionImportMode.Copy;

        for (var i = 0; i < selected.Count; i++)
        {
            var v = selected[i];
            var label = $"[{i + 1}/{selected.Count}] {v.Id}";
            OkBtn.Content = $"正在导入（{i + 1}/{selected.Count}）";

            var progress = new Progress<ImportProgress>(p =>
            {
                Progress.Value = p.Percent;
                StatusText.Text = string.IsNullOrEmpty(p.Current)
                    ? $"{label} 正在整理文件…（{p.Done}/{p.Total}）"
                    : $"{label} 正在复制 {p.Current}（{p.Done}/{p.Total} 个文件）";
            });

            try
            {
                await _service.ImportAsync(v, mode, withLibs, progress);
                ImportedCount++;
            }
            catch (Exception) when (mode == VersionImportMode.Link)
            {
                // 目录链接失败（跨卷 / 权限）时自动降级为复制
                try
                {
                    StatusText.Text = $"{label} 链接失败，改为复制导入…";
                    await _service.ImportAsync(v, VersionImportMode.Copy, withLibs, progress);
                    ImportedCount++;
                    Errors.Add($"{v.Id}：无法创建目录链接，已改为复制导入。");
                }
                catch (Exception ex2)
                {
                    Errors.Add($"{v.Id}：{ex2.Message}");
                }
            }
            catch (Exception ex)
            {
                Errors.Add($"{v.Id}：{ex.Message}");
            }
        }

        _importing = false;
        SetBusy(false);

        if (ImportedCount > 0)
        {
            DialogResult = true;
            return;
        }

        StatusText.Text = "导入失败，没有版本被导入。";
        MessageBox.Show(this, "没有版本被导入：\n\n" + string.Join("\n", Errors),
            "导入失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        UpdateOkState();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (_importing) return;
        try { DialogResult = false; }
        catch { Close(); }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            try { DragMove(); } catch { }
        }
    }

    #endregion
}
