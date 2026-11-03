using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NEXLauncher.Controls;
using NEXLauncher.Services;

namespace NEXLauncher;

/// <summary>
/// 「资源下载」页：按 PCL2 的下载页规格重做。
/// 左侧分类栏（整合包 / 模组 / 资源包 / 光影包 / 数据包），右侧搜索 + 筛选 + 结果列表 + 分页，
/// 点击结果进入项目详情（介绍 / 图片 / 版本列表），每个版本可单独下载。
/// </summary>
public partial class MainWindow
{
    #region 状态
    private string _dlType = "mod";
    private int _dlPage;
    private const int DlPageSize = 20;
    private string _dlQuery = "";
    private int _dlTotal;
    private CancellationTokenSource? _dlCts;

    private ContentControl? _dlHost;
    private UIElement? _dlListView;
    private StackPanel? _dlRowsPanel;
    private TextBlock? _dlStatus;
    private TextBox? _dlSearchBox;
    private ComboBox? _dlSortBox;
    private ComboBox? _dlVersionBox;
    private ComboBox? _dlLoaderBox;
    private WrapPanel? _dlCategoryChips;
    private string? _dlCategoryId;
    private string? _dlGameVersion;
    private string? _dlLoader;
    private Button? _dlPrevBtn;
    private Button? _dlNextBtn;
    private TextBlock? _dlPageText;
    private ScrollViewer? _dlPageScroll;
    private StackPanel? _dlRailPanel;

    private ModrinthProject? _dlCurrent;
    private ModrinthProjectDetail? _dlDetail;
    private string? _dlDetailZh;
    private List<ModrinthVersion> _dlVersions = [];
    private ContentControl? _dlTabBody;
    private readonly List<Button> _dlTabButtons = [];
    private int _dlTab;
    private List<ModrinthProject> _dlLastHits = [];
    private ComboBox? _dlVerGameBox;
    private ComboBox? _dlVerLoaderBox;
    private TextBlock? _dlVerCount;
    private StackPanel? _dlVersionRows;

    private static readonly (string Icon, string Label, string Type)[] DlTypes =
    [
        ("\uE7FC", "整合包", "modpack"),
        ("\uE74C", "模组", "mod"),
        ("\uE7B8", "资源包", "resourcepack"),
        ("\uE790", "光影包", "shader"),
        ("\uE8A5", "数据包", "datapack"),
        ("\uE945", "插件", "plugin"),
    ];

    /// <summary>每类最多可翻 2000 页（Modrinth 搜索接口支持到 offset 4 万+）。</summary>
    private const int DlMaxPages = 2000;

    private static readonly (string Label, string Key)[] DlSorts =
    [
        ("相关度", "relevance"),
        ("下载量", "downloads"),
        ("最近更新", "updated"),
        ("最新发布", "newest"),
    ];
    #endregion

    private static string DlTitle(string type) => DlTypes.FirstOrDefault(t => t.Type == type).Label switch
    {
        null or "" => "资源下载",
        var l => l,
    };

    private static string DlIcon(string type) => DlTypes.FirstOrDefault(t => t.Type == type).Icon ?? "\uE74C";

