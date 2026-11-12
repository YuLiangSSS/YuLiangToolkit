using System.Text.Json;
using YuLiangToolkit.Core.Expressions;
using YuLiangToolkit.Core.Storage;
using YuLiangToolkit.Tools.Calculator;
using YuLiangToolkit.Tools.Calculator.Core;

namespace YuLiangToolkit.FrameworkSelfCheck;

/// <summary>
/// 「计算器」自检：共享表达式引擎的计算语义（优先级 / 右结合乘方 / 一元负号 / 省略乘号 / 阶乘 /
/// 角度制与弧度制 / 常量 / 无定义值）、结果格式化（<see cref="MathFormat"/>）、
/// 计算器自己的输入构造（数字 / 小数点去重 / 退格 / ± / Ans / x² / 1/x）、
/// 历史记录规则（新的在前 / 相邻去重 / 上限裁剪 / 清空 / JSON 往返）与配置默认值 / 夹取。
///
/// <para><b>辈分计算模式</b>的断言在 <see cref="KinCalculatorChecks"/>，由本类的
/// <see cref="Run"/> 末尾调用（同属 <c>calculator</c> 组）。</para>
///
/// <para><b>全程纯逻辑</b>：不创建窗口、不碰剪贴板、不启动任何进程，也不写真实 Config
/// （配置往返还只在内存里做 JSON 序列化 / 反序列化）。</para>
/// </summary>
internal static class CalculatorChecks
{
    /// <summary>
    /// 自检框架传进来的是 <c>Action&lt;string,bool,string&gt;</c>（detail 必填），
    /// 这里包一层带默认值的委托，模块内部就能写 <c>check("名字", ok)</c> 而不必到处补 <c>""</c>。
    /// </summary>
    private delegate void CheckFn(string name, bool ok, string detail = "");

    public static void Run(Action<string, bool, string> check, Action<string, bool> report)
    {
        CheckFn fn = (name, ok, detail) => check(name, ok, detail);

        CheckMetadata(fn);
        CheckPrecedence(fn);
        CheckImplicitMultiplication(fn);
        CheckFactorial(fn);
        CheckAngleUnits(fn);
        CheckConstantsAndSpecialValues(fn);
        CheckCalculatorSymbols(fn);
        CheckEngineWrapper(fn);
        CheckFormatting(fn);
        CheckExactBigNumbers(fn);
        CheckInput(fn);
        CheckHistory(fn);
        CheckConfig(fn);
        CheckKeypad(fn);

        // 辈分计算模式（同一工具页的第二个模式）：断言放在单独的文件里，共用本组
        KinCalculatorChecks.Run(check, report);

        report("[计算器] 表达式求值 / 优先级 / 省略乘号 / 阶乘 / 角度制 / 格式化 / 输入构造 / 历史与配置", true);
    }

    // ---------------------------------------------------------------- 小工具

    private static CalculatorResult Eval(
        string? expression,
        AngleUnit unit = AngleUnit.Radians,
        int digits = 12)
        => CalculatorEngine.Evaluate(expression, unit, digits);

    private static double Value(string expression, AngleUnit unit = AngleUnit.Radians)
        => Eval(expression, unit).Value;

    private static bool Near(double actual, double expected, double tolerance = 1e-12)
        => double.IsFinite(actual) && Math.Abs(actual - expected) <= tolerance;

    private static CalculatorKey? Key(IReadOnlyList<CalculatorKey> keys, string label)
        => keys.FirstOrDefault(key => key.Label == label);

    private static string Describe(CalculatorResult result)
        => $"Success={result.Success} Text=\"{result.Text}\" Error=\"{result.Error}\"";

    // ---------------------------------------------------------------- 工具元数据

    private static void CheckMetadata(CheckFn check)
    {
        var tool = new CalculatorTool();
        check("[计算器] 工具 Id / 名称 / Emoji / 分类 / 排序符合约定",
            tool.Id == "calculator" && tool.Name == "计算器" && tool.EmojiIcon == "🧮"
            && tool.Category == "数学" && tool.Order == 510,
            $"{tool.Id} / {tool.Name} / {tool.EmojiIcon} / {tool.Category} / {tool.Order}");

        check("[计算器] 标签覆盖数学 / 计算器 / 科学计算器 / 表达式 / 三角函数 / 辈分",
            tool.Tags.Contains("数学") && tool.Tags.Contains("计算器") && tool.Tags.Contains("科学计算器")
            && tool.Tags.Contains("表达式") && tool.Tags.Contains("三角函数") && tool.Tags.Contains("辈分"),
            string.Join(" / ", tool.Tags));

        check("[计算器] 描述能说明「科学计算 + 表达式 + 历史 + 辈分计算」",
            tool.Description.Contains("科学") && tool.Description.Contains("表达式")
            && tool.Description.Contains("历史") && tool.Description.Contains("辈分"),
            tool.Description);

        check("[计算器] 工具 Id 与 CalculatorConfig.ToolId 一致（Data / Config 目录名）",
            tool.Id == CalculatorConfig.ToolId, CalculatorConfig.ToolId);

        check("[计算器] 实现了 ITool（外壳能自动发现）",
            tool is YuLiangToolkit.Core.Tools.ITool,
            tool.GetType().FullName ?? "");
    }

