using System.Xml;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;

namespace YuLiangToolkit.Tools.TextEditor.Core;

/// <summary>
/// 「文本编辑器」的语法高亮定义注册表：把 AvalonEdit 内置定义（C#/C++/Java/HTML/CSS/JSON/Markdown/
/// PowerShell/Python/PHP/TSQL/VB/XML…）和我们自己写的 <c>Highlighting\*.xshd</c>
/// （Go / Rust / Ruby / F# / Kotlin / TypeScript / Vue / Batch / INI / YAML / Dockerfile / Lua / SQL）
/// 统一成「文件路径 → 定义」的解析入口。
///
/// <para><b>为什么不直接 <c>HighlightingManager.RegisterHighlighting</c></b>：内置定义已经占用了
/// <c>.md</c> / <c>.sql</c> 等扩展名，再注册会撞车；而且 AvalonEdit 只按扩展名找定义，
/// 像 <c>Dockerfile</c>（没有扩展名）、<c>Gemfile</c> 这种文件名优先的情况它根本处理不了。
/// 所以这里做一层自己的映射（扩展名 / 文件名 → 定义名），查不到再回退
/// <see cref="HighlightingManager.GetDefinitionByExtension"/>。</para>
///
/// <para><b>自定义定义怎么来</b>：<c>Highlighting\*.xshd</c> 由 csproj 作为嵌入资源编进程序集，
/// 首次使用时用 <see cref="HighlightingLoader"/> 逐个读入（单个坏掉只跳过它，不影响其它语言）；
/// 颜色名沿用内置风格的（Comment / String / NumberLiteral / Keywords / Type / MethodCall…），
/// 这样 <see cref="EditorHighlighting"/> 的 VS 深色配色能统一套上去。</para>
/// </summary>
public static class EditorSyntaxDefinitions
{
    private static readonly object Gate = new();

    private static Dictionary<string, IHighlightingDefinition>? _byName;
    private static Dictionary<string, string>? _byExtension;
    private static Dictionary<string, string>? _byFileName;
    private static List<string>? _loadErrors;
    private static bool _initialized;

    // ---------------- 内置定义（AvalonEdit 自带）的补充扩展名 / 文件名 ----------------
    // 只列内置 xshd 没覆盖、但用户希望认出来的写法；内置已注册的扩展名走末尾的兜底查询。

    /// <summary>扩展名 → 定义名（内置定义没有注册、或要抢在内置之前生效的）。</summary>
    private static readonly (string Ext, string DefinitionName)[] ExtraExtensions =
    {
        // C / C++：内置 C++ 定义只注册了 .c/.h/.cc/.cpp/.hpp，补齐常见写法
        (".cxx", "C++"), (".hxx", "C++"), (".hh", "C++"), (".h++", "C++"), (".inl", "C++"),
        // Java / PHP / JavaScript / JSON 常见变体
        (".php3", "PHP"), (".php4", "PHP"), (".php5", "PHP"), (".phtml", "PHP"),
        (".mjs", "JavaScript"), (".cjs", "JavaScript"), (".jsx", "JavaScript"),
        (".jsonc", "Json"),
        // Markdown 常见后缀（预览也按同一组判断）
        (".markdown", "MarkDown"), (".mdown", "MarkDown"), (".mkd", "MarkDown"),
        // XHTML 用 HTML 定义；VBScript 用 VB
        (".xhtml", "HTML"),
        (".vbs", "VB"),
        // SQL 用我们自己的通用 SQL 定义（内置 TSQL 只认 SQL Server 语法）
        (".sql", "SQL"),
    };

    /// <summary>没有扩展名、按文件名识别的（小写比较）。</summary>
    private static readonly (string FileName, string DefinitionName)[] FileNames =
    {
        ("dockerfile", "Dockerfile"),
        ("gemfile", "Ruby"),
        ("rakefile", "Ruby"),
        ("vagrantfile", "Ruby"),
    };

