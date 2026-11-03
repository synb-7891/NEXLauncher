using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace NEXLauncher.Services;

public sealed class VersionPlayStats
{
    public long TotalSeconds { get; set; }
    public int LaunchCount { get; set; }
    public DateTime? LastPlayed { get; set; }
}

/// <summary>
/// 游戏时长统计：记录每个版本的累计游玩时长、启动次数和最近游玩时间。
/// 数据保存在 %LocalAppData%\NEXLauncher\playtime.json。
/// </summary>
public sealed class PlaytimeStatsService
{
    private static readonly string StatsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NEXLauncher", "playtime.json");

    private static readonly Lazy<PlaytimeStatsService> LazyInstance = new(() => new PlaytimeStatsService());
    public static PlaytimeStatsService Instance => LazyInstance.Value;

    private readonly object _lock = new();
    private readonly Dictionary<string, VersionPlayStats> _stats;

    private PlaytimeStatsService()
    {
        _stats = Load();
    }

    private static Dictionary<string, VersionPlayStats> Load()
    {
        try
        {
            if (File.Exists(StatsPath))
            {
                return JsonSerializer.Deserialize<Dictionary<string, VersionPlayStats>>(File.ReadAllText(StatsPath))
                    ?? new Dictionary<string, VersionPlayStats>();
            }
        }
        catch { /* 损坏则重置 */ }
        return new Dictionary<string, VersionPlayStats>();
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StatsPath)!);
            File.WriteAllText(StatsPath, JsonSerializer.Serialize(_stats));
        }
        catch { /* 统计失败不影响游戏 */ }
    }

    /// <summary>附加到游戏进程：立即记录一次启动，进程退出时累计游玩时长。</summary>
    public void AttachProcess(string versionId, Process process)
    {
        var startTime = DateTime.Now;
        lock (_lock)
        {
            var s = GetOrCreate(versionId);
            s.LaunchCount++;
            s.LastPlayed = startTime;
            Save();
        }

        try
        {
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) =>
            {
                var elapsed = (long)(DateTime.Now - startTime).TotalSeconds;
                // 运行不足 10 秒视为启动失败，不计入时长
                if (elapsed < 10) return;
                lock (_lock)
                {
                    GetOrCreate(versionId).TotalSeconds += elapsed;
                    Save();
                }
            };
        }
        catch { /* 进程事件不可用时仅记录启动次数 */ }
    }

    private VersionPlayStats GetOrCreate(string versionId)
    {
        if (!_stats.TryGetValue(versionId, out var s))
        {
            s = new VersionPlayStats();
            _stats[versionId] = s;
        }
        return s;
    }

    /// <summary>获取所有统计（按累计时长降序）。</summary>
    public IReadOnlyList<KeyValuePair<string, VersionPlayStats>> GetAll()
    {
        lock (_lock)
        {
            return _stats.OrderByDescending(kv => kv.Value.TotalSeconds).ToList();
        }
    }

    public (long TotalSeconds, int TotalLaunches) GetTotals()
    {
        lock (_lock)
        {
            return (_stats.Values.Sum(s => s.TotalSeconds), _stats.Values.Sum(s => s.LaunchCount));
        }
    }

    public static string FormatDuration(long totalSeconds)
    {
        var ts = TimeSpan.FromSeconds(totalSeconds);
        if (ts.TotalHours >= 1) return $"{(int)ts.TotalHours} 小时 {ts.Minutes} 分钟";
        if (ts.TotalMinutes >= 1) return $"{ts.Minutes} 分钟";
        return $"{ts.Seconds} 秒";
    }
}
