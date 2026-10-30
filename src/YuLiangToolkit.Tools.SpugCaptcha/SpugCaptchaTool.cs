using System.Windows;
using YuLiangToolkit.Core.Tools;
using YuLiangToolkit.Tools.SpugCaptcha.ViewModels;
using YuLiangToolkit.Tools.SpugCaptcha.Views;

namespace YuLiangToolkit.Tools.SpugCaptcha;

/// <summary>
/// Spug 验证码测试工具：左右两栏分别联调 Spug 推送助手（push.spug.cc）的短信 / 邮箱验证码模板。
/// 左栏粘贴短信模板推送 URL、填手机号；右栏粘贴邮箱模板推送 URL、填邮箱；分别「发送验证码」，
/// 收到验证码后到底部输入并「验证」——Spug 没有校验接口，所以验证是本地比对。
/// </summary>
public class SpugCaptchaTool : ITool
{
    public string Id => "spug-captcha";
    public string Name => "Spug 验证码测试";
    public string Description => "联调 Spug 推送助手的短信 / 邮箱验证码：粘贴模板 URL、发送验证码、本地校验并查看接口返回与 request_id";
    public string EmojiIcon => "📨";
    public string Category => "开发";

    /// <summary>主页标签：卡片上显示成蓝色小字，点主页的标签即可快速筛选。</summary>
    public IReadOnlyList<string> Tags => new[] { "Spug", "验证码", "短信", "邮箱", "推送", "联调", "测试" };

    public int Order => 910;

    public FrameworkElement CreateView()
        => new SpugCaptchaView { DataContext = new SpugCaptchaViewModel() };
}
