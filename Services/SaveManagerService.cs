using System.IO;
using System.IO.Compression;

namespace NEXLauncher.Services;

public sealed record WorldInfo(string Name, string Path, long SizeBytes, DateTime LastWrite)
{
    public string SizeText => SizeBytes >= 1073741824
        ? $"{SizeBytes / 1073741824.0:0.#} GB"
        : $"{SizeBytes / 1048576.0:0.#} MB";
}

public sealed record BackupInfo(string Path, string WorldName, DateTime Created, long SizeBytes)
{
    public string SizeText => $"{SizeBytes / 1048576.0:0.#} MB";
}

/// <summary>
/// 存档管理：列出 saves 下的世界、打包备份、从备份恢复、删除。
/// 所有写操作都限制在游戏目录的 saves / nex-backups 下，避免误删其它文件。
/// </summary>
public sealed class SaveManagerService
{
    public static string BackupsDir(string gameDirectory) => Path.Combine(gameDirectory, "nex-backups");

    public IReadOnlyList<WorldInfo> ListWorlds(string gameDirectory)
    {
        var saves = Path.Combine(gameDirectory, "saves");
        var list = new List<WorldInfo>();
        if (!Directory.Exists(saves)) return list;

        foreach (var dir in Directory.EnumerateDirectories(saves))
        {
            try
            {
                var info = new DirectoryInfo(dir);
                if (info.Name.StartsWith(".nex-restore-", StringComparison.Ordinal)) continue; // 恢复中的临时目录
                list.Add(new WorldInfo(info.Name, dir, DirSize(dir), info.LastWriteTime));
            }
            catch { }
        }
        return list.OrderByDescending(w => w.LastWrite).ToList();
    }

    public static long DirSize(string dir)
    {
        long total = 0;
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                try { total += new FileInfo(f).Length; } catch { }
            }
        }
        catch { }
        return total;
    }

    /// <summary>把整个世界目录打包为 nex-backups 下的 zip，返回备份文件路径。</summary>
    public string BackupWorld(string worldPath, string gameDirectory)
    {
        var saves = Path.GetFullPath(Path.Combine(gameDirectory, "saves"));
        var world = Path.GetFullPath(worldPath);
        if (!world.StartsWith(saves + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("只能备份 saves 目录下的存档。");
        if (!Directory.Exists(world)) throw new DirectoryNotFoundException("存档目录不存在。");

        var backupDir = BackupsDir(gameDirectory);
        Directory.CreateDirectory(backupDir);

        var worldName = Path.GetFileName(world);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var zipPath = Path.Combine(backupDir, $"{Sanitize(worldName)}_{stamp}.zip");

        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        var root = Path.GetDirectoryName(world)!;
        foreach (var file in Directory.EnumerateFiles(world, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(root, file);   // 保留世界名作为顶层目录
            zip.CreateEntryFromFile(file, rel, CompressionLevel.Optimal);
        }
        return zipPath;
    }

    public IReadOnlyList<BackupInfo> ListBackups(string gameDirectory)
    {
        var dir = BackupsDir(gameDirectory);
        if (!Directory.Exists(dir)) return Array.Empty<BackupInfo>();

        var list = new List<BackupInfo>();
        foreach (var f in Directory.EnumerateFiles(dir, "*.zip"))
        {
            try
            {
                var info = new FileInfo(f);
                var name = Path.GetFileNameWithoutExtension(info.Name);
                var cut = name.LastIndexOf('_');
                var world = cut > 0 ? name[..cut] : name;
                list.Add(new BackupInfo(f, world, info.LastWriteTime, info.Length));
            }
            catch { }
        }
        return list.OrderByDescending(b => b.Created).ToList();
    }

    /// <summary>把备份恢复到 saves 下；若同名世界已存在，则恢复为「名称-恢复-时间」。</summary>
    public string RestoreBackup(string zipPath, string gameDirectory)
    {
        var saves = Path.Combine(gameDirectory, "saves");
        Directory.CreateDirectory(saves);

        // 备份内的顶层目录就是世界名
        string worldName;
        using (var zip = ZipFile.OpenRead(zipPath))
        {
            var first = zip.Entries.FirstOrDefault(e => !string.IsNullOrEmpty(e.Name));
            if (first == null) throw new InvalidOperationException("备份文件是空的。");
            var top = first.FullName.Replace('\\', '/').Split('/')[0];
            worldName = string.IsNullOrWhiteSpace(top) ? "restored-world" : Sanitize(top);
        }

        var target = Path.Combine(saves, worldName);
        if (Directory.Exists(target))
            target = Path.Combine(saves, $"{worldName}-恢复-{DateTime.Now:MMdd-HHmmss}");

        // 先解到 saves 内的临时目录（同盘才能 Move），再把顶层世界目录移到目标位置
        var temp = Path.Combine(saves, ".nex-restore-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(temp);
        try
        {
            ZipFile.ExtractToDirectory(zipPath, temp, true);
            var top = Directory.EnumerateDirectories(temp).FirstOrDefault();
            if (top == null) throw new InvalidOperationException("备份内容不完整（没有世界目录）。");
            Directory.Move(top, target);
        }
        finally
        {
            try { Directory.Delete(temp, true); } catch { }
        }
        return target;
    }

    /// <summary>删除存档（只允许 saves 下的一级子目录）。</summary>
    public void DeleteWorld(string worldPath, string gameDirectory)
    {
        var saves = Path.GetFullPath(Path.Combine(gameDirectory, "saves"));
        var world = Path.GetFullPath(worldPath);
        if (!world.StartsWith(saves + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("只能删除 saves 目录下的存档。");
        if (Path.GetDirectoryName(world)!.Equals(saves, StringComparison.OrdinalIgnoreCase) == false)
            throw new InvalidOperationException("只能删除 saves 下的一级存档目录。");
        Directory.Delete(world, true);
    }

    public void DeleteBackup(string zipPath, string gameDirectory)
    {
        var dir = Path.GetFullPath(BackupsDir(gameDirectory));
        var full = Path.GetFullPath(zipPath);
        if (!full.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("只能删除备份目录下的文件。");
        File.Delete(full);
    }

    private static string Sanitize(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name;
    }
}
