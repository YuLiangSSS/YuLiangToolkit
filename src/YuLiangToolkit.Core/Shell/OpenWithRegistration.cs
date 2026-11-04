using System.Diagnostics;
using System.IO;
using System.Security;
using Microsoft.Win32;

namespace YuLiangToolkit.Core.Shell;

/// <summary>
/// Windows「右键文件 → 打开方式」的登记服务（**只写 HKCU，不需要管理员权限**）。
///
/// <para><b>为什么是 3 个入口、还要入口副本</b>：Windows 的「打开方式」列表按 **exe 路径**去重 ——
/// 同一个 exe 写三条不同参数（不同命令）只会显示成一条。所以这里用主程序 exe 自己当「按文件类型打开」，
/// 再复制出两个极小的入口副本（<c>*.open-text.exe</c> / <c>*.open-archive.exe</c>，体积 = 主 exe，
/// 依赖仍在主程序目录里）分别给「以文本方式」「以压缩包方式」用。这样列表里正好三条、点哪条进哪种模式。
/// 已实测：Applications 条目按 exe 路径去重，路径不同即可显示三条。</para>
///
/// <para><b>命令格式</b>（解析见 <see cref="OpenWithArguments"/>）：</para>
/// <list type="bullet">
///   <item><c>"主程序.exe" "%1"</c> → 自动模式（和历史 PDF / 图片关联兼容）；</item>
///   <item><c>"主程序.open-text.exe" --open-as=text "%1"</c>；</item>
///   <item><c>"主程序.open-archive.exe" --open-as=archive "%1"</c>。</item>
/// </list>
///
/// <para><b>会改哪些键</b>：只动 <c>HKCU\Software\Classes\Applications\&lt;入口 exe 名&gt;</c> 和
/// <c>HKCU\Software\YuLiangToolkit\OpenWith</c>（只记「已登记 + 主程序路径」，用来判断状态；
/// 取消时连同三个 Applications 条目、两个入口副本一起清掉，保证「打开方式」里看不到本工具）。
/// 不动文件后缀的默认程序（UserChoice 由系统保护，改不了也不去硬抢）。</para>
///
/// <para><b>构建门禁</b>：只有 <b>Release / publish.ps1 发布版</b>允许登记（<see cref="IsBuildSupported"/>）。
/// Debug 构建默认关闭开关，启动时还会自动 <see cref="ClearAll"/> 清理残留 —— 避免开发 / 自检时把
/// <c>bin\Debug</c> 路径写进用户系统注册表。取消 / 清理在 Debug 下仍然可用。</para>
///
/// <para><b>自检绝不调用 <see cref="Register"/> / <see cref="Unregister"/></b>：
/// 那会往用户真实注册表写东西。只允许测纯逻辑 + 只读的 <see cref="IsRegistered"/> / <see cref="Describe"/>。</para>
/// </summary>
public static class OpenWithRegistration
{
    /// <summary>Windows 里显示的应用名前缀。</summary>
    public const string AppDisplayName = "凉雨工具集";

    /// <summary>当前用户注册表里 Applications 的根路径。</summary>
    public const string RegistryApplicationsPath = @"Software\Classes\Applications";

    /// <summary>本功能自己的状态键（不在 Classes 下，避免被当成文件关联）。</summary>
    public const string StateKeyPath = @"Software\YuLiangToolkit\OpenWith";

#if DEBUG
    /// <summary>
    /// 当前构建是否允许登记「打开方式」。
    /// <para>**只有 Release 构建 / publish.ps1 发布出来的 exe 才允许**：Debug 版是开发 / 自检用的，
    /// 不能让 `bin\Debug` 路径写进用户系统注册表（换目录、重建、删 bin 都会留下坏入口）。
    /// 由编译符号决定，Debug 构建里这个属性是 <c>false</c>，主程序 / 工具页会禁用开关。</para>
    /// <para>用属性而不是 <c>const</c>：const false 会让后面的分支变成“不可达代码”警告。</para>
    /// </summary>
    public static bool IsBuildSupported => false;

    /// <summary>Debug 构建里拒绝登记 / 说明为什么的文案。</summary>
    public const string UnsupportedBuildMessage =
        "当前是 Debug 构建：Debug 版不登记「打开方式」（防止把 bin\\Debug 路径写进系统注册表），"
        + "启动时也会自动清理已有登记。请用 Release 构建或 publish.ps1 发布出来的 exe。";
#else
    /// <summary>当前构建是否允许登记「打开方式」（Release / 发布版为 <c>true</c>，见 Debug 分支说明）。</summary>
    public static bool IsBuildSupported => true;