    // ---------------------------------------------------------------- 运算优先级 / 结合性

    private static void CheckPrecedence(CheckFn check)
    {
        check("[计算器] 优先级：2+3*4=14、10-2-3=5、2+3*4-5=9",
            Near(Value("2+3*4"), 14) && Near(Value("10-2-3"), 5) && Near(Value("2+3*4-5"), 9),
            $"{Value("2+3*4")} / {Value("10-2-3")} / {Value("2+3*4-5")}");

        check("[计算器] 乘方右结合：2^3^2=512（不是 64）",
            Near(Value("2^3^2"), 512), Value("2^3^2").ToString("R"));

        check("[计算器] 一元负号比乘方松：-2^2=-4、2^-1=0.5",
            Near(Value("-2^2"), -4) && Near(Value("2^-1"), 0.5),
            $"{Value("-2^2")} / {Value("2^-1")}");

        check("[计算器] 括号改变优先级：(2+3)*4=20、(1+2)*(3+4)=21",
            Near(Value("(2+3)*4"), 20) && Near(Value("(1+2)*(3+4)"), 21),
            $"{Value("(2+3)*4")} / {Value("(1+2)*(3+4)")}");

        check("[计算器] 除法与小数：6/4=1.5、10/8=1.25",
            Near(Value("6/4"), 1.5) && Near(Value("10/8"), 1.25),
            $"{Value("6/4")} / {Value("10/8")}");

        check("[计算器] mod 取模：7 mod 3 = 1（用关键字写法）",
            Near(Value("7 mod 3"), 1), Value("7 mod 3").ToString("R"));
    }

    // ---------------------------------------------------------------- 省略乘号（变量 x 只在引擎层）

    private static void CheckImplicitMultiplication(CheckFn check)
    {
        var parsed = MathExpression.TryParse("2x", out var expression, out var error);
        check("[计算器] 省略乘号：x=3 时 2x=6（变量只给函数绘录用，计算器会拒绝）",
            parsed && expression is not null && Near(expression.Evaluate(3), 6),
            error ?? "");

        check("[计算器] 省略乘号：2(3+4)=14、2π≈6.28318530718",
            Near(Value("2(3+4)"), 14) && Near(Value("2π"), 2 * Math.PI),
            $"{Value("2(3+4)")} / {Value("2π").ToString("R")}");

        var parsedProduct = MathExpression.TryParse("(x+1)(x-1)", out var product, out var productError);
        check("[计算器] 省略乘号：(x+1)(x-1) 在 x=4 时 = 15",
            parsedProduct && product is not null && Near(product.Evaluate(4), 15),
            productError ?? "");

        check("[计算器] 省略乘号：3sin(30)（角度制）= 1.5",
            Near(Value("3sin(30)", AngleUnit.Degrees), 1.5, 1e-9),
            Value("3sin(30)", AngleUnit.Degrees).ToString("R"));
    }

    // ---------------------------------------------------------------- 阶乘

    private static void CheckFactorial(CheckFn check)
    {
        check("[计算器] 阶乘：5!=120、0!=1、170! 仍是有限值",
            Near(Value("5!"), 120) && Near(Value("0!"), 1) && double.IsFinite(Value("170!")),
            $"{Value("5!")} / {Value("0!")} / {Value("170!").ToString("R")}");

        check("[计算器] 阶乘：(-1)! 与 5.5! 都是无定义（NaN），不抛异常",
            double.IsNaN(Value("(-1)!")) && double.IsNaN(Value("5.5!")),
            $"{Value("(-1)!")} / {Value("5.5!")}");

        check("[计算器] 阶乘结果格式化：5! 显示成 120",
            Eval("5!").Text == "120", Eval("5!").Text);
    }

