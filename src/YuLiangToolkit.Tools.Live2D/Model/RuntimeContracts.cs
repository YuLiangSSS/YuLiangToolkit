using System.Text.Json;
using YuLiangToolkit.Tools.Live2D.Interop;

namespace YuLiangToolkit.Tools.Live2D.Model;

/// <summary>
/// 一套 Live2D 模型资源（一个模型文件夹）：model3.json 里引用的 moc / 纹理 / 物理 / cdi3，
/// 以及文件夹里扫到的动作（*.motion3.json）与表情（*.exp3.json）。
/// </summary>
public sealed class Live2DModelAsset
{
    /// <summary>模型文件夹绝对路径。</summary>
    public required string Directory { get; init; }

    /// <summary>显示名（一般取 model3.json 文件名或文件夹名）。</summary>
    public required string Name { get; init; }

    /// <summary>model3.json 绝对路径（找不到为 null = 只有 moc/纹理的裸模型，按约定猜路径）。</summary>
    public string? Model3Path { get; init; }

    /// <summary>moc3 绝对路径。</summary>
    public required string MocPath { get; init; }

    /// <summary>纹理图集绝对路径（按 model3.json Textures 顺序）。</summary>
    public required IReadOnlyList<string> TexturePaths { get; init; }

    /// <summary>physics3.json 绝对路径（可空）。</summary>
    public string? PhysicsPath { get; init; }

    /// <summary>cdi3.json（显示信息：参数 / 部件的中文名与分组）绝对路径（可空）。</summary>
    public string? Cdi3Path { get; init; }

    /// <summary>动作文件（*.motion3.json，已排序）。</summary>
    public IReadOnlyList<string> MotionPaths { get; init; } = Array.Empty<string>();

    /// <summary>表情文件（*.exp3.json，已排序）。</summary>
    public IReadOnlyList<string> ExpressionPaths { get; init; } = Array.Empty<string>();

    /// <summary>动作列表（含时长 / Loop / 待机候选，来自 motion3.json 的 Meta）。</summary>
    public IReadOnlyList<MotionEntry> Motions { get; init; } = Array.Empty<MotionEntry>();

    /// <summary>表情列表（UI 直接用）。</summary>
    public IReadOnlyList<ExpressionEntry> Expressions { get; init; } = Array.Empty<ExpressionEntry>();

    /// <summary>model3.json 里 Groups(Target=Parameter, Name=EyeBlink) 声明的眨眼参数（可空）。</summary>
    internal IReadOnlyList<string> EyeBlinkParameterIds { get; init; } = Array.Empty<string>();

    /// <summary>model3.json 里 Groups(Target=Parameter, Name=LipSync) 声明的口型参数（可空）。</summary>
    internal IReadOnlyList<string> LipSyncParameterIds { get; init; } = Array.Empty<string>();

    /// <summary>model3.json 的 Version（裸模型为 0）。</summary>
    internal int Model3Version { get; init; }

    /// <summary>内置模型目录（exe 旁边：<c>Live2D\Models\DeepSeek</c>）。</summary>
    public static string BuiltInModelDirectory
        => Path.Combine(AppContext.BaseDirectory, "Live2D", "Models", "DeepSeek");

