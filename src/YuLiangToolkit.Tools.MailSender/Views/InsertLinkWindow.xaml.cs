using System.Windows;

namespace YuLiangToolkit.Tools.MailSender.Views;

/// <summary>
/// 插入超链接的小窗：显示文字 + 链接地址。打开时用选区里已有的文字预填显示文字，
/// 所以「选中一段文字再点 🔗」就等于给它加链接。
/// </summary>
public partial class InsertLinkWindow : Window
{
    /// <summary>链接显示文字。</summary>
    public string LinkText => LinkTextBox.Text.Trim();

    /// <summary>链接地址（已去掉首尾空白）。</summary>
    public string LinkUrl => UrlInput.Text.Trim();

    public InsertLinkWindow(string? initialText = null)
    {
        InitializeComponent();
        LinkTextBox.Text = initialText ?? "";
        if (string.IsNullOrWhiteSpace(LinkTextBox.Text)) LinkTextBox.Text = "点这里";
        Loaded += (_, _) =>
        {
            UrlInput.Focus();
            UrlInput.CaretIndex = UrlInput.Text.Length;
        };
    }

    private void OnInsertClick(object sender, RoutedEventArgs e)
    {
        var url = LinkUrl;
        if (url.Length == 0 || url == "https://" || url == "http://")
        {
            MessageBox.Show(this, "请填写链接地址，例如 https://resend.com", "地址不能为空",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            MessageBox.Show(this, "链接地址要以 http:// 或 https:// 开头。", "地址格式不对",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        DialogResult = true;
    }
}
