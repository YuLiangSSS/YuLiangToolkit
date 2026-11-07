using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using YuLiangToolkit.Tools.Mail;

namespace YuLiangToolkit.Tools.Mail.Views;

/// <summary>
/// 网络代理设置弹窗：编辑的是 <see cref="MailProxySettings"/> 的副本，点「保存」后调用方把字段抄回全局配置；
/// 取消则原设置不变。默认自动跟随 Windows 系统代理，并遵守系统代理例外列表。
/// </summary>
public partial class MailProxyDialog : Window
{
    private readonly MailProxySettings _draft;
    private bool _loading = true;

    public MailProxyDialog(MailProxySettings current)
    {
        InitializeComponent();
        _draft = new MailProxySettings
        {
            Mode = current.Mode,
            Host = current.Host,
            Port = current.Port,
            Type = current.Type,
        };
        Loaded += OnLoaded;
    }

    public MailProxySettings Draft => _draft;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _loading = true;
        ModeCombo.SelectedItem = FindItemByTag(ModeCombo, _draft.Mode) ?? ModeCombo.Items[0];
        HostBox.Text = _draft.Host;
        PortBox.Text = _draft.Port.ToString();
        TypeCombo.SelectedItem = FindItemByTag(TypeCombo, _draft.Type) ?? TypeCombo.Items[0];
        _loading = false;
        UpdateCustomVisibility();
        UpdateStatus();
    }

    private void OnModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        _draft.Mode = SelectedTag(ModeCombo) ?? "auto";
        UpdateCustomVisibility();
        UpdateStatus();
    }

    private void OnTypeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        _draft.Type = SelectedTag(TypeCombo) ?? "http";
        UpdateStatus();
    }

    private void UpdateCustomVisibility()
        => CustomPanel.Visibility = _draft.Mode == "custom" ? Visibility.Visible : Visibility.Collapsed;

    private void UpdateStatus()
    {
        var preview = new MailProxySettings
        {
            Mode = _draft.Mode,
            Host = HostBox.Text.Trim(),
            Port = int.TryParse(PortBox.Text.Trim(), out var port) ? port : 0,
            Type = SelectedTag(TypeCombo) ?? _draft.Type,
        };
        StatusText.Text = "当前：" + preview.Describe("imap.gmail.com");
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        _draft.Mode = SelectedTag(ModeCombo) ?? "auto";
        _draft.Type = SelectedTag(TypeCombo) ?? "http";
        _draft.Host = HostBox.Text.Trim();
        if (!int.TryParse(PortBox.Text.Trim(), out var port) || port is <= 0 or > 65535)
        {
            MessageBox.Show(this, "代理端口应为 1 ~ 65535 的数字。", "网络代理", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _draft.Port = port;
        if (_draft.Mode == "custom" && string.IsNullOrWhiteSpace(_draft.Host))
        {
            MessageBox.Show(this, "自定义代理需要填写代理服务器地址。", "网络代理", MessageBoxButton.OK, MessageBoxImage.Information);
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

    private static string? SelectedTag(ComboBox combo)
        => (combo.SelectedItem as ComboBoxItem)?.Tag as string;

    private static object? FindItemByTag(ComboBox combo, string? tag)
    {
        foreach (var item in combo.Items)
        {
            if (item is ComboBoxItem { Tag: string itemTag } && string.Equals(itemTag, tag, StringComparison.OrdinalIgnoreCase))
                return item;
        }

        return null;
    }
}