    /// <summary>Debug 构建里拒绝登记 / 说明为什么的文案（Release 下为空）。</summary>
    public const string UnsupportedBuildMessage = "";
#endif

    /// <summary>
    /// PDF / 图片阅读器的「文件关联」开关登记时用的 ProgId。它们也会把本工具加进
    /// 「打开方式」（OpenWithProgids），所以本功能关闭时要顺手清掉这些打开方式条目 ——
    /// 用户关掉「打开方式」关联的预期是本软件从列表里消失；**只删 OpenWithProgids 引用，
    /// 不删 ProgId 本身 / 不改后缀默认程序**，所以双击关联不受影响。
    /// </summary>
    public static IReadOnlyList<string> KnownAssociationProgIds { get; } = new[]
    {
        "YuLiangToolkit.PictureViewer",
        "YuLiangToolkit.PdfReader",
    };

    /// <summary>上述 ProgId 可能登记在哪些后缀的 OpenWithProgids（= 图片 8 种 + PDF）。</summary>
    private static readonly string[] s_readerOpenWithExtensions = OpenWithCatalog.Targets
        .Where(target => target.ToolId is "picture-viewer" or "pdf-reader")
        .SelectMany(target => target.Extensions)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    /// <summary>三个入口（顺序 = 界面展示顺序）：Auto 用主 exe，另外两个用入口副本。</summary>
    public static IReadOnlyList<OpenWithOpener> Openers { get; } = new[]
    {
        new OpenWithOpener(
            OpenWithMode.Auto,
            AppDisplayName + "（按文件类型打开）",
            "按扩展名自动选 PDF / 图片 / Word / Excel / 压缩包 / 播放器 / 文本编辑器；认不出用文本编辑器兜底",
            ""),
        new OpenWithOpener(
            OpenWithMode.Text,
            AppDisplayName + "（以文本方式打开）",
            "一律用文本编辑器打开；自动保存会暂停，避免把陌生文件误写坏",
            ".open-text.exe"),
        new OpenWithOpener(
            OpenWithMode.Archive,
            AppDisplayName + "（以压缩包方式打开）",
            "一律用压缩包工具打开、探测并管理压缩包",
            ".open-archive.exe"),
    };

