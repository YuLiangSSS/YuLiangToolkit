using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using YuLiangToolkit.Core.Configuration;
using YuLiangToolkit.Core.Storage;
using YuLiangToolkit.Tools.SpugCaptcha;

namespace YuLiangToolkit.FrameworkSelfCheck;

/// <summary>
/// 「Spug 验证码测试工具」的自检：接口纯逻辑（URL / 参数校验、GET 地址拼接、POST JSON、响应解析、
/// 本地验证、打码、配置落盘）逐条断言；再用一个假的 <see cref="HttpMessageHandler"/> 把
/// <see cref="SpugSender"/> 的请求方法 / Content-Type / body 真跑一遍。
///
/// <para>全程**不碰外网**、不发真实短信 / 邮件（真联网那步由人工用工具 / 临时控制台验收）。</para>
/// </summary>
internal static class SpugChecks
{
    /// <summary>自检的断言回调：说明文字可以省，与其它 Checks 文件的写法兼容。</summary>
    private delegate void CheckFn(string name, bool ok, string detail = "");

    public static void Run(Action<string, bool, string> check, Action<string, bool> report)
    {
        CheckFn assert = (name, ok, detail) => check(name, ok, detail);

        CheckMetadata(assert);
        CheckValidation(assert);
        CheckSmsUrl(assert);
        CheckMailJson(assert);
        CheckResponseParsing(assert);
        CheckMasking(assert);
        CheckVerifier(assert);
        CheckSender(assert);
        CheckConfig(assert);
    }

    // ---------------------------------------------------------------- 工具元数据

    private static void CheckMetadata(CheckFn check)
    {
        var tool = new SpugCaptchaTool();
        check("[Spug] 工具 Id / 名称 / 分类 / 排序符合约定",
            tool.Id == "spug-captcha" && tool.Name == "Spug 验证码测试"
            && tool.Category == "开发" && tool.Order == 910
            && tool.Tags.Contains("Spug") && tool.Tags.Contains("验证码"),
            $"{tool.Id} / {tool.Name} / {tool.Category} / {tool.Order}");
        check("[Spug] 工具描述里写明用途（验证码 / 模板 URL）",
            tool.Description.Contains("验证码") && tool.Description.Contains("URL"), tool.Description);
    }

    // ---------------------------------------------------------------- 输入校验

    private static void CheckValidation(CheckFn check)
    {
        check("[Spug] URL 校验：http / https 绝对地址通过，空 / 相对 / 其它协议不通过",
            SpugApi.IsHttpUrl("https://push.spug.cc/sms/x")
            && SpugApi.IsHttpUrl("http://127.0.0.1:8080/mail/x")
            && !SpugApi.IsHttpUrl("") && !SpugApi.IsHttpUrl("/sms/x") && !SpugApi.IsHttpUrl("ftp://x/y"));

        check("[Spug] 模板类型识别：按路径 /sms/ 与 /mail/ 区分",
            SpugApi.IsSmsTemplateUrl("https://push.spug.cc/sms/abc")
            && !SpugApi.IsSmsTemplateUrl("https://push.spug.cc/mail/abc")
            && SpugApi.IsMailTemplateUrl("https://push.spug.cc/mail/abc")
            && !SpugApi.IsMailTemplateUrl("https://push.spug.cc/sms/abc")
            && !SpugApi.IsMailTemplateUrl("https://push.spug.cc/other/abc"));

        check("[Spug] 手机号校验：11 位以 1 开头才算有效",
            SpugApi.IsValidPhone("13800138000")
            && !SpugApi.IsValidPhone("12800138000")
            && !SpugApi.IsValidPhone("1380013800")
            && !SpugApi.IsValidPhone("138001380001")
            && !SpugApi.IsValidPhone("1380013800a")
            && !SpugApi.IsValidPhone(""));

        check("[Spug] 验证码校验：4~6 位数字或字母",
            SpugApi.IsValidCode("1234") && SpugApi.IsValidCode("ABCDEF") && SpugApi.IsValidCode("a1B2")
            && !SpugApi.IsValidCode("123") && !SpugApi.IsValidCode("1234567") && !SpugApi.IsValidCode("12 34"));

        check("[Spug] 短信有效分钟数：1~120 的整数（0 / 121 / 非数字都挡掉）",
            SpugApi.IsValidSmsMinutes("5") && SpugApi.IsValidSmsMinutes("120")
            && !SpugApi.IsValidSmsMinutes("0") && !SpugApi.IsValidSmsMinutes("121")
            && !SpugApi.IsValidSmsMinutes("五分钟") && !SpugApi.IsValidSmsMinutes(""));

        check("[Spug] 邮箱校验：常见地址通过，缺 @ / 多地址 / 空格不通过",
            SpugApi.IsValidEmail("user@example.com") && SpugApi.IsValidEmail("a.b+tag@sub.example.cn")
            && !SpugApi.IsValidEmail("user.example.com")
            && !SpugApi.IsValidEmail("a@b.com,c@d.com")
            && !SpugApi.IsValidEmail("a b@example.com")
            && !SpugApi.IsValidEmail(""));
    }

