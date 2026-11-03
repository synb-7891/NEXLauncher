using System.Windows;
using System.Windows.Controls;

namespace NEXLauncher;

public partial class ModloaderDialog : Window
{
    private readonly string _gameVersion;
    private readonly Services.ModloaderService _service = new();

    // 各加载器的版本缓存（最新在前）
    private readonly Dictionary<string, List<string>> _versions = new();
    // Forge 官方推荐版
    private string? _forgeRecommended;

    public string SelectedLoaderId { get; private set; } = "vanilla";
    public string SelectedLoaderVersion { get; private set; } = "";

    public ModloaderDialog(string gameVersion)
    {
        InitializeComponent();
        _gameVersion = gameVersion;
        VersionLabel.Text = $"目标游戏版本：{gameVersion}";
        // 默认原版：直接隐藏版本下拉区（PCL2 风格，避免出现空框）
        VersionSection.Visibility = Visibility.Collapsed;
        VanillaHint.Visibility = Visibility.Visible;
        OkBtn.IsEnabled = true;
        Loaded += async (_, _) => await PrefetchLoaderVersionsAsync();
    }

    /// <summary>窗口打开后并行获取三种加载器的版本列表，把"最新版本"直接显示在选项上（PCL2 风格）。</summary>
    private async Task PrefetchLoaderVersionsAsync()
    {
        var fabricTask = SafeAsync(async () => await _service.GetFabricLoaderVersionsAsync(_gameVersion));
        var forgeTask = SafeAsync(async () => await _service.GetForgeLoaderInfoAsync(_gameVersion));
        var quiltTask = SafeAsync(async () => await _service.GetQuiltLoaderVersionsAsync(_gameVersion));

        await Task.WhenAll(fabricTask, forgeTask, quiltTask);

        if (fabricTask.Result != null)
        {
            _versions["fabric"] = fabricTask.Result!;
            RadioFabric.Content = $"Fabric · 最新 {fabricTask.Result!.FirstOrDefault() ?? "无可用版本"} · 轻量，模组生态最丰富";
        }
        else
        {
            RadioFabric.Content = "Fabric · 最新版本获取失败（点选后重试）· 轻量，模组生态最丰富";
        }

        if (forgeTask.Result != null)
        {
            var info = forgeTask.Result!;
            _versions["forge"] = info.Versions;
            _forgeRecommended = info.Recommended;
            var latest = info.Latest ?? info.Versions.FirstOrDefault();
            var mark = info.Recommended != null ? $"，推荐 {info.Recommended}" : "";
            RadioForge.Content = $"Forge · 最新 {latest ?? "无可用版本"}{mark} · 大型模组首选";
        }
        else
        {
            RadioForge.Content = "Forge · 最新版本获取失败（点选后重试）· 大型模组首选";
        }

        if (quiltTask.Result != null)
        {
            _versions["quilt"] = quiltTask.Result!;
            RadioQuilt.Content = $"Quilt · 最新 {quiltTask.Result!.FirstOrDefault() ?? "无可用版本"} · Fabric 分支，兼容性更好";
        }
        else
        {
            RadioQuilt.Content = "Quilt · 最新版本获取失败（点选后重试）· Fabric 分支";
        }

        // 如果用户已经选了某个加载器，刷新其版本下拉
        if (SelectedLoaderId != "vanilla")
            FillVersionCombo(SelectedLoaderId);
    }

    private static async Task<T?> SafeAsync<T>(Func<Task<T>> func) where T : class
    {
        try { return await func(); }
        catch { return null; }
    }

    private async void LoaderChoice_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        string loaderId = RadioFabric.IsChecked == true ? "fabric"
                        : RadioForge.IsChecked == true ? "forge"
                        : RadioQuilt.IsChecked == true ? "quilt" : "vanilla";
        SelectedLoaderId = loaderId;

        if (loaderId == "vanilla")
        {
            // PCL2 风格：原版没有版本可选，隐藏版本区并给出说明，避免出现空的下拉框
            VersionSection.Visibility = Visibility.Collapsed;
            VanillaHint.Visibility = Visibility.Visible;
            VersionCombo.IsEnabled = false;
            VersionCombo.Items.Clear();
            OkBtn.IsEnabled = true;
            LoadingHint.Text = "";
            return;
        }

        VanillaHint.Visibility = Visibility.Collapsed;
        VersionSection.Visibility = Visibility.Visible;

        OkBtn.IsEnabled = false;
        if (_versions.TryGetValue(loaderId, out var cached) && cached.Count > 0)
        {
            FillVersionCombo(loaderId);
            return;
        }

        // 缓存没有（预取失败或还没回来）：点选时重新加载，下拉框先给占位项避免空白
        VersionCombo.IsEnabled = false;
        VersionCombo.Items.Clear();
        VersionCombo.Items.Add(new ComboBoxItem { Content = "正在获取版本列表…", IsEnabled = false });
        VersionCombo.SelectedIndex = 0;
        LoadingHint.Text = "正在加载可用版本...";
        try
        {
            List<string> versions;
            if (loaderId == "forge")
            {
                var info = await _service.GetForgeLoaderInfoAsync(_gameVersion);
                _forgeRecommended = info.Recommended;
                versions = info.Versions;
            }
            else if (loaderId == "fabric")
            {
                versions = await _service.GetFabricLoaderVersionsAsync(_gameVersion);
            }
            else
            {
                versions = await _service.GetQuiltLoaderVersionsAsync(_gameVersion);
            }
            _versions[loaderId] = versions;
            FillVersionCombo(loaderId);
        }
        catch (Exception ex)
        {
            LoadingHint.Text = "加载失败：" + ex.Message;
        }
    }

    /// <summary>填充版本下拉：标"最新 / 推荐"，Forge 默认选中官方推荐版（PCL2 同款）。</summary>
    private void FillVersionCombo(string loaderId)
    {
        VersionCombo.Items.Clear();
        if (!_versions.TryGetValue(loaderId, out var versions) || versions.Count == 0)
        {
            VersionCombo.IsEnabled = false;
            LoadingHint.Text = $"该游戏版本（{_gameVersion}）暂无可用的 {loaderId.ToUpperInvariant()} 版本。";
            return;
        }

        LoadingHint.Text = $"共 {versions.Count} 个版本" +
            (loaderId == "forge" && _forgeRecommended != null ? "，已默认选中官方推荐版" : "，已默认选中最新版");
        for (int i = 0; i < versions.Count; i++)
        {
            var v = versions[i];
            var suffix = i == 0 ? "（最新）"
                : loaderId == "forge" && v == _forgeRecommended ? "（推荐）"
                : "";
            VersionCombo.Items.Add(new ComboBoxItem { Content = v + suffix, Tag = v });
        }

        // PCL2 行为：Forge 默认选官方推荐版（没有推荐才选最新），其它加载器默认最新
        var defaultIndex = 0;
        if (loaderId == "forge" && _forgeRecommended != null)
        {
            var idx = versions.IndexOf(_forgeRecommended);
            if (idx > 0) defaultIndex = idx;
        }
        VersionCombo.SelectedIndex = defaultIndex;
        VersionCombo.IsEnabled = true;
        OkBtn.IsEnabled = true;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (RadioFabric.IsChecked == true || RadioForge.IsChecked == true || RadioQuilt.IsChecked == true)
        {
            SelectedLoaderVersion = (VersionCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
            if (string.IsNullOrEmpty(SelectedLoaderVersion))
            {
                MessageBox.Show("请选择加载器版本。", "NEX", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
