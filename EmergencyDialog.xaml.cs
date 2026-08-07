using System.Windows;

namespace WinHardwareMultitool;

public partial class EmergencyDialog : Window
{
    public EmergencyDialog(string message)
    {
        InitializeComponent();
        MessageText.Text = message;
    }

    private void OnOkClick(object sender, RoutedEventArgs e) => Close();
}