    // ---------------------------------------------------------------- 短信 GET 地址

    private static void CheckSmsUrl(CheckFn check)
    {
        var basic = SpugApi.BuildSmsRequestUrl("https://push.spug.cc/sms/CODE", "13800138000", "123456", null);
        check("[Spug] 短信请求：拼出 ?to=&code=，没有 number 时不多带参数",
            basic == "https://push.spug.cc/sms/CODE?to=13800138000&code=123456", basic);

        var withNumber = SpugApi.BuildSmsRequestUrl("https://push.spug.cc/sms/CODE", "13800138000", "A1B2", "5");
        check("[Spug] 短信请求：带有效时长时追加 number",
            withNumber == "https://push.spug.cc/sms/CODE?to=13800138000&code=A1B2&number=5", withNumber);

        var existingQuery = SpugApi.BuildSmsRequestUrl("https://push.spug.cc/sms/CODE?foo=1", "13800138000", "1234", null);
        check("[Spug] 短信请求：保留 URL 已有 query，用 & 追加",
            existingQuery == "https://push.spug.cc/sms/CODE?foo=1&to=13800138000&code=1234", existingQuery);

        var trailingQuestion = SpugApi.BuildSmsRequestUrl("https://push.spug.cc/sms/CODE?", "13800138000", "1234", "5");
        check("[Spug] 短信请求：URL 以 ? 结尾时不拼出 ?&",
            trailingQuestion == "https://push.spug.cc/sms/CODE?to=13800138000&code=1234&number=5", trailingQuestion);

        var fragment = SpugApi.BuildSmsRequestUrl("https://push.spug.cc/sms/CODE#frag", "13800138000", "1234", null);
        check("[Spug] 短信请求：丢掉 # 片段，参数不会跑到片段后面",
            fragment == "https://push.spug.cc/sms/CODE?to=13800138000&code=1234", fragment);

        var encoded = SpugApi.BuildSmsRequestUrl("https://push.spug.cc/sms/CODE", "13800138000", "a b", null);
        check("[Spug] 短信请求：参数值做百分号编码",
            encoded.EndsWith("code=a%20b"), encoded);
    }

    // ---------------------------------------------------------------- 邮箱 POST JSON

    private static void CheckMailJson(CheckFn check)
    {
        var json = SpugApi.BuildMailJson("user@example.com", "登录验证", "123456", "5");
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        check("[Spug] 邮箱请求 JSON：字段是 to / scene / code / minute",
            root.GetProperty("to").GetString() == "user@example.com"
            && root.GetProperty("scene").GetString() == "登录验证"
            && root.GetProperty("code").GetString() == "123456"
            && root.GetProperty("minute").GetString() == "5",
            json);

        var chinese = SpugApi.BuildMailJson("a@b.com", "注册验证", "ABCD", "五分钟");
        using var chineseDocument = JsonDocument.Parse(chinese);
        var chineseRoot = chineseDocument.RootElement;
        check("[Spug] 邮箱请求 JSON：中文 scene / 中文分钟数解析回来一致（JSON 里转义不影响）",
            chineseRoot.GetProperty("scene").GetString() == "注册验证"
            && chineseRoot.GetProperty("minute").GetString() == "五分钟", chinese);
    }

