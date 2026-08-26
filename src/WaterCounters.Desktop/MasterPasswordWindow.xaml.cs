using System.Windows;

namespace WaterCounters.Desktop;

/// <summary>
/// Ввод мастер-пароля от <c>secrets.enc</c>.
///
/// Именно окно, а не запрос в консоли: обработчик работает в трее и консоли у него
/// нет. «Пропустить» — рабочий вариант: без секретов не выйдет войти в кабинет и
/// отправить письмо, но распознавание и запись истории продолжат работать.
/// </summary>
public partial class MasterPasswordWindow : Window
{
    public MasterPasswordWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => Password.Focus();
    }

    public string? EnteredPassword { get; private set; }

    public bool ShouldRemember => Remember.IsChecked == true;

    private void OnAccept(object sender, RoutedEventArgs e)
    {
        if (Password.Password.Length == 0)
        {
            Error.Text = "Пароль не введён.";
            Error.Visibility = Visibility.Visible;
            return;
        }

        EnteredPassword = Password.Password;
        DialogResult = true;
        Close();
    }

    private void OnSkip(object sender, RoutedEventArgs e)
    {
        EnteredPassword = null;
        DialogResult = false;
        Close();
    }
}
