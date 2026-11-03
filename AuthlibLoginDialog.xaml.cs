using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace NEXLauncher;

public partial class AuthlibLoginDialog : Window
{
    private readonly Services.AuthlibService _service = new();
    private readonly Services.LauncherSettings _settings;

    /// <summary>登录成功后的账号（用于保存到账号列表）。</summary>
    public Services.SavedAccountItem? ResultAccount { get; private set; }

    public AuthlibLoginDialog(Services.LauncherSettings settings)
    {
        _settings = settings;
        InitializeComponent();
        ServerBox.LostFocus += async (_, _) => await ProbeServerAsync();
        ServerBox.SelectionChanged += async (_, _) => await ProbeServerAsync();
    }

    /// <summary>探测服务器名称（失败不阻塞登录）。</summary>
    private async Task ProbeServerAsync()
    {
        if (ServerBox.SelectedItem is ComboBoxItem { Content: string s } && !string.IsNullOrWhiteSpace(s))
            ServerBox.Text = s;
        var raw = ServerBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(raw))
        {
            ServerNameText.Text = "请输入或从下拉框选择认证服务器 API 地址。";
            return;
        }
        var root = Services.AuthlibService.NormalizeApiRoot(raw);
        ServerNameText.Text = "正在连接服务器...";
        try
        {
            var info = await _service.FetchServerInfoAsync(root);
            ServerNameText.Text = $"服务器名称：{info.ServerName}";
        }
        catch (Exception ex)
        {
            // 给出更具体的原因：域名解析失败（服务器已关停）/ 超时 / TLS 问题
            var reason = ex.Message.Contains("No such host is known", StringComparison.OrdinalIgnoreCase)
                         || ex.Message.Contains("getaddrinfo", StringComparison.OrdinalIgnoreCase)
                ? "域名无法解析（该服务器可能已关停）"
                : ex.Message.Contains("timed out", StringComparison.OrdinalIgnoreCase)
                    ? "连接超时（服务器无响应或被墙）"
                    : "连接失败：" + ex.Message.Split('\n')[0];
            ServerNameText.Text = $"{reason}；仍可尝试登录。";
        }
    }

    private async void PassBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) await DoLoginAsync();
    }

    private async void Ok_Click(object sender, RoutedEventArgs e) => await DoLoginAsync();

    private async Task DoLoginAsync()
    {
        var root = Services.AuthlibService.NormalizeApiRoot(ServerBox.Text);
        var user = UserBox.Text.Trim();
        var pass = PassBox.Password;
        if (string.IsNullOrWhiteSpace(user) || string.IsNullOrEmpty(pass))
        {
            StatusText.Text = "请填写账号和密码。";
            return;
        }

        OkBtn.IsEnabled = false;
        StatusText.Text = "正在登录...";
        try
        {
            var clientToken = _settings.AuthlibClientToken;
            if (string.IsNullOrEmpty(clientToken))
            {
                clientToken = Guid.NewGuid().ToString("N");
                _settings.AuthlibClientToken = clientToken;
            }
            var result = await _service.AuthenticateAsync(root, user, pass, clientToken);
            ResultAccount = new Services.SavedAccountItem
            {
                Type = "authlib",
                Name = result.Profile.Name,
                Uuid = result.Profile.Id,
                AccessToken = result.AccessToken,
                ApiRoot = root,
                ServerName = result.ServerName
            };
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            StatusText.Text = "登录失败：" + ex.Message;
        }
        finally
        {
            OkBtn.IsEnabled = true;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
