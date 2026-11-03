using System.Diagnostics;
using System.IO;
using System.IO.Compression;

namespace NEXLauncher.Services;

public sealed record ServerInstance(string Name, string Directory, string? JarFile, bool HasStartScript);

/// <summary>
/// 服务端管理：导入服务端整合包（zip），自动生成 eula.txt 和开服脚本，列出并启动服务端。
/// 服务端实例保存在 游戏目录\servers\&lt;名称&gt;\。
/// </summary>
public sealed class ServerDirectoryService
{
    private readonly string _serversRoot;

    public ServerDirectoryService(string gameDirectory)
    {
        _serversRoot = Path.Combine(gameDirectory, "servers");
        System.IO.Directory.CreateDirectory(_serversRoot);
    }

    public string ServersRoot => _serversRoot;

    public IReadOnlyList<ServerInstance> ListServers()
    {
        var result = new List<ServerInstance>();
        foreach (var dir in System.IO.Directory.GetDirectories(_serversRoot))
        {
            result.Add(new ServerInstance(
                Path.GetFileName(dir),
                dir,
                FindServerJar(dir),
                File.Exists(Path.Combine(dir, "start.bat"))));
        }
        return result.OrderByDescending(s => System.IO.Directory.GetLastWriteTime(s.Directory)).ToList();
    }

    /// <summary>导入服务端整合包 zip：解压、写 eula、生成开服脚本。返回实例。</summary>
    public ServerInstance ImportPackage(string zipPath, Action<string>? reportProgress = null)
    {
        var name = Path.GetFileNameWithoutExtension(zipPath);
        var targetDir = Path.Combine(_serversRoot, MakeUniqueName(name));

        reportProgress?.Invoke("正在解压整合包...");
        ZipFile.ExtractToDirectory(zipPath, targetDir);

        // 部分整合包会把文件包在一层同名文件夹里，展开它
        FlattenSingleSubdirectory(targetDir);

        // 自动同意 EULA，免去手动编辑
        reportProgress?.Invoke("写入 eula.txt...");
        File.WriteAllText(Path.Combine(targetDir, "eula.txt"),
            "# 由 NEX Launcher 自动生成\n# https://aka.ms/MinecraftEULA\neula=true\n");

        var jar = FindServerJar(targetDir)
            ?? throw new InvalidDataException("压缩包中未找到服务端 jar 文件，无法生成开服脚本。");

        reportProgress?.Invoke("生成开服脚本...");
        CreateStartScript(targetDir, jar);

        return new ServerInstance(Path.GetFileName(targetDir), targetDir, jar, true);
    }

    /// <summary>为已有服务端目录生成/重写开服脚本。</summary>
    public void CreateStartScript(string serverDir, string jarFile)
    {
        var script = $"""
            @echo off
            title NEX Server - {Path.GetFileName(serverDir)}
            cd /d "%~dp0"
            java -Xmx2G -Xms1G -jar "{jarFile}" nogui
            pause
            """;
        File.WriteAllText(Path.Combine(serverDir, "start.bat"), script + Environment.NewLine);
    }

    /// <summary>在新控制台窗口中启动服务端。</summary>
    public void StartServer(ServerInstance server)
    {
        var scriptPath = Path.Combine(server.Directory, "start.bat");
        if (!File.Exists(scriptPath))
        {
            if (server.JarFile == null)
                throw new FileNotFoundException("该服务端缺少 jar 文件，无法启动。");
            CreateStartScript(server.Directory, server.JarFile);
        }
        Process.Start(new ProcessStartInfo
        {
            FileName = scriptPath,
            WorkingDirectory = server.Directory,
            UseShellExecute = true
        });
    }

    private static string? FindServerJar(string dir)
    {
        // 优先识别常见服务端 jar 名称
        var jars = System.IO.Directory.GetFiles(dir, "*.jar", SearchOption.TopDirectoryOnly);
        if (jars.Length == 0) return null;
        var preferred = jars.FirstOrDefault(j =>
        {
            var n = Path.GetFileName(j).ToLowerInvariant();
            return n.Contains("server") || n.Contains("paper") || n.Contains("purpur")
                || n.Contains("spigot") || n.Contains("fabric") || n.Contains("forge")
                || n.Contains("quilt") || n.Contains("mohist") || n.Contains("vanilla");
        });
        return Path.GetFileName(preferred ?? jars[0]);
    }

    private static void FlattenSingleSubdirectory(string dir)
    {
        var subDirs = System.IO.Directory.GetDirectories(dir);
        var files = System.IO.Directory.GetFiles(dir);
        if (files.Length == 0 && subDirs.Length == 1)
        {
            var inner = subDirs[0];
            foreach (var f in System.IO.Directory.GetFiles(inner))
                File.Move(f, Path.Combine(dir, Path.GetFileName(f)), true);
            foreach (var d in System.IO.Directory.GetDirectories(inner))
                System.IO.Directory.Move(d, Path.Combine(dir, Path.GetFileName(d)));
            System.IO.Directory.Delete(inner, true);
        }
    }

    private string MakeUniqueName(string baseName)
    {
        var name = string.IsNullOrWhiteSpace(baseName) ? "server" : baseName;
        var candidate = name;
        var i = 2;
        while (System.IO.Directory.Exists(Path.Combine(_serversRoot, candidate)))
            candidate = $"{name}-{i++}";
        return candidate;
    }
}
