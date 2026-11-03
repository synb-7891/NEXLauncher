using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;

namespace NEXLauncher;

public partial class OfflineLoginDialog : Window
{
    public string UserName { get; private set; } = string.Empty;

    public OfflineLoginDialog()
    {
        InitializeComponent();
        Loaded += (_, _) => UsernameBox.Focus();
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        var name = UsernameBox.Text.Trim();
        if (name.Length < 3 || name.Length > 16 || !Regex.IsMatch(name, "^[A-Za-z0-9_]+$"))
        {
            MessageBox.Show("昵称需为 3-16 位字母、数字或下划线。", "NEX", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        UserName = name;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Enter) Confirm_Click(this, e);
        else if (e.Key == Key.Escape) { DialogResult = false; Close(); }
    }
}
