using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Kumo.Models;

namespace Kumo.Services;

public sealed record PreparedGame(
    Process Process,
    string VersionId,
    string InstanceDirectory,
    IReadOnlyList<string> ArgumentsForDisplay);

public sealed class GameLauncher
{
    private readonly string _dataDirectory;
    private readonly VersionProfileService _profiles;
    private readonly VersionInstaller _installer;
    private readonly JavaService _java;

    public GameLauncher(
        string dataDirectory,
        VersionProfileService profiles,
        VersionInstaller installer,
        JavaService java)
    {
        _dataDirectory = dataDirectory;
        _profiles = profiles;
        _installer = installer;
        _java = java;
    }

    public async Task<PreparedGame> LaunchAsync(
        string versionId,
        AccountInfo account,
        int memoryMb,
        string? configuredJava,
        IProgress<DownloadProgress>? progress,
        Action<string>? output,
        CancellationToken cancellationToken,
        string? extraJvmArguments = null,
        string? extraGameArguments = null,
        bool versionIsolation = true,
        GlobalMinecraftOptions? globalGameOptions = null)
    {
        if (string.IsNullOrWhiteSpace(versionId) || Path.GetFileName(versionId) != versionId)
            throw new InvalidOperationException("版本名称无效。");
        if (memoryMb < 1024 || memoryMb > 32768)
            throw new InvalidOperationException("内存应在 1024 MB 到 32768 MB 之间。");

        progress?.Report(new DownloadProgress("正在检查游戏文件", 0, 1, versionId));
        await _installer.EnsureVersionAsync(versionId, progress, cancellationToken);
        var profile = await _profiles.ResolveAsync(versionId, cancellationToken);
        var javaRuntime = await _java.EnsureJavaAsync(profile, configuredJava, progress, cancellationToken);

        var instance = versionIsolation
            ? Path.Combine(_dataDirectory, "versions", versionId)
            : _dataDirectory;
        Directory.CreateDirectory(instance);
        Directory.CreateDirectory(Path.Combine(instance, "mods"));
        Directory.CreateDirectory(Path.Combine(instance, "saves"));
        if (globalGameOptions is not null)
        {
            try
            {
                GlobalGameOptionsService.Apply(instance, globalGameOptions);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogService.Warn($"无法同步全局游戏设置：{ex.Message}");
            }
        }

        progress?.Report(new DownloadProgress("正在准备游戏运行环境", 0, 1, versionId));
        var natives = Path.Combine(_dataDirectory, "natives", versionId);
        Directory.CreateDirectory(natives);
        ExtractNatives(profile, natives);
        await PrepareLegacyAssetsAsync(profile, instance, cancellationToken);

        var baseVersion = await FindBaseVersionAsync(versionId, cancellationToken);
        var classpath = BuildClasspath(profile, baseVersion);
        var token = account.AccessToken;
        if (string.IsNullOrWhiteSpace(token))
            token = account.Kind == AccountKind.Offline ? "offline" : "0";
        var uuid = account.MinecraftUuid;
        if (string.IsNullOrWhiteSpace(uuid))
            uuid = CreateOfflineUuid(account.Name);
        uuid = uuid.Replace("-", "");

        var placeholders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["auth_player_name"] = account.Name,
            ["version_name"] = versionId,
            ["game_directory"] = instance,
            ["assets_root"] = Path.Combine(_dataDirectory, "assets"),
            ["game_assets"] = Path.Combine(_dataDirectory, "assets", "virtual", profile.Str("assets") ?? "legacy"),
            ["assets_index_name"] = profile.Str("assets") ?? profile.Obj("assetIndex")?.Str("id") ?? "legacy",
            ["auth_uuid"] = uuid,
            ["auth_access_token"] = token,
            ["auth_session"] = account.Kind == AccountKind.Offline ? "offline:" + uuid : token + ":" + uuid,
            ["auth_xuid"] = account.Xuid ?? "",
            ["clientid"] = "kumo",
            ["user_type"] = account.Kind == AccountKind.Microsoft ? "msa" : "legacy",
            ["version_type"] = profile.Str("type") ?? "release",
            ["natives_directory"] = natives,
            ["launcher_name"] = "Kumo",
            ["launcher_version"] = "1.0.0",
            ["classpath"] = classpath,
            ["classpath_separator"] = Path.PathSeparator.ToString(),
            ["library_directory"] = Path.Combine(_dataDirectory, "libraries")
        };

