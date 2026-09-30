using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace EnmaStudio;

/// <summary>Themed yes/no question (optionally with a third "cancel" choice).</summary>
public partial class ConfirmDialog : Window
{
    private bool? _result;

    private ConfirmDialog(string title, string message, string yes, string no, string? cancel)
    {
        InitializeComponent();
        Box.Header = title;
        MessageText.Text = message;
        YesButton.Content = yes;
        NoButton.Content = no;
        if (cancel != null)
        {
            CancelButton.Content = cancel;
            CancelButton.Visibility = Visibility.Visible;
        }

        SourceInitialized += (_, _) => NativeMethods.DisableRoundedCorners(new WindowInteropHelper(this).Handle);
        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
        };
    }

    /// <summary>True = yes, false = no, null = cancelled (Esc or the cancel button).</summary>
    public static bool? Ask(Window? owner, string title, string message,
                            string yes = "Yes", string no = "No", string? cancel = null)
    {
        var dialog = new ConfirmDialog(title, message, yes, no, cancel);
        if (owner is { IsVisible: true }) dialog.Owner = owner;
        else dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        dialog.ShowDialog();
        return dialog._result;
    }

    private void Yes_Click(object sender, RoutedEventArgs e) => Finish(true);

    private void No_Click(object sender, RoutedEventArgs e) => Finish(false);

    private void Cancel_Click(object sender, RoutedEventArgs e) => Finish(null);

    private void Finish(bool? result)
    {
        _result = result;
        Close();
    }
}
