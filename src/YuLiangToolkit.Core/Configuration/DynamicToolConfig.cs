using System.Text.Json;
using System.Text.Json.Nodes;
using YuLiangToolkit.Core.Mvvm;
using YuLiangToolkit.Core.Storage;

namespace YuLiangToolkit.Core.Configuration;

/// <summary>
/// 动态键值配置：<b>不声明属性也能持久化选项</b>，文件为
/// <c>Config\&lt;功能名&gt;\options.json</c>，内容是扁平的键值对。
///
/// <para><b>什么时候用</b>：原型阶段 / 选项零散不想建配置类 / 需要运行时才知道名字的开关。
/// 选项固定且成体系的功能，请优先用强类型 <see cref="ToolConfig"/>（有编译期检查、可读性更好）。</para>
///
/// <para><b>XAML 直接绑定索引器</b>（值一改就防抖写盘）：</para>
/// <code>
/// &lt;CheckBox IsChecked="{Binding Dyn[autoRename]}" /&gt;
/// &lt;TextBox  Text="{Binding Dyn[lastUrl], UpdateSourceTrigger=PropertyChanged}" /&gt;
/// </code>
///
/// <para><b>代码读写</b>：</para>
/// <code>
/// var dyn = DynamicToolConfig.Load("flash-downloader");
/// dyn.SetValue("timeout", 30);
/// int t = dyn.GetInt("timeout", 10);
/// bool ok = dyn.GetBool("autoRename");
/// </code>
/// </summary>
public sealed class DynamicToolConfig : ObservableObject
{
    private readonly ToolConfigStore _store;
    private readonly Dictionary<string, JsonNode?> _values;
    private readonly object _gate = new();

    private DynamicToolConfig(ToolConfigStore store, Dictionary<string, JsonNode?> values)
    {
        _store = store;
        _values = values;
        _store.TrackModel(this, Snapshot);
    }

    /// <summary>加载（或新建）某功能的动态配置。</summary>
    /// <param name="toolId">功能 Id，同时是 Config 下的小文件夹名。</param>
    /// <param name="fileName">配置文件名，默认 options.json。</param>
    public static DynamicToolConfig Load(string toolId, string fileName = "options.json")
    {
        var store = ToolConfigStore.For(toolId, fileName);
        var values = JsonFileStore.Read<Dictionary<string, JsonNode?>>(store.FilePath)
                     ?? new Dictionary<string, JsonNode?>(StringComparer.OrdinalIgnoreCase);
        return new DynamicToolConfig(store, values);
    }

    /// <summary>
    /// 带默认值的加载：<paramref name="defaults"/> 里声明过、但文件里还不存在的键，
    /// 用默认值补齐并立刻落盘。推荐这种写法，原因有三：
    /// <list type="number">
    ///   <item>配置文件一生成就能看出这个功能有哪些动态选项（自带文档）；</item>
    ///   <item>绑 CheckBox 时不会因为“键不存在 → 读取返回 null”导致<b>第一次点击看起来没反应</b>
    ///         （WPF 里 IsChecked 为 null 时点一下会变成 false）；</item>
    ///   <item>读取端拿到的一定是对的类型（bool/string/int…），不用到处写回退值。</item>
    /// </list>
    /// </summary>
    /// <example>
    /// <code>
    /// Dyn = DynamicToolConfig.LoadWithDefaults("flash-downloader", new Dictionary&lt;string, object?&gt;
    /// {
    ///     ["autoRename"] = true,
    ///     ["lastUrl"] = "",
    /// });
    /// </code>
    /// </example>
    public static DynamicToolConfig LoadWithDefaults(
        string toolId,
        IReadOnlyDictionary<string, object?> defaults,
        string fileName = "options.json")
    {
        var config = Load(toolId, fileName);
        var seeded = false;
        foreach (var (key, value) in defaults)
        {
            if (config.Contains(key)) continue;
            config.SetValue(key, value);
            seeded = true;
        }
        if (seeded) config.SaveNow();
        return config;
    }

    /// <summary>所属存储（可直接拿 <c>FilePath</c> 展示给用户）。</summary>
    public ToolConfigStore Store => _store;

    /// <summary>配置文件路径。</summary>
    public string FilePath => _store.FilePath;

