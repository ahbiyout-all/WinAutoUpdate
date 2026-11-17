using System.Windows;

namespace UpdateAgent.LabManager.Desktop.Views;

public partial class TokenDialog : Window
{
    private readonly bool _requireToken;

    public TokenDialog(bool requireToken = false)
    {
        InitializeComponent();
        _requireToken = requireToken;
        TokenBox.Password = App.Settings.AdminToken;
        CancelButton.Visibility = requireToken ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var token = TokenBox.Password.Trim();
        if (string.IsNullOrEmpty(token))
        {
            MessageBox.Show(this, "토큰을 입력하세요.", "UpdateAgent LabManager",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        App.Settings.AdminToken = token;
        App.Settings.Save();
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
