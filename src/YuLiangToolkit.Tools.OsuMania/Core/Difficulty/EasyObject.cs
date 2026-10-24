// 来源：HeavenUsurperToolkit-master/StarRating.cs 底部的 EasyObject（该文件是第三方
// “Star Rating Rebirth / srcalc” 思路的 C# 重写，不是 osu!lazer 源码直拷；数学工具来自 ppy osu!lazer，MIT）。
// 本项目逐行照搬，只改了命名空间（并把 List<ManiaHitObject> 换成 IReadOnlyList<ManiaNote>）。

using System;
using System.Collections.Generic;
using YuLiangToolkit.Tools.OsuMania.Core;

namespace YuLiangToolkit.Tools.OsuMania.Core.Difficulty;

/// <summary>
/// 星数算法内部使用的简化音符（只保留头 / 尾 / 列）。
/// 来源：移植自 HeavenUsurperToolkit 的 StarRating.cs（Star Rating Rebirth / srcalc 思路）。
/// </summary>
public sealed class EasyObject
{
    /// <summary>头（开始）时间。</summary>
    public int Head;

    /// <summary>尾（结束）时间。</summary>
    public int Tail;

    /// <summary>列号。</summary>
    public int Key;

    /// <summary>是否长条（头尾时间不相等）。</summary>
    public bool IsLong => Head != Tail;

    /// <summary>构造一个简化音符。</summary>
    public EasyObject(double startTime, double endTime, int column)
    {
        Head = (int)startTime;
        Tail = (int)endTime;
        Key = column;
    }

    /// <summary>把工作模型的打击物转成星数算法用的简化音符数组。</summary>
    public static EasyObject[] FromManiaObjects(IReadOnlyList<ManiaNote> objects)
    {
        EasyObject[] easyObjects = new EasyObject[objects.Count];
        for (int i = 0; i < objects.Count; i++)
        {
            easyObjects[i] = new EasyObject(objects[i].StartTime, objects[i].EndTime, objects[i].Column);
        }

        return easyObjects;
    }

    /// <summary>按速率缩放（rate != 1 时用；会原地修改传入的数组，与旧版一致）。</summary>
    public static EasyObject[] FromRate(EasyObject[] objects, double rate)
    {
        for (int i = 0; i < objects.Length; i++)
        {
            objects[i].Head = (int)(objects[i].Head / rate);
            objects[i].Tail = (int)(objects[i].Tail / rate);
        }

        return objects;
    }
}