    // ---------------------------------------------------------------- 角度制 / 弧度制

    private static void CheckAngleUnits(CheckFn check)
    {
        check("[计算器] 角度制：sin(90)=1、cos(180)=-1",
            Near(Value("sin(90)", AngleUnit.Degrees), 1, 1e-9)
            && Near(Value("cos(180)", AngleUnit.Degrees), -1, 1e-9),
            $"{Value("sin(90)", AngleUnit.Degrees).ToString("R")} / {Value("cos(180)", AngleUnit.Degrees).ToString("R")}");

        check("[计算器] 弧度制：sin(pi/2)=1、cos(pi)=-1",
            Near(Value("sin(pi/2)"), 1, 1e-12) && Near(Value("cos(pi)"), -1, 1e-12),
            $"{Value("sin(pi/2)").ToString("R")} / {Value("cos(pi)").ToString("R")}");

        check("[计算器] 反三角按当前单位返回：asin(1) 角度制=90、弧度制=π/2",
            Near(Value("asin(1)", AngleUnit.Degrees), 90, 1e-9)
            && Near(Value("asin(1)", AngleUnit.Radians), Math.PI / 2, 1e-12),
            $"{Value("asin(1)", AngleUnit.Degrees).ToString("R")} / {Value("asin(1)").ToString("R")}");

        check("[计算器] 角度制结果文本：sin(90) 显示为 1",
            Eval("sin(90)", AngleUnit.Degrees).Text == "1",
            Eval("sin(90)", AngleUnit.Degrees).Text);

        check("[计算器] 角度换算函数不受当前单位影响：deg(pi)=180、rad(180)=π",
            Near(Value("deg(pi)", AngleUnit.Radians), 180, 1e-9)
            && Near(Value("rad(180)", AngleUnit.Degrees), Math.PI, 1e-12),
            $"{Value("deg(pi)").ToString("R")} / {Value("rad(180)").ToString("R")}");
    }

    // ---------------------------------------------------------------- 常量 / 无定义 / ∞

    private static void CheckConstantsAndSpecialValues(CheckFn check)
    {
        check("[计算器] 常量：π / e / τ 都能解析",
            Near(Value("π"), Math.PI) && Near(Value("e"), Math.E) && Near(Value("tau"), Math.Tau),
            $"{Value("π").ToString("R")} / {Value("e").ToString("R")} / {Value("tau").ToString("R")}");

        check("[计算器] sqrt(-1) 求值为 NaN，界面文本是「无定义」（不是错误）",
            double.IsNaN(Value("sqrt(-1)")) && Eval("sqrt(-1)").Success
            && Eval("sqrt(-1)").Text == "无定义",
            Describe(Eval("sqrt(-1)")));

        check("[计算器] 1/0 求值为 +∞，界面文本是「+∞」",
            double.IsPositiveInfinity(Value("1/0")) && Eval("1/0").Text == "+∞",
            Describe(Eval("1/0")));

        check("[计算器] ln(0) 无定义、ln(-1) 无定义",
            double.IsNaN(Value("ln(0)")) && double.IsNaN(Value("ln(-1)")),
            $"{Value("ln(0)")} / {Value("ln(-1)")}");

        check("[计算器] 常量文本：π 保留 12 位有效数字",
            Eval("π").Text == "3.14159265359", Eval("π").Text);
    }

    // ---------------------------------------------------------------- 计算器符号兼容

    private static void CheckCalculatorSymbols(CheckFn check)
    {
        check("[计算器] × ÷ − 三个计算器符号：6×7=42、10÷4=2.5、2−5=-3",
            Near(Value("6×7"), 42) && Near(Value("10÷4"), 2.5) && Near(Value("2−5"), -3),
            $"{Value("6×7")} / {Value("10÷4")} / {Value("2−5")}");

        check("[计算器] 全角括号与上标： （1+2）² = 9",
            Near(Value("（1+2）²"), 9), Value("（1+2）²").ToString("R"));

        check("[计算器] 科学函数：sqrt(16)=4、cbrt(27)=3、abs(-5)=5、2^10=1024",
            Near(Value("sqrt(16)"), 4) && Near(Value("cbrt(27)"), 3)
            && Near(Value("abs(-5)"), 5) && Near(Value("2^10"), 1024),
            $"{Value("sqrt(16)")} / {Value("cbrt(27)")} / {Value("abs(-5)")} / {Value("2^10")}");

        check("[计算器] 对数：log(100)=2、log2(8)=3、log(2,8)=3",
            Near(Value("log(100)"), 2, 1e-12) && Near(Value("log2(8)"), 3, 1e-12)
            && Near(Value("log(2,8)"), 3, 1e-12),
            $"{Value("log(100)").ToString("R")} / {Value("log2(8)").ToString("R")} / {Value("log(2,8)").ToString("R")}");

        check("[计算器] 取整：floor(2.9)=2、ceil(2.1)=3、round(2.5)=3",
            Near(Value("floor(2.9)"), 2) && Near(Value("ceil(2.1)"), 3) && Near(Value("round(2.5)"), 3),
            $"{Value("floor(2.9)")} / {Value("ceil(2.1)")} / {Value("round(2.5)")}");
    }