        var arguments = new List<string>
        {
            $"-Xms{Math.Min(1024, memoryMb)}M",
            $"-Xmx{memoryMb}M"
        };

        var argumentsNode = profile.Obj("arguments");
        if (argumentsNode?.Arr("jvm") is JsonArray jvmArguments)
            AddArgumentElements(arguments, jvmArguments, placeholders);
        else
        {
            arguments.Add("-Djava.library.path=" + natives);
            arguments.Add("-cp");
            arguments.Add(classpath);
        }
        if (!string.IsNullOrWhiteSpace(extraJvmArguments))
            arguments.AddRange(SplitCommandLine(extraJvmArguments));

        var mainClass = profile.Str("mainClass")
                        ?? throw new InvalidDataException("版本配置缺少主类。");
        arguments.Add(mainClass);

        var gameArgumentStart = arguments.Count;
        if (argumentsNode?.Arr("game") is JsonArray gameArguments)
            AddArgumentElements(arguments, gameArguments, placeholders);
        else if (profile.Str("minecraftArguments") is { } legacyArguments)
            arguments.AddRange(SplitCommandLine(ReplacePlaceholders(legacyArguments, placeholders)));
        else
            throw new InvalidDataException("版本配置缺少游戏启动参数。");
        if (!string.IsNullOrWhiteSpace(extraGameArguments))
            arguments.AddRange(SplitCommandLine(extraGameArguments));
        DeduplicateGameArguments(arguments, gameArgumentStart);

        var displayArguments = arguments.Select(arg =>
            arg.Replace(token, "<access-token>", StringComparison.Ordinal)).ToList();
        var javaExecutable = javaRuntime.Executable;
        var javawExecutable = Path.Combine(
            Path.GetDirectoryName(javaExecutable)!, "javaw.exe");
        if (OperatingSystem.IsWindows() && File.Exists(javawExecutable))
            javaExecutable = javawExecutable;
        output?.Invoke($"Java {javaRuntime.MajorVersion} · {javaExecutable}");
        output?.Invoke($"版本目录：{instance}");