    /// <summary>
    /// 发现一套模型：参数可以是模型文件夹，或 model3.json 直接路径。
    /// 找不到 model3.json 时按「文件夹里唯一的 .moc3」+「texture_00.png 所在子文件夹」兜底。
    /// </summary>
    public static Live2DModelAsset Discover(string folderOrModel3Path)
    {
        if (string.IsNullOrWhiteSpace(folderOrModel3Path)) folderOrModel3Path = BuiltInModelDirectory;
        var full = Path.GetFullPath(folderOrModel3Path);

        string folder;
        string? model3Path;
        if (File.Exists(full))
        {
            if (!full.EndsWith(".model3.json", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"不是 model3.json 文件：{full}", nameof(folderOrModel3Path));
            folder = Path.GetDirectoryName(full)!;
            model3Path = full;
        }
        else if (System.IO.Directory.Exists(full))
        {
            folder = full;
            model3Path = FindModel3(folder);
        }
        else
        {
            throw new FileNotFoundException($"模型文件夹或 model3.json 不存在：{full}", full);
        }

        // 动作 / 表情：递归按扩展名扫（不写死 Animation\ / Exp3\ 目录名）
        var motionPaths = new List<string>();
        var expressionPaths = new List<string>();
        foreach (var path in System.IO.Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(path);
            if (name.EndsWith(".motion3.json", StringComparison.OrdinalIgnoreCase)) motionPaths.Add(path);
            else if (name.EndsWith(".exp3.json", StringComparison.OrdinalIgnoreCase)) expressionPaths.Add(path);
        }

        motionPaths.Sort(StringComparer.OrdinalIgnoreCase);
        expressionPaths.Sort(StringComparer.OrdinalIgnoreCase);

        var motions = BuildMotionEntries(motionPaths);
        var expressions = BuildExpressionEntries(expressionPaths);

        if (model3Path is not null)
            return DiscoverFromModel3(folder, model3Path, motionPaths, expressionPaths, motions, expressions);
        return DiscoverBareModel(folder, motionPaths, expressionPaths, motions, expressions);
    }

    /// <summary>在模型文件夹里找 model3.json：优先与文件夹同名，其次取文件名排序第一个。</summary>
    private static string? FindModel3(string folder)
    {
        var candidates = System.IO.Directory.GetFiles(folder, "*.model3.json", SearchOption.TopDirectoryOnly);
        if (candidates.Length == 0) return null;
        if (candidates.Length == 1) return candidates[0];

        var folderName = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        foreach (var candidate in candidates)
        {
            if (string.Equals(Path.GetFileName(candidate), folderName + ".model3.json", StringComparison.OrdinalIgnoreCase))
                return candidate;
        }

        Array.Sort(candidates, StringComparer.OrdinalIgnoreCase);
        return candidates[0];
    }

    private static Live2DModelAsset DiscoverFromModel3(string folder, string model3Path,
        List<string> motionPaths, List<string> expressionPaths,
        IReadOnlyList<MotionEntry> motions, IReadOnlyList<ExpressionEntry> expressions)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(model3Path));
        var root = document.RootElement;

        var version = GetInt32(root, "Version");
        if (!root.TryGetProperty("FileReferences", out var references) || references.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"model3.json 缺少 FileReferences：{model3Path}");

        var name = Path.GetFileName(model3Path);
        name = name[..^".model3.json".Length];

        var mocPath = ResolveRequired(folder, references, "Moc", model3Path);
        var texturePaths = new List<string>();
        if (references.TryGetProperty("Textures", out var textures) && textures.ValueKind == JsonValueKind.Array)
        {
            foreach (var texture in textures.EnumerateArray())
            {
                if (texture.ValueKind != JsonValueKind.String) continue;
                var raw = texture.GetString();
                if (string.IsNullOrWhiteSpace(raw)) continue;
                texturePaths.Add(Path.GetFullPath(Path.Combine(folder, raw)));
            }
        }

        if (texturePaths.Count == 0) texturePaths.AddRange(FindTextureSet(folder));

        var physicsPath = ResolveOptional(folder, references, "Physics");
        var cdi3Path = ResolveOptional(folder, references, "DisplayInfo");

        // 裸模型/简化模型兜底：model3 没写 physics / cdi3 时，文件夹里只有一个就认它
        physicsPath ??= FindUnique(folder, "*.physics3.json");
        cdi3Path ??= FindUnique(folder, "*.cdi3.json");

        var (eyeBlinkIds, lipSyncIds) = ParseParameterGroups(root);