    // ---------------------------------------------------------------- 响应解析

    private static void CheckResponseParsing(CheckFn check)
    {
        var ok = SpugApi.ParseResponse("""{"code": 200, "msg": "请求成功", "request_id": "N6KQ8m2W4xY7p9aB"}""", 200);
        check("[Spug] 响应解析：code=200 判成功，取出 msg / request_id",
            ok.Success && ok.Code == 200 && ok.Message == "请求成功" && ok.RequestId == "N6KQ8m2W4xY7p9aB",
            $"{ok.Code} / {ok.Message} / {ok.RequestId}");

        var badPhone = SpugApi.ParseResponse("""{"code": 400, "msg": "to参数包含无效手机号"}""", 200);
        check("[Spug] 响应解析：业务失败（HTTP 仍 200）判失败并保留中文 msg",
            !badPhone.Success && badPhone.Code == 400 && badPhone.Message.Contains("无效手机号"),
            $"{badPhone.Code} / {badPhone.Message}");

        var badToken = SpugApi.ParseResponse("""{"code": 404, "msg": "模板编码错误"}""", 200);
        check("[Spug] 响应解析：404 模板编码错误也按失败处理",
            !badToken.Success && badToken.Code == 404, $"{badToken.Code} / {badToken.Message}");

        var notJson = SpugApi.ParseResponse("<html>500</html>", 500);
        check("[Spug] 响应解析：非 JSON 响应转成可读失败，不抛异常",
            !notJson.Success && notJson.Code == -1 && notJson.Message.Contains("不是合法 JSON")
            && notJson.RawBody == "<html>500</html>", notJson.Message);

        var empty = SpugApi.ParseResponse("", 200);
        check("[Spug] 响应解析：空响应体也安全（判失败）", !empty.Success && empty.Code == -1);

        var array = SpugApi.ParseResponse("[]", 200);
        check("[Spug] 响应解析：JSON 数组结构不认识 → 判失败", !array.Success && array.Message.Contains("格式无法识别"));
    }

    // ---------------------------------------------------------------- 打码 / 验证码生成

    private static void CheckMasking(CheckFn check)
    {
        var masked = SpugApi.MaskUrl("https://push.spug.cc/sms/DemoTokenABCDEFGH123456");
        check("[Spug] 日志 URL 打码：模板编码只露前 4 后 4",
            masked.Contains("Demo***3456") && !masked.Contains("DemoTokenABCDEFGH123456"), masked);

        check("[Spug] 手机号 / 邮箱打码",
            SpugApi.MaskPhone("13800138000") == "138****8000"
            && SpugApi.MaskEmail("demo@example.com") == "de***@example.com"
            && SpugApi.MaskPhone("") == "***", SpugApi.MaskEmail("demo@example.com"));

        var codes = Enumerable.Range(0, 200).Select(_ => SpugCaptchaVerifier.GenerateCode()).ToList();
        check("[Spug] 本地验证码生成：6 位纯数字",
            codes.All(code => code.Length == 6 && code.All(char.IsDigit)),
            string.Join(",", codes.Take(3)));

        var randomResolved = SpugCaptchaVerifier.TryResolveCode("", out var randomCode, out var randomError);
        check("[Spug] 验证码选择：留空 → 随机生成 6 位、没有错误",
            randomResolved && randomError.Length == 0 && randomCode.Length == 6 && randomCode.All(char.IsDigit),
            randomCode);

        var customResolved = SpugCaptchaVerifier.TryResolveCode("  AB12  ", out var customCode, out var customError);
        check("[Spug] 验证码选择：填了自定义 → 用自定义值（去首尾空格）",
            customResolved && customError.Length == 0 && customCode == "AB12", customCode);

        var badResolved = SpugCaptchaVerifier.TryResolveCode("abc", out _, out var badCodeError);
        check("[Spug] 验证码选择：自定义格式不对 → 拒绝并给原因",
            !badResolved && badCodeError.Contains("4~6 位"), badCodeError);
    }

