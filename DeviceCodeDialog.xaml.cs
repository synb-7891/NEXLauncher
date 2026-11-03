using System.Windows;

namespace NEXLauncher;

public partial class DeviceCodeDialog : Window
{
    public DeviceCodeDialog()
    {
        InitializeComponent();
    }

    public void UpdateCode(string message)
    {
        if (Dispatcher.CheckAccess())
            CodeText.Text = message;
        else
            Dispatcher.Invoke(() => CodeText.Text = message);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