        return new Live2DModelAsset
        {
            Directory = folder,
            Name = name,
            Model3Path = model3Path,
            MocPath = mocPath,
            TexturePaths = texturePaths,
            PhysicsPath = physicsPath,
            Cdi3Path = cdi3Path,
            MotionPaths = motionPaths,
            ExpressionPaths = expressionPaths,
            Motions = motions,
            Expressions = expressions,
            EyeBlinkParameterIds = eyeBlinkIds,
            LipSyncParameterIds = lipSyncIds,
            Model3Version = version,
        };
    }

    private static Live2DModelAsset DiscoverBareModel(string folder,
        List<string> motionPaths, List<string> expressionPaths,
        IReadOnlyList<MotionEntry> motions, IReadOnlyList<ExpressionEntry> expressions)
    {
        // 兜底约定：文件夹里唯一的 .moc3（递归找）+ texture_00.png 所在子文件夹里的全部 png
        var mocCandidates = System.IO.Directory.GetFiles(folder, "*.moc3", SearchOption.AllDirectories);
        if (mocCandidates.Length == 0)
            throw new FileNotFoundException($"模型文件夹里既没有 model3.json 也没有 moc3：{folder}", folder);
        if (mocCandidates.Length > 1)
        {
            Array.Sort(mocCandidates, StringComparer.OrdinalIgnoreCase);
            throw new InvalidDataException(
                $"没有 model3.json，且文件夹里有多个 moc3（{mocCandidates.Length} 个），无法确定用哪个：" +
                string.Join("、", mocCandidates.Select(Path.GetFileName)));
        }

        var name = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return new Live2DModelAsset
        {
            Directory = folder,
            Name = name,
            Model3Path = null,
            MocPath = mocCandidates[0],
            TexturePaths = FindTextureSet(folder),
            PhysicsPath = FindUnique(folder, "*.physics3.json"),
            Cdi3Path = FindUnique(folder, "*.cdi3.json"),
            MotionPaths = motionPaths,
            ExpressionPaths = expressionPaths,
            Motions = motions,
            Expressions = expressions,
            EyeBlinkParameterIds = Array.Empty<string>(),
            LipSyncParameterIds = Array.Empty<string>(),
            Model3Version = 0,
        };
    }

    /// <summary>找 texture_00.png 所在子文件夹，把它里面的 png 按文件名排序当作纹理集。</summary>
    private static List<string> FindTextureSet(string folder)
    {
        var pngs = System.IO.Directory.GetFiles(folder, "*.png", SearchOption.AllDirectories);
        if (pngs.Length == 0) return new List<string>();

        var byDirectory = pngs.GroupBy(Path.GetDirectoryName)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var group in byDirectory)
        {
            if (group.Any(p => string.Equals(Path.GetFileName(p), "texture_00.png", StringComparison.OrdinalIgnoreCase)))
            {
                var list = group.ToList();
                list.Sort((a, b) => string.CompareOrdinal(Path.GetFileName(a), Path.GetFileName(b)));
                return list;
            }
        }

        // 没有 texture_00.png：取 png 最多的目录
        var best = byDirectory.OrderByDescending(g => g.Count()).First();
        var fallback = best.ToList();
        fallback.Sort((a, b) => string.CompareOrdinal(Path.GetFileName(a), Path.GetFileName(b)));
        return fallback;
    }

    private static string ResolveRequired(string folder, JsonElement references, string key, string model3Path)
    {
        var path = ResolveOptional(folder, references, key)
                   ?? throw new InvalidDataException($"model3.json 缺少 FileReferences.{key}：{model3Path}");
        if (!File.Exists(path)) throw new FileNotFoundException($"model3.json 引用的 {key} 不存在：{path}", path);
        return path;
    }

    private static string? ResolveOptional(string folder, JsonElement references, string key)
    {
        if (!references.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.String) return null;
        var raw = value.GetString();
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return Path.GetFullPath(Path.Combine(folder, raw));
    }

    private static string? FindUnique(string folder, string pattern)
    {
        var found = System.IO.Directory.GetFiles(folder, pattern, SearchOption.AllDirectories);
        return found.Length == 1 ? found[0] : null;
    }

    private static int GetInt32(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : 0;

    /// <summary>解析 model3.json 的 Groups：眨眼 / 口型参数 Id 列表。</summary>
    private static (IReadOnlyList<string> EyeBlink, IReadOnlyList<string> LipSync) ParseParameterGroups(JsonElement root)
    {
        var eyeBlink = new List<string>();
        var lipSync = new List<string>();
        if (!root.TryGetProperty("Groups", out var groups) || groups.ValueKind != JsonValueKind.Array)
            return (eyeBlink, lipSync);

        foreach (var group in groups.EnumerateArray())
        {
            if (group.ValueKind != JsonValueKind.Object) continue;
            if (!group.TryGetProperty("Target", out var target) || target.ValueKind != JsonValueKind.String) continue;
            if (!string.Equals(target.GetString(), "Parameter", StringComparison.Ordinal)) continue;
            if (!group.TryGetProperty("Name", out var nameElement) || nameElement.ValueKind != JsonValueKind.String) continue;

            var name = nameElement.GetString();
            List<string>? destination = null;
            if (string.Equals(name, "EyeBlink", StringComparison.OrdinalIgnoreCase)) destination = eyeBlink;
            else if (string.Equals(name, "LipSync", StringComparison.OrdinalIgnoreCase)) destination = lipSync;
            if (destination is null) continue;

            if (!group.TryGetProperty("Ids", out var ids) || ids.ValueKind != JsonValueKind.Array) continue;
            foreach (var id in ids.EnumerateArray())
            {
                if (id.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(id.GetString()))
                    destination.Add(id.GetString()!);
            }
        }

        return (eyeBlink, lipSync);
    }

    /// <summary>读动作文件里最基础的 Meta，生成 UI 用的条目（动作时长 / Loop / 是否待机候选）。</summary>
    private static IReadOnlyList<MotionEntry> BuildMotionEntries(List<string> motionPaths)
    {
        var entries = new List<MotionEntry>(motionPaths.Count);
        foreach (var path in motionPaths)
        {
            double duration = 0;
            var loop = false;
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllBytes(path));
                if (document.RootElement.TryGetProperty("Meta", out var meta) && meta.ValueKind == JsonValueKind.Object)
                {
                    if (meta.TryGetProperty("Duration", out var durationElement) && durationElement.ValueKind == JsonValueKind.Number)
                        duration = durationElement.GetDouble();
                    if (meta.TryGetProperty("Loop", out var loopElement))
                        loop = loopElement.ValueKind == JsonValueKind.True;
                }
            }
            catch (JsonException)
            {
                // 坏文件别阻断整套模型加载：时长按 0 / 不循环处理，播放时会再报错
            }

            var fileName = Path.GetFileName(path);
            var name = fileName[..^".motion3.json".Length];
            entries.Add(new MotionEntry
            {
                Name = name,
                FilePath = path,
                Duration = duration,
                Loop = loop,
                IsIdleCandidate = false,
            });
        }

        if (entries.Count == 0) return entries;

        // 待机候选：名字里带 IDLE 的优先；都没有就取「循环且时长最长」的那个
        var idleIndex = entries.FindIndex(e => e.Name.Contains("IDLE", StringComparison.OrdinalIgnoreCase));
        if (idleIndex < 0)
        {
            var best = -1;
            for (var i = 0; i < entries.Count; i++)
            {
                if (!entries[i].Loop) continue;
                if (best < 0 || entries[i].Duration > entries[best].Duration) best = i;
            }

            idleIndex = best;
        }

        if (idleIndex < 0) idleIndex = 0;
        for (var i = 0; i < entries.Count; i++)
        {
            if (i != idleIndex) continue;
            entries[i] = new MotionEntry
            {
                Name = entries[i].Name,
                FilePath = entries[i].FilePath,
                Duration = entries[i].Duration,
                Loop = entries[i].Loop,
                IsIdleCandidate = true,
            };
        }

        return entries;
    }

    /// <summary>把表情文件生成 UI 用的条目（名字 = 文件名去掉 .exp3.json）。</summary>
    private static IReadOnlyList<ExpressionEntry> BuildExpressionEntries(List<string> expressionPaths)
    {
        var entries = new List<ExpressionEntry>(expressionPaths.Count);
        foreach (var path in expressionPaths)
        {
            var fileName = Path.GetFileName(path);
            entries.Add(new ExpressionEntry
            {
                Name = fileName[..^".exp3.json".Length],
                FilePath = path,
            });
        }

        return entries;
    }

    public override string ToString() => Name;
}

