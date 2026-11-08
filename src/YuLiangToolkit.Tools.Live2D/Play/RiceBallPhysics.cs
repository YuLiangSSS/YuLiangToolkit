using System;
using System.Collections.Generic;

namespace YuLiangToolkit.Tools.Live2D.Play;

/// <summary>
/// 一颗被扔到屏幕上的大白饭团（纯数据 + 物理状态；屏幕坐标是 DIP，y 轴向下）。
/// <para>由 <see cref="PetFoodWindow"/> 持有一组，按 <see cref="RiceBallPhysics.Step"/> 每帧推进；
/// 拖拽时 <see cref="IsDragging"/> = true（物理暂停，位置跟鼠标），松手后按最近移动轨迹给初速度。</para>
/// </summary>
public sealed class RiceBallBody
{
    /// <summary>饭团种类（决定外观半径 / 形状）。</summary>
    public RiceBallKind Kind { get; init; }

    /// <summary>中心 X（DIP，相对于投喂层窗口）。</summary>
    public double X { get; set; }

    /// <summary>中心 Y（DIP，相对于投喂层窗口）。</summary>
    public double Y { get; set; }

    /// <summary>水平速度（DIP / 秒）。</summary>
    public double VelocityX { get; set; }

    /// <summary>竖直速度（DIP / 秒；向下为正）。</summary>
    public double VelocityY { get; set; }

    /// <summary>旋转角度（度；只影响观感）。</summary>
    public double RotationDegrees { get; set; }

    /// <summary>角速度（度 / 秒；按水平速度给一点滚动感）。</summary>
    public double AngularVelocity { get; set; }

    /// <summary>是否正在被鼠标拖着（拖着时不受重力、位置直接跟鼠标）。</summary>
    public bool IsDragging { get; set; }

    /// <summary>是否已经被吃掉 / 移除（窗口据此回收）。</summary>
    public bool Consumed { get; set; }

    /// <summary>视觉半径（DIP）：三角形约 26、球形 24、太鼓 27、俵形 30。</summary>
    public double Radius => RiceBallPhysics.GetRadius(Kind);

    /// <summary>复制一份（自检不想改原对象时用）。</summary>
    public RiceBallBody Clone() => new()
    {
        Kind = Kind,
        X = X,
        Y = Y,
        VelocityX = VelocityX,
        VelocityY = VelocityY,
        RotationDegrees = RotationDegrees,
        AngularVelocity = AngularVelocity,
        IsDragging = IsDragging,
        Consumed = Consumed,
    };
}

/// <summary>米饭散落粒子（吃掉饭团时炸开的小饭粒；只有视觉，不参与碰撞）。</summary>
public sealed class RiceCrumb
{
    /// <summary>X（DIP）。</summary>
    public double X { get; set; }

    /// <summary>Y（DIP）。</summary>
    public double Y { get; set; }

    /// <summary>水平速度（DIP / 秒）。</summary>
    public double VelocityX { get; set; }

    /// <summary>竖直速度（DIP / 秒）。</summary>
    public double VelocityY { get; set; }

    /// <summary>剩余寿命（秒；0 = 消失）。</summary>
    public double Life { get; set; }

    /// <summary>初始寿命（秒；用来算透明度）。</summary>
    public double MaxLife { get; set; }

    /// <summary>半径（DIP，1.2 ~ 2.6）。</summary>
    public double Size { get; set; }

    /// <summary>当前透明度（0 ~ 1；寿命后段淡出）。</summary>
    public double Opacity => MaxLife <= 0 ? 0 : Math.Clamp(Life / MaxLife, 0, 1);
}

/// <summary>饭团拖拽轨迹的一个采样点（松手时用来算抛出速度）。</summary>
public readonly record struct RiceBallDragSample(double X, double Y, double TimeSeconds);

