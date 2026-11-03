using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NEXLauncher.Services;

namespace NEXLauncher;

/// <summary>
/// 整合包管理窗口：列出已安装整合包（含安装记录）、支持从文件安装与一键卸载。
/// 完整版与公共版共用本窗口，功能完全一致。
/// </summary>
public sealed class ModpackManagerWindow : Window
{
    private readonly string _gameDir;
    private readonly Func<Task> _onChanged;
    private readonly StackPanel _list = new();
    private readonly TextBlock _status = new();

    public ModpackManagerWindow(string gameDir, Func<Task> onChanged)
    {
        _gameDir = gameDir;
        _onChanged = onChanged;

        Title = "整合包 — NEX";
        Width = 760;
        Height = 620;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = B(UiPalette.Bg);
        Foreground = B(UiPalette.Text);
        WindowStyle = WindowStyle.ToolWindow;

        var root = new DockPanel { Margin = new Thickness(22, 16, 22, 16) };
        Content = root;

        // 顶部：标题 + 操作按钮
        var head = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        DockPanel.SetDock(head, Dock.Top);
        head.Children.Add(new TextBlock
        {
            Text = "整合包管理",
            FontSize = 22,
            FontWeight = FontWeights.SemiBold,
            Foreground = B(UiPalette.Text)
        });
        head.Children.Add(new TextBlock
        {
            Text = "支持 Modrinth（.mrpack）与 CurseForge（.zip）整合包；安装时会把模组、配置、资源包写入游戏目录，并在 nex-modpacks 下留记录，方便一键卸载。",
            FontSize = 12,
            Foreground = B(UiPalette.Muted),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0)
        });