/// <summary>加载模型的选项。</summary>
public sealed class RuntimeLoadOptions
{
    /// <summary>纹理最大尺寸（2048 / 4096）。</summary>
    public int TextureMaxSize { get; init; } = 2048;

    /// <summary>优先当作待机动作的 motion 文件名（空 = 自动从动作列表里猜）。</summary>
    public string? IdleMotionName { get; init; }

    /// <summary>进度回调（0~1）。</summary>
    public Action<double>? Progress { get; init; }

    /// <summary>日志回调。</summary>
    public Action<string>? Log { get; init; }

    /// <summary>取消令牌。</summary>
    public CancellationToken CancellationToken { get; init; }
}

/// <summary>一个可播放的动作（motion3.json）。</summary>
public sealed class MotionEntry
{
    /// <summary>动作名（一般取文件名，去掉 .motion3.json）。</summary>
    public required string Name { get; init; }

    /// <summary>动作文件绝对路径。</summary>
    public required string FilePath { get; init; }

    /// <summary>时长（秒）。</summary>
    public double Duration { get; init; }

    /// <summary>是否建议循环（Meta.Loop）。</summary>
    public bool Loop { get; init; }

    /// <summary>是否自动把它当待机动作（Idle 命名或 Meta.Loop 且最长）。</summary>
    public bool IsIdleCandidate { get; init; }