/// <summary>
/// 饭团物理：重力 / 地面与墙壁反弹 / 空气阻力 / 拖拽抛出速度 / 嘴里命中判定 / 米饭粒子。
/// <para>全部是纯静态函数，不碰 UI；自检可以直接喂固定 dt 断言轨迹。</para>
/// </summary>
public static class RiceBallPhysics
{
    /// <summary>重力加速度（DIP / 秒²）。</summary>
    public const double Gravity = 2200.0;

    /// <summary>地面 / 墙壁反弹系数（0 = 不弹，1 = 完全反弹）。</summary>
    public const double Restitution = 0.45;

    /// <summary>落地后的水平摩擦（每秒保留多少比例）。</summary>
    public const double FloorFriction = 0.86;

    /// <summary>空气阻力（每帧保留比例；越大越接近无阻力）。</summary>
    public const double AirDrag = 0.9997;

    /// <summary>超过这个速度就夹住（防止鼠标甩太快把饭团甩出屏幕）。</summary>
    public const double MaxThrowSpeed = 2600.0;

    /// <summary>天花板反弹系数（轻弹一下，不按地面的全弹）。</summary>
    private const double CeilingRestitution = 0.3;

    /// <summary>落地后竖直速度低于它就算「不动了」，直接归零（避免贴地无限小抖动）。</summary>
    private const double RestVelocityY = 40.0;

    /// <summary>落地后水平速度低于它就算「滚不动了」，直接归零。</summary>
    private const double RestVelocityX = 10.0;

    /// <summary>角速度 = 水平速度 × 这个系数（滚动感；纯观感，不影响物理）。</summary>
    private const double AngularVelocityPerVelocityX = 0.4;

    /// <summary>形状 → 视觉半径（DIP）。</summary>
    public static double GetRadius(RiceBallKind kind) => kind switch
    {
        RiceBallKind.Triangle => 27.0,
        RiceBallKind.Sphere => 24.0,
        RiceBallKind.Taiko => 27.0,
        RiceBallKind.Bale => 30.0,
        _ => 26.0,
    };

    /// <summary>
    /// 推进一步物理：重力 → 位移 → 四边反弹（地面按 <see cref="Restitution"/> 与
    /// <see cref="FloorFriction"/>，左右墙反弹，天花板轻弹）→ 夹在区域内。拖动中的饭团不推进。
    /// </summary>
    public static void Step(RiceBallBody body, double deltaSeconds, double areaWidth, double areaHeight)
    {
        ArgumentNullException.ThrowIfNull(body);

        // 拖动中：位置由鼠标直接给，物理暂停；已经吃掉 / 移除的也不再推进
        if (body.IsDragging || body.Consumed) return;
        if (double.IsNaN(deltaSeconds) || deltaSeconds <= 0) return;

        // ① 重力 + 空气阻力（阻力按 60 帧/秒 归一化：pow(AirDrag, dt*60)，和帧率无关）
        body.VelocityY += Gravity * deltaSeconds;
        var drag = Math.Pow(AirDrag, deltaSeconds * 60.0);
        body.VelocityX *= drag;
        body.VelocityY *= drag;

        // ② 位移
        body.X += body.VelocityX * deltaSeconds;
        body.Y += body.VelocityY * deltaSeconds;

        // ③ 边界：都夹回区域内（区域比饭团还小时避免 min > max）
        var radius = body.Radius;
        var minX = radius;
        var maxX = Math.Max(minX, areaWidth - radius);
        var minY = radius;
        var maxY = Math.Max(minY, areaHeight - radius);

        // 地面：贴地 + 反弹 + 摩擦
        var onFloor = false;
        if (body.Y > maxY)
        {
            body.Y = maxY;
            if (body.VelocityY > 0)
            {
                body.VelocityY = -body.VelocityY * Restitution;
                // 摩擦常量的注释是「每秒保留比例」→ 按 dt 归一化（60fps 下约 0.86/秒）
                body.VelocityX *= Math.Pow(FloorFriction, deltaSeconds);
            }
            onFloor = true;
        }

        // 左右墙：位置夹回 + 水平速度反向
        if (body.X < minX)
        {
            body.X = minX;
            if (body.VelocityX < 0) body.VelocityX = -body.VelocityX * Restitution;
        }
        else if (body.X > maxX)
        {
            body.X = maxX;
            if (body.VelocityX > 0) body.VelocityX = -body.VelocityX * Restitution;
        }

        // 天花板：轻弹一下（0.3 倍）
        if (body.Y < minY)
        {
            body.Y = minY;
            if (body.VelocityY < 0) body.VelocityY = -body.VelocityY * CeilingRestitution;
        }

        // 速度很小就归零：贴地后不再抖 / 不再无限滑行
        if (onFloor)
        {
            if (Math.Abs(body.VelocityY) < RestVelocityY) body.VelocityY = 0;
            if (Math.Abs(body.VelocityX) < RestVelocityX) body.VelocityX = 0;
        }

        // ④ 旋转：角速度跟水平速度走（滚动感），角度再按角速度推进
        body.AngularVelocity = body.VelocityX * AngularVelocityPerVelocityX;
        body.RotationDegrees += body.AngularVelocity * deltaSeconds;
    }

