using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using YuLiangToolkit.Core.Mvvm;

namespace YuLiangToolkit.Core.Notifications;

/// <summary>
/// 发出过的一条通知的历史记录。
///
/// <para><b>为什么在 Core</b>：任何工具调 <see cref="NotificationCenter.Show"/> 都应该自动进历史，
/// 而工具们只认识 Core。记录里带 <see cref="SourceId"/> / <see cref="SourceName"/>，
/// 「通知管理」工具据此分组 —— 以后新加的工具只要发通知，历史里自动多一个来源，
/// **不需要改通知管理工具的代码**。</para>
/// </summary>
/// <param name="Id">唯一 Id（删除 / 定位用）。</param>
/// <param name="TimestampUtc">发出的时刻（UTC，显示时按本机时区换算）。</param>
/// <param name="Title">标题。</param>
/// <param name="Body">正文。</param>
/// <param name="Kind">语义类型。</param>
/// <param name="SourceId">哪个工具发的（<c>ITool.Id</c>；外壳自己发的用 <c>shell</c>）。</param>
/// <param name="SourceName">来源显示名（「秒表」「快捷键」…；空则用 Id）。</param>
/// <param name="DeliveredVia">实际走的是哪条通道（应用内 / Windows）。</param>
/// <param name="WasFallback">是不是因为首选通道不可用而换了通道。</param>
public sealed record NotificationRecord(
    Guid Id,
    DateTime TimestampUtc,
    string Title,
    string Body,
    NotificationKind Kind,
    string SourceId,
    string SourceName,
    NotificationMode DeliveredVia,
    bool WasFallback = false)
{
    /// <summary>本机时间（显示用）。</summary>
    [JsonIgnore]
    public DateTime TimestampLocal => TimestampUtc.ToLocalTime();

    /// <summary>
    /// 时间显示：今天只写 <c>14:05</c>；昨天写 <c>昨天 14:05</c>；更早写 <c>11月12日 14:05</c>；
    /// 跨年再带上年份。
    /// </summary>
    /// <param name="nowLocal">「现在」的本机时间（自检可传固定值）。</param>
    public string DescribeTime(DateTime nowLocal)
    {
        var local = TimestampLocal;
        var time = local.ToString("HH:mm");

        var days = (nowLocal.Date - local.Date).Days;
        return days switch
        {
            0 => time,
            1 => "昨天 " + time,
            _ when local.Year == nowLocal.Year => $"{local.Month}月{local.Day}日 {time}",
            _ => $"{local.Year}年{local.Month}月{local.Day}日 {time}",
        };
    }

    /// <summary>来源显示名（空则退回 Id）。</summary>
    [JsonIgnore]
    public string DisplaySource => string.IsNullOrWhiteSpace(SourceName) ? SourceId : SourceName;

    /// <summary>送达通道的中文写法（界面直接显示，别把枚举名露给用户）。</summary>
    [JsonIgnore]
    public string DeliveredViaText => DeliveredVia == NotificationMode.InApp
        ? (WasFallback ? "应用内（退回）" : "应用内")
        : (WasFallback ? "Windows（退回）" : "Windows");

    /// <summary>正文的一行摘要（换行压成空格，超长截断）。</summary>
    public string DescribeBody(int maxLength = 160)
    {
        var text = (Body ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        return text.Length <= maxLength ? text : text[..maxLength] + "…";
    }
}

/// <summary>
/// 通知历史：所有工具发过的通知按时间倒序存一份，可删单条 / 清空 / 按来源与类型筛选。
///
/// <para><b>持久化</b>：默认存 <c>Config\notification-history\history.json</c>，
/// 重启后还在；「记住历史」关掉后只存在内存里（并且不再写盘）。</para>
///
/// <para><b>上限</b>：超出上限时**丢最旧的**（保留最新的）。</para>
/// </summary>
public sealed class NotificationHistory : ObservableObject
{
    /// <summary>默认最多保留多少条。</summary>
    public const int DefaultCapacity = 500;

    /// <summary>开关 / 上限变化时触发（通知管理工具据此写配置）。</summary>
    public event EventHandler? SettingsChanged;

    /// <summary>历史条目变化（新增 / 删除 / 清空）时触发。</summary>
    public event EventHandler? Changed;

    private bool _enabled = true;
    private int _capacity = DefaultCapacity;

    /// <summary>全部记录（最新在前）。</summary>
    public ObservableCollection<NotificationRecord> Items { get; } = new();

    /// <summary>要不要记录（关掉 = 不再往里加，已有记录保留）。</summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (!Set(ref _enabled, value)) return;
            SettingsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>最多保留多少条（100~5000；超出丢最旧的）。</summary>
    public int Capacity
    {
        get => _capacity;
        set
        {
            var clamped = Math.Clamp(value, 100, 5000);
            if (!Set(ref _capacity, clamped)) return;
            TrimToCapacity();
            SettingsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>记录一条（<see cref="Enabled"/> 为真时）。</summary>
    public void Add(NotificationRecord record)
    {
        if (record is null || !Enabled) return;

        Items.Insert(0, record);
        TrimToCapacity();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>删掉一条（没找到也安全）。</summary>
    public bool Remove(Guid id)
    {
        for (var i = 0; i < Items.Count; i++)
        {
            if (Items[i].Id != id) continue;
            Items.RemoveAt(i);
            Changed?.Invoke(this, EventArgs.Empty);
            return true;
        }

        return false;
    }

    /// <summary>清空全部（返回清掉几条）。</summary>
    public int Clear()
    {
        var count = Items.Count;
        if (count == 0) return 0;

        Items.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
        return count;
    }

    /// <summary>超出上限时丢最旧的。</summary>
    private void TrimToCapacity()
    {
        while (Items.Count > Capacity) Items.RemoveAt(Items.Count - 1);
    }

    /// <summary>历史里出现过的来源（去重、按出现顺序），给筛选下拉框用。</summary>
    public IReadOnlyList<(string Id, string Name)> Sources()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var list = new List<(string, string)>();

        foreach (var item in Items)
        {
            if (!seen.Add(item.SourceId)) continue;
            list.Add((item.SourceId, item.DisplaySource));
        }

        return list;
    }

    /// <summary>整批替换（加载持久化文件时用，不触发一条条 Changed）。</summary>
    public void ReplaceAll(IEnumerable<NotificationRecord> records)
    {
        Items.Clear();
        foreach (var record in records) Items.Add(record);

        TrimToCapacity();
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
