using System.Windows;
using System.Windows.Media;

namespace YuLiangToolkit.Tools.ScreenPen;

/// <summary>透明覆盖层里的批注画布；只负责保存模型和重绘，不处理鼠标。</summary>
public sealed class ScreenPenCanvas : FrameworkElement
{
    private readonly List<ScreenPenAnnotation> _items = new();
    private readonly Stack<ScreenPenAnnotation> _redo = new();
    private ScreenPenAnnotation? _current;

    public IReadOnlyList<ScreenPenAnnotation> Items => _items;
    public bool CanUndo => _items.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public string Backdrop { get; private set; } = "none";

    public void SetBackdrop(string backdrop)
    {
        Backdrop = backdrop;
        InvalidateVisual();
    }

    public void Begin(ScreenPenAnnotation annotation)
    {
        _current = annotation;
        _redo.Clear();
        InvalidateVisual();
    }

    public void AddPoint(Point point)
    {
        if (_current is null) return;
        _current.Points.Add(point);
        InvalidateVisual();
    }

    public void Complete()
    {
        if (_current is null) return;
        if (_current.Points.Count > 0)
        {
            _items.Add(_current);
            _redo.Clear();
        }
        _current = null;
        InvalidateVisual();
    }

    public void Cancel()
    {
        _current = null;
        InvalidateVisual();
    }

    public bool Undo()
    {
        if (_items.Count == 0) return false;
        var last = _items[^1];
        _items.RemoveAt(_items.Count - 1);
        _redo.Push(last);
        InvalidateVisual();
        return true;
    }

    public bool Redo()
    {
        if (_redo.Count == 0) return false;
        _items.Add(_redo.Pop());
        InvalidateVisual();
        return true;
    }

    public void Clear()
    {
        _items.Clear();
        _redo.Clear();
        _current = null;
        InvalidateVisual();
    }

    public bool EraseAt(Point point)
    {
        for (var i = _items.Count - 1; i >= 0; i--)
        {
            if (!HitTest(_items[i], point)) continue;
            _items.RemoveAt(i);
            InvalidateVisual();
            return true;
        }
        return false;
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        if (Backdrop == "whiteboard")
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(245, 248, 248, 248)), null, new Rect(RenderSize));
        else if (Backdrop == "blackboard")
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(245, 12, 16, 24)), null, new Rect(RenderSize));

        foreach (var item in _items) item.Draw(dc);
        _current?.Draw(dc);
    }

    private static bool HitTest(ScreenPenAnnotation item, Point point)
    {
        if (item.Points.Count == 0) return false;
        var tolerance = Math.Max(6, item.Thickness * 1.8);

        if (item.Tool == ScreenPenToolKind.Text)
            return new Rect(item.Points[0], new Size(120, 40)).Contains(point);

        if (item.Points.Count == 1)
            return (item.Points[0] - point).Length <= tolerance;

        for (var i = 1; i < item.Points.Count; i++)
        {
            if (DistanceToSegment(point, item.Points[i - 1], item.Points[i]) <= tolerance) return true;
        }

        if (item.Tool is ScreenPenToolKind.Rectangle or ScreenPenToolKind.Ellipse &&
            item.Points.Count >= 2)
        {
            var a = item.Points[0];
            var b = item.Points[^1];
            var rect = new Rect(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y),
                Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
            return rect.Contains(point);
        }

        return false;
    }

    private static double DistanceToSegment(Point p, Point a, Point b)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        if (Math.Abs(dx) < 0.001 && Math.Abs(dy) < 0.001) return (p - a).Length;
        var t = ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / (dx * dx + dy * dy);
        t = Math.Clamp(t, 0, 1);
        return (p - new Point(a.X + t * dx, a.Y + t * dy)).Length;
    }
}