    public override string ToString() => Name;
}

/// <summary>一个表情（exp3.json）。</summary>
public sealed class ExpressionEntry
{
    /// <summary>表情名（一般取文件名，去掉 .exp3.json）。</summary>
    public required string Name { get; init; }

    /// <summary>表情文件绝对路径。</summary>
    public required string FilePath { get; init; }

    public override string ToString() => Name;
}

/// <summary>参数在界面上的绑定行（参数对象 + cdi3 里的中文名 / 分组）。</summary>
public sealed class ParameterBinding
{
    public required CubismParameter Parameter { get; init; }
    public required string DisplayName { get; init; }
    public string GroupName { get; init; } = "其他";
    public string GroupId { get; init; } = "";

    public string Id => Parameter.Id;
    public float Minimum => Parameter.Minimum;
    public float Maximum => Parameter.Maximum;
    public float DefaultValue => Parameter.DefaultValue;

    public float Value
    {
        get => Parameter.Value;
        set => Parameter.Value = value;
    }
}

/// <summary>部件在界面上的绑定行（部件对象 + cdi3 里的中文名 / 分组）。</summary>
public sealed class PartBinding
{
    public required CubismPart Part { get; init; }
    public required string DisplayName { get; init; }
    public string GroupName { get; init; } = "其他";

    public string Id => Part.Id;

    public float Opacity
    {
        get => Part.Opacity;
        set => Part.Opacity = value;
    }
}

/// <summary>参数分组（来自 cdi3.json）。</summary>
public sealed class ParameterGroupInfo
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public IReadOnlyList<string> ParameterIds { get; init; } = Array.Empty<string>();
}

/// <summary>部件分组（来自 cdi3.json）。</summary>
public sealed class PartGroupInfo
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public IReadOnlyList<string> PartIds { get; init; } = Array.Empty<string>();
}

/// <summary>cdi3.json 的显示信息：参数 / 部件的中文名与分组（UI 列表按它组织）。</summary>
public sealed class DisplayInfo
{
    public IReadOnlyList<ParameterGroupInfo> ParameterGroups { get; init; } = Array.Empty<ParameterGroupInfo>();
    public IReadOnlyList<PartGroupInfo> PartGroups { get; init; } = Array.Empty<PartGroupInfo>();
    public IReadOnlyDictionary<string, string> ParameterNames { get; init; } = new Dictionary<string, string>();
    public IReadOnlyDictionary<string, string> PartNames { get; init; } = new Dictionary<string, string>();
}
