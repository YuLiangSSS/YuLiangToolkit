using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace YuLiangToolkit.Tools.OsuMania.Core;

/// <summary>
/// 转谱算法共用的随机数 / 时间取整 / 随机抽样工具（早期版本 <c>Helper.cs</c> 里被用到的部分，逐行改写）。
/// </summary>
internal static class OsuTransformUtils
{
    /// <summary>
    /// Fix Error 反算节拍时依次尝试的等分数（早期版本 <c>Helper.DIVIDE_NUMBER</c>，顺序不能变）。
    /// </summary>
    public static readonly int[] DivideNumber = { 2, 4, 8, 3, 6, 9, 5, 7, 12, 16, 48, 35, 64 };

    /// <summary>Fix Error 允许的时间误差（早期版本 <c>Helper.ERROR</c>）。</summary>
    public const double Error = 2.0;

    /// <summary>
    /// 由种子文本造随机数生成器（早期版本两处 <c>ApplyToBeatmap</c> 里同一段逻辑）：
    /// 种子文本为空 → <c>Guid.NewGuid().GetHashCode()</c>（每次不同）；
    /// 否则 SHA256(UTF8(种子文本)) 的前 4 字节按小端转 int 当种子（固定种子可复现）。
    /// </summary>
    public static Random CreateRandom(string? seedText)
    {
        int seed;
        if (string.IsNullOrEmpty(seedText))
        {
            seed = Guid.NewGuid().GetHashCode();
        }
        else
        {
            byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(seedText));
            seed = BitConverter.ToInt32(bytes, 0);
        }

        return new Random(seed);
    }

    /// <summary>
    /// 把时间反算到最近的 “1/(bpm/divide)” 网格上（早期版本 <c>Helper.PreciseTime</c> 的逐行改写）。
    /// 依次尝试 <see cref="DivideNumber"/> 里的等分数，误差小于 <see cref="Error"/> 就采用。
    /// </summary>
    /// <param name="time">要修正的时间（毫秒）。</param>
    /// <param name="bpm">节拍长度（毫秒，即 BeatLength）。</param>
    /// <param name="offset">网格原点（早期版本传时间点的 Time / 第一根红线的 Time）。</param>
    public static double PreciseTime(double time, double bpm, double offset)
    {
        foreach (int t in DivideNumber)
        {
            double tem = time;
            time = offset + Math.Round((time - offset) / (bpm / t)) * bpm / t;
            if (Math.Abs(time - tem) < Error)
                return time;
            else
                time = tem;
        }
        return time;
    }

    /// <summary>
    /// 随机选择元素（早期版本 <c>Helper.SelectRandom</c> 的逐行改写；注意 RNG 调用顺序与删除元素的顺序都照搬，
    /// 换写法会让固定种子的结果变掉）。
    /// </summary>
    /// <param name="enumerable">候选集合。</param>
    /// <param name="rng">随机数生成器。</param>
    /// <param name="times">选几个。</param>
    /// <param name="duplicate">是否允许重复（false 时选不满会抛异常，与早期版本一致）。</param>
    public static IEnumerable<T> SelectRandom<T>(this IEnumerable<T> enumerable, Random rng, int times = 1, bool duplicate = false)
    {
        if (times <= 0)
        {
            return [];
        }

        var result = new List<T>();
        var list = enumerable.ToList();

        if (duplicate)
        {
            while (times > 0)
            {
                int index = rng.Next(list.Count);
                result.Add(list[index]);
                times--;
            }
        }
        else
        {
            if (times > enumerable.Count())
            {
                throw new InvalidOperationException("Cannot select more items than the count of the enumerable.");
            }
            while (times > 0)
            {
                int index = rng.Next(list.Count);
                result.Add(list[index]);
                list.RemoveAt(index);
                times--;
            }
        }
        return result.AsEnumerable();
    }
}
