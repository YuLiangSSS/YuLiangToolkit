using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace YuLiangToolkit.Tools.OsuMania.Core;

/// <summary>批处理里单个文件的结果状态。</summary>
public enum OsuBatchItemStatus
{
    /// <summary>转换成功（已写出新文件）。</summary>
    Converted,

    /// <summary>跳过（不满足转换条件 / 已经是目标状态 / 用户取消）。</summary>
    Skipped,

    /// <summary>失败（读不了 / 写不了 / 转换过程抛异常）。</summary>
    Failed,
}

/// <summary>批处理里一个文件的结果。</summary>
public sealed class OsuBatchItem
{
    /// <summary>源文件完整路径。</summary>
    public required string FilePath { get; init; }

    /// <summary>结果状态。</summary>
    public OsuBatchItemStatus Status { get; init; }

    /// <summary>输出文件路径（仅 <see cref="OsuBatchItemStatus.Converted"/> 时有值）。</summary>
    public string? OutputPath { get; init; }

    /// <summary>跳过原因 / 失败原因（中文）。</summary>
    public string? Message { get; init; }
}

/// <summary>批处理进度快照（给 UI 的 <see cref="IProgress{T}"/> 用）。</summary>
public sealed class OsuBatchProgress
{
    /// <summary>已处理数量（含跳过 / 失败）。</summary>
    public int Processed { get; init; }

    /// <summary>总数量。</summary>
    public int Total { get; init; }

    /// <summary>刚处理完的文件。</summary>
    public string FilePath { get; init; } = "";

    /// <summary>百分比（0~100）。</summary>
    public double Percent => Total <= 0 ? 0 : Processed * 100.0 / Total;
}

/// <summary>一整批的结果汇总。</summary>
public sealed class OsuBatchResult
{
    /// <summary>逐个文件的结果（按处理顺序）。</summary>
    public IReadOnlyList<OsuBatchItem> Items { get; init; } = Array.Empty<OsuBatchItem>();

    /// <summary>总文件数。</summary>
    public int Total => Items.Count;

    /// <summary>成功数量。</summary>
    public int ConvertedCount => Items.Count(i => i.Status == OsuBatchItemStatus.Converted);

    /// <summary>跳过数量。</summary>
    public int SkippedCount => Items.Count(i => i.Status == OsuBatchItemStatus.Skipped);

    /// <summary>失败数量。</summary>
    public int FailedCount => Items.Count(i => i.Status == OsuBatchItemStatus.Failed);

    /// <summary>给界面直接显示的一行汇总。</summary>
    public string SummaryText => Total == 0
        ? "没有找到 .osu 文件"
        : $"共 {Total} 个：成功 {ConvertedCount} · 跳过 {SkippedCount} · 失败 {FailedCount}";
}

/// <summary>
/// 批量处理入口（早期版本“拖文件夹进来批量转换”行为的纯逻辑版，可直接被界面 / 自检复用）。
///
/// <para>流程：拖入的路径集合 → 递归枚举 <c>.osu</c>（大小写不敏感）→ 逐个 <see cref="OsuBeatmapFile.TryLoad"/>
/// → 引擎转换 → <see cref="OsuBeatmapFile.Save"/> 写文件 → 汇总结果。单个文件失败不会中断整批。</para>
///
/// <para>不依赖 WPF 的 <c>DragEventArgs</c>：界面把 <c>e.Data.GetData(DataFormats.FileDrop)</c> 转成
/// <c>string[]</c> 传给这里即可。</para>
///
/// <para><b>必须保持串行</b>：OsuParsers 的解码 / 编码内部是 static 状态、非线程安全（多线程同时
/// Decode 会串台），所以这里逐文件顺序处理，<b>不要</b>改写成 Parallel / Task.WhenAll；
/// 界面层也请在单个后台任务里按顺序调用。</para>
/// </summary>
public static class OsuFileBatch
{
    /// <summary>识别的谱面扩展名。</summary>
    public const string OsuExtension = ".osu";

