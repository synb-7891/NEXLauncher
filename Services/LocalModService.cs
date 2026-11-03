using System.IO;
using System.IO.Compression;
using System.Text.Json;

namespace NEXLauncher.Services;

public sealed record LocalModInfo(
    string FileName,
    string FullPath,
    string Loader,      // Forge / NeoForge / Fabric / Quilt / 未知
    string ModId,
    string Version);

/// <summary>
/// 扫描游戏目录下的 mods 文件夹，解析每个 jar 的内部元数据，
/// 识别它属于哪个模组加载器。
/// </summary>
public sealed class LocalModService
{
    public IReadOnlyList<LocalModInfo> ScanMods(string gameDirectory)
    {
        var modsDir = Path.Combine(gameDirectory, "mods");
        var result = new List<LocalModInfo>();
        if (!Directory.Exists(modsDir)) return result;

        foreach (var file in Directory.EnumerateFiles(modsDir, "*.jar", SearchOption.TopDirectoryOnly))
        {
            result.Add(InspectJar(file));
        }
        // 禁用的模组（.jar.disabled）也列出，标记为未知加载器
        foreach (var file in Directory.EnumerateFiles(modsDir, "*.jar.disabled", SearchOption.TopDirectoryOnly))
        {
            result.Add(new LocalModInfo(
                Path.GetFileName(file) + "（已禁用）", file, "未知",
                Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(file)), ""));
        }
        return result.OrderBy(m => m.FileName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static LocalModInfo InspectJar(string path)
    {
        var fileName = Path.GetFileName(path);
        try
        {
            using var zip = ZipFile.OpenRead(path);

            // Fabric
            var fabricEntry = zip.GetEntry("fabric.mod.json");
            if (fabricEntry != null)
            {
                var (id, version) = ReadFabricMeta(fabricEntry);
                return new LocalModInfo(fileName, path, "Fabric", id, version);
            }

            // Quilt
            if (zip.GetEntry("quilt.mod.json") != null)
                return new LocalModInfo(fileName, path, "Quilt",
                    Path.GetFileNameWithoutExtension(path), "");

            // NeoForge（1.20.2+ 的 NeoForge 用 neoforge.mods.toml）
            if (zip.GetEntry("META-INF/neoforge.mods.toml") != null)
                return new LocalModInfo(fileName, path, "NeoForge",
                    Path.GetFileNameWithoutExtension(path), "");

            // Forge（新版 mods.toml / 旧版 mcmod.info）
            if (zip.GetEntry("META-INF/mods.toml") != null || zip.GetEntry("mcmod.info") != null)
                return new LocalModInfo(fileName, path, "Forge",
                    Path.GetFileNameWithoutExtension(path), "");
        }
        catch
        {
            // jar 损坏或不可读
        }
        return new LocalModInfo(fileName, path, "未知", Path.GetFileNameWithoutExtension(path), "");
    }

    /// <summary>禁用模组：xxx.jar → xxx.jar.disabled（游戏不再加载）。</summary>
    public void Disable(string fullPath)
    {
        if (!File.Exists(fullPath)) throw new FileNotFoundException("模组文件不存在。", fullPath);
        if (fullPath.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)) return;
        File.Move(fullPath, fullPath + ".disabled");
    }

    /// <summary>启用模组：xxx.jar.disabled → xxx.jar。</summary>
    public void Enable(string fullPath)
    {
        if (!File.Exists(fullPath)) throw new FileNotFoundException("模组文件不存在。", fullPath);
        if (!fullPath.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)) return;
        var target = fullPath[..^".disabled".Length];
        if (File.Exists(target))
            throw new IOException("已存在同名启用文件：" + Path.GetFileName(target));
        File.Move(fullPath, target);
    }

    /// <summary>删除模组（只允许 mods 目录下的一级文件）。</summary>
    public void Delete(string fullPath, string gameDirectory)
    {
        var mods = Path.GetFullPath(Path.Combine(gameDirectory, "mods"));
        var full = Path.GetFullPath(fullPath);
        if (!string.Equals(Path.GetDirectoryName(full), mods, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("只能删除 mods 目录下的文件。");
        File.Delete(full);
    }

    private static (string Id, string Version) ReadFabricMeta(ZipArchiveEntry entry)
    {
        try
        {
            using var stream = entry.Open();
            using var doc = JsonDocument.Parse(stream);
            var root = doc.RootElement;
            var id = root.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
            var version = root.TryGetProperty("version", out var vEl) ? vEl.GetString() ?? "" : "";
            return (id, version);
        }
        catch
        {
            return ("", "");
        }
    }
}