    // ---------------------------------------------------------------- 求值封装（空 / 语法错 / 变量 x）

    private static void CheckEngineWrapper(CheckFn check)
    {
        var empty = Eval("");
        check("[计算器] 空表达式返回「空」而不是错误（结果区什么都不显示）",
            !empty.Success && !empty.HasValue && empty.Error is null,
            Describe(empty));

        var syntax = Eval("1+");
        check("[计算器] 语法错误返回中文错误提示，不抛异常",
            !syntax.Success && !string.IsNullOrWhiteSpace(syntax.Error),
            Describe(syntax));

        var thrown = false;
        var position = -1;
        try { MathExpression.Parse("1+"); }
        catch (ExpressionSyntaxException ex)
        {
            thrown = true;
            position = ex.Position;
        }
        check("[计算器] 直接 Parse 语法错误会抛 ExpressionSyntaxException 并带出错位置",
            thrown && position >= 0, $"Position={position}");

        var variable = Eval("2x");
        check("[计算器] 表达式里用了变量 x 时提示专用错误（不是算成 0）",
            !variable.Success && variable.Error == CalculatorEngine.VariableHint,
            Describe(variable));

        check("[计算器] 有效位数生效：1/3 在 4 位时是 0.3333、12 位时是 0.333333333333",
            Eval("1/3", digits: 4).Text == "0.3333"
            && Eval("1/3", digits: 12).Text == "0.333333333333",
            $"{Eval("1/3", digits: 4).Text} / {Eval("1/3", digits: 12).Text}");
    }

    // ---------------------------------------------------------------- 结果格式化

    private static void CheckFormatting(CheckFn check)
    {
        check("[计算器] 格式化：0 与 -0 都显示 0（不出现 -0）",
            MathFormat.Format(0) == "0" && MathFormat.Format(-0.0) == "0",
            $"{MathFormat.Format(0)} / {MathFormat.Format(-0.0)}");

        check("[计算器] 格式化：去掉末尾多余的 0（1.5000 → 1.5、3.0 → 3）",
            MathFormat.Format(1.5) == "1.5" && MathFormat.Format(3.0) == "3",
            $"{MathFormat.Format(1.5)} / {MathFormat.Format(3.0)}");

        check("[计算器] 格式化：有效位数 6 时 1/3 = 0.333333",
            MathFormat.Format(1.0 / 3.0, 6) == "0.333333",
            MathFormat.Format(1.0 / 3.0, 6));

        check("[计算器] 格式化：大数用科学计数法 1.2e+15、很小的数也用 e 写法",
            MathFormat.Format(1.2e15) == "1.2e+15"
            && MathFormat.Format(1.2e-9).Contains("e-"),
            $"{MathFormat.Format(1.2e15)} / {MathFormat.Format(1.2e-9)}");

        check("[计算器] 格式化：NaN → 无定义、+∞ / -∞ → +∞ / -∞",
            MathFormat.Format(double.NaN) == "无定义"
            && MathFormat.Format(double.PositiveInfinity) == "+∞"
            && MathFormat.Format(double.NegativeInfinity) == "-∞",
            $"{MathFormat.Format(double.NaN)} / {MathFormat.Format(double.PositiveInfinity)} / {MathFormat.Format(double.NegativeInfinity)}");

        check("[计算器] FormatFixed：固定小数位（1/3 两位 = 0.33、2 三位 = 2）",
            MathFormat.FormatFixed(1.0 / 3.0, 2) == "0.33"
            && MathFormat.FormatFixed(2.0, 3) == "2",
            $"{MathFormat.FormatFixed(1.0 / 3.0, 2)} / {MathFormat.FormatFixed(2.0, 3)}");
    }

    // ---------------------------------------------------------------- 任意精度大数（精确优先）