    /// <summary>路径是否是 .osu 文件（扩展名大小写不敏感，与早期版本 <c>Path.GetExtension(s).ToLower() == ".osu"</c> 一致）。</summary>
    public static bool IsOsuFile(string? path)
        => !string.IsNullOrEmpty(path) && string.Equals(Path.GetExtension(path), OsuExtension, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 把「拖入的文件 / 文件夹」展开成 .osu 文件列表：文件直接收（只收 .osu），文件夹递归收（只收 .osu）。
    /// 重复路径去重（Windows 下忽略大小写）；不存在的路径忽略；没权限的子目录跳过，不抛异常。
    ///
    /// <para>返回顺序：先按拖入顺序处理文件，文件夹内再按目录名 / 文件名排序（早期版本依赖系统枚举顺序，新版固定排序，
    /// 只为让批量结果可复现）。</para>
    /// </summary>
    public static IReadOnlyList<string> CollectOsuFiles(IEnumerable<string>? droppedPaths)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddFile(string path)
        {
            string full;
            try
            {
                full = Path.GetFullPath(path);
            }
            catch
            {
                return;
            }
            if (seen.Add(full))
            {
                result.Add(full);
            }
        }

        void AddDirectory(string directory)
        {
            try
            {
                foreach (var file in Directory.GetFiles(directory).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                {
                    if (IsOsuFile(file))
                    {
                        AddFile(file);
                    }
                }

                foreach (var sub in Directory.GetDirectories(directory).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
                {
                    AddDirectory(sub);
                }
            }
            catch (Exception)
            {
                // 没权限 / 目录被占用：跳过这个目录，整批继续。
            }
        }

        if (droppedPaths is null)
        {
            return result;
        }

        foreach (var raw in droppedPaths)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            string path = raw.Trim();
            try
            {
                if (File.Exists(path))
                {
                    if (IsOsuFile(path))
                    {
                        AddFile(path);
                    }
                }
                else if (Directory.Exists(path))
                {
                    AddDirectory(path);
                }
            }
            catch (Exception)
            {
                // 路径奇怪（权限 / 非法字符）：忽略。
            }
        }

        return result;
    }

    /// <summary>
    /// 批量转 K。参数见 <see cref="KeyConverterOptions"/>；单文件结果里带输出路径。
    /// </summary>
    public static OsuBatchResult RunKeyConverter(IEnumerable<string>? droppedPaths, KeyConverterOptions options,
        IProgress<OsuBatchProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        return Run(droppedPaths, file =>
        {
            var result = KeyConverterEngine.Apply(file, options);
            return result.Converted
                ? new OsuBatchItem { FilePath = file.FilePath, Status = OsuBatchItemStatus.Converted, OutputPath = result.OutputPath }
                : new OsuBatchItem { FilePath = file.FilePath, Status = OsuBatchItemStatus.Skipped, Message = result.SkipReason };
        }, progress, cancellationToken);
    }

    /// <summary>
    /// 批量转 LN。参数见 <see cref="LnTransformerOptions"/>；单文件结果里带输出路径。
    /// </summary>
    public static OsuBatchResult RunLnTransformer(IEnumerable<string>? droppedPaths, LnTransformerOptions options,
        IProgress<OsuBatchProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        return Run(droppedPaths, file =>
        {
            var result = LnTransformerEngine.Apply(file, options);
            return result.Converted
                ? new OsuBatchItem { FilePath = file.FilePath, Status = OsuBatchItemStatus.Converted, OutputPath = result.OutputPath }
                : new OsuBatchItem { FilePath = file.FilePath, Status = OsuBatchItemStatus.Skipped, Message = result.SkipReason };
        }, progress, cancellationToken);
    }

    /// <summary>
    /// 单文件转 K（加载 + 转换 + 保存），不抛异常。
    /// </summary>
    public static OsuBatchItem ConvertOne(string filePath, KeyConverterOptions options)
        => ConvertOne(filePath, file =>
        {
            var result = KeyConverterEngine.Apply(file, options);
            return result.Converted
                ? new OsuBatchItem { FilePath = file.FilePath, Status = OsuBatchItemStatus.Converted, OutputPath = result.OutputPath }
                : new OsuBatchItem { FilePath = file.FilePath, Status = OsuBatchItemStatus.Skipped, Message = result.SkipReason };
        });

    /// <summary>
    /// 单文件转 LN（加载 + 转换 + 保存），不抛异常。
    /// </summary>
    public static OsuBatchItem ConvertOne(string filePath, LnTransformerOptions options)
        => ConvertOne(filePath, file =>
        {
            var result = LnTransformerEngine.Apply(file, options);
            return result.Converted
                ? new OsuBatchItem { FilePath = file.FilePath, Status = OsuBatchItemStatus.Converted, OutputPath = result.OutputPath }
                : new OsuBatchItem { FilePath = file.FilePath, Status = OsuBatchItemStatus.Skipped, Message = result.SkipReason };
        });

    /// <summary>单文件处理（加载 + 调用转换回调）；加载失败 / 回调抛异常都转成 <see cref="OsuBatchItemStatus.Failed"/>。</summary>
    public static OsuBatchItem ConvertOne(string filePath, Func<OsuBeatmapFile, OsuBatchItem> convert)
    {
        ArgumentNullException.ThrowIfNull(convert);

        if (!OsuBeatmapFile.TryLoad(filePath, out var file, out var error))
        {
            return new OsuBatchItem { FilePath = filePath, Status = OsuBatchItemStatus.Failed, Message = error };
        }

        try
        {
            return convert(file!);
        }
        catch (Exception ex)
        {
            return new OsuBatchItem
            {
                FilePath = filePath,
                Status = OsuBatchItemStatus.Failed,
                Message = $"处理失败（{ex.GetType().Name}）：{ex.Message}",
            };
        }
    }

    /// <summary>
    /// 通用批量执行：展开文件列表 → 逐个加载并调用 <paramref name="convert"/> → 汇报进度。
    /// 单个文件失败不中断整批；<paramref name="cancellationToken"/> 取消后停止（已处理的文件保留结果）。
    /// </summary>
    public static OsuBatchResult Run(IEnumerable<string>? droppedPaths, Func<OsuBeatmapFile, OsuBatchItem> convert,
        IProgress<OsuBatchProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(convert);

        var files = CollectOsuFiles(droppedPaths);
        var items = new List<OsuBatchItem>(files.Count);
        int total = files.Count;

        for (int i = 0; i < total; i++)
        {
            string path = files[i];

            if (cancellationToken.IsCancellationRequested)
            {
                items.Add(new OsuBatchItem { FilePath = path, Status = OsuBatchItemStatus.Skipped, Message = "已取消。" });
                continue;
            }

            var item = ConvertOne(path, convert);
            items.Add(item);
            progress?.Report(new OsuBatchProgress { Processed = items.Count, Total = total, FilePath = path });
        }

        return new OsuBatchResult { Items = items };
    }
}