    /// <summary>所有键（快照）。</summary>
    public IReadOnlyList<string> Keys
    {
        get { lock (_gate) return _values.Keys.ToList(); }
    }

    /// <summary>
    /// 索引器：XAML 里写 <c>{Binding Dyn[someKey]}</c> 即可双向绑定。
    /// 读取时自动把 JSON 值还原成 bool / long / double / string（容器类型返回 JsonElement）。
    /// </summary>
    public object? this[string key]
    {
        get
        {
            lock (_gate)
                return _values.TryGetValue(key, out var node) ? ToClr(node) : null;
        }
        set => SetValue(key, value);
    }

    /// <summary>读取强类型值；键不存在或类型不符时返回 <paramref name="fallback"/>。</summary>
    public T Get<T>(string key, T fallback = default!)
    {
        lock (_gate)
        {
            if (!_values.TryGetValue(key, out var node) || node is null) return fallback;
            try { return node.Deserialize<T>(JsonFileStore.Options) ?? fallback; }
            catch { return fallback; }
        }
    }

    /// <summary>写入一个值（传 null 等于删除该键）。写完后自动防抖保存。</summary>
    public void SetValue<T>(string key, T value)
    {
        if (string.IsNullOrWhiteSpace(key)) return;
        lock (_gate)
        {
            if (value is null) _values.Remove(key);
            else _values[key] = JsonSerializer.SerializeToNode(value, JsonFileStore.Options);
        }
        OnPropertyChanged("Item[]"); // 让所有索引器绑定刷新
        OnPropertyChanged(key);
    }

    public bool GetBool(string key, bool fallback = false) => Get(key, fallback);

    public string GetString(string key, string fallback = "") => Get(key, fallback);

    public int GetInt(string key, int fallback = 0) => Get(key, fallback);

    public long GetLong(string key, long fallback = 0) => Get(key, fallback);

    public double GetDouble(string key, double fallback = 0) => Get(key, fallback);

    /// <summary>按键取值，等价于 <see cref="GetInt"/> 等具名方法（值类型不符时返回 fallback）。</summary>
    public bool TryGet<T>(string key, out T value)
    {
        lock (_gate)
        {
            if (_values.TryGetValue(key, out var node) && node is not null)
            {
                try
                {
                    var parsed = node.Deserialize<T>(JsonFileStore.Options);
                    if (parsed is not null) { value = parsed; return true; }
                }
                catch { /* 类型不符，走 fallback */ }
            }
        }
        value = default!;
        return false;
    }

    public bool Contains(string key)
    {
        lock (_gate) return _values.ContainsKey(key);
    }

    public bool Remove(string key)
    {
        bool removed;
        lock (_gate) removed = _values.Remove(key);
        if (removed)
        {
            OnPropertyChanged("Item[]");
            OnPropertyChanged(key);
        }
        return removed;
    }

    public void Clear()
    {
        lock (_gate) _values.Clear();
        OnPropertyChanged("Item[]");
    }

    /// <summary>当前全部键值的 JSON 文本（界面预览 / 排错）。</summary>
    public string ToJson() => JsonFileStore.ToJson(Snapshot());

    /// <summary>立刻落盘（正常无需手动调用）。</summary>
    public void SaveNow() => _store.SaveNow();

    /// <summary>写盘用的快照：复制一份字典，避免序列化时字典正被修改。</summary>
    private object Snapshot()
    {
        lock (_gate) return new Dictionary<string, JsonNode?>(_values, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>JSON 节点 → 普通 CLR 值（bool / long / double / decimal / string / JsonElement）。</summary>
    private static object? ToClr(JsonNode? node)
    {
        if (node is null) return null;
        if (node is JsonArray or JsonObject) return node.Deserialize<JsonElement>(JsonFileStore.Options);
        if (node is not JsonValue value) return null;

        if (value.TryGetValue<bool>(out var b)) return b;
        if (value.TryGetValue<string>(out var s)) return s;
        if (value.TryGetValue<long>(out var l)) return l;
        if (value.TryGetValue<double>(out var d)) return d;
        if (value.TryGetValue<decimal>(out var m)) return m;
        return value.ToJsonString();
    }
}