    /// <summary>
    /// v1.16 起的「精确优先」：整数 / 有限小数走 BigInteger 十进制引擎，超大结果不再丢位或变 ∞；
    /// 函数 / 常量 / 除不尽照旧回退 double。这里断言两条路径的分工，防止以后有人把精确路径删掉。
    /// </summary>
    private static void CheckExactBigNumbers(CheckFn check)
    {
        var bigFactorial = Eval("100!");
        check("[计算器] 精确路径：100! 出完整 158 位整数（不再变 ∞）",
            bigFactorial.Success && bigFactorial.IsExact && bigFactorial.Text.Length == 158
            && bigFactorial.Text.StartsWith("93326215443944152681699238856266700490715968264381621468592963895217599993229915608941463976156518286253697920827223758251185210916864000000000000000000000000",
                StringComparison.Ordinal),
            $"len={bigFactorial.Text.Length}");

        var power = Eval("2^1000");
        check("[计算器] 精确路径：2^1000 出完整 302 位整数，首尾数字都对",
            power.IsExact && power.Text.Length == 302
            && power.Text.StartsWith("10715086071862673209", StringComparison.Ordinal)
            && power.Text.EndsWith("205668069376", StringComparison.Ordinal),
            $"len={power.Text.Length} head={power.Text[..20]} tail={power.Text[^12..]}");

        var bigSum = Eval("12345678901234567890+1");
        check("[计算器] 精确路径：20 位大整数相加不丢位（12345678901234567890 + 1）",
            bigSum.IsExact && bigSum.Text == "12345678901234567891", bigSum.Text);

        check("[计算器] 精确路径：0.1+0.2 显示 0.3（不是 0.30000000000000004）",
            Eval("0.1+0.2").Text == "0.3", Eval("0.1+0.2").Text);

        check("[计算器] 精确路径：6/4 显示 1.5、1/8 显示 0.125（除得尽就走精确路径）",
            Eval("6/4") is { IsExact: true, Text: "1.5" } && Eval("1/8") is { IsExact: true, Text: "0.125" },
            $"{Eval("6/4").Text} / {Eval("1/8").Text}");

        check("[计算器] 精确路径：超大精确结果不会改成科学计数法",
            Eval("10^30").Text == "1" + new string('0', 30) && Eval("10^30").Text.Length == 31,
            Eval("10^30").Text);

        check("[计算器] 回退路径：1/3 仍按有效位数显示 0.333333333333（除不尽不接管）",
            !Eval("1/3").IsExact && Eval("1/3").Text == "0.333333333333", Eval("1/3").Text);

        check("[计算器] 回退路径：sin(90) 角度制仍是 1、π 仍是 12 位小数（函数 / 常量不接管）",
            !Eval("sin(90)", AngleUnit.Degrees).IsExact && Eval("sin(90)", AngleUnit.Degrees).Text == "1"
            && !Eval("π").IsExact && Eval("π").Text == "3.14159265359",
            $"{Eval("sin(90)", AngleUnit.Degrees).Text} / {Eval("π").Text}");

        check("[计算器] 回退路径：2^-1 与 2^0.5 交给 double（0.5 / √2 ≈ 1.41421356237）",
            !Eval("2^-1").IsExact && Near(Value("2^-1"), 0.5)
            && !Eval("2^0.5").IsExact && Near(Value("2^0.5"), Math.Sqrt(2), 1e-9),
            $"{Eval("2^-1").Text} / {Eval("2^0.5").Text}");

        check("[计算器] 回退路径：结果位数失控时回退成 double 的 +∞，不卡死",
            Eval("9^1000000").Text == "+∞", Eval("9^1000000").Text);

        check("[计算器] 精确路径：5! 与 20!/18! 仍按整数给出（阶乘 / 大整数除法）",
            Eval("20!/18!") is { IsExact: true, Text: "380" }, Eval("20!/18!").Text);

        check("[计算器] 精确路径：mod 取模结果精确（7 mod 3 = 1、10.5 mod 1 = 0.5）",
            Eval("7 mod 3") is { IsExact: true, Text: "1" } && Eval("10.5 mod 1").Text == "0.5",
            $"{Eval("7 mod 3").Text} / {Eval("10.5 mod 1").Text}");

        var exactAns = Eval("2^200");
        check("[计算器] Ans：精确大数的 Ans 文本就是完整数字（不塞回 double 丢精度）",
            exactAns.IsExact && CalculatorEngine.GetAnsText(exactAns) == exactAns.Text
            && exactAns.Text.Length == 61,
            $"len={CalculatorEngine.GetAnsText(exactAns).Length}");
    }

