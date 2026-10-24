// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.
//
// 来源：HeavenUsurperToolkit-master/Utils.cs（ppy/osu!lazer 的数学工具移植版；旧文件保留了 ppy 的 MIT 版权头），
// 逐行照搬到本项目，只改了命名空间 / 类名。不要“优化”这里的数学与顺序。

using System;

namespace YuLiangToolkit.Tools.OsuMania.Core.Difficulty;

/// <summary>
/// 星数计算用到的 8 个数学工具函数（二分查找、累积和、平滑、插值、缩放、找同列下一个音符）。
///
/// <para>来源：ppy/osu!lazer（MIT），经 HeavenUsurperToolkit 移植。逐行照搬，行为不能改。</para>
/// </summary>
public static class Utils
{
    /// <summary>二分查找左边界：找不到时返回“应该插入的位置”（等价 C++ lower_bound）。</summary>
    public static int SearchSortedLeft(int[] array, int value)
    {
        int index = Array.BinarySearch(array, value);
        return index >= 0 ? index : ~index;
    }

    /// <summary>二分查找右边界：等于 value 的最后一个下标 + 1（等价 C++ upper_bound）。</summary>
    public static int SearchSortedRight(int[] array, int value)
    {
        int index = Array.BinarySearch(array, value);
        if (index >= 0)
        {
            while (index < array.Length - 1 && array[index + 1] == value)
            {
                index++;
            }
            return index + 1;
        }
        return ~index;
    }

    /// <summary>分段常量函数的累积和（梯形法，段内 f 取左端点的值）。</summary>
    public static double[] CumulativeSum(int[] x, double[] f)
    {
        double[] F = new double[x.Length];
        for (int i = 1; i < x.Length; i++)
        {
            F[i] = F[i - 1] + f[i - 1] * (x[i] - x[i - 1]);
        }
        return F;
    }

    /// <summary>查询分段常量函数在 q 处的累积和。</summary>
    public static double QueryCumsum(int q, int[] x, double[] F, double[] f)
    {
        if (q <= x[0]) return 0.0;
        if (q >= x[^1]) return F[^1];

        int i = SearchSortedLeft(x, q) - 1;
        return F[i] + f[i] * (q - x[i]);
    }

    /// <summary>在拐角处做窗口平滑：<paramref name="mode"/> = "sum" 累加、="avg" 取平均。</summary>
    public static double[] SmoothOnCorners(int[] x, double[] f, int window, double scale = 1.0, string mode = "sum")
    {
        double[] F = CumulativeSum(x, f);
        double[] g = new double[f.Length];

        for (int i = 0; i < x.Length; i++)
        {
            int s = x[i];
            int a = Math.Max(s - window, x[0]);
            int b = Math.Min(s + window, x[^1]);
            double val = QueryCumsum(b, x, F, f) - QueryCumsum(a, x, F, f);

            if (mode == "avg")
            {
                g[i] = (b - a) > 0 ? val / (b - a) : 0.0;
            }
            else
            {
                g[i] = scale * val;
            }
        }

        return g;
    }

    /// <summary>线性插值：把 oldX/oldVals 上的值插到 newX 上。</summary>
    public static double[] InterpValues(int[] newX, int[] oldX, double[] oldVals)
    {
        double[] newVals = new double[newX.Length];
        for (int i = 0; i < newX.Length; i++)
        {
            int idx = SearchSortedLeft(oldX, newX[i]);
            if (idx == 0)
            {
                newVals[i] = oldVals[0];
            }
            else if (idx >= oldX.Length)
            {
                newVals[i] = oldVals[^1];
            }
            else
            {
                double t = (double)(newX[i] - oldX[idx - 1]) / (oldX[idx] - oldX[idx - 1]);
                newVals[i] = oldVals[idx - 1] + t * (oldVals[idx] - oldVals[idx - 1]);
            }
        }
        return newVals;
    }

    /// <summary>阶梯插值：newX 上的每个点取 oldX 中最后一个 &lt;= 它的值。</summary>
    public static double[] StepInterp(int[] newX, int[] oldX, double[] oldVals)
    {
        double[] newVals = new double[newX.Length];
        for (int i = 0; i < newX.Length; i++)
        {
            int idx = SearchSortedRight(oldX, newX[i]) - 1;
            idx = Math.Clamp(idx, 0, oldVals.Length - 1);
            newVals[i] = oldVals[idx];
        }
        return newVals;
    }

    /// <summary>高星段缩放：9 星以上按 1/1.2 压缩。</summary>
    public static double RescaleHigh(double sr)
    {
        if (sr <= 9) return sr;
        return 9 + (sr - 9) * (1 / 1.2);
    }

    /// <summary>找同一列里的下一个音符；没有就返回 (0, int.MaxValue, int.MaxValue)。</summary>
    public static EasyObject FindNextNoteInColumn(EasyObject note, int[] times, EasyObject[][] noteSeqByColumn)
    {
        int idx = Array.BinarySearch(times, note.Head);
        if (idx < 0) idx = ~idx;

        return (idx + 1 < noteSeqByColumn[note.Key].Length) ?
            noteSeqByColumn[note.Key][idx + 1] :
            new EasyObject(0, int.MaxValue, int.MaxValue);
    }
}
