using System.Windows;
using System.Windows.Input;

namespace DeskFolders.Views;

public partial class RenameDialog : Window
{
    public string NewName => NameBox.Text;

    public RenameDialog(string currentName, string title = "Переименовать папку")
    {
        InitializeComponent();
        TitleLabel.Text = title;
        NameBox.Text = currentName;
        Loaded += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };
    }

    private void OnBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            DialogResult = true;
            Close();
        }
        else if (e.Key == Key.Escape)
        {
            DialogResult = false;
            Close();
        }
    }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}