    // ---------------------------------------------------------------- 本地验证

    private static void CheckVerifier(CheckFn check)
    {
        var now = DateTimeOffset.Now;
        var sms = new PendingCode(SpugChannel.Sms, "123456", "13800138000", now);
        var mail = new PendingCode(SpugChannel.Mail, "ABCDEF", "user@example.com", now);

        check("[Spug] 验证：没发过验证码 → 失败并提示先发送",
            !SpugCaptchaVerifier.Verify("123456", Array.Empty<PendingCode>()).Success);

        check("[Spug] 验证：空输入 / 格式不对 → 失败",
            !SpugCaptchaVerifier.Verify("", new[] { sms }).Success
            && SpugCaptchaVerifier.Verify("abc", new[] { sms }).Message.Contains("4~6 位"));

        var wrong = SpugCaptchaVerifier.Verify("999999", new[] { sms, mail });
        check("[Spug] 验证：对不上 → 失败，并列出已发送通道",
            !wrong.Success && wrong.Message.Contains("不匹配") && wrong.Message.Contains("短信") && wrong.Message.Contains("邮箱"),
            wrong.Message);

        var smsOk = SpugCaptchaVerifier.Verify("  123456  ", new[] { sms, mail });
        check("[Spug] 验证：短信验证码通过（忽略首尾空格），只匹配短信通道",
            smsOk.Success && smsOk.MatchedChannels.Count == 1 && smsOk.MatchedChannels[0] == "短信",
            string.Join("+", smsOk.MatchedChannels));

        var mailOk = SpugCaptchaVerifier.Verify("abcdef", new[] { sms, mail });
        check("[Spug] 验证：邮箱验证码不区分大小写，匹配邮箱通道",
            mailOk.Success && mailOk.MatchedChannels.SequenceEqual(new[] { "邮箱" }), mailOk.Message);

        var bothPending = new[]
        {
            new PendingCode(SpugChannel.Sms, "666888", "13800138000", now),
            new PendingCode(SpugChannel.Mail, "666888", "user@example.com", now),
        };
        var bothOk = SpugCaptchaVerifier.Verify("666888", bothPending);
        check("[Spug] 验证：两个通道发了同一个码 → 同时匹配短信 + 邮箱",
            bothOk.Success && bothOk.MatchedChannels.Count == 2, bothOk.Message);
    }

    // ---------------------------------------------------------------- 真实 HTTP 层（假 handler）

