using System.Windows;

namespace TelPhone.Views;

public partial class ContactGroupWindow : Window
{
    public ContactGroupWindow(IEnumerable<string> groups, string initialGroup, bool importing)
    {
        InitializeComponent();
        Title = importing ? "Группа для импорта" : "Сменить группу контакта";
        GroupName.ItemsSource = groups;
        GroupName.Text = initialGroup;
        UpdateExisting.Visibility = importing ? Visibility.Visible : Visibility.Collapsed;
        Confirm.Content = importing ? "Импортировать" : "Сохранить";
    }

    public string Group => GroupName.Text.Trim();
    public bool UpdateExistingGroup => UpdateExisting.IsChecked == true;

    private void OnConfirm(object sender, RoutedEventArgs e) => DialogResult = true;
}
