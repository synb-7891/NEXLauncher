using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;

namespace NEXLauncher.Services;

public enum DownloadKind { Game, Mod }

public enum DownloadStatus { Pending, Downloading, Done, Error }

public sealed class DownloadItem : INotifyPropertyChanged
{
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public required string Name { get; init; }
    public required DownloadKind Kind { get; init; }

    private DownloadStatus _status = DownloadStatus.Pending;
    public DownloadStatus Status
    {
        get => _status;
        set { _status = value; OnChanged(); OnChanged(nameof(StatusText)); }
    }

    /// <summary>0.0 ~ 1.0</summary>
    private double _progress;
    public double Progress
    {
        get => _progress;
        set { _progress = Math.Clamp(value, 0, 1); OnChanged(); OnChanged(nameof(ProgressPercent)); }
    }

    private string _detail = "";
    /// <summary>附加信息：如 3.2MB / 15.0MB</summary>
    public string Detail
    {
        get => _detail;
        set { _detail = value; OnChanged(); }
    }

    public int ProgressPercent => (int)Math.Round(Progress * 100);

    public string StatusText => Status switch
    {
        DownloadStatus.Pending => "等待中",
        DownloadStatus.Downloading => $"{ProgressPercent}%",
        DownloadStatus.Done => "已完成",
        DownloadStatus.Error => "失败",
        _ => ""
    };

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// 下载任务中心：游戏版本下载与模组/资源下载分组管理，
/// 右下角悬浮面板绑定这里的数据。线程安全（UI 线程封送）。
/// </summary>
public sealed class DownloadManagerService
{
    public static DownloadManagerService Instance { get; } = new();

    /// <summary>游戏版本下载组（面板上半部分）</summary>
    public ObservableCollection<DownloadItem> GameDownloads { get; } = new();
    /// <summary>模组 / 整合包 / 资源包下载组（面板下半部分）</summary>
    public ObservableCollection<DownloadItem> ModDownloads { get; } = new();

    /// <summary>任务数量变化时触发（用于悬浮按钮角标与自动弹出）</summary>
    public event EventHandler? TasksChanged;

    private DownloadManagerService() { }

    private ObservableCollection<DownloadItem> CollectionOf(DownloadKind kind)
        => kind == DownloadKind.Game ? GameDownloads : ModDownloads;

    public DownloadItem Add(string name, DownloadKind kind)
    {
        var item = new DownloadItem { Name = name, Kind = kind };
        RunOnUi(() =>
        {
            // 同名且未完成的任务不重复添加
            var existing = CollectionOf(kind).FirstOrDefault(x =>
                x.Name == name && x.Status is DownloadStatus.Pending or DownloadStatus.Downloading);
            if (existing == null)
                CollectionOf(kind).Insert(0, item);
        });
        TasksChanged?.Invoke(this, EventArgs.Empty);
        return item;
    }

    public void Complete(DownloadItem item)
    {
        RunOnUi(() => { item.Status = DownloadStatus.Done; item.Progress = 1; });
        TasksChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Fail(DownloadItem item, string message)
    {
        RunOnUi(() => { item.Status = DownloadStatus.Error; item.Detail = message; });
        TasksChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>清除已完成和失败的任务</summary>
    public void ClearFinished()
    {
        RunOnUi(() =>
        {
            foreach (var col in new[] { GameDownloads, ModDownloads })
                for (var i = col.Count - 1; i >= 0; i--)
                    if (col[i].Status is DownloadStatus.Done or DownloadStatus.Error)
                        col.RemoveAt(i);
        });
        TasksChanged?.Invoke(this, EventArgs.Empty);
    }

    public int ActiveCount =>
        GameDownloads.Count(x => x.Status is DownloadStatus.Pending or DownloadStatus.Downloading)
        + ModDownloads.Count(x => x.Status is DownloadStatus.Pending or DownloadStatus.Downloading);

    public bool HasAny => GameDownloads.Count + ModDownloads.Count > 0;

    private static void RunOnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess()) action();
        else dispatcher.Invoke(action);
    }
}