    /// <summary>
    /// 从拖拽轨迹算松手速度：取最近 <paramref name="sampleCount"/> 个采样点做差分（最少 2 个点），
    /// 夹到 <see cref="MaxThrowSpeed"/>；轨迹不足时返回 (0,0)。<paramref name="nowSeconds"/> 是松手时刻。
    /// </summary>
    public static (double VelocityX, double VelocityY) ComputeThrowVelocity(
        IReadOnlyList<RiceBallDragSample> trail, double nowSeconds, int sampleCount = 5)
    {
        if (trail is null || trail.Count < 2) return (0, 0);

        // 取最后 sampleCount 个点；sampleCount 传 0 / 1 / 负数也至少用 2 个点
        var take = Math.Clamp(sampleCount, 2, trail.Count);
        var first = trail[trail.Count - take];
        var last = trail[trail.Count - 1];

        // 用「最后一点 - 窗口最早一点」的位置差 ÷ 时间差；采样时间缺失（都记 0）时用松手时刻兜底
        var span = last.TimeSeconds - first.TimeSeconds;
        if (span <= 0.0001) span = nowSeconds - first.TimeSeconds;
        if (span <= 0.0001) return (0, 0);

        var velocityX = (last.X - first.X) / span;
        var velocityY = (last.Y - first.Y) / span;
        if (double.IsNaN(velocityX) || double.IsNaN(velocityY) ||
            double.IsInfinity(velocityX) || double.IsInfinity(velocityY))
        {
            return (0, 0);
        }

        // 夹最大速度（按矢量长度夹，方向不变）
        var speed = Math.Sqrt(velocityX * velocityX + velocityY * velocityY);
        if (speed > MaxThrowSpeed && speed > 1e-9)
        {
            var scale = MaxThrowSpeed / speed;
            velocityX *= scale;
            velocityY *= scale;
        }

        return (velocityX, velocityY);
    }