    #region 页面骨架
    private UIElement BuildDownloadPage(string defaultType)
    {
        _dlType = string.IsNullOrEmpty(defaultType) ? "mod" : defaultType;
        _dlPage = 0;
        _dlQuery = "";
        _dlCategoryId = null;
        _dlGameVersion = null;
        _dlLoader = null;

        var outer = new Grid();
        outer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(128) });
        outer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // ---- 左：分类栏 ----
        var railCard = Card();
        railCard.Margin = new Thickness(0, 0, 12, 0);
        railCard.VerticalAlignment = VerticalAlignment.Top;
        _dlRailPanel = new StackPanel { Margin = new Thickness(8, 10, 8, 10) };
        foreach (var (icon, label, type) in DlTypes)
            _dlRailPanel.Children.Add(BuildRailItem(icon, label, type));
        railCard.Children.Add(_dlRailPanel);
        Grid.SetColumn(railCard, 0);
        outer.Children.Add(railCard);

        // ---- 右：内容（列表 / 详情切换）----
        _dlHost = new ContentControl();
        _dlListView = BuildDlListView();
        _dlHost.Content = _dlListView;
        Grid.SetColumn(_dlHost, 1);
        outer.Children.Add(_dlHost);

        _dlPageScroll = FindAncestorScrollViewer(PageHost);
        RefreshRailSelection();
        if (_allVersions.Count == 0) _ = LoadVersionsForCatalogAsync();
        _ = LoadDlResultsAsync();

        // 自动化自检：NEX_CATALOG_E2E=1 时验证「列表加载 / 行内下载按钮 / 中文搜索 / 详情页」并截图到 %TEMP%
        if (Environment.GetEnvironmentVariable("NEX_CATALOG_E2E") == "1")
            _ = Task.Run(RunCatalogE2EAsync);
        return outer;
    }

    /// <summary>找到页面所在的滚动容器，翻页后回到顶部。</summary>
    private static ScrollViewer? FindAncestorScrollViewer(DependencyObject node)
    {
        var current = node;
        while (current != null)
        {
            if (current is ScrollViewer sv) return sv;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private Button BuildRailItem(string icon, string label, string type)
    {
        var btn = new Button
        {
            Tag = type,
            Height = 38,
            Margin = new Thickness(0, 0, 0, 2),
            Padding = new Thickness(10, 0, 10, 0),
            Background = Brushes.Transparent,
            Foreground = Brush(UiPalette.TextDim),
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(new TextBlock
        {
            Text = icon,
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 14,
            Width = 22,
            VerticalAlignment = VerticalAlignment.Center
        });
        row.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center
        });
        btn.Content = row;
        btn.Click += (_, _) =>
        {
            if (_dlType == type) return;
            _dlType = type;
            _dlPage = 0;
            _dlCategoryId = null;
            RefreshRailSelection();
            _dlListView = BuildDlListView();
            if (_dlHost != null) _dlHost.Content = _dlListView;
            _ = LoadDlResultsAsync();
        };
        return btn;
    }

    private void RefreshRailSelection()
    {
        if (_dlRailPanel == null) return;
        foreach (var child in _dlRailPanel.Children)
        {
            if (child is not Button b) continue;
            var on = (string)b.Tag == _dlType;
            b.Background = on ? Brush(UiPalette.SurfaceAlt) : Brushes.Transparent;
            b.Foreground = on ? Brush(UiPalette.Accent) : Brush(UiPalette.TextDim);
            if (b.Content is StackPanel sp && sp.Children.Count == 2 && sp.Children[0] is TextBlock iconTb)
                iconTb.Foreground = on ? Brush(UiPalette.Accent) : Brush(UiPalette.Muted);
        }
    }

    /// <summary>列表视图：标题 + 搜索行 + 筛选项 + 结果 + 分页。</summary>
    private UIElement BuildDlListView()
    {
        var root = new StackPanel();
        root.Children.Add(Label("下载", 11, UiPalette.Accent));
        root.Children.Add(Heading(DlTitle(_dlType)));
        root.Children.Add(Body("搜索并下载整合包、模组、资源包、光影包、数据包与插件（支持中文搜索，简介自动翻译为中文）；点击条目可查看介绍、图片和全部版本。"));
        root.Children.Add(BuildDlSearchCard());

        _dlStatus = new TextBlock
        {
            FontSize = 12,
            Foreground = Brush(UiPalette.Muted),
            Margin = new Thickness(2, 2, 0, 8)
        };
        root.Children.Add(_dlStatus);

        _dlRowsPanel = new StackPanel();
        root.Children.Add(_dlRowsPanel);

        root.Children.Add(BuildDlPager());
        return root;
    }

    private UIElement BuildDlSearchCard()
    {
        var card = Card();
        card.Margin = new Thickness(0, 12, 0, 10);
        var inner = new StackPanel { Margin = new Thickness(14, 12, 14, 12) };
        card.Children.Add(inner);

        // 第 1 行：搜索框 + 搜索按钮
        var searchRow = new Grid();
        searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var boxHost = new Border
        {
            Background = Brush(UiPalette.Bg),
            BorderBrush = Brush(UiPalette.Border),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Height = 36
        };
        var boxGrid = new Grid();
        boxGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        boxGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var searchIcon = new TextBlock
        {
            Text = "\uE721",
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 13,
            Foreground = Brush(UiPalette.Muted),
            Margin = new Thickness(12, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        boxGrid.Children.Add(searchIcon);
        _dlSearchBox = new TextBox
        {
            Text = "",
            FontSize = 13,
            Background = Brushes.Transparent,
            Foreground = Brush(UiPalette.Text),
            BorderThickness = new Thickness(0),
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Thickness(0)
        };
        _dlSearchBox.KeyDown += async (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            _dlQuery = _dlSearchBox.Text.Trim();
            _dlPage = 0;
            await LoadDlResultsAsync();
        };
        Grid.SetColumn(_dlSearchBox, 1);
        boxGrid.Children.Add(_dlSearchBox);
        boxHost.Child = boxGrid;
        Grid.SetColumn(boxHost, 0);
        searchRow.Children.Add(boxHost);

        var searchBtn = new Button
        {
            Content = "搜索",
            Width = 84,
            Height = 36,
            Margin = new Thickness(10, 0, 0, 0),
            Background = Brush(UiPalette.Accent),
            Foreground = Brush(UiPalette.Bg),
            BorderThickness = new Thickness(0),
            FontWeight = FontWeights.SemiBold,
            Cursor = Cursors.Hand
        };
        searchBtn.Click += async (_, _) =>
        {
            _dlQuery = _dlSearchBox?.Text.Trim() ?? "";
            _dlPage = 0;
            await LoadDlResultsAsync();
        };
        Grid.SetColumn(searchBtn, 1);
        searchRow.Children.Add(searchBtn);
        inner.Children.Add(searchRow);

        // 第 2 行：排序 / 版本 / 加载器 / 重置
        var filterRow = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        _dlSortBox = DlCombo(DlSorts.Select(s => s.Label).ToList(), 0, 118);
        _dlSortBox.SelectionChanged += async (_, _) =>
        {
            _dlPage = 0;
            await LoadDlResultsAsync();
        };
        filterRow.Children.Add(DlFilter("排序", _dlSortBox));

        _dlVersionBox = DlCombo(["全部版本"], 0, 128);
        _dlVersionBox.SelectionChanged += async (_, _) =>
        {
            var text = _dlVersionBox.SelectedItem as string;
            _dlGameVersion = string.IsNullOrEmpty(text) || text == "全部版本" ? null : text;
            _dlPage = 0;
            await LoadDlResultsAsync();
        };
        filterRow.Children.Add(DlFilter("版本", _dlVersionBox));

        _dlLoaderBox = DlCombo(["全部加载器", "Fabric", "Forge", "NeoForge", "Quilt"], 0, 132);
        _dlLoaderBox.SelectionChanged += async (_, _) =>
        {
            var text = _dlLoaderBox.SelectedItem as string;
            _dlLoader = string.IsNullOrEmpty(text) || text == "全部加载器" ? null : text.ToLowerInvariant();
            _dlPage = 0;
            await LoadDlResultsAsync();
        };
        _dlLoaderBox.Visibility = _dlType is "mod" or "modpack" ? Visibility.Visible : Visibility.Collapsed;
        filterRow.Children.Add(DlFilter("加载器", _dlLoaderBox));

        var resetBtn = new Button
        {
            Content = "重置条件",
            Padding = new Thickness(12, 7, 12, 7),
            Margin = new Thickness(0, 0, 0, 0),
            Background = Brush(UiPalette.SurfaceAlt),
            Foreground = Brush(UiPalette.TextDim),
            BorderThickness = new Thickness(0),
            FontSize = 12,
            Cursor = Cursors.Hand
        };
        resetBtn.Click += async (_, _) =>
        {
            if (_dlSearchBox != null) _dlSearchBox.Text = "";
            _dlQuery = "";
            _dlPage = 0;
            _dlCategoryId = null;
            _dlGameVersion = null;
            _dlLoader = null;
            if (_dlSortBox != null) _dlSortBox.SelectedIndex = 0;
            if (_dlVersionBox != null) _dlVersionBox.SelectedIndex = 0;
            if (_dlLoaderBox != null) _dlLoaderBox.SelectedIndex = 0;
            RefreshCategoryChips();
            await LoadDlResultsAsync();
        };
        filterRow.Children.Add(resetBtn);
        inner.Children.Add(filterRow);

        // 第 3 行：分类标签（PCL2 风格的类型标签条）
        _dlCategoryChips = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        inner.Children.Add(_dlCategoryChips);
        BuildCategoryChips();
        return card;
    }

    private static ComboBox DlCombo(IEnumerable<string> items, int selected, double width) => new()
    {
        ItemsSource = items.ToList(),
        SelectedIndex = selected,
        Width = width,
        FontSize = 12,
        Padding = new Thickness(10, 6, 8, 6),
        Background = Brush(UiPalette.Bg),
        Foreground = Brush(UiPalette.Text),
        BorderBrush = Brush(UiPalette.Border)
    };

    private static FrameworkElement DlFilter(string label, FrameworkElement control)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 14, 6) };
        panel.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 12,
            Foreground = Brush(UiPalette.Muted),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0)
        });
        panel.Children.Add(control);
        return panel;
    }

    private void BuildCategoryChips()
    {
        if (_dlCategoryChips == null) return;
        _dlCategoryChips.Children.Clear();
        var cats = CatalogCategoryMap.TryGetValue(_dlType, out var list) ? list : [("全部", "")];
        foreach (var (label, id) in cats)
        {
            var active = string.Equals(string.IsNullOrEmpty(id) ? null : id, _dlCategoryId)
                         || (string.IsNullOrEmpty(id) && string.IsNullOrEmpty(_dlCategoryId));
            var b = new Button
            {
                Content = label,
                Tag = id,
                Padding = new Thickness(11, 5, 11, 5),
                Margin = new Thickness(0, 0, 6, 6),
                FontSize = 12,
                Background = active ? Brush(UiPalette.Accent) : Brush(UiPalette.SurfaceAlt),
                Foreground = active ? Brush(UiPalette.Bg) : Brush(UiPalette.TextDim),
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand
            };
            b.Click += async (_, _) =>
            {
                _dlCategoryId = string.IsNullOrEmpty(id) ? null : id;
                _dlPage = 0;
                RefreshCategoryChips();
                await LoadDlResultsAsync();
            };
            _dlCategoryChips.Children.Add(b);
        }
    }

    private void RefreshCategoryChips() => BuildCategoryChips();

    private UIElement BuildDlPager()
    {
        var row = new DockPanel { Margin = new Thickness(2, 12, 2, 4) };
        _dlPageText = new TextBlock
        {
            Text = "",
            FontSize = 12,
            Foreground = Brush(UiPalette.Muted),
            VerticalAlignment = VerticalAlignment.Center
        };
        row.Children.Add(_dlPageText);

        var nav = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        _dlNextBtn = new Button
        {
            Content = "下一页 ▶",
            Padding = new Thickness(14, 7, 14, 7),
            FontSize = 12,
            Background = Brush(UiPalette.SurfaceAlt),
            Foreground = Brush(UiPalette.TextDim),
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand
        };
        _dlNextBtn.Click += async (_, _) =>
        {
            if (!_dlNextBtn.IsEnabled) return;
            _dlPage++;
            await LoadDlResultsAsync();
            _dlPageScroll?.ScrollToTop();
        };
        _dlPrevBtn = new Button
        {
            Content = "◀ 上一页",
            Padding = new Thickness(14, 7, 14, 7),
            FontSize = 12,
            Margin = new Thickness(0, 0, 8, 0),
            Background = Brush(UiPalette.SurfaceAlt),
            Foreground = Brush(UiPalette.TextDim),
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand
        };
        _dlPrevBtn.Click += async (_, _) =>
        {
            if (!_dlPrevBtn.IsEnabled) return;
            _dlPage--;
            await LoadDlResultsAsync();
            _dlPageScroll?.ScrollToTop();
        };
        nav.Children.Add(_dlPrevBtn);
        nav.Children.Add(_dlNextBtn);
        DockPanel.SetDock(nav, Dock.Right);
        row.Children.Add(nav);
        return row;
    }
    #endregion

    #region 搜索结果
    private async Task LoadDlResultsAsync()
    {
        _dlCts?.Cancel();
        _dlCts = new CancellationTokenSource();
        var token = _dlCts.Token;

        if (_dlStatus != null) _dlStatus.Text = "正在加载...";
        if (_dlRowsPanel != null) _dlRowsPanel.Children.Clear();

        var sortKey = _dlSortBox?.SelectedIndex is int idx && idx >= 0 && idx < DlSorts.Length
            ? DlSorts[idx].Key : "relevance";

        try
        {
            // 中文搜索：先把关键词翻译成英文再搜（Modrinth 只认英文），翻译失败则用原词
            var effectiveQuery = _dlQuery;
            string? translatedQuery = null;
            if (!string.IsNullOrEmpty(_dlQuery) && ModrinthService.ContainsCjk(_dlQuery))
            {
                if (_dlStatus != null) _dlStatus.Text = "正在翻译搜索词...";
                translatedQuery = await ModrinthService.TranslateQueryToEnglishAsync(_dlQuery, token);
                if (!string.IsNullOrEmpty(translatedQuery)
                    && !translatedQuery.Equals(_dlQuery, StringComparison.OrdinalIgnoreCase))
                    effectiveQuery = translatedQuery;
                if (token.IsCancellationRequested) return;
                if (_dlStatus != null) _dlStatus.Text = "正在加载...";
            }

            var page = await _modrinthService.SearchPagedAsync(
                effectiveQuery, _dlType, _dlCategoryId,
                BuildGameVersionFacet(_dlGameVersion),
                string.IsNullOrEmpty(_dlLoader) ? null : new[] { _dlLoader },
                sortKey, _dlPage * DlPageSize, DlPageSize, token);
            if (token.IsCancellationRequested || _dlRowsPanel == null) return;

            _dlTotal = page.TotalHits;
            _dlLastHits = page.Hits;
            _dlRowsPanel.Children.Clear();

            if (page.Hits.Count == 0)
            {
                if (_dlStatus != null) _dlStatus.Text = "没有找到符合条件的内容，试试减少筛选条件或换个关键词。";
                UpdatePager();
                return;
            }

            if (_dlStatus != null)
                _dlStatus.Text = string.IsNullOrEmpty(_dlQuery)
                    ? $"热门{DlTitle(_dlType)} · 共 {FormatCount(_dlTotal)} 个"
                    : translatedQuery != null
                        ? $"“{_dlQuery}”（翻译为 “{translatedQuery}”）的搜索结果 · 共 {FormatCount(_dlTotal)} 个"
                        : $"“{_dlQuery}” 的搜索结果 · 共 {FormatCount(_dlTotal)} 个";

            foreach (var item in page.Hits)
                _dlRowsPanel.Children.Add(BuildDlRow(item));

            UpdatePager();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (token.IsCancellationRequested) return;
            if (_dlStatus != null) _dlStatus.Text = "加载失败：" + ex.Message;
            if (_dlRowsPanel == null) return;
            var retry = new Button
            {
                Content = "重试",
                Padding = new Thickness(16, 8, 16, 8),
                Margin = new Thickness(0, 6, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                Background = Brush(UiPalette.Accent),
                Foreground = Brush(UiPalette.Bg),
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand
            };
            retry.Click += async (_, _) => await LoadDlResultsAsync();
            _dlRowsPanel.Children.Add(retry);
            UpdatePager();
        }
    }

    /// <summary>把 "1.20" 展开成 1.20 系列的全部正式版 ID（Modrinth facets 需要精确版本号）。</summary>
    private List<string>? BuildGameVersionFacet(string? family)
    {
        if (string.IsNullOrEmpty(family)) return null;
        var ids = _allVersions
            .Where(v => v.Category == "正式版" && (v.Id == family || v.Id.StartsWith(family + ".", StringComparison.Ordinal)))
            .Select(v => v.Id)
            .ToList();
        return ids.Count > 0 ? ids : [family];
    }

    private void UpdatePager()
    {
        var totalPages = Math.Max(1, (int)Math.Ceiling(_dlTotal / (double)DlPageSize));
        if (_dlPageText != null)
            _dlPageText.Text = _dlTotal == 0
                ? ""
                : $"第 {_dlPage + 1} / {Math.Min(totalPages, DlMaxPages)} 页 · 共 {FormatCount(_dlTotal)} 个结果";
        if (_dlPrevBtn != null) _dlPrevBtn.IsEnabled = _dlPage > 0;
        if (_dlNextBtn != null) _dlNextBtn.IsEnabled = _dlPage + 1 < totalPages && _dlPage < DlMaxPages - 1;
    }

    /// <summary>结果行（PCL2 规格）：图标 44 + 名称/简介/标签 + 右侧下载量与更新时间。</summary>
    private UIElement BuildDlRow(ModrinthProject item)
    {
        var border = new Border
        {
            Background = Brushes.Transparent,
            BorderBrush = Brush(UiPalette.Border),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(10, 10, 10, 10),
            Cursor = Cursors.Hand
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // 图标
        var icon = new Border
        {
            Width = 44,
            Height = 44,
            CornerRadius = new CornerRadius(8),
            Background = Brush(UiPalette.SurfaceAlt),
            Margin = new Thickness(0, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock
            {
                Text = string.IsNullOrEmpty(item.Title) ? "?" : item.Title[..1].ToUpperInvariant(),
                FontSize = 17,
                FontWeight = FontWeights.Bold,
                Foreground = Brush(UiPalette.Accent),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        if (!string.IsNullOrEmpty(item.IconUrl)) _ = LoadDlIconAsync(icon, item.IconUrl);
        Grid.SetColumn(icon, 0);
        grid.Children.Add(icon);

        // 中间文字
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var titleRow = new WrapPanel();
        titleRow.Children.Add(new TextBlock
        {
            Text = item.Title,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush(UiPalette.Text),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 420
        });
        if (!string.IsNullOrEmpty(item.Slug) && !item.Slug.Equals(item.Title, StringComparison.OrdinalIgnoreCase))
            titleRow.Children.Add(new TextBlock
            {
                Text = "  |  " + item.Slug,
                FontSize = 11,
                Foreground = Brush(UiPalette.Muted),
                VerticalAlignment = VerticalAlignment.Center
            });
        text.Children.Add(titleRow);

        var descTb = new TextBlock
        {
            Text = item.Description,
            FontSize = 12,
            Foreground = Brush(UiPalette.MutedLight),
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = item.Description,
            Margin = new Thickness(0, 3, 0, 0),
            Tag = item.Id
        };
        text.Children.Add(descTb);
        // 异步把英文简介换成 MCIM 的中文翻译（缓存命中则秒换）
        _ = ApplyTranslatedDescriptionAsync(item, descTb);

        var meta = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        foreach (var loader in item.Loaders) meta.Children.Add(LoaderBadge(loader));
        var range = VersionRangeSummary(item.Versions);
        if (range.Length > 0) meta.Children.Add(DlMetaTag(range));
        foreach (var c in item.Categories.Where(c => !IsLoaderName(c)).Take(3))
            meta.Children.Add(DlMetaTag(CategoryLabel(_dlType, c)));
        text.Children.Add(meta);

        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        // 右侧：行内「下载」按钮（一键下载最新版）+ 下载量 / 更新时间
        var right = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        var dlBtn = new Button
        {
            Content = "下载",
            Padding = new Thickness(14, 6, 14, 6),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Background = Brush(UiPalette.Accent),
            Foreground = Brush(UiPalette.Bg),
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            HorizontalAlignment = HorizontalAlignment.Right,
            ToolTip = "下载该项目最新发布的文件"
        };
        dlBtn.Click += async (_, _) => await DownloadProjectLatestAsync(item, dlBtn);
        right.Children.Add(dlBtn);
        right.Children.Add(new TextBlock
        {
            Text = $"↓ {FormatCount(item.Downloads)}",
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush(UiPalette.TextDim),
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 6, 0, 0)
        });
        right.Children.Add(new TextBlock
        {
            Text = RelativeTime(item.DateModified),
            FontSize = 11,
            Foreground = Brush(UiPalette.Muted),
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 2, 0, 0)
        });
        Grid.SetColumn(right, 2);
        grid.Children.Add(right);

        border.Child = grid;
        border.MouseEnter += (_, _) => border.Background = Brush(UiPalette.SurfaceAlt);
        border.MouseLeave += (_, _) => border.Background = Brushes.Transparent;
        border.MouseLeftButtonUp += (_, _) => ShowDlDetail(item);
        return border;
    }

    private static bool IsLoaderName(string c) =>
        c is "forge" or "neoforge" or "fabric" or "quilt" or "liteloader" or "rift";

    private static TextBlock DlMetaTag(string text) => new()
    {
        Text = text,
        FontSize = 11,
        Foreground = Brush(UiPalette.Muted),
        Background = Brush(UiPalette.SurfaceAlt),
        Margin = new Thickness(0, 0, 6, 2),
        Padding = new Thickness(6, 2, 6, 2)
    };

    /// <summary>把 Modrinth 分类 ID 换成中文标签（没有映射就原样显示）。</summary>
    private static string CategoryLabel(string type, string id)
    {
        if (CatalogCategoryMap.TryGetValue(type, out var list))
        {
            foreach (var (label, cid) in list)
                if (cid == id) return label;
        }
        return id;
    }

    private static async Task LoadDlIconAsync(Border host, string url)
    {
        try
        {
            var bmp = await LoadBitmapAsync(url, 96);
            if (bmp == null) return;
            await host.Dispatcher.InvokeAsync(() =>
            {
                var img = new Image
                {
                    Source = bmp,
                    Stretch = Stretch.UniformToFill,
                    Width = 44,
                    Height = 44
                };
                RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.Linear);
                host.Child = img;
            });
        }
        catch { /* 图标失败保留首字母占位 */ }
    }
    #endregion

    #region 项目详情
    private void ShowDlDetail(ModrinthProject item)
    {
        _dlCurrent = item;
        _dlDetail = null;
        _dlDetailZh = null;
        _dlVersions = [];
        _dlTab = 0;
        if (_dlHost == null) return;
        _dlHost.Content = BuildDlDetailShell(item);
        _ = LoadDlDetailAsync(item);
    }

    private UIElement BuildDlDetailShell(ModrinthProject item)
    {
        var root = new StackPanel();

        var back = new Button
        {
            Content = "← 返回列表",
            Padding = new Thickness(12, 6, 12, 6),
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = Brushes.Transparent,
            Foreground = Brush(UiPalette.Muted),
            BorderThickness = new Thickness(0),
            FontSize = 12,
            Cursor = Cursors.Hand,
            Margin = new Thickness(0, 0, 0, 10)
        };
        back.Click += (_, _) =>
        {
            if (_dlHost != null && _dlListView != null) _dlHost.Content = _dlListView;
        };
        root.Children.Add(back);

        // 头部：图标 + 标题 / 作者 / 数据 / 标签 + 右侧下载按钮
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new Border
        {
            Width = 72,
            Height = 72,
            CornerRadius = new CornerRadius(12),
            Background = Brush(UiPalette.SurfaceAlt),
            Margin = new Thickness(0, 0, 16, 0),
            VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock
            {
                Text = string.IsNullOrEmpty(item.Title) ? "?" : item.Title[..1].ToUpperInvariant(),
                FontSize = 26,
                FontWeight = FontWeights.Bold,
                Foreground = Brush(UiPalette.Accent),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        if (!string.IsNullOrEmpty(item.IconUrl)) _ = LoadDlIconAsync72(icon, item.IconUrl);
        head.Children.Add(icon);

        var info = new StackPanel();
        info.Children.Add(new TextBlock
        {
            Text = item.Title,
            FontSize = 22,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush(UiPalette.Text),
            TextWrapping = TextWrapping.Wrap
        });
        if (!string.IsNullOrEmpty(item.Slug) && !item.Slug.Equals(item.Title, StringComparison.OrdinalIgnoreCase))
            info.Children.Add(new TextBlock
            {
                Text = item.Slug,
                FontSize = 12,
                Foreground = Brush(UiPalette.Muted),
                Margin = new Thickness(0, 2, 0, 0)
            });
        info.Children.Add(new TextBlock
        {
            Text = item.Description,
            FontSize = 13,
            Foreground = Brush(UiPalette.MutedLight),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0)
        });

        var meta = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        meta.Children.Add(DlMetaText($"作者：{item.Author}"));
        meta.Children.Add(DlMetaText($"↓ {FormatCount(item.Downloads)} 次下载"));
        meta.Children.Add(DlMetaText("来源：Modrinth（镜像）"));
        meta.Children.Add(DlMetaText("更新于 " + RelativeTime(item.DateModified)));
        info.Children.Add(meta);

        var tags = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        foreach (var loader in item.Loaders) tags.Children.Add(LoaderBadge(loader));
        foreach (var c in item.Categories.Where(c => !IsLoaderName(c)).Take(8))
            tags.Children.Add(DlMetaTag(CategoryLabel(_dlType, c)));
        if (tags.Children.Count > 0) info.Children.Add(tags);

        Grid.SetColumn(info, 1);
        head.Children.Add(info);

        // 右侧：主下载按钮（一键下载最新版）+ 跳转版本列表
        var actions = new StackPanel { VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(16, 0, 0, 0) };
        var dlLatestBtn = new Button
        {
            Content = "下载最新版",
            Padding = new Thickness(18, 9, 18, 9),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Background = Brush(UiPalette.Accent),
            Foreground = Brush(UiPalette.Bg),
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            ToolTip = "自动选择该项目最新发布的文件并下载"
        };
        dlLatestBtn.Click += async (_, _) => await DownloadLatestAsync(item, dlLatestBtn);
        actions.Children.Add(dlLatestBtn);
        var pickBtn = new Button
        {
            Content = "选择版本…",
            Padding = new Thickness(18, 7, 18, 7),
            Margin = new Thickness(0, 8, 0, 0),
            FontSize = 12,
            Background = Brush(UiPalette.SurfaceAlt),
            Foreground = Brush(UiPalette.TextDim),
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            ToolTip = "打开版本列表，按游戏版本/加载器挑选"
        };
        pickBtn.Click += (_, _) =>
        {
            _dlTab = 2;
            RefreshDlTabs();
        };
        actions.Children.Add(pickBtn);
        Grid.SetColumn(actions, 2);
        head.Children.Add(actions);
        root.Children.Add(head);

        // Tab：介绍 / 图片 / 版本
        var tabs = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 16, 0, 0) };
        _dlTabButtons.Clear();
        string[] names = ["介绍", "图片", "版本列表"];
        for (var i = 0; i < names.Length; i++)
        {
            var index = i;
            var b = new Button
            {
                Content = names[i],
                Tag = index,
                Padding = new Thickness(16, 7, 16, 7),
                Margin = new Thickness(0, 0, 8, 0),
                FontSize = 13,
                Background = Brushes.Transparent,
                Foreground = Brush(UiPalette.Muted),
                BorderThickness = new Thickness(0, 0, 0, 2),
                Cursor = Cursors.Hand
            };
            b.Click += (_, _) =>
            {
                _dlTab = index;
                RefreshDlTabs();
            };
            _dlTabButtons.Add(b);
            tabs.Children.Add(b);
        }
        root.Children.Add(tabs);

        root.Children.Add(new Border
        {
            Height = 1,
            Background = Brush(UiPalette.Border),
            Margin = new Thickness(0, 0, 0, 12)
        });

        _dlTabBody = new ContentControl();
        root.Children.Add(_dlTabBody);
        RefreshDlTabs();
        return root;
    }

    private static async Task LoadDlIconAsync72(Border host, string url)
    {
        try
        {
            var bmp = await LoadBitmapAsync(url, 144);
            if (bmp == null) return;
            await host.Dispatcher.InvokeAsync(() =>
            {
                var img = new Image
                {
                    Source = bmp,
                    Stretch = Stretch.UniformToFill,
                    Width = 72,
                    Height = 72
                };
                RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.Linear);
                host.Child = img;
            });
        }
        catch { /* 保持首字母占位 */ }
    }

    private static TextBlock DlMetaText(string text) => new()
    {
        Text = text,
        FontSize = 11,
        Foreground = Brush(UiPalette.Muted),
        Margin = new Thickness(0, 0, 16, 4)
    };

    private void RefreshDlTabs()
    {
        if (_dlTabButtons.Count == 0) return;
        for (var i = 0; i < _dlTabButtons.Count; i++)
        {
            var on = i == _dlTab;
            var b = _dlTabButtons[i];
            b.Foreground = on ? Brush(UiPalette.Accent) : Brush(UiPalette.Muted);
            b.BorderBrush = on ? Brush(UiPalette.Accent) : Brushes.Transparent;
            b.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
        }
        if (_dlTabBody == null) return;
        _dlTabBody.Content = _dlTab switch
        {
            1 => BuildDlImagesTab(),
            2 => BuildDlVersionsTab(),
            _ => BuildDlIntroTab()
        };
    }

    private UIElement BuildDlIntroTab()
    {
        if (_dlDetail == null)
            return new TextBlock { Text = "正在加载介绍...", FontSize = 12, Foreground = Brush(UiPalette.Muted) };

        var body = PlainDescription(_dlDetail.Body);
        if (string.IsNullOrWhiteSpace(body))
            body = string.IsNullOrWhiteSpace(_dlDetail.Description) ? "作者没有填写介绍。" : _dlDetail.Description;

        var card = Card();
        var inner = new StackPanel { Margin = new Thickness(16, 14, 16, 16) };

        // 中文简介（MCIM 机器翻译）置顶展示，英文原文正文在其后
        if (!string.IsNullOrEmpty(_dlDetailZh))
        {
            inner.Children.Add(Label("简介（中文翻译）", 11, UiPalette.Accent));
            inner.Children.Add(new TextBlock
            {
                Text = _dlDetailZh,
                FontSize = 13,
                Foreground = Brush(UiPalette.Text),
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 21,
                Margin = new Thickness(0, 4, 0, 12)
            });
        }

        inner.Children.Add(new TextBlock
        {
            Text = body,
            FontSize = 13,
            Foreground = Brush(UiPalette.TextDim),
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 22
        });

        var links = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
        void AddLink(string label, string? url)
        {
            if (string.IsNullOrEmpty(url)) return;
            var b = new Button
            {
                Content = label,
                Padding = new Thickness(12, 6, 12, 6),
                Margin = new Thickness(0, 0, 8, 6),
                FontSize = 12,
                Background = Brush(UiPalette.SurfaceAlt),
                Foreground = Brush(UiPalette.TextDim),
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand
            };
            b.Click += (_, _) => OpenExternal(url);
            links.Children.Add(b);
        }
        AddLink("源码仓库", _dlDetail.SourceUrl);
        AddLink("问题反馈", _dlDetail.IssuesUrl);
        AddLink("Wiki", _dlDetail.WikiUrl);
        AddLink("在浏览器中打开", $"https://modrinth.com/{_dlType}/{_dlDetail.Slug}");
        if (links.Children.Count > 0) inner.Children.Add(links);

        card.Children.Add(inner);
        return card;
    }

    private UIElement BuildDlImagesTab()
    {
        var root = new StackPanel();
        if (_dlDetail == null)
        {
            root.Children.Add(new TextBlock { Text = "正在加载图片...", FontSize = 12, Foreground = Brush(UiPalette.Muted) });
            return root;
        }
        if (_dlDetail.Gallery.Count == 0)
        {
            root.Children.Add(new TextBlock { Text = "作者没有上传图片。", FontSize = 12, Foreground = Brush(UiPalette.Muted) });
            return root;
        }

        var wrap = new WrapPanel();
        foreach (var g in _dlDetail.Gallery)
        {
            var bmpHost = new Border
            {
                Width = 320,
                Height = 180,
                CornerRadius = new CornerRadius(10),
                Background = Brush(UiPalette.SurfaceAlt),
                Margin = new Thickness(0, 0, 10, 10),
                ClipToBounds = true
            };
            var img = new Image { Stretch = Stretch.UniformToFill };
            bmpHost.Child = img;
            _ = LoadGalleryImageAsync(img, g.Url);
            if (!string.IsNullOrEmpty(g.Title)) bmpHost.ToolTip = g.Title;
            wrap.Children.Add(bmpHost);
        }
        root.Children.Add(wrap);
        return root;
    }

    private static async Task LoadGalleryImageAsync(Image target, string url)
    {
        try
        {
            var bmp = await LoadBitmapAsync(url, 640);
            if (bmp == null) return;
            await target.Dispatcher.InvokeAsync(() => target.Source = bmp);
        }
        catch { /* 图片失败留空 */ }
    }

    private UIElement BuildDlVersionsTab()
    {
        var root = new StackPanel();
        if (_dlVersions.Count == 0)
        {
            root.Children.Add(new TextBlock
            {
                Text = _dlDetail == null ? "正在加载版本列表..." : "该项目暂无可用版本。",
                FontSize = 12,
                Foreground = Brush(UiPalette.Muted)
            });
            return root;
        }

        // 版本筛选：游戏版本 + 加载器
        var filters = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
        var gameOptions = new List<string> { "全部游戏版本" };
        gameOptions.AddRange(_dlVersions.SelectMany(v => v.GameVersions).Distinct()
            .OrderByDescending(VersionSortKey).Take(40));
        _dlVerGameBox = DlCombo(gameOptions, 0, 150);
        _dlVerGameBox.SelectionChanged += (_, _) => FillVersionRows();
        filters.Children.Add(DlFilter("游戏版本", _dlVerGameBox));

        var loaders = _dlVersions.SelectMany(v => v.Loaders).Distinct()
            .OrderBy(l => l).ToList();
        if (loaders.Count > 1)
        {
            _dlVerLoaderBox = DlCombo(new[] { "全部加载器" }.Concat(loaders.Select(FormatLoader)), 0, 132);
            _dlVerLoaderBox.SelectionChanged += (_, _) => FillVersionRows();
            filters.Children.Add(DlFilter("加载器", _dlVerLoaderBox));
        }
        else
        {
            _dlVerLoaderBox = null;
        }

        _dlVerCount = new TextBlock
        {
            Text = "",
            FontSize = 12,
            Foreground = Brush(UiPalette.Muted),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 6)
        };
        filters.Children.Add(_dlVerCount);
        root.Children.Add(filters);

        // 表头
        var header = BuildVersionGridRow("版本", "游戏版本", "加载器", "更新时间", "下载量", "");
        header.Background = Brush(UiPalette.SurfaceAlt);
        header.Margin = new Thickness(0, 0, 0, 2);
        root.Children.Add(header);

        _dlVersionRows = new StackPanel();
        root.Children.Add(_dlVersionRows);
        FillVersionRows();
        return root;
    }

    private void FillVersionRows()
    {
        if (_dlVersionRows == null) return;
        _dlVersionRows.Children.Clear();
        if (_dlVerGameBox == null) return;

        var game = _dlVerGameBox.SelectedItem as string;
        var loader = _dlVerLoaderBox?.SelectedItem as string;

        var filtered = _dlVersions.Where(v =>
            (string.IsNullOrEmpty(game) || game == "全部游戏版本" || v.GameVersions.Contains(game))
            && (string.IsNullOrEmpty(loader) || loader == "全部加载器"
                || v.Loaders.Any(l => FormatLoader(l) == loader))
        ).ToList();

        if (_dlVerCount != null) _dlVerCount.Text = $"共 {filtered.Count} 个版本";
        if (filtered.Count == 0)
        {
            _dlVersionRows.Children.Add(new TextBlock
            {
                Text = "没有匹配的版本，换个游戏版本或加载器试试。",
                FontSize = 12,
                Foreground = Brush(UiPalette.Muted),
                Margin = new Thickness(2, 8, 0, 0)
            });
            return;
        }

        foreach (var v in filtered)
            _dlVersionRows.Children.Add(BuildVersionRow(v));
    }

    private static string FormatLoader(string l) => l.ToLowerInvariant() switch
    {
        "neoforge" => "NeoForge",
        "fabric" => "Fabric",
        "forge" => "Forge",
        "quilt" => "Quilt",
        "datapack" => "数据包",
        "minecraft" => "原版",
        _ => char.ToUpperInvariant(l[0]) + l[1..]
    };

    private static double VersionSortKey(string v)
    {
        double s = 0;
        foreach (var p in v.Split('.'))
        {
            if (!double.TryParse(p, System.Globalization.CultureInfo.InvariantCulture, out var d)) break;
            s = s * 1000 + d;
        }
        return s;
    }

    /// <summary>版本行：可点击整行下载（PCL2 的版本列表带独立下载按钮）。</summary>
    private UIElement BuildVersionRow(ModrinthVersion v)
    {
        var file = v.PrimaryFile;
        var rowBorder = BuildVersionGridRow(
            v.VersionNumber,
            v.GameVersions.Count == 0 ? "—"
                : string.Join(", ", v.GameVersions.OrderByDescending(VersionSortKey).Take(3))
                  + (v.GameVersions.Count > 3 ? " …" : ""),
            v.Loaders.Count == 0 ? "—" : string.Join(" / ", v.Loaders.Select(FormatLoader)),
            v.DatePublished.LocalDateTime.ToString("yyyy-MM-dd"),
            FormatCount(v.Downloads),
            "");
        var hostGrid = (Grid)rowBorder.Child!;

        var btn = new Button
        {
            Content = "下载",
            Padding = new Thickness(12, 5, 12, 5),
            FontSize = 12,
            Background = Brush(UiPalette.Accent),
            Foreground = Brush(UiPalette.Bg),
            BorderThickness = new Thickness(0),
            FontWeight = FontWeights.SemiBold,
            Cursor = Cursors.Hand,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = file?.FileName
        };
        btn.Click += async (_, _) => await DownloadVersionAsync(v, btn);
        Grid.SetColumn(btn, 5);
        hostGrid.Children.Add(btn);

        // 版本类型标签（Beta / Alpha）贴在版本号后面
        if (!string.Equals(v.VersionType, "release", StringComparison.OrdinalIgnoreCase))
        {
            var tag = new Border
            {
                Background = Brush(v.VersionType.Equals("beta", StringComparison.OrdinalIgnoreCase) ? "#C7793A" : "#8A6D3B"),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(5, 1, 5, 1),
                Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = v.VersionType,
                    FontSize = 9,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = Brushes.White
                }
            };
            if (hostGrid.Children.Count > 0 && hostGrid.Children[0] is StackPanel nameStack)
                nameStack.Children.Add(tag);
        }

        return rowBorder;
    }

    /// <summary>版本表格的一行（6 列 Grid，返回可点击高亮的 Border）。</summary>
    private static Border BuildVersionGridRow(string name, string game, string loader, string date, string downloads, string _)
    {
        var grid = new Grid();
        double[] widths = [0, 118, 92, 88, 74, 74];
        for (var i = 0; i < widths.Length; i++)
            grid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = i == 0 ? new GridLength(1, GridUnitType.Star) : new GridLength(widths[i])
            });

        var nameRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        nameRow.Children.Add(new TextBlock
        {
            Text = name,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush(UiPalette.Text),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        Grid.SetColumn(nameRow, 0);
        grid.Children.Add(nameRow);

        void Cell(string text, int col, HorizontalAlignment align = HorizontalAlignment.Left)
        {
            var tb = new TextBlock
            {
                Text = text,
                FontSize = 11,
                Foreground = Brush(UiPalette.Muted),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = align,
                Margin = new Thickness(0, 0, 4, 0),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            Grid.SetColumn(tb, col);
            grid.Children.Add(tb);
        }
        Cell(game, 1);
        Cell(loader, 2);
        Cell(date, 3);
        Cell(downloads, 4, HorizontalAlignment.Right);

        var row = new Border
        {
            Child = grid,
            Padding = new Thickness(10, 8, 10, 8),
            Background = Brushes.Transparent,
            BorderBrush = Brush(UiPalette.Border),
            BorderThickness = new Thickness(0, 0, 0, 1)
        };
        row.MouseEnter += (_, _) => row.Background = Brush(UiPalette.SurfaceAlt);
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        return row;
    }

    /// <summary>头部「下载最新版」：取最新发布且带文件的版本直接下载，无需进版本列表。</summary>
    private async Task DownloadLatestAsync(ModrinthProject item, Button btn)
    {
        var original = btn.Content;
        btn.IsEnabled = false;
        btn.Content = "获取版本…";
        try
        {
            var versions = _dlVersions.Count > 0 && _dlCurrent?.Id == item.Id
                ? _dlVersions
                : await _modrinthService.GetVersionsAsync(item.Id);
            var latest = versions
                .OrderByDescending(v => v.DatePublished)
                .FirstOrDefault(v => v.PrimaryFile is { Url.Length: > 0 });
            if (latest == null)
            {
                btn.Content = original;
                MessageBox.Show(this, "该项目暂无可下载的文件。", "NEX",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            await DownloadVersionAsync(latest, btn, "下载最新版");
        }
        catch (Exception ex)
        {
            btn.Content = original;
            MessageBox.Show(this, "获取版本列表失败：\n" + ex.Message, "NEX",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            btn.IsEnabled = true;
        }
    }

    /// <summary>把文件下载到对应资源目录（mods/resourcepacks/…），带下载管理器进度，返回保存路径。</summary>
    private async Task<string> DownloadFileToLibraryAsync(string displayName, string url, string fileName)
    {
        var dir = Path.Combine(_gameDirectory, DlSubDir(_dlType));
        Directory.CreateDirectory(dir);
        var dest = Path.Combine(dir, fileName);
        var dl = DownloadManagerService.Instance.Add(displayName, DownloadKind.Mod);
        dl.Status = DownloadStatus.Downloading;
        dl.Detail = "准备下载...";
        try
        {
            var progress = new Progress<(long Received, long Total)>(p =>
            {
                if (p.Total > 0)
                {
                    dl.Progress = (double)p.Received / p.Total;
                    dl.Detail = $"{FormatBytes(p.Received)} / {FormatBytes(p.Total)}";
                }
                else dl.Detail = FormatBytes(p.Received);
            });
            await _modrinthService.DownloadFileAsync(url, dest, progress);

            // 第一道防火墙：下载防护——伪装文件落地即扫描，威胁自动隔离
            if (ScanDownloadedFile(dest))
            {
                DownloadManagerService.Instance.Fail(dl, "安全防护：文件已隔离");
                throw new InvalidOperationException(
                    "安全防护已拦截该文件（伪装/可疑内容），已自动放入隔离区。\n" +
                    "详见「安全防护」页。正规模组/资源不会是可执行程序或脚本。");
            }

            DownloadManagerService.Instance.Complete(dl);
            return dest;
        }
        catch (Exception ex)
        {
            DownloadManagerService.Instance.Fail(dl, ex.Message);
            throw;
        }
    }

    /// <summary>列表行内「下载」：直接抓该项目最新发布的文件下载，无需进详情页。</summary>
    private async Task DownloadProjectLatestAsync(ModrinthProject item, Button btn)
    {
        var original = btn.Content;
        btn.IsEnabled = false;
        btn.Content = "…";
        try
        {
            var latest = await _modrinthService.GetLatestDownloadAsync(item.Id);
            if (latest is not { } found || string.IsNullOrEmpty(found.Url))
            {
                btn.Content = original;
                MessageBox.Show(this, "该项目暂无可下载的文件。", "NEX",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            btn.Content = "下载中";
            var dest = await DownloadFileToLibraryAsync($"{item.Title} 最新版", found.Url, found.FileName);
            btn.Content = "已下载";
            btn.Background = Brush(UiPalette.SurfaceAlt);
            btn.Foreground = Brush(UiPalette.TextDim);
            if (_dlStatus != null) _dlStatus.Text = $"已保存到 {Path.Combine(DlSubDir(_dlType), found.FileName)}";
            if (_dlType == "modpack")
                await AskInstallDownloadedModpackAsync(dest);
        }
        catch (Exception ex)
        {
            btn.Content = original;
            MessageBox.Show(this, "下载失败：\n" + ex.Message, "NEX", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            btn.IsEnabled = true;
            _ = Task.Delay(2200).ContinueWith(_ => Dispatcher.InvokeAsync(() =>
            {
                if (btn.Content is not "已下载") return;
                btn.Content = original;
                btn.Background = Brush(UiPalette.Accent);
                btn.Foreground = Brush(UiPalette.Bg);
            }));
        }
    }

    private async Task DownloadVersionAsync(ModrinthVersion version, Button btn, string? restoreLabel = null)
    {
        var file = version.PrimaryFile;
        if (file == null || string.IsNullOrEmpty(file.Url))
        {
            btn.Content = "无文件";
            return;
        }
        var original = btn.Content;
        btn.IsEnabled = false;
        btn.Content = "下载中";
        try
        {
            var dest = await DownloadFileToLibraryAsync(
                $"{_dlCurrent?.Title ?? "资源"} {version.VersionNumber}", file.Url, file.FileName);
            btn.Content = "已下载";
            btn.Background = Brush(UiPalette.SurfaceAlt);
            btn.Foreground = Brush(UiPalette.TextDim);
            if (_dlStatus != null) _dlStatus.Text = $"已保存到 {Path.Combine(DlSubDir(_dlType), file.FileName)}";
            if (_dlType == "modpack")
                await AskInstallDownloadedModpackAsync(dest);
        }
        catch (Exception ex)
        {
            btn.Content = "失败";
            MessageBox.Show(this, "下载失败：\n" + ex.Message, "NEX", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            btn.IsEnabled = true;
            if (btn.Content is "失败") btn.Content = original;
        }

        // 2 秒后按钮恢复，方便重复下载其它版本
        if (btn.Content is "已下载")
        {
            _ = Task.Delay(2200).ContinueWith(_ => Dispatcher.InvokeAsync(() =>
            {
                if (btn.Content is not "已下载") return;
                btn.Content = restoreLabel ?? "下载";
                btn.Background = Brush(UiPalette.Accent);
                btn.Foreground = Brush(UiPalette.Bg);
            }));
        }
    }

    private static string DlSubDir(string type) => type switch
    {
        "resourcepack" => "resourcepacks",
        "shader" => "shaderpacks",
        "datapack" => "datapacks",
        "modpack" => "modpacks",
        "plugin" => "plugins",
        _ => "mods"
    };

    #region 简介中文翻译（MCIM 翻译接口，内存 + 磁盘双缓存）
    private static readonly ConcurrentDictionary<string, string> ZhSummaryCache = new(StringComparer.Ordinal);
    private static readonly SemaphoreSlim ZhSummaryGate = new(3, 3);
    private static bool _zhCacheLoaded;

    private static string ZhCachePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NEXLauncher", "modrinth-zh.json");

    /// <summary>取项目简介的中文翻译；null 表示没有翻译（缓存空串避免重复请求）。</summary>
    private async Task<string?> GetTranslatedSummaryCachedAsync(string projectId)
    {
        EnsureZhCacheLoaded();
        if (ZhSummaryCache.TryGetValue(projectId, out var hit))
            return string.IsNullOrEmpty(hit) ? null : hit;

        await ZhSummaryGate.WaitAsync();
        try
        {
            if (ZhSummaryCache.TryGetValue(projectId, out hit))
                return string.IsNullOrEmpty(hit) ? null : hit;
            var zh = await _modrinthService.GetTranslatedSummaryAsync(projectId);
            ZhSummaryCache[projectId] = zh ?? "";
            if (zh != null) SaveZhCache();
            return zh;
        }
        finally { ZhSummaryGate.Release(); }
    }

    private static void EnsureZhCacheLoaded()
    {
        if (_zhCacheLoaded) return;
        _zhCacheLoaded = true;
        try
        {
            if (!File.Exists(ZhCachePath)) return;
            var map = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(ZhCachePath));
            if (map == null) return;
            foreach (var kv in map)
                if (!string.IsNullOrEmpty(kv.Value)) ZhSummaryCache[kv.Key] = kv.Value;
        }
        catch { /* 缓存损坏则重新拉取 */ }
    }

    private static void SaveZhCache()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ZhCachePath)!);
            var map = ZhSummaryCache.Where(kv => !string.IsNullOrEmpty(kv.Value))
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
            File.WriteAllText(ZhCachePath, JsonSerializer.Serialize(map));
        }
        catch { /* 缓存写失败不影响功能 */ }
    }

    /// <summary>异步把结果行的英文简介换成中文翻译（Guard：控件仍属于同一项目才更新）。</summary>
    private async Task ApplyTranslatedDescriptionAsync(ModrinthProject item, TextBlock target)
    {
        try
        {
            var zh = await GetTranslatedSummaryCachedAsync(item.Id);
            if (zh == null) return;
            await Dispatcher.InvokeAsync(() =>
            {
                if (!Equals(target.Tag as string, item.Id)) return;
                target.Text = zh;
                target.ToolTip = zh + "\n\n原文：" + item.Description;
            });
        }
        catch { /* 翻译失败保留英文简介 */ }
    }
    #endregion

    private async Task LoadDlDetailAsync(ModrinthProject item)
    {
        string? error = null;
        try
        {
            // 详情、版本列表、简介中文翻译三路并行
            var detailTask = _modrinthService.GetProjectAsync(item.Id);
            var versionsTask = _modrinthService.GetVersionsAsync(item.Id);
            var zhTask = GetTranslatedSummaryCachedAsync(item.Id);
            await Task.WhenAll(detailTask, versionsTask, zhTask);
            var detail = await detailTask;
            var versions = await versionsTask;
            var zh = await zhTask;
            if (_dlCurrent?.Id != item.Id) return;
            if (detail == null)
            {
                error = "源站没有返回该项目的详情。";
            }
            else
            {
                _dlDetail = detail;
                _dlDetailZh = zh;
                _dlVersions = versions
                    .OrderByDescending(v => v.DatePublished)
                    .ToList();
                RefreshDlTabs();
                return;
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }

        if (_dlCurrent?.Id != item.Id || _dlTabBody == null) return;
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = "详情加载失败：" + error,
            FontSize = 12,
            Foreground = Brush(UiPalette.Muted),
            TextWrapping = TextWrapping.Wrap
        });
        var retry = new Button
        {
            Content = "重试",
            Padding = new Thickness(16, 8, 16, 8),
            Margin = new Thickness(0, 10, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = Brush(UiPalette.Accent),
            Foreground = Brush(UiPalette.Bg),
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand
        };
        retry.Click += (_, _) =>
        {
            if (_dlTabBody != null)
                _dlTabBody.Content = new TextBlock
                {
                    Text = "正在加载介绍...",
                    FontSize = 12,
                    Foreground = Brush(UiPalette.Muted)
                };
            _ = LoadDlDetailAsync(item);
        };
        panel.Children.Add(retry);
        _dlTabBody.Content = panel;
    }
    #endregion

    #region 杂项
    private static void OpenExternal(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch { /* 忽略浏览器启动失败 */ }
    }

    /// <summary>Markdown 正文转纯文本（PCL2 的介绍区也是纯文本渲染）。</summary>
    private static string PlainDescription(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return "";
        var t = markdown;
        t = Regex.Replace(t, @"<[^>]+>", "");
        t = Regex.Replace(t, @"!\[[^\]]*\]\([^)]*\)", "");
        t = Regex.Replace(t, @"\[([^\]]*)\]\([^)]*\)", "$1");
        t = Regex.Replace(t, @"```[a-zA-Z]*", "");
        t = Regex.Replace(t, @"^\s{0,3}#{1,6}\s*", "", RegexOptions.Multiline);
        t = Regex.Replace(t, @"^\s{0,3}>\s?", "", RegexOptions.Multiline);
        t = Regex.Replace(t, @"^\s{0,3}(-{3,}|\*{3,}|_{3,})\s*$", "", RegexOptions.Multiline);
        t = Regex.Replace(t, @"[*_~`]{1,3}", "");
        t = Regex.Replace(t, @"\n{3,}", "\n\n");
        return t.Trim();
    }

    private async Task LoadVersionsForCatalogAsync()
    {
        try
        {
            if (_allVersions.Count == 0) await LoadVersionsAsync();
        }
        catch { /* 版本列表失败不影响下载页 */ }

        if (_dlVersionBox == null) return;
        var families = _allVersions
            .Where(v => v.Category == "正式版")
            .Select(v =>
            {
                var parts = v.Id.Split('.');
                return parts.Length >= 2 ? $"{parts[0]}.{parts[1]}" : v.Id;
            })
            .Distinct()
            .OrderByDescending(VersionSortKey)
            .Take(40)
            .ToList();
        if (families.Count == 0)
            families = ["1.21", "1.20", "1.19", "1.18", "1.17", "1.16", "1.12", "1.8"];

        var current = _dlGameVersion;
        _dlVersionBox.ItemsSource = new[] { "全部版本" }.Concat(families).ToList();
        _dlVersionBox.SelectedItem = current != null && families.Contains(current) ? current : "全部版本";    }
    #endregion

    #region 自动化自检（NEX_CATALOG_E2E=1 触发）
    /// <summary>资源下载页回归自检：默认列表、行内下载按钮、中文搜索、详情页（含中文简介与下载按钮）。</summary>
    private async Task RunCatalogE2EAsync()
    {
        var result = new Dictionary<string, object?>();
        try
        {
            // 1) 默认列表加载
            result["listLoaded"] = await WaitUntilAsync(() => _dlRowsPanel is { Children.Count: > 0 }, 35);
            result["rowCount"] = await Dispatcher.InvokeAsync(() => _dlRowsPanel?.Children.Count ?? 0);
            await Task.Delay(1200);
            await Dispatcher.InvokeAsync(() => SaveElementPng(PageHost, "nex_catalog_list.png"));
            result["rowHasDownloadButton"] = await Dispatcher.InvokeAsync(() =>
                FindVisualChildren<Button>(_dlRowsPanel).Any(b => b.Content as string == "下载"));
            result["rowHasChineseDesc"] = await Dispatcher.InvokeAsync(() =>
                FindVisualChildren<TextBlock>(_dlRowsPanel).Any(t => ModrinthService.ContainsCjk(t.Text) && t.FontSize == 12));

            // 2) 中文搜索「钠」
            await Dispatcher.InvokeAsync(() =>
            {
                _dlQuery = "钠";
                _dlPage = 0;
                if (_dlSearchBox != null) _dlSearchBox.Text = "钠";
            });
            await Dispatcher.InvokeAsync(async () => await LoadDlResultsAsync());
            await WaitUntilAsync(() =>
                _dlStatus?.Text is { } s && (s.Contains("搜索结果") || s.Contains("没有找到") || s.Contains("加载失败")), 45);
            result["zhSearchStatus"] = await Dispatcher.InvokeAsync(() => _dlStatus?.Text);
            result["zhRowCount"] = await Dispatcher.InvokeAsync(() => _dlRowsPanel?.Children.Count ?? 0);
            await Dispatcher.InvokeAsync(() => SaveElementPng(PageHost, "nex_catalog_zhsearch.png"));

            // 3) 打开第一条详情
            var hit = _dlLastHits.FirstOrDefault();
            result["firstHit"] = hit?.Title;
            if (hit != null)
            {
                await Dispatcher.InvokeAsync(() => ShowDlDetail(hit));
                result["detailLoaded"] = await WaitUntilAsync(() => _dlDetail != null, 45);
                await Task.Delay(1200);
                result["detailZh"] = _dlDetailZh;
                result["versionCount"] = _dlVersions.Count;
                await Dispatcher.InvokeAsync(() => SaveElementPng(PageHost, "nex_catalog_detail.png"));
                result["detailHasLatestButton"] = await Dispatcher.InvokeAsync(() =>
                    _dlHost != null && FindVisualChildren<Button>(_dlHost).Any(b => b.Content as string == "下载最新版"));
                result["detailErrorText"] = await Dispatcher.InvokeAsync(() =>
                    _dlHost != null && FindVisualChildren<TextBlock>(_dlHost).Any(t => t.Text.Contains("详情加载失败")));
            }
        }
        catch (Exception ex)
        {
            result["error"] = ex.ToString();
        }
        try
        {
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "nex_catalog_e2e.json"),
                JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* 忽略自检输出失败 */ }
    }

    private async Task<bool> WaitUntilAsync(Func<bool> condition, int timeoutSec)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < timeoutSec)
        {
            if (await Dispatcher.InvokeAsync(condition)) return true;
            await Task.Delay(400);
        }
        return false;
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject? root) where T : DependencyObject
    {
        if (root == null) yield break;
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T hit) yield return hit;
            foreach (var sub in FindVisualChildren<T>(child)) yield return sub;
        }
    }

    /// <summary>把任意界面元素离屏渲染成 PNG（不依赖窗口前台，被遮挡也能截）。</summary>
    private static void SaveElementPng(FrameworkElement el, string fileName)
    {
        try
        {
            el.UpdateLayout();
            var w = (int)Math.Ceiling(el.ActualWidth);
            var h = (int)Math.Ceiling(el.ActualHeight);
            if (w < 10 || h < 10) return;
            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(el);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using var fs = File.Create(Path.Combine(Path.GetTempPath(), fileName));
            enc.Save(fs);
        }
        catch { /* 自检截图失败不影响主流程 */ }
    }
    #endregion
}