    // ---------------------------------------------------------------- 输入构造

    private static void CheckInput(CheckFn check)
    {
        check("[计算器] 输入：追加数字 / 小数点 / 运算符",
            CalculatorInput.Append("", "1") == "1"
            && CalculatorInput.Append("1", "2") == "12"
            && CalculatorInput.Append("1", "+") == "1+"
            && CalculatorInput.Append("1+", "2") == "1+2",
            CalculatorInput.Append("1+", "2"));

        check("[计算器] 输入：同一个数字里小数点去重（1.2 再按 . 不变，1+2 可以加小数点）",
            CalculatorInput.Append("1", ".") == "1."
            && CalculatorInput.Append("1.2", ".") == "1.2"
            && CalculatorInput.Append("1+2", ".") == "1+2.",
            $"{CalculatorInput.Append("1.2", ".")} / {CalculatorInput.Append("1+2", ".")}");

        check("[计算器] 输入：退格删一个字符、空串安全；清空返回空串",
            CalculatorInput.Backspace("123") == "12"
            && CalculatorInput.Backspace("1+") == "1"
            && CalculatorInput.Backspace("") == ""
            && CalculatorInput.Backspace(null) == ""
            && CalculatorInput.Clear() == "",
            CalculatorInput.Backspace("123"));

        check("[计算器] 输入：± 对纯数字直接加 / 去负号（12 ↔ -12）",
            CalculatorInput.ToggleSign("12") == "-12"
            && CalculatorInput.ToggleSign("-12") == "12"
            && CalculatorInput.ToggleSign("12.5") == "-12.5",
            CalculatorInput.ToggleSign("12"));

        check("[计算器] 输入：± 对复合表达式整体取负，再按一次还原；空输入变成负号",
            CalculatorInput.ToggleSign("5+3") == "-(5+3)"
            && CalculatorInput.ToggleSign("-(5+3)") == "5+3"
            && CalculatorInput.ToggleSign("") == "-"
            && Near(Value(CalculatorInput.ToggleSign("5+3")), -8),
            CalculatorInput.ToggleSign("5+3"));

        check("[计算器] 输入：Ans 插入会自动补省略乘号（2+1.5 / 2*1.5）",
            CalculatorInput.InsertNumber("", "1.5") == "1.5"
            && CalculatorInput.InsertNumber("2+", "1.5") == "2+1.5"
            && CalculatorInput.InsertNumber("2", "1.5") == "2*1.5",
            $"{CalculatorInput.InsertNumber("2+", "1.5")} / {CalculatorInput.InsertNumber("2", "1.5")}");

        check("[计算器] 输入：Ans 插入负数时表达式仍能算（2+-1.5 = 0.5）",
            CalculatorInput.InsertNumber("2+", "-1.5") == "2+-1.5"
            && Near(Value("2+-1.5"), 0.5),
            CalculatorInput.InsertNumber("2+", "-1.5"));

        check("[计算器] 输入：x² 在末尾操作数后加 ²（25² = 625），末尾不是操作数时不动",
            CalculatorInput.AppendSquare("25") == "25²"
            && CalculatorInput.AppendSquare("") == ""
            && CalculatorInput.AppendSquare("2+") == "2+"
            && Near(Value(CalculatorInput.AppendSquare("25")), 625),
            CalculatorInput.AppendSquare("25"));

        check("[计算器] 输入：1/x 把整条表达式放进 1/(…)，空输入给 1/",
            CalculatorInput.WrapReciprocal("") == "1/"
            && CalculatorInput.WrapReciprocal("4") == "1/(4)"
            && Near(Value(CalculatorInput.WrapReciprocal("4")), 0.25),
            CalculatorInput.WrapReciprocal("4"));

        check("[计算器] 输入：纯数字判定 / 括号配平判定",
            CalculatorInput.IsPlainNumber("12.5") && CalculatorInput.IsPlainNumber("-3")
            && !CalculatorInput.IsPlainNumber("1+2") && !CalculatorInput.IsPlainNumber("")
            && CalculatorInput.HasBalancedParentheses("(1+2)")
            && !CalculatorInput.HasBalancedParentheses("(1+2"),
            $"{CalculatorInput.IsPlainNumber("12.5")} / {CalculatorInput.HasBalancedParentheses("(1+2")}");
    }

    // ---------------------------------------------------------------- 历史记录

    private static void CheckHistory(CheckFn check)
    {
        check("[计算器] 历史：拼成「表达式 = 结果」并能拆回来",
            CalculatorHistory.Compose("2+3", "5") == "2+3 = 5"
            && CalculatorHistory.GetExpression("2+3 = 5") == "2+3"
            && CalculatorHistory.GetResultText("2+3 = 5") == "5",
            CalculatorHistory.Compose("2+3", "5"));

        var item = new CalculatorHistoryItem("2+3 = 5");
        check("[计算器] 历史行：回填用的是表达式部分，显示的是结果部分",
            item.Expression == "2+3" && item.ResultText == "5"
            && new CalculatorHistoryItem("没有等号的旧数据").Expression == "没有等号的旧数据",
            $"{item.Expression} / {item.ResultText}");

        check("[计算器] 历史：新记录排在最前面",
            CalculatorHistory.Add(new[] { "1+1 = 2" }, "2+2 = 4", 50)
                .SequenceEqual(new[] { "2+2 = 4", "1+1 = 2" }),
            string.Join(" | ", CalculatorHistory.Add(new[] { "1+1 = 2" }, "2+2 = 4", 50)));

        check("[计算器] 历史：相邻去重（连着按两次 Enter 不会刷屏）",
            CalculatorHistory.Add(new[] { "1+1 = 2" }, "1+1 = 2", 50).Count == 1,
            CalculatorHistory.Add(new[] { "1+1 = 2" }, "1+1 = 2", 50).Count.ToString());

        check("[计算器] 历史：超上限裁剪（最多 3 条时只留最新的 3 条）",
            CalculatorHistory.Add(new[] { "3", "2", "1" }, "4", 3)
                .SequenceEqual(new[] { "4", "3", "2" }),
            string.Join(" | ", CalculatorHistory.Add(new[] { "3", "2", "1" }, "4", 3)));

        check("[计算器] 历史：空 / 空白记录不会进列表；Trim 按上限保留前面的",
            CalculatorHistory.Add(null, "  ", 50).Count == 0
            && CalculatorHistory.Add(null, null, 50).Count == 0
            && CalculatorHistory.Trim(new[] { "a", "b", "c" }, 2).SequenceEqual(new[] { "a", "b" }),
            string.Join(" | ", CalculatorHistory.Trim(new[] { "a", "b", "c" }, 2)));

        check("[计算器] 历史：清空返回空列表",
            CalculatorHistory.Clear().Count == 0);
    }

    // ---------------------------------------------------------------- 配置

    private static void CheckConfig(CheckFn check)
    {
        var config = new CalculatorConfig();
        check("[计算器] 配置默认：弧度 / 12 位有效数字 / 50 条历史 / 实时预览开",
            config.AngleUnit == "弧度" && config.SignificantDigits == 12
            && config.HistoryLimit == 50 && config.EnableLivePreview
            && config.History.Count == 0
            && config.AngleUnitValue == AngleUnit.Radians
            && config.SafeSignificantDigits == 12 && config.SafeHistoryLimit == 50,
            config.ToJson());

        check("[计算器] 配置：角度单位只有「弧度 / 角度」两个选项，且属性会映射成枚举",
            CalculatorConfig.AngleUnitOptions.SequenceEqual(new[] { "弧度", "角度" })
            && CalculatorConfig.NormalizeAngleUnit(null) == "弧度"
            && CalculatorConfig.NormalizeAngleUnit("乱写的值") == "弧度"
            && CalculatorConfig.NormalizeAngleUnit("角度") == "角度"
            && calculatorConfigWithAngle("角度").AngleUnitValue == AngleUnit.Degrees,
            string.Join(" / ", CalculatorConfig.AngleUnitOptions));

        var clamped = new CalculatorConfig { SignificantDigits = 99, HistoryLimit = 999 };
        check("[计算器] 配置夹取：有效位数上限 12、历史条数上限 200",
            clamped.SafeSignificantDigits == 12 && clamped.SafeHistoryLimit == 200,
            $"{clamped.SafeSignificantDigits} / {clamped.SafeHistoryLimit}");

        clamped.SignificantDigits = 1;
        clamped.HistoryLimit = 1;
        check("[计算器] 配置夹取：有效位数下限 3、历史条数下限 10",
            clamped.SafeSignificantDigits == 3 && clamped.SafeHistoryLimit == 10,
            $"{clamped.SafeSignificantDigits} / {clamped.SafeHistoryLimit}");

        var roundTrip = new CalculatorConfig
        {
            AngleUnit = "角度",
            SignificantDigits = 8,
            HistoryLimit = 120,
            EnableLivePreview = false,
        };
        roundTrip.History.Add("2+3 = 5");
        roundTrip.History.Add("sin(90) = 1");

        var json = JsonFileStore.Serialize(roundTrip);
        var parsed = JsonSerializer.Deserialize<CalculatorConfig>(json, JsonFileStore.Options);

        check("[计算器] 配置 JSON 往返：角度 / 精度 / 历史条数 / 实时预览 / 历史记录都不丢",
            parsed is not null
            && parsed.AngleUnit == "角度" && parsed.SignificantDigits == 8
            && parsed.HistoryLimit == 120 && !parsed.EnableLivePreview
            && parsed.History.SequenceEqual(new[] { "2+3 = 5", "sin(90) = 1" }),
            json);

        check("[计算器] 配置 JSON 不写只读计算属性（SafeSignificantDigits / AngleUnitValue）",
            !json.Contains("safeSignificantDigits", StringComparison.Ordinal)
            && !json.Contains("angleUnitValue", StringComparison.Ordinal),
            json.Length > 200 ? json[..200] + "…" : json);
    }

    /// <summary>配置属性写进去以后再读枚举映射（小工具，避免在 check 里写多行）。</summary>
    private static CalculatorConfig calculatorConfigWithAngle(string angleUnit)
        => new() { AngleUnit = angleUnit };

    // ---------------------------------------------------------------- 键盘元数据

    private static void CheckKeypad(CheckFn check)
    {
        var basic = CalculatorKey.Basic;
        var scientific = CalculatorKey.Scientific;

        check("[计算器] 基础键盘共 24 个键，0-9 与常用运算符 / 括号 / = 齐全",
            basic.Count == 24
            && Enumerable.Range(0, 10).All(n => Key(basic, n.ToString()) is not null)
            && Key(basic, ".") is not null && Key(basic, "+") is not null && Key(basic, "−") is not null
            && Key(basic, "×") is not null && Key(basic, "÷") is not null
            && Key(basic, "(") is not null && Key(basic, ")") is not null,
            basic.Count.ToString());

        check("[计算器] 基础键盘：C / ⌫ / ± / Ans / x² / 1/x / = 都在，= 是主按钮",
            Key(basic, "C")?.Kind == CalculatorKeyKind.Clear
            && Key(basic, "⌫")?.Kind == CalculatorKeyKind.Backspace
            && Key(basic, "±")?.Kind == CalculatorKeyKind.ToggleSign
            && Key(basic, "Ans")?.Kind == CalculatorKeyKind.InsertAns
            && Key(basic, "x²")?.Kind == CalculatorKeyKind.Square
            && Key(basic, "1/x")?.Kind == CalculatorKeyKind.Reciprocal
            && Key(basic, "=")?.Kind == CalculatorKeyKind.Evaluate
            && Key(basic, "=")?.IsAccent == true,
            "C / ⌫ / ± / Ans / x² / 1/x / =");

        check("[计算器] 科学键盘共 24 个键，题目要求的函数 / 常量全在",
            scientific.Count == 24
            && new[]
            {
                "sin", "cos", "tan", "asin", "acos", "atan", "ln", "log", "log2", "sqrt", "cbrt",
                "^", "x²", "!", "π", "e", "abs", "exp", "floor", "ceil", "round", "1/x", "mod",
            }.All(label => Key(scientific, label) is not null),
            string.Join(" / ", scientific.Select(key => key.Label)));

        check("[计算器] 键盘按钮的追加文本是解析器认识的写法（×→*、÷→/、mod 带空格）",
            Key(basic, "×")?.Insert == "*" && Key(basic, "÷")?.Insert == "/"
            && Key(basic, "−")?.Insert == "-" && Key(basic, "+")?.Insert == "+"
            && Key(scientific, "mod")?.Insert == " mod "
            && Key(scientific, "sin")?.Insert == "sin("
            && Key(scientific, "π")?.Insert == "π"
            && Near(Value("2" + Key(basic, "×")?.Insert + "3"), 6),
            $"{Key(basic, "×")?.Insert} / {Key(scientific, "mod")?.Insert} / {Key(scientific, "sin")?.Insert}");

        check("[计算器] 键盘按钮：追加函数名后补个数字就能算（sin( + 30 + ) 角度制 = 1）",
            Near(Value((Key(scientific, "sin")?.Insert ?? "") + "30)", AngleUnit.Degrees), 0.5, 1e-9),
            Value((Key(scientific, "sin")?.Insert ?? "") + "30)", AngleUnit.Degrees).ToString("R"));
    }
}
