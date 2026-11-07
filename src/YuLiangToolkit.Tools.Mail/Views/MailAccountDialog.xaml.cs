using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using YuLiangToolkit.Tools.Mail;

namespace YuLiangToolkit.Tools.Mail.Views;

/// <summary>
/// 邮箱账号设置弹窗：预设 / 显示名称 / 邮箱 / 授权码 / IMAP 服务器 / 端口 / SSL / 是否参与代收。
///
/// <para>用「草稿对象」编辑：<see cref="Draft"/> 是账号的临时副本，点「保存」后由调用方把字段抄回原账号
/// （编辑场景）或直接加入账号列表（新增场景），点「取消」则原账号完全不受影响。</para>
///
/// <para>密码框：<c>PasswordBox.Password</c> 不能绑定，这里和草稿的 <see cref="MailAccountConfig.Password"/>
/// 手动同步；窗口打开时显示的是已保存的圆点，重新输入即覆盖。</para>
/// </summary>
public partial class MailAccountDialog : Window
{
    private readonly MailAccountConfig _draft;
    private readonly MailProxySettings _proxy;
    private bool _loading = true;

    public MailAccountDialog(MailAccountConfig draft, MailProxySettings proxy)
    {
        InitializeComponent();
        _draft = draft;
        _proxy = proxy;
        DataContext = this;
        Loaded += OnLoaded;
    }

    /// <summary>正在编辑的账号草稿（保存后由调用方取走）。</summary>
    public MailAccountConfig Draft => _draft;

    /// <summary>服务商预设下拉项。</summary>
    public IReadOnlyList<MailPreset> Presets => MailPresets.All;

    /// <summary>复制一份账号（用于「编辑」时先改副本，取消不污染原账号）。</summary>
    public static MailAccountConfig Clone(MailAccountConfig source) => new()
    {
        Id = source.Id,
        DisplayName = source.DisplayName,
        Email = source.Email,
        PasswordCipher = source.PasswordCipher,
        Host = source.Host,
        Port = source.Port,
        UseSsl = source.UseSsl,
        Enabled = source.Enabled,
    };

    /// <summary>把草稿的字段抄回原账号（编辑保存时调用）。</summary>
    public static void CopyTo(MailAccountConfig source, MailAccountConfig target)
    {
        target.DisplayName = source.DisplayName;
        target.Email = source.Email;
        target.PasswordCipher = source.PasswordCipher;
        target.Host = source.Host;
        target.Port = source.Port;
        target.UseSsl = source.UseSsl;
        target.Enabled = source.Enabled;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        PresetCombo.ItemsSource = Presets;
        PresetCombo.SelectedItem = MailPresets.Match(_draft.Host, _draft.Port, _draft.UseSsl);

        _loading = true;
        PasswordInput.Password = _draft.Password;
        _loading = false;
        UpdatePresetHint();
    }

    private void OnPresetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || PresetCombo.SelectedItem is not MailPreset preset) return;
        preset.ApplyTo(_draft);
        UpdatePresetHint();
    }

    private void UpdatePresetHint()
    {
        var preset = PresetCombo.SelectedItem as MailPreset ?? MailPresets.Custom;
        HintText.Text = preset.Hint;
        HelpButton.Visibility = string.IsNullOrWhiteSpace(preset.HelpUrl) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnHelpClick(object sender, RoutedEventArgs e)
    {
        if (PresetCombo.SelectedItem is not MailPreset { HelpUrl: { Length: > 0 } url }) return;
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch
        {
            // 打不开浏览器不影响填写
        }
    }

    private async void OnTestClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_draft.Email) || string.IsNullOrWhiteSpace(_draft.Host))
        {
            TestStatus.Text = "先填好邮箱地址和 IMAP 服务器再测试。";
            return;
        }

        if (string.IsNullOrWhiteSpace(_draft.Password))
        {
            TestStatus.Text = "授权码 / 应用专用密码还没填。";
            return;
        }

        TestButton.IsEnabled = false;
        TestStatus.Text = "连接中…";
        try
        {
            var service = new ImapMailService(_proxy);
            TestStatus.Text = await service.TestConnectionAsync(_draft);
        }
        catch (Exception ex)
        {
            TestStatus.Text = "❌ " + ImapMailService.DescribeError(ex);
        }
        finally
        {
            TestButton.IsEnabled = true;
        }
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_draft.Email))
        {
            MessageBox.Show(this, "请先填写邮箱地址。", "账号设置", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (string.IsNullOrWhiteSpace(_draft.Host))
        {
            MessageBox.Show(this, "请先填写 IMAP 服务器（或选一个服务商预设）。", "账号设置", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnTitleBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _draft.Password = PasswordInput.Password;
    }
}