    /// <summary>Markdown 的扩展名（预览窗、状态提示共用；与上面的映射保持一致）。</summary>
    private static readonly HashSet<string> MarkdownExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".md", ".markdown", ".mdown", ".mkd",
    };

    // ---------------- 自定义 xshd（项目自带）的扩展名 ----------------

    /// <summary>自定义定义名 → 认领的扩展名（Dockerfile 没有扩展名，靠文件名）。</summary>
    private static readonly (string DefinitionName, string[] Extensions)[] CustomExtensions =
    {
        ("Go", new[] { ".go" }),
        ("Rust", new[] { ".rs" }),
        ("Ruby", new[] { ".rb", ".rake", ".gemspec" }),
        ("F#", new[] { ".fs", ".fsi", ".fsx" }),
        ("Kotlin", new[] { ".kt", ".kts" }),
        ("TypeScript", new[] { ".ts", ".tsx", ".mts", ".cts" }),
        ("Vue", new[] { ".vue" }),
        ("Batch", new[] { ".bat", ".cmd" }),
        ("INI", new[] { ".ini", ".cfg", ".inf" }),
        ("YAML", new[] { ".yml", ".yaml" }),
        ("Lua", new[] { ".lua" }),
        ("SQL", new[] { ".sql" }),
    };

    /// <summary>所有认识的语言（给文档 / 自检 / 可选的文件对话框过滤用）。</summary>
    public static IReadOnlyList<string> KnownDefinitionNames
    {
        get
        {
            EnsureInitialized();
            return _byName!.Keys.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    /// <summary>加载嵌入 xshd 时的错误（正常应为空；自检会断言）。</summary>
    public static IReadOnlyList<string> LoadErrors
    {
        get
        {
            EnsureInitialized();
            return _loadErrors!.ToList();
        }
    }

    /// <summary>是不是 Markdown 文件（.md / .markdown / .mdown / .mkd）。</summary>
    public static bool IsMarkdownFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            return MarkdownExtensions.Contains(Path.GetExtension(path.Trim()));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 按文件路径取语法高亮定义：文件名映射（Dockerfile / Gemfile…）→ 扩展名映射（自定义语言 + 补充扩展名）
    /// → AvalonEdit 内置注册表兜底；都不认识返回 <c>null</c>（纯文本）。
    /// <para>任何路径异常 / 定义加载异常都返回 null，不往外抛（调用方是纯显示逻辑）。</para>
    /// </summary>
    public static IHighlightingDefinition? Resolve(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        try
        {
            EnsureInitialized();
            var fileName = Path.GetFileName(path.Trim());
            if (string.IsNullOrEmpty(fileName)) return null;

            if (_byFileName!.TryGetValue(fileName, out var byFileName))
                return GetDefinition(byFileName);

            var ext = Path.GetExtension(fileName);
            if (!string.IsNullOrEmpty(ext) && _byExtension!.TryGetValue(ext, out var byExtension))
                return GetDefinition(byExtension);

            // 兜底：AvalonEdit 自己注册的定义（.cs / .java / .py / .ps1 / .xml / .html …）
            return HighlightingManager.Instance.GetDefinitionByExtension(ext);
        }
        catch
        {
            return null;
        }
    }

    private static IHighlightingDefinition? GetDefinition(string name)
    {
        if (_byName!.TryGetValue(name, out var custom)) return custom;
        try
        {
            return HighlightingManager.Instance.GetDefinition(name);
        }
        catch
        {
            return null;
        }
    }

    // ---------------- 初始化 ----------------

    private static void EnsureInitialized()
    {
        if (_initialized) return;
        lock (Gate)
        {
            if (_initialized) return;

            var byName = LoadCustomDefinitions(out var errors);

            var byExtension = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (definitionName, extensions) in CustomExtensions)
            {
                foreach (var ext in extensions)
                    byExtension[ext] = definitionName;
            }
            foreach (var (ext, definitionName) in ExtraExtensions)
                byExtension[ext] = definitionName;

            var byFileName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (fileName, definitionName) in FileNames)
                byFileName[fileName] = definitionName;

            _byName = byName;
            _byExtension = byExtension;
            _byFileName = byFileName;
            _loadErrors = errors;
            _initialized = true;
        }
    }

    /// <summary>把嵌入资源里的 <c>*.xshd</c> 全部读出来（单个失败只记错误，不影响别的语言）。</summary>
    private static Dictionary<string, IHighlightingDefinition> LoadCustomDefinitions(out List<string> errors)
    {
        var result = new Dictionary<string, IHighlightingDefinition>(StringComparer.OrdinalIgnoreCase);
        var problems = new List<string>();

        var assembly = typeof(EditorSyntaxDefinitions).Assembly;
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit };
        foreach (var resourceName in assembly.GetManifestResourceNames())
        {
            if (!resourceName.EndsWith(".xshd", StringComparison.OrdinalIgnoreCase)) continue;

            try
            {
                using var stream = assembly.GetManifestResourceStream(resourceName);
                if (stream is null)
                {
                    problems.Add($"{resourceName}: 资源流为空");
                    continue;
                }

                using var reader = XmlReader.Create(stream, settings);
                var definition = HighlightingLoader.Load(reader, HighlightingManager.Instance);
                if (string.IsNullOrWhiteSpace(definition.Name))
                {
                    problems.Add($"{resourceName}: 定义没有 name");
                    continue;
                }
                result[definition.Name] = definition;
            }
            catch (Exception ex)
            {
                problems.Add($"{resourceName}: {ex.Message}");
            }
        }

        errors = problems;
        return result;
    }
}