    /// <summary>
    /// 判断饭团在 <paramref name="deltaSeconds"/> 内是否会经过以 (<paramref name="centerX"/>,
    /// <paramref name="centerY"/>) 为圆心、<paramref name="radius"/> 为半径的嘴部圆（线段与圆求交）；
    /// 命中返回 true 并把最近点的参数 t（0~1，线段上的投影位置）给 <paramref name="hitT"/>。
    /// </summary>
    public static bool TryHitMouth(
        double startX, double startY, double velocityX, double velocityY,
        double deltaSeconds, double areaHeight,
        double centerX, double centerY, double radius,
        out double hitT)
    {
        hitT = 0;
        if (double.IsNaN(deltaSeconds) || deltaSeconds <= 0) return false;
        if (double.IsNaN(radius) || double.IsInfinity(radius) || radius <= 0) return false;

        // 这一帧的相对位移：水平按直线，竖直用 vy*dt + 0.5*g*dt²（二次近似，下坠更准）
        var endX = startX + velocityX * deltaSeconds;
        var endY = startY + velocityY * deltaSeconds + 0.5 * Gravity * deltaSeconds * deltaSeconds;
        if (double.IsNaN(endX) || double.IsNaN(endY)) return false;

        // 地面先挡住：这一帧掉不到地面以下（嘴在地面上方时才有意义）
        if (areaHeight > 0 && endY > areaHeight) endY = areaHeight;

        var deltaX = endX - startX;
        var deltaY = endY - startY;
        var lengthSquared = deltaX * deltaX + deltaY * deltaY;

        if (lengthSquared <= 1e-9)
        {
            // 这一帧没动：起点已经在嘴里也算命中（t = 0）
            var standX = startX - centerX;
            var standY = startY - centerY;
            if (standX * standX + standY * standY <= radius * radius)
            {
                hitT = 0;
                return true;
            }
            return false;
        }

        // 圆心在运动线段上的投影（夹到 0~1），最近点距离 ≤ 半径 = 线段与圆相交
        var projection = ((centerX - startX) * deltaX + (centerY - startY) * deltaY) / lengthSquared;
        var t = Math.Clamp(projection, 0, 1);
        var closestX = startX + deltaX * t;
        var closestY = startY + deltaY * t;
        var offsetX = closestX - centerX;
        var offsetY = closestY - centerY;
        if (offsetX * offsetX + offsetY * offsetY > radius * radius) return false;

        hitT = t;
        return true;
    }

    /// <summary>吃掉饭团时炸出一把米粒（约 <paramref name="count"/> 颗，向上散开）。</summary>
    public static void SpawnCrumbs(List<RiceCrumb> target, double centerX, double centerY, int count, Random? random = null)
    {
        if (target is null || count <= 0) return;
        random ??= Random.Shared;

        for (var i = 0; i < count; i++)
        {
            // 角度取「偏上」的扇形：-160° ~ -20°（屏幕坐标 y 向下，负角 = 朝上）
            var angle = (-160.0 + random.NextDouble() * 140.0) * (Math.PI / 180.0);
            var speed = 120.0 + random.NextDouble() * 300.0;   // 120 ~ 420 DIP/秒
            var maxLife = 0.5 + random.NextDouble() * 0.4;     // 0.5 ~ 0.9 秒
            target.Add(new RiceCrumb
            {
                X = centerX,
                Y = centerY,
                VelocityX = Math.Cos(angle) * speed,
                VelocityY = Math.Sin(angle) * speed,
                Life = maxLife,
                MaxLife = maxLife,
                Size = 1.2 + random.NextDouble() * 1.4,        // 1.2 ~ 2.6 DIP
            });
        }
    }

    /// <summary>推进米粒（重力 + 寿命衰减），死的从列表里移除。</summary>
    public static void StepCrumbs(List<RiceCrumb> crumbs, double deltaSeconds)
    {
        if (crumbs is null || crumbs.Count == 0) return;
        if (double.IsNaN(deltaSeconds) || deltaSeconds <= 0) return;

        // 倒序遍历：移除后面的元素不会影响还没处理的更小下标
        for (var i = crumbs.Count - 1; i >= 0; i--)
        {
            var crumb = crumbs[i];
            crumb.VelocityY += Gravity * 0.6 * deltaSeconds; // 米粒很轻，重力只用 0.6 倍
            crumb.X += crumb.VelocityX * deltaSeconds;
            crumb.Y += crumb.VelocityY * deltaSeconds;
            crumb.Life -= deltaSeconds;
            if (crumb.Life <= 0) crumbs.RemoveAt(i);
        }
    }
}
