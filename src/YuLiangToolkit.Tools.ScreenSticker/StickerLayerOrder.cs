namespace YuLiangToolkit.Tools.ScreenSticker;

/// <summary>
/// 贴纸层级（谁在上面）的纯列表操作：配置里 <c>Stickers</c> 的顺序就是层级，
/// <b>下标 0 最靠下、最后一个最靠上</b>。窗口只负责按这个顺序重新排一遍 z-order。
///
/// <para>抽成纯函数是为了自检能直接断言（不创建窗口），见 <c>ScreenStickerChecks</c>。</para>
/// </summary>
public static class StickerLayerOrder
{
    /// <summary>按对象身份找下标（找不到返回 -1）。</summary>
    public static int IndexOf(IList<StickerEntry> list, StickerEntry entry)
    {
        for (var i = 0; i < list.Count; i++)
        {
            if (ReferenceEquals(list[i], entry)) return i;
        }
        return -1;
    }

    /// <summary>上移一层（往上 = 更靠前 = 下标 + 1）；已经在最上层返回 false。</summary>
    public static bool MoveUp(IList<StickerEntry> list, StickerEntry entry)
    {
        var index = IndexOf(list, entry);
        if (index < 0 || index >= list.Count - 1) return false;
        (list[index], list[index + 1]) = (list[index + 1], list[index]);
        return true;
    }

    /// <summary>下移一层（往下 = 更靠后 = 下标 - 1）；已经在最下层返回 false。</summary>
    public static bool MoveDown(IList<StickerEntry> list, StickerEntry entry)
    {
        var index = IndexOf(list, entry);
        if (index <= 0) return false;
        (list[index], list[index - 1]) = (list[index - 1], list[index]);
        return true;
    }

    /// <summary>置于最上层；已经是最上层返回 false。</summary>
    public static bool MoveToTop(IList<StickerEntry> list, StickerEntry entry)
    {
        var index = IndexOf(list, entry);
        if (index < 0 || index == list.Count - 1) return false;
        list.RemoveAt(index);
        list.Add(entry);
        return true;
    }

    /// <summary>置于最下层；已经是最下层返回 false。</summary>
    public static bool MoveToBottom(IList<StickerEntry> list, StickerEntry entry)
    {
        var index = IndexOf(list, entry);
        if (index <= 0) return false;
        list.RemoveAt(index);
        list.Insert(0, entry);
        return true;
    }
}
