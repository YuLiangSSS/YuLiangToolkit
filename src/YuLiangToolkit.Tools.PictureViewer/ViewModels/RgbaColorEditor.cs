using YuLiangToolkit.Core.Mvvm;

namespace YuLiangToolkit.Tools.PictureViewer.ViewModels;

/// <summary>
/// 一个 RGBA（0~255）颜色编辑器：设置面板里「R / G / B / A 四个数字框 + 调色板 + 预览色块」共用。
/// 数字超出范围会夹住；每次改动抛出 <see cref="Changed"/>，ViewModel 据此写回配置并通知界面重排。
/// </summary>
public sealed class RgbaColorEditor : ObservableObject
{
    private int _r;
    private int _g;
    private int _b;
    private int _a = 255;

    /// <summary>颜色变了（用户拖 / 打字 / 点调色板都算）。</summary>
    public event EventHandler? Changed;

    public RgbaColorEditor(string? initialHex) => Load(initialHex, raiseChanged: false);

    /// <summary>当前颜色（<c>#AARRGGBB</c>）。</summary>
    public string Hex => PictureBorderStyle.FromRgba(_r, _g, _b, _a);

    public int R
    {
        get => _r;
        set => SetChannel(ref _r, value, nameof(R));
    }

    public int G
    {
        get => _g;
        set => SetChannel(ref _g, value, nameof(G));
    }

    public int B
    {
        get => _b;
        set => SetChannel(ref _b, value, nameof(B));
    }

    public int A
    {
        get => _a;
        set => SetChannel(ref _a, value, nameof(A));
    }

    /// <summary>从颜色字符串重新加载（解析失败就保持当前值）。</summary>
    public void Load(string? hex, bool raiseChanged = true) => Load(hex, fallback: null, raiseChanged);

    /// <summary>从颜色字符串重新加载；解析失败时用 <paramref name="fallback"/>。</summary>
    public void Load(string? hex, string? fallback, bool raiseChanged = true)
    {
        if (!PictureBorderStyle.TryParseArgb(hex, out var argb))
        {
            if (string.IsNullOrWhiteSpace(fallback) || !PictureBorderStyle.TryParseArgb(fallback, out argb)) return;
        }

        var (r, g, b, a) = PictureBorderStyle.Split(argb);
        var changed = r != _r || g != _g || b != _b || a != _a;
        _r = r;
        _g = g;
        _b = b;
        _a = a;
        OnPropertyChanged(nameof(R));
        OnPropertyChanged(nameof(G));
        OnPropertyChanged(nameof(B));
        OnPropertyChanged(nameof(A));
        OnPropertyChanged(nameof(Hex));

        if (raiseChanged && changed) Changed?.Invoke(this, EventArgs.Empty);
    }

    private void SetChannel(ref int field, int value, string propertyName)
    {
        var clamped = Math.Clamp(value, 0, 255);
        if (field == clamped) return;

        field = clamped;
        OnPropertyChanged(propertyName);
        OnPropertyChanged(nameof(Hex));
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