        var startInfo = new ProcessStartInfo(javaExecutable)
        {
            WorkingDirectory = instance,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data)) output?.Invoke(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data)) output?.Invoke("[stderr] " + e.Data);
        };
        if (!process.Start())
            throw new InvalidOperationException("无法启动 Java 进程。");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        LogService.Info($"已启动 Minecraft {versionId}，Java {javaRuntime.MajorVersion}，PID {process.Id}");
        return new PreparedGame(process, versionId, instance, displayArguments);
    }

    public static string CreateOfflineUuid(string username)
    {
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes("OfflinePlayer:" + username));
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x30);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private string BuildClasspath(JsonObject profile, string baseVersion)
    {
        var librariesByArtifact = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (profile.Arr("libraries") is JsonArray libraries)
        {
            foreach (var node in libraries)
            {
                if (node is not JsonObject library || !VersionInstaller.RulesAllow(library.Arr("rules")))
                    continue;
                var artifact = library.Obj("downloads")?.Obj("artifact");
                var relative = artifact?.Str("path") ??
                               (library.Str("name") is { } name ? JsonHelpers.ToMavenPath(name) : null);
                if (string.IsNullOrWhiteSpace(relative)) continue;
                if (!VersionInstaller.IsNativeArtifactCompatible(relative)) continue;
                var path = Path.Combine(_dataDirectory, "libraries", relative);
                if (!File.Exists(path)) continue;
                var artifactKey = GetClasspathArtifactKey(library.Str("name"), relative);
                librariesByArtifact[artifactKey] = path;
            }
        }

        var paths = librariesByArtifact.Values
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var jar = Path.Combine(_dataDirectory, "versions", baseVersion, baseVersion + ".jar");
        if (!File.Exists(jar)) throw new FileNotFoundException("找不到 Minecraft 客户端 JAR。", jar);
        paths.Add(jar);
        return string.Join(Path.PathSeparator, paths);
    }

    internal static string GetClasspathArtifactKey(string? libraryName, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(libraryName)) return relativePath;
        var parts = libraryName.Split(':');
        if (parts.Length < 2) return relativePath;
        return parts.Length >= 4
            ? $"{parts[0]}:{parts[1]}:{parts[3]}"
            : $"{parts[0]}:{parts[1]}";
    }

    private void ExtractNatives(JsonObject profile, string nativesDirectory)
    {
        if (profile.Arr("libraries") is not JsonArray libraries) return;
        foreach (var node in libraries)
        {
            if (node is not JsonObject library || !VersionInstaller.RulesAllow(library.Arr("rules"))) continue;
            if (library.Obj("downloads")?.Obj("classifiers") is not JsonObject classifiers) continue;
            foreach (var pair in classifiers)
            {
                if (!IsWindowsNative(pair.Key) || pair.Value is not JsonObject classifier ||
                    classifier.Str("path") is not { } relative) continue;
                var jarPath = Path.Combine(_dataDirectory, "libraries", relative);
                if (!File.Exists(jarPath)) continue;
                try
                {
                    using var archive = ZipFile.OpenRead(jarPath);
                    foreach (var entry in archive.Entries)
                    {
                        if (string.IsNullOrEmpty(entry.Name)) continue;
                        var fileName = Path.GetFileName(entry.FullName);
                        if (entry.FullName.StartsWith("META-INF", StringComparison.OrdinalIgnoreCase))
                            continue;
                        var target = Path.Combine(nativesDirectory, fileName);
                        entry.ExtractToFile(target, true);
                    }
                }
                catch (Exception ex)
                {
                    LogService.Warn($"提取原生库 {Path.GetFileName(jarPath)} 失败：{ex.Message}");
                }
            }
        }
    }

    private async Task PrepareLegacyAssetsAsync(
        JsonObject profile,
        string instanceDirectory,
        CancellationToken cancellationToken)
    {
        var indexId = profile.Str("assets") ?? profile.Obj("assetIndex")?.Str("id");
        if (string.IsNullOrWhiteSpace(indexId)) return;
        var indexPath = Path.Combine(_dataDirectory, "assets", "indexes", indexId + ".json");
        if (!File.Exists(indexPath)) return;
        var index = JsonNode.Parse(await File.ReadAllTextAsync(indexPath, cancellationToken)) as JsonObject;
        if (index is null || index.Obj("objects") is not JsonObject objects) return;
        var isVirtual = index.Bool("virtual");
        var mapToResources = index.Bool("map_to_resources");
        if (!isVirtual && !mapToResources) return;

        var targets = new List<string>();
        if (isVirtual) targets.Add(Path.Combine(_dataDirectory, "assets", "virtual", indexId));
        if (mapToResources) targets.Add(Path.Combine(instanceDirectory, "resources"));
        foreach (var targetRoot in targets)
        {
            foreach (var pair in objects)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var hash = (pair.Value as JsonObject)?.Str("hash");
                if (string.IsNullOrWhiteSpace(hash) || hash.Length < 2) continue;
                var source = Path.Combine(_dataDirectory, "assets", "objects", hash[..2], hash);
                if (!File.Exists(source)) continue;
                var relative = pair.Key.Replace('/', Path.DirectorySeparatorChar);
                var destination = Path.GetFullPath(Path.Combine(targetRoot, relative));
                if (!destination.StartsWith(Path.GetFullPath(targetRoot) + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                if (!File.Exists(destination)) File.Copy(source, destination);
            }
        }
    }

    private async Task<string> FindBaseVersionAsync(string versionId, CancellationToken cancellationToken)
    {
        var id = versionId;
        for (var depth = 0; depth < 20; depth++)
        {
            var profile = await _profiles.LoadLocalAsync(id, cancellationToken);
            var parent = profile.Str("inheritsFrom");
            if (string.IsNullOrWhiteSpace(parent)) return id;
            id = parent;
        }
        throw new InvalidDataException("版本继承链过深。");
    }

    private static void AddArgumentElements(
        ICollection<string> output,
        JsonArray elements,
        IReadOnlyDictionary<string, string> placeholders)
    {
        foreach (var node in elements)
        {
            if (node is JsonValue value && value.TryGetValue<string>(out var text))
            {
                output.Add(ReplacePlaceholders(text, placeholders));
                continue;
            }
            if (node is not JsonObject conditional ||
                !VersionInstaller.RulesAllow(conditional.Arr("rules"))) continue;
            var valueNode = conditional["value"];
            if (valueNode is JsonValue single && single.TryGetValue<string>(out var item))
                output.Add(ReplacePlaceholders(item, placeholders));
            else if (valueNode is JsonArray multiple)
            {
                foreach (var itemNode in multiple)
                {
                    if (itemNode is JsonValue itemValue && itemValue.TryGetValue<string>(out var itemText))
                        output.Add(ReplacePlaceholders(itemText, placeholders));
                }
            }
        }
    }

    private static string ReplacePlaceholders(
        string input,
        IReadOnlyDictionary<string, string> placeholders)
    {
        foreach (var pair in placeholders)
            input = input.Replace("${" + pair.Key + "}", pair.Value, StringComparison.OrdinalIgnoreCase);
        return input;
    }

    private static IEnumerable<string> SplitCommandLine(string commandLine)
    {
        var current = new StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < commandLine.Length; i++)
        {
            var c = commandLine[i];
            if (c == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }
            if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (current.Length > 0)
                {
                    yield return current.ToString();
                    current.Clear();
                }
                continue;
            }
            current.Append(c);
        }
        if (current.Length > 0) yield return current.ToString();
    }

    internal static void DeduplicateGameArguments(List<string> arguments, int startIndex)
    {
        if (startIndex < 0 || startIndex >= arguments.Count) return;
        var groups = new List<(string? Option, List<string> Tokens)>();
        for (var index = startIndex; index < arguments.Count; index++)
        {
            var token = arguments[index];
            if (!token.StartsWith("--", StringComparison.Ordinal))
            {
                groups.Add((null, [token]));
                continue;
            }

            var tokens = new List<string> { token };
            if (index + 1 < arguments.Count &&
                !arguments[index + 1].StartsWith("--", StringComparison.Ordinal))
                tokens.Add(arguments[++index]);
            groups.Add((token, tokens));
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var selected = new List<(string? Option, List<string> Tokens)>();
        for (var index = groups.Count - 1; index >= 0; index--)
        {
            var group = groups[index];
            if (group.Option is not null && !seen.Add(group.Option))
                continue;
            selected.Add(group);
        }
        selected.Reverse();

        arguments.RemoveRange(startIndex, arguments.Count - startIndex);
        foreach (var group in selected)
            arguments.AddRange(group.Tokens);
    }

    private static bool IsWindowsNative(string classifier) =>
        classifier.Equals("natives-windows", StringComparison.OrdinalIgnoreCase) ||
        classifier.Equals("natives-windows-64", StringComparison.OrdinalIgnoreCase) ||
        classifier.Equals("natives-windows-x86", StringComparison.OrdinalIgnoreCase) ||
        classifier.Equals("natives-windows-arm64", StringComparison.OrdinalIgnoreCase);
}
