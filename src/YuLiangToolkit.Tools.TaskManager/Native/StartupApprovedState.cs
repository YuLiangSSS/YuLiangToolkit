using System.Buffers.Binary;

namespace YuLiangToolkit.Tools.TaskManager.Native;

/// <summary>
/// Windows 开机启动项的「启用 / 禁用」标记（纯函数）。
///
/// <para>任务管理器 / Windows 设置里关掉一个启动项，实际是往
/// <c>…\Explorer\StartupApproved\Run</c>（启动文件夹是 <c>StartupFolder</c>）写一个 12 字节的
/// <c>REG_BINARY</c>：前 4 字节是状态（<c>2</c> = 启用，<c>6</c> = 启用（策略），<c>3</c> = 禁用），
/// 后 8 字节是操作时间（FILETIME）。<b>没有这条记录 = 启用</b>。</para>
///
/// <para>这里只负责解析 / 生成这段二进制；真正读写注册表在 <see cref="StartupManager"/>。</para>
/// </summary>
public static class StartupApprovedState
{
    /// <summary>启用标记。</summary>
    public const uint EnabledState = 2;

    /// <summary>禁用标记。</summary>
    public const uint DisabledState = 3;

    /// <summary>「由策略启用」的标记（Windows 也会写它）。</summary>
    public const uint EnabledByPolicyState = 6;

    /// <summary>读取状态：true = 启用，false = 禁用，null = 没有记录 / 认不出来（按启用处理）。</summary>
    public static bool? ParseEnabled(byte[]? value)
    {
        if (value is null || value.Length < 4) return null;
        var state = BinaryPrimitives.ReadUInt32LittleEndian(value);
        return state switch
        {
            EnabledState => true,
            EnabledByPolicyState => true,
            DisabledState => false,
            _ => null,
        };
    }

    /// <summary>没有记录时按「启用」处理（与任务管理器一致）。</summary>
    public static bool IsEnabled(byte[]? value) => ParseEnabled(value) ?? true;

    /// <summary>生成写入注册表用的 12 字节值（状态 + 操作时间）。</summary>
    public static byte[] Create(bool enabled, DateTime utcNow)
    {
        var buffer = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, enabled ? EnabledState : DisabledState);
        var fileTime = utcNow.ToFileTimeUtc();
        BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(4), fileTime);
        return buffer;
    }

    /// <summary>给日志 / 详情用的人类可读描述。</summary>
    public static string Describe(byte[]? value)
    {
        var enabled = ParseEnabled(value);
        return enabled switch
        {
            true => "已启用",
            false => "已禁用",
            null => value is null || value.Length == 0 ? "已启用（无 StartupApproved 记录）" : "未知（StartupApproved 值无法识别）",
        };
    }
}