        var btnRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 12, 0, 0)
        };
        var installBtn = new Button
        {
            Content = "+ 安装整合包…",
            Padding = new Thickness(16, 8, 16, 8),
            Background = B(UiPalette.Accent),
            Foreground = B(UiPalette.Bg),
            BorderThickness = new Thickness(0),
            FontWeight = FontWeights.SemiBold,
            Cursor = System.Windows.Input.Cursors.Hand,
            FontSize = 12
        };
        installBtn.Click += (_, _) => PickFileAndInstall();
        btnRow.Children.Add(installBtn);

        var refreshBtn = new Button
        {
            Content = "刷新",
            Padding = new Thickness(14, 8, 14, 8),
            Margin = new Thickness(8, 0, 0, 0),
            Background = B(UiPalette.SurfaceAlt),
            Foreground = B(UiPalette.TextDim),
            BorderThickness = new Thickness(0),
            Cursor = System.Windows.Input.Cursors.Hand,
            FontSize = 12
        };
        refreshBtn.Click += (_, _) => Reload();
        btnRow.Children.Add(refreshBtn);

        var openDirBtn = new Button
        {
            Content = "打开 modpacks 文件夹",
            Padding = new Thickness(14, 8, 14, 8),
            Margin = new Thickness(8, 0, 0, 0),
            Background = B(UiPalette.SurfaceAlt),
            Foreground = B(UiPalette.Muted),
            BorderThickness = new Thickness(0),
            Cursor = System.Windows.Input.Cursors.Hand,
            FontSize = 12
        };
        openDirBtn.Click += (_, _) => OpenModpacksDir();
        btnRow.Children.Add(openDirBtn);

        head.Children.Add(btnRow);
        root.Children.Add(head);

        // 底部：状态
        _status.FontSize = 11;
        _status.Foreground = B(UiPalette.Muted);
        _status.Margin = new Thickness(0, 10, 0, 0);
        _status.TextWrapping = TextWrapping.Wrap;
        DockPanel.SetDock(_status, Dock.Bottom);
        root.Children.Add(_status);

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        scroll.Content = _list;
        root.Children.Add(scroll);

        Reload();
    }

    private void OpenModpacksDir()
    {
        var dir = Path.Combine(_gameDir, "modpacks");
        try
        {
            Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start("explorer.exe", dir);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "打开文件夹失败：" + ex.Message, "NEX",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void PickFileAndInstall()
    {
        var picker = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择整合包文件",
            Filter = "整合包 (*.mrpack;*.zip)|*.mrpack;*.zip|Modrinth 整合包 (*.mrpack)|*.mrpack|整合包压缩包 (*.zip)|*.zip|所有文件 (*.*)|*.*",
            InitialDirectory = Directory.Exists(Path.Combine(_gameDir, "modpacks"))
                ? Path.Combine(_gameDir, "modpacks") : _gameDir
        };
        if (picker.ShowDialog(this) != true) return;

        ModpackInfo info;
        try
        {
            info = await ModpackService.InspectAsync(picker.FileName);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "无法读取整合包：\n" + ex.Message, "NEX",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

#if PUBLIC_EDITION
        const bool allowDownload = false;
#else
        const bool allowDownload = true;
#endif

        var installed = new List<string>();
        try
        {
            var vdir = Path.Combine(_gameDir, "versions");
            if (Directory.Exists(vdir))
                installed = Directory.GetDirectories(vdir)
                    .Select(Path.GetFileName)
                    .Where(n => !string.IsNullOrEmpty(n))
                    .Select(n => n!)
                    .OrderByDescending(n => File.GetLastWriteTimeUtc(Path.Combine(vdir, n, n + ".json")))
                    .ToList();
        }
        catch { }

        var dlg = new ModpackInstallDialog(info, _gameDir, installed,
            async (mc, loaderId, loaderVersion) =>
            {
#if PUBLIC_EDITION
                await Task.CompletedTask;
                return null;
#else
                if (!File.Exists(Path.Combine(_gameDir, "versions", mc, mc + ".json")))
                    await new MinecraftService(_gameDir).InstallAsync(mc);
                return mc;
#endif
            }, allowDownload)
        {
            Owner = this
        };
        dlg.ShowDialog();
        if (dlg.Result != null)
        {
            Reload();
            _ = _onChanged.Invoke();
        }
    }

    private void Reload()
    {
        _list.Children.Clear();
        List<ModpackRecord> installed;
        try { installed = ModpackService.ListInstalled(_gameDir); }
        catch (Exception ex)
        {
            _status.Text = "读取安装记录失败：" + ex.Message;
            return;
        }

        if (installed.Count == 0)
        {
            _list.Children.Add(new Border
            {
                Background = B(UiPalette.Surface),
                BorderBrush = B(UiPalette.Border),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(16, 18, 16, 18),
                Margin = new Thickness(0, 4, 0, 0),
                Child = new TextBlock
                {
                    Text = "还没有通过本启动器安装的整合包。\n\n"
                         + "点上方「+ 安装整合包…」选择 .mrpack / .zip 文件；\n"
                         + "也可以在「资源下载 → 整合包」里下载后点「立即安装」。",
                    FontSize = 12,
                    Foreground = B(UiPalette.Muted),
                    TextWrapping = TextWrapping.Wrap,
                    LineHeight = 21
                }
            });
            _status.Text = "未检测到已安装整合包。";
            return;
        }

        foreach (var rec in installed) _list.Children.Add(BuildRow(rec));
        _status.Text = $"共 {installed.Count} 个已安装整合包 · 安装记录存放在 {ModpackService.InstancesRoot(_gameDir)}";
    }

    private UIElement BuildRow(ModpackRecord rec)
    {
        var card = new Border
        {
            Background = B(UiPalette.Surface),
            BorderBrush = B(UiPalette.Border),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(14, 12, 14, 12),
            Margin = new Thickness(0, 0, 0, 10)
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var info = new StackPanel();
        info.Children.Add(new TextBlock
        {
            Text = rec.Name,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = B(UiPalette.Text),
            TextWrapping = TextWrapping.Wrap
        });

        var meta = new List<string> { $"版本 {rec.Version}" };
        if (!string.IsNullOrEmpty(rec.McVersion)) meta.Add("MC " + rec.McVersion);
        if (!string.IsNullOrEmpty(rec.LoaderId)) meta.Add(rec.LoaderId + (string.IsNullOrEmpty(rec.LoaderVersion) ? "" : " " + rec.LoaderVersion));
        meta.Add($"文件 {rec.Files.Count} 个");
        meta.Add("安装于 " + rec.InstalledAt);
        if (!string.IsNullOrEmpty(rec.BackupDir)) meta.Add("原 mods 已备份");

        info.Children.Add(new TextBlock
        {
            Text = string.Join(" · ", meta),
            FontSize = 11.5,
            Foreground = B(UiPalette.Muted),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0)
        });

        info.Children.Add(new TextBlock
        {
            Text = $"格式：{FormatName(rec.Format)}",
            FontSize = 11,
            Foreground = B(UiPalette.MutedLight),
            Margin = new Thickness(0, 2, 0, 0)
        });
        Grid.SetColumn(info, 0);
        grid.Children.Add(info);

        var btnCol = new StackPanel
        {
            Orientation = Orientation.Vertical,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0)
        };

        var uninstallBtn = new Button
        {
            Content = "卸载",
            Padding = new Thickness(14, 6, 14, 6),
            Background = B("#3A1D22"),
            Foreground = B("#F87171"),
            BorderThickness = new Thickness(0),
            Cursor = System.Windows.Input.Cursors.Hand,
            FontSize = 12
        };
        uninstallBtn.Click += (_, _) => Uninstall(rec);
        btnCol.Children.Add(uninstallBtn);

        Grid.SetColumn(btnCol, 1);
        grid.Children.Add(btnCol);

        card.Child = grid;
        return card;
    }

    private void Uninstall(ModpackRecord rec)
    {
        var hasBackup = !string.IsNullOrEmpty(rec.BackupDir) && Directory.Exists(rec.BackupDir);
        var msg = $"确定要卸载整合包「{rec.Name}」吗？\n\n"
                  + $"将删除本次安装写入游戏目录的 {rec.Files.Count} 个文件（模组、配置等），"
                  + $"并移除安装记录。\n\n"
                  + (hasBackup
                      ? $"安装前备份的 mods 文件夹还在：\n{rec.BackupDir}\n卸载后可以还原它。\n\n"
                      : "")
                  + "此操作不可撤销（不会删除你的存档、游戏版本和资源包以外的个人文件）。";

        if (MessageBox.Show(this, msg, "卸载整合包", MessageBoxButton.YesNo, MessageBoxImage.Warning)
            != MessageBoxResult.Yes) return;

        var restore = false;
        if (hasBackup)
            restore = MessageBox.Show(this,
                "是否把安装前备份的 mods 文件夹还原回去？\n\n选「是」：还原备份（推荐，回到安装前的模组状态）\n选「否」：保留当前 mods 情况，备份文件夹保留不删",
                "还原备份", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

        try
        {
            ModpackService.Uninstall(_gameDir, rec, restore);
            Reload();
            _ = _onChanged.Invoke();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "卸载失败：\n" + ex.Message, "NEX",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static string FormatName(string format) => format switch
    {
        "mrpack" => "Modrinth 整合包（.mrpack）",
        "curseforge" => "CurseForge 整合包（.zip）",
        _ => "整合包压缩包（.zip）"
    };

    private static SolidColorBrush B(string hex) =>
        new((Color)ColorConverter.ConvertFromString(hex));
}