    private static void CheckSender(CheckFn check)
    {
        var smsHandler = new StubHandler(_ => JsonResponse("""{"code":200,"msg":"请求成功","request_id":"req-1"}"""));
        var smsSender = new SpugSender(new HttpClient(smsHandler));
        var smsResult = smsSender.SendSmsAsync("https://push.spug.cc/sms/CODE", "13800138000", "123456", "5")
            .GetAwaiter().GetResult();
        check("[Spug] 发送器：短信走 GET，query 带 to / code / number",
            smsResult.Success && smsHandler.LastMethod == HttpMethod.Get
            && smsHandler.LastUri!.Contains("?to=13800138000") && smsHandler.LastUri.Contains("&code=123456")
            && smsHandler.LastUri.Contains("&number=5"),
            $"{smsHandler.LastMethod} {smsHandler.LastUri}");

        var mailHandler = new StubHandler(_ => JsonResponse("""{"code":400,"msg":"to参数包含无效邮箱地址"}"""));
        var mailSender = new SpugSender(new HttpClient(mailHandler));
        var mailResult = mailSender.SendMailAsync("https://push.spug.cc/mail/CODE", "user@example.com", "登录验证", "ABCDEF", "5")
            .GetAwaiter().GetResult();
        var mailBody = mailHandler.LastBody ?? "";
        using var mailDocument = JsonDocument.Parse(mailBody);
        var mailRoot = mailDocument.RootElement;
        check("[Spug] 发送器：邮箱走 POST + application/json，body 带 to / scene / code / minute",
            mailHandler.LastMethod == HttpMethod.Post
            && mailHandler.LastContentType == "application/json"
            && mailRoot.GetProperty("to").GetString() == "user@example.com"
            && mailRoot.GetProperty("scene").GetString() == "登录验证"
            && mailRoot.GetProperty("code").GetString() == "ABCDEF"
            && mailRoot.GetProperty("minute").GetString() == "5",
            $"{mailHandler.LastMethod} {mailHandler.LastContentType} {mailBody}");
        check("[Spug] 发送器：业务失败（code=400）原样返回失败原因",
            !mailResult.Success && mailResult.Code == 400 && mailResult.Message.Contains("无效邮箱"),
            mailResult.Message);

        var boomHandler = new StubHandler(_ => throw new HttpRequestException("模拟断网"));
        var boomSender = new SpugSender(new HttpClient(boomHandler));
        var boomResult = boomSender.SendSmsAsync("https://push.spug.cc/sms/CODE", "13800138000", "123456", null)
            .GetAwaiter().GetResult();
        check("[Spug] 发送器：网络异常不抛到界面，转成中文失败说明",
            !boomResult.Success && boomResult.Code == -1 && boomResult.Message.Contains("网络请求失败"),
            boomResult.Message);
    }

    // ---------------------------------------------------------------- 配置

    private static void CheckConfig(CheckFn check)
    {
        var store = ToolConfigStore.For("spug-captcha");
        var cfg = store.Load<SpugCaptchaConfig>();
        check("[Spug] 配置：首次加载即有默认值（5 分钟 / 登录验证）",
            cfg.SmsMinutes == "5" && cfg.MailMinutes == "5" && cfg.MailScene == "登录验证",
            $"SmsMinutes={cfg.SmsMinutes}, MailScene={cfg.MailScene}, MailMinutes={cfg.MailMinutes}");

        cfg.SmsUrl = "https://push.spug.cc/sms/selfcheck";
        cfg.MailUrl = "https://push.spug.cc/mail/selfcheck";
        cfg.Phone = "13800138000";
        cfg.Email = "user@example.com";
        Thread.Sleep(800);
        var saved = JsonFileStore.Read<SpugCaptchaConfig>(cfg.FilePath);
        check("[Spug] 配置：URL / 收件人改动会自动落盘（无保存按钮）",
            saved is
            {
                SmsUrl: "https://push.spug.cc/sms/selfcheck",
                MailUrl: "https://push.spug.cc/mail/selfcheck",
                Phone: "13800138000",
                Email: "user@example.com",
            },
            cfg.FilePath);

        check("[Spug] 配置：不把验证码之类的运行时字段写进 JSON",
            !cfg.ToJson().Contains("pending", StringComparison.OrdinalIgnoreCase)
            && !cfg.ToJson().Contains("verifycode", StringComparison.OrdinalIgnoreCase),
            cfg.ToJson());
    }

    // ---------------------------------------------------------------- 假 HTTP handler

    /// <summary>假 handler：记录请求方法 / URL / Content-Type / body，按给定逻辑回响应。</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

        public HttpMethod? LastMethod { get; private set; }
        public string? LastUri { get; private set; }
        public string? LastContentType { get; private set; }
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastMethod = request.Method;
            LastUri = request.RequestUri?.ToString();
            LastContentType = request.Content?.Headers.ContentType?.MediaType;
            if (request.Content is not null)
                LastBody = await request.Content.ReadAsStringAsync(cancellationToken);
            return _responder(request);
        }
    }

    private static HttpResponseMessage JsonResponse(string json)
        => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}