    /// <summary>
    /// 把「可能是指向入口副本的 exe 路径」还原成主程序 exe 路径。
    /// 程序被入口副本启动时（<see cref="Environment.ProcessPath"/> 是副本），登记 / 状态检查都要用主 exe。
    /// </summary>
    public static string ResolveCanonicalExecutablePath(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath)) return "";

        string full;
        try { full = Path.GetFullPath(executablePath.Trim().Trim('"')); }
        catch { return executablePath.Trim(); }

        var name = Path.GetFileName(full);
        var directory = Path.GetDirectoryName(full) ?? "";
        foreach (var opener in Openers)
        {
            if (opener.FileNameSuffix.Length == 0) continue;
            if (!name.EndsWith(opener.FileNameSuffix, StringComparison.OrdinalIgnoreCase)) continue;

            var stem = name[..^opener.FileNameSuffix.Length];
            if (stem.Length == 0) continue;
            return Path.Combine(directory, stem + ".exe");
        }

        return full;
    }

    /// <summary>根据 exe 文件名判断是不是我们的入口副本；是就返回对应模式，否则 null。</summary>
    public static OpenWithMode? InferModeFromExecutable(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath)) return null;
        var name = Path.GetFileName(executablePath.Trim().Trim('"'));
        foreach (var opener in Openers)
        {
            if (opener.FileNameSuffix.Length == 0) continue;
            if (name.EndsWith(opener.FileNameSuffix, StringComparison.OrdinalIgnoreCase)) return opener.Mode;
        }

        return null;
    }

    /// <summary>某个模式对应的入口 exe 文件名（Auto = 主程序自己的文件名）。</summary>
    public static string GetOpenerFileName(string mainExecutablePath, OpenWithMode mode)
    {
        var name = Path.GetFileName(mainExecutablePath);
        if (string.IsNullOrWhiteSpace(name)) name = "YuLiangTookit.exe";
        if (mode == OpenWithMode.Auto) return name;

        var stem = Path.GetFileNameWithoutExtension(name);
        var opener = Openers.FirstOrDefault(o => o.Mode == mode);
        var suffix = opener?.FileNameSuffix;
        return string.IsNullOrEmpty(suffix) ? name : stem + suffix;
    }

    /// <summary>某个模式对应的入口 exe 完整路径（Auto = 主程序 exe）。</summary>
    public static string GetOpenerPath(string mainExecutablePath, OpenWithMode mode)
    {
        var full = ResolveCanonicalExecutablePath(mainExecutablePath);
        if (string.IsNullOrWhiteSpace(full)) full = mainExecutablePath;
        var directory = Path.GetDirectoryName(full) ?? "";
        if (string.IsNullOrEmpty(directory)) return GetOpenerFileName(full, mode);
        return Path.Combine(directory, GetOpenerFileName(full, mode));
    }

    /// <summary>拼「用这个入口打开文件」的注册表命令（路径带空格也不会坏）。</summary>
    public static string BuildOpenCommand(string openerPath, OpenWithMode mode)
    {
        var quoted = Quote(openerPath);
        return mode == OpenWithMode.Auto
            ? $"{quoted} \"%1\""
            : $"{quoted} {OpenWithArguments.BuildModeArgument(mode)} \"%1\"";
    }

    /// <summary>当前是否登记过（状态键 + 三条命令都在）。只读，自检可以调。</summary>
    public static bool IsRegistered(string mainExecutablePath)
    {
        try
        {
            using var state = Registry.CurrentUser.OpenSubKey(StateKeyPath);
            if (state?.GetValue("Installed") is not int installed || installed != 1) return false;

            var main = ResolveCanonicalExecutablePath(mainExecutablePath);
            foreach (var opener in Openers)
            {
                var keyName = GetOpenerFileName(main, opener.Mode);
                using var command = Registry.CurrentUser.OpenSubKey(
                    $@"{RegistryApplicationsPath}\{keyName}\shell\open\command");
                if (command?.GetValue("") is not string text || text.Length == 0) return false;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>登记状态说明（界面显示 / 自检只读）。</summary>
    public static OpenWithRegistrationState Describe(string mainExecutablePath)
    {
        var main = ResolveCanonicalExecutablePath(mainExecutablePath);
        var paths = Openers.Select(opener => GetOpenerPath(main, opener.Mode)).ToArray();

        if (string.IsNullOrWhiteSpace(main) || !File.Exists(main))
        {
            return new OpenWithRegistrationState(
                false, false, paths,
                "找不到主程序 exe，无法登记「打开方式」。");
        }

        if (!IsBuildSupported)
        {
            // Debug 构建：开关不可用；若之前用 Release / 旧版本登记过，启动时会自动清理
            return new OpenWithRegistrationState(
                IsRegistered(main), false, paths, UnsupportedBuildMessage);
        }

        if (!IsRegistered(main))
        {
            return new OpenWithRegistrationState(
                false, true, paths,
                "未登记：右键文件 → 打开方式 里还看不到「凉雨工具集」。勾选上面的复选框后会多出 3 个入口"
                + "（按文件类型 / 以文本方式 / 以压缩包方式），只写当前用户注册表，不需要管理员。");
        }

        var upToDate = Openers
            .Where(opener => opener.Mode != OpenWithMode.Auto)
            .All(opener => IsOpenerFileUpToDate(GetOpenerPath(main, opener.Mode), main));

        var message = upToDate
            ? "已登记：右键文件 → 打开方式 里能看到 3 个「凉雨工具集」入口"
              + "（按文件类型 / 以文本方式 / 以压缩包方式）。刚登记时资源管理器的列表可能要等几秒才刷新。"
            : "已登记，但入口副本和当前程序版本不一致（可能刚更新过程序）。点「修复 / 刷新」会重建入口副本。";

        return new OpenWithRegistrationState(true, upToDate, paths, message);
    }

    /// <summary>登记 / 修复「打开方式」（可重复调用，幂等；Debug 构建直接拒绝）。</summary>
    public static OpenWithRegistrationResult Register(string mainExecutablePath)
    {
        if (!IsBuildSupported) return new(false, UnsupportedBuildMessage);

        var main = ResolveCanonicalExecutablePath(mainExecutablePath);
        if (string.IsNullOrWhiteSpace(main) || !File.Exists(main))
            return new(false, $"找不到主程序 exe：{main}");

        var warnings = new List<string>();
        try
        {
            using var classes = Registry.CurrentUser.CreateSubKey(RegistryApplicationsPath);
            if (classes is null) return new(false, "打不开当前用户的 Applications 注册表键。");

            foreach (var opener in Openers)
            {
                var openerPath = GetOpenerPath(main, opener.Mode);
                if (opener.Mode != OpenWithMode.Auto)
                {
                    try { EnsureOpenerFile(main, openerPath); }
                    catch (Exception ex) when (IsRegistryIoException(ex))
                    {
                        warnings.Add($"入口副本 {Path.GetFileName(openerPath)} 更新失败：{ex.Message}");
                    }
                }

                WriteApplicationEntry(classes, opener, main, openerPath);
            }

            // PDF / 图片阅读器如果也登记了打开方式，列表里会和本功能的「自动」入口重复（还可能是旧 exe 路径）。
            // 打开本开关时顺手清掉那些 OpenWithProgids 引用；本功能的自动入口会把 PDF / 图片继续交给对应阅读器。
            RemoveReaderOpenWithProgids();

            using (var state = Registry.CurrentUser.CreateSubKey(StateKeyPath))
            {
                state?.SetValue("Installed", 1, RegistryValueKind.DWord);
                state?.SetValue("MainExecutable", main);
            }

            NotifyShell();
            var message = Describe(main).Message;
            if (warnings.Count > 0) message += "（" + string.Join("；", warnings) + "）";
            return new(true, message);
        }
        catch (Exception ex) when (IsRegistryIoException(ex))
        {
            return new(false, $"登记「打开方式」失败：{ex.Message}（只写当前用户注册表，正常不需要管理员）");
        }
    }

    /// <summary>
    /// 取消登记：删掉三个 Applications 条目（含主程序自动入口）、入口副本 exe、状态键。
    /// <para><b>「开关关掉 = 打开方式里看不到本软件」</b>：自动入口也可能是 PDF / 图片阅读器登记时
    /// 一起建的，这里同样清掉（阅读器的双击关联走 ProgId / 默认程序，不受影响；重新打开本开关后会重建自动入口）。
    /// 这正符合「关闭开关后右键打开方式里看不到本工具」的预期。</para>
    /// </summary>
    public static OpenWithRegistrationResult Unregister(string mainExecutablePath)
    {
        var main = ResolveCanonicalExecutablePath(mainExecutablePath);
        try
        {
            using (var classes = Registry.CurrentUser.OpenSubKey(RegistryApplicationsPath, writable: true))
            {
                if (classes is not null)
                {
                    foreach (var opener in Openers)
                    {
                        var openerPath = GetOpenerPath(main, opener.Mode);
                        classes.DeleteSubKeyTree(Path.GetFileName(openerPath), throwOnMissingSubKey: false);
                    }
                }

                // 入口副本 exe 只删文本 / 压缩包两个副本，主程序本身当然不能删
                foreach (var opener in Openers)
                {
                    if (opener.Mode == OpenWithMode.Auto) continue;
                    TryDeleteFile(GetOpenerPath(main, opener.Mode));
                }
            }

            // PDF / 图片阅读器通过 OpenWithProgids 登记的打开方式条目也清掉（只删引用，不动双击关联）
            RemoveReaderOpenWithProgids();

            Registry.CurrentUser.DeleteSubKeyTree(StateKeyPath, throwOnMissingSubKey: false);
            NotifyShell();
            return new(true, "已清理「打开方式」：本工具在右键 → 打开方式里的入口已全部移除"
                             + "（PDF / 图片阅读器登记的打开方式条目也一并清掉；双击关联不受影响）。");
        }
        catch (Exception ex) when (IsRegistryIoException(ex))
        {
            return new(false, $"取消「打开方式」失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 本工具是否在系统里留下了「打开方式」痕迹（状态键 / 入口副本 exe / Applications 条目）。
    /// **只读**：启动时据此决定要不要清理，避免没开启时每次都白跑一遍注册表。
    /// </summary>
    public static bool HasRegistrationArtifacts(string? mainExecutablePath)
    {
        var main = ResolveCanonicalExecutablePath(mainExecutablePath);
        if (string.IsNullOrWhiteSpace(main)) return false;

        try
        {
            foreach (var opener in Openers)
            {
                if (opener.Mode == OpenWithMode.Auto) continue;
                if (File.Exists(GetOpenerPath(main, opener.Mode))) return true;
            }

            using (var state = Registry.CurrentUser.OpenSubKey(StateKeyPath))
            {
                if (state is not null) return true;
            }

            using (var classes = Registry.CurrentUser.OpenSubKey(RegistryApplicationsPath))
            {
                if (classes is null) return false;

                // 三个入口只要还有任何一个 Applications 键，就算「本功能留下过痕迹」。
                // 自动入口要和 PDF / 图片阅读器建的那条区分开：只有我们的名字（带「（」后缀）才算；
                // 阅读器自己的 OpenWithProgids / Applications 归阅读器的「文件关联」开关管，别去动。
                foreach (var opener in Openers)
                {
                    using var app = classes.OpenSubKey(GetOpenerFileName(main, opener.Mode));
                    if (app is null) continue;
                    if (opener.Mode != OpenWithMode.Auto) return true;
                    if (app.GetValue("FriendlyAppName") is string friendly
                        && friendly.StartsWith(AppDisplayName + "（", StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 强制清理「打开方式」：删掉状态键、入口副本 exe 与三个 Applications 条目
    /// （包括 PDF / 图片阅读器顺手建的自动入口，保证列表里彻底看不到本工具）。
    /// <para>**Debug 构建也可以用**（启动时的自动清理 / 主页设置的「清理」按钮都走它）；
    /// 没有任何痕迹时直接返回成功，不写注册表。幂等。</para>
    /// </summary>
    public static OpenWithRegistrationResult ClearAll(string mainExecutablePath)
    {
        var main = ResolveCanonicalExecutablePath(mainExecutablePath);
        if (string.IsNullOrWhiteSpace(main))
            return new(false, "找不到主程序路径，无法清理「打开方式」。");

        if (!HasRegistrationArtifacts(main))
            return new(true, "当前没有「打开方式」登记（无需清理）。");

        return Unregister(main);
    }

    /// <summary>
    /// 程序更新 / 移动目录后修复入口副本和注册表命令（未登记时什么都不做）。
    /// 主程序启动时若已登记会自动调一次；「打开方式」页的「修复 / 刷新」也调它。
    /// </summary>
    public static OpenWithRegistrationResult EnsureUpToDate(string mainExecutablePath)
    {
        if (!IsBuildSupported) return new(false, UnsupportedBuildMessage);

        var main = ResolveCanonicalExecutablePath(mainExecutablePath);
        if (string.IsNullOrWhiteSpace(main) || !File.Exists(main))
            return new(false, $"找不到主程序 exe：{main}");

        if (!IsRegistered(main)) return new(true, "未登记「打开方式」，跳过刷新。");

        var warnings = new List<string>();
        try
        {
            var changed = false;
            using var classes = Registry.CurrentUser.OpenSubKey(RegistryApplicationsPath, writable: true);
            if (classes is null) return new(false, "读不到当前用户的 Applications 注册表键。");

            foreach (var opener in Openers)
            {
                var openerPath = GetOpenerPath(main, opener.Mode);
                if (opener.Mode != OpenWithMode.Auto)
                {
                    try
                    {
                        if (EnsureOpenerFile(main, openerPath)) changed = true;
                    }
                    catch (Exception ex) when (IsRegistryIoException(ex))
                    {
                        warnings.Add($"入口副本 {Path.GetFileName(openerPath)} 更新失败：{ex.Message}");
                    }
                }

                if (!ApplicationEntryMatches(classes, opener, main, openerPath))
                {
                    WriteApplicationEntry(classes, opener, main, openerPath);
                    changed = true;
                }
            }

            if (changed) NotifyShell();
            var message = Describe(main).Message;
            if (warnings.Count > 0) message += "（" + string.Join("；", warnings) + "）";
            return new(true, message);
        }
        catch (Exception ex) when (IsRegistryIoException(ex))
        {
            return new(false, $"刷新「打开方式」失败：{ex.Message}");
        }
    }

    /// <summary>通知资源管理器刷新「打开方式」/ 图标缓存（不刷新的话要等一会儿才生效）。</summary>
    public static void NotifyShell()
    {
        try { SHChangeNotify(ShcneAssocChanged, 0, IntPtr.Zero, IntPtr.Zero); }
        catch { /* shell32 一直都在，失败也无所谓 */ }
    }

    /// <summary>打开 Windows「默认应用」设置页（UserChoice 只能由用户自己确认）。</summary>
    public static void OpenDefaultAppsSettings()
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:defaultapps") { UseShellExecute = true });
        }
        catch
        {
            // 打不开就算了（界面里还有文字说明）
        }
    }

    // ---------------------------------------------------------------- 内部实现

    private static void WriteApplicationEntry(
        RegistryKey classes, OpenWithOpener opener, string mainExecutablePath, string openerPath)
    {
        var keyName = Path.GetFileName(openerPath);
        using var app = classes.CreateSubKey(keyName)
                       ?? throw new IOException($"无法创建注册表键：{keyName}");
        app.SetValue("FriendlyAppName", opener.DisplayName);

        using (var icon = app.CreateSubKey("DefaultIcon"))
            icon?.SetValue("", $"{Quote(mainExecutablePath)},0");

        using var open = app.CreateSubKey(@"shell\open")
                         ?? throw new IOException($"无法创建注册表键：{keyName}\\shell\\open");
        open.SetValue("FriendlyAppName", opener.DisplayName);

        using var command = open.CreateSubKey("command")
                            ?? throw new IOException($"无法创建注册表键：{keyName}\\shell\\open\\command");
        command.SetValue("", BuildOpenCommand(openerPath, opener.Mode));
    }

    private static bool ApplicationEntryMatches(
        RegistryKey classes, OpenWithOpener opener, string mainExecutablePath, string openerPath)
    {
        var keyName = Path.GetFileName(openerPath);
        using var app = classes.OpenSubKey(keyName);
        if (app is null) return false;
        if (!string.Equals(app.GetValue("FriendlyAppName") as string, opener.DisplayName, StringComparison.Ordinal))
            return false;

        using var command = app.OpenSubKey(@"shell\open\command");
        return string.Equals(
            command?.GetValue("") as string,
            BuildOpenCommand(openerPath, opener.Mode),
            StringComparison.Ordinal);
    }

    /// <summary>确保入口副本存在且与主 exe 同版本；返回是否真的复制了。Auto 不需要副本。</summary>
    private static bool EnsureOpenerFile(string mainExecutablePath, string openerPath)
    {
        if (IsOpenerFileUpToDate(openerPath, mainExecutablePath)) return false;

        // 正在运行的入口副本不能覆盖自己（Windows 会锁 exe）；下一次从主程序启动时再刷新
        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath)
            && string.Equals(Path.GetFullPath(openerPath), Path.GetFullPath(processPath), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        File.Copy(mainExecutablePath, openerPath, overwrite: true);
        try { File.SetLastWriteTimeUtc(openerPath, File.GetLastWriteTimeUtc(mainExecutablePath)); }
        catch { /* 时间戳设不上就下次再比一次，不影响启动 */ }
        return true;
    }

    private static bool IsOpenerFileUpToDate(string openerPath, string mainExecutablePath)
    {
        try
        {
            var opener = new FileInfo(openerPath);
            var main = new FileInfo(mainExecutablePath);
            return opener.Exists && main.Exists
                   && opener.Length == main.Length
                   && opener.LastWriteTimeUtc == main.LastWriteTimeUtc;
        }
        catch
        {
            return false;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // 入口副本可能正在运行 / 被占用：留着也无害，下次登记会覆盖
        }
    }

    /// <summary>把 PDF / 图片阅读器登记在 OpenWithProgids 里的「打开方式」引用删掉（不动 ProgId 本身）。</summary>
    private static void RemoveReaderOpenWithProgids()
    {
        foreach (var extension in s_readerOpenWithExtensions)
        {
            RemoveProgIdValues($@"Software\Classes\{extension}\OpenWithProgids");
            RemoveProgIdValues(
                $@"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\{extension}\OpenWithProgids");
        }
    }

    private static void RemoveProgIdValues(string keyPath)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: true);
            if (key is null) return;
            foreach (var progId in KnownAssociationProgIds)
                key.DeleteValue(progId, throwOnMissingValue: false);
        }
        catch
        {
            // 清理可选，失败不影响主流程
        }
    }

    private static string Quote(string path) => $"\"{path.Trim().Trim('"')}\"";

    private static bool IsRegistryIoException(Exception ex)
        => ex is SecurityException or UnauthorizedAccessException or IOException or ObjectDisposedException;

    private const int ShcneAssocChanged = 0x08000000;

    [System.Runtime.InteropServices.DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
}
