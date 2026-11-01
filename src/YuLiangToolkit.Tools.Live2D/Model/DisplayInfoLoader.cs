using System.Text.Json;
using YuLiangToolkit.Tools.Live2D.Interop;

namespace YuLiangToolkit.Tools.Live2D.Model;

/// <summary>
/// cdi3.json（Cubism Display Info）解析：参数 / 部件的中文名与分组。
///
/// <para>本模型的 cdi3 有 509 个参数（147 个部件）、21 个参数分组；参数里还有一排
/// <c>Divider*</c> / <c>Paramshuiyin*</c> 分隔行，原样保留（UI 直接按名字显示即可）。</para>
///
/// <para>没 cdi3 文件时也能用：名字全部退化成 Id，分组为「其他」。</para>
/// </summary>
internal static class DisplayInfoLoader
{
    private const string OtherGroupName = "其他";

    /// <summary>解析 cdi3（可空）：失败 / 不存在返回 null，不阻断模型加载。</summary>
    public static DisplayInfo? TryLoad(string? cdi3Path, Action<string>? log = null)
    {
        if (string.IsNullOrWhiteSpace(cdi3Path) || !File.Exists(cdi3Path)) return null;

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(cdi3Path));
            var root = document.RootElement;

            var parameterNames = new Dictionary<string, string>(StringComparer.Ordinal);
            var parameterGroupIdByParameter = new Dictionary<string, string>(StringComparer.Ordinal);
            var parameterOrder = new List<string>();
            var partNames = new Dictionary<string, string>(StringComparer.Ordinal);

            if (root.TryGetProperty("Parameters", out var parameters) && parameters.ValueKind == JsonValueKind.Array)
            {
                foreach (var parameter in parameters.EnumerateArray())
                {
                    if (parameter.ValueKind != JsonValueKind.Object) continue;
                    var id = GetString(parameter, "Id");
                    if (string.IsNullOrEmpty(id)) continue;
                    parameterOrder.Add(id);

                    var name = GetString(parameter, "Name");
                    if (!string.IsNullOrEmpty(name)) parameterNames[id] = name;

                    var groupId = GetString(parameter, "GroupId");
                    if (!string.IsNullOrEmpty(groupId)) parameterGroupIdByParameter[id] = groupId;
                }
            }

            if (root.TryGetProperty("Parts", out var parts) && parts.ValueKind == JsonValueKind.Array)
            {
                foreach (var part in parts.EnumerateArray())
                {
                    if (part.ValueKind != JsonValueKind.Object) continue;
                    var id = GetString(part, "Id");
                    if (string.IsNullOrEmpty(id)) continue;
                    var name = GetString(part, "Name");
                    if (!string.IsNullOrEmpty(name)) partNames[id] = name;
                }
            }

            // 参数分组：ParameterGroups 里声明的顺序 + 组内参数按 cdi3 里出现的顺序
            var groupNameById = new Dictionary<string, string>(StringComparer.Ordinal);
            var groupOrder = new List<string>();
            if (root.TryGetProperty("ParameterGroups", out var groups) && groups.ValueKind == JsonValueKind.Array)
            {
                foreach (var group in groups.EnumerateArray())
                {
                    if (group.ValueKind != JsonValueKind.Object) continue;
                    var id = GetString(group, "Id");
                    if (string.IsNullOrEmpty(id) || groupNameById.ContainsKey(id)) continue;
                    var name = GetString(group, "Name");
                    groupNameById[id] = string.IsNullOrEmpty(name) ? id : name;
                    groupOrder.Add(id);
                }
            }

            var parameterIdsByGroup = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var ungrouped = new List<string>();
            foreach (var id in parameterOrder)
            {
                if (parameterGroupIdByParameter.TryGetValue(id, out var groupId) && groupNameById.ContainsKey(groupId))
                {
                    if (!parameterIdsByGroup.TryGetValue(groupId, out var list))
                    {
                        list = new List<string>();
                        parameterIdsByGroup[groupId] = list;
                    }

                    list.Add(id);
                }
                else
                {
                    ungrouped.Add(id);
                }
            }

            var parameterGroups = new List<ParameterGroupInfo>(groupOrder.Count + 1);
            foreach (var groupId in groupOrder)
            {
                var ids = parameterIdsByGroup.TryGetValue(groupId, out var list)
                    ? (IReadOnlyList<string>)list
                    : Array.Empty<string>();
                parameterGroups.Add(new ParameterGroupInfo
                {
                    Id = groupId,
                    Name = groupNameById[groupId],
                    ParameterIds = ids,
                });
            }

            if (ungrouped.Count > 0)
            {
                parameterGroups.Add(new ParameterGroupInfo
                {
                    Id = "",
                    Name = OtherGroupName,
                    ParameterIds = ungrouped,
                });
            }

            // cdi3 里没有部件分组（本模型只有 Parts 名字表），部件统一归「其他」
            var display = new DisplayInfo
            {
                ParameterGroups = parameterGroups,
                PartGroups = Array.Empty<PartGroupInfo>(),
                ParameterNames = parameterNames,
                PartNames = partNames,
            };
            return display;
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            log?.Invoke($"cdi3 解析失败（忽略显示名）：{exception.Message}");
            return null;
        }
    }

    /// <summary>把 moc 里的参数对象 + cdi3 名字 / 分组拼成 UI 用的绑定行。</summary>
    public static ParameterBinding[] BuildParameterBindings(CubismModel model, DisplayInfo? display)
    {
        var groupNameByParameter = new Dictionary<string, string>(StringComparer.Ordinal);
        var groupIdByParameter = new Dictionary<string, string>(StringComparer.Ordinal);
        if (display is not null)
        {
            foreach (var group in display.ParameterGroups)
            {
                foreach (var parameterId in group.ParameterIds)
                {
                    groupNameByParameter[parameterId] = group.Name;
                    groupIdByParameter[parameterId] = group.Id;
                }
            }
        }

        var bindings = new ParameterBinding[model.ParameterCount];
        for (var i = 0; i < bindings.Length; i++)
        {
            var parameter = model.Parameters[i];
            var displayName = display is not null && display.ParameterNames.TryGetValue(parameter.Id, out var name)
                ? name
                : parameter.Id;

            bindings[i] = new ParameterBinding
            {
                Parameter = parameter,
                DisplayName = displayName,
                GroupName = groupNameByParameter.GetValueOrDefault(parameter.Id, OtherGroupName),
                GroupId = groupIdByParameter.GetValueOrDefault(parameter.Id, ""),
            };
        }

        return bindings;
    }

    /// <summary>把 moc 里的部件对象 + cdi3 名字拼成 UI 用的绑定行。</summary>
    public static PartBinding[] BuildPartBindings(CubismModel model, DisplayInfo? display)
    {
        var bindings = new PartBinding[model.PartCount];
        for (var i = 0; i < bindings.Length; i++)
        {
            var part = model.Parts[i];
            var displayName = display is not null && display.PartNames.TryGetValue(part.Id, out var name)
                ? name
                : part.Id;

            bindings[i] = new PartBinding
            {
                Part = part,
                DisplayName = displayName,
                GroupName = OtherGroupName,
            };
        }

        return bindings;
    }

    private static string GetString(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";
}
