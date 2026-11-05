namespace YuLiangToolkit.Core.Expressions;

/// <summary>
/// 三角函数的角度单位：决定 sin / cos / tan 等「输入」和 asin / acos / atan 等「输出」按哪个单位解释。
/// <para>计算器的「角度制 / 弧度制」开关、函数绘图的全局角度设置都映射到它。</para>
/// </summary>
public enum AngleUnit
{
    /// <summary>弧度（数学默认；sin(π/2) = 1）。</summary>
    Radians = 0,

    /// <summary>角度（一圈 360°；sin(90) = 1）。</summary>
    Degrees = 1,
}
