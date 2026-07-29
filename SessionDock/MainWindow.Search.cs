using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SessionDock.Services;

namespace SessionDock;

public partial class MainWindow
{
    private readonly SearchQueryState _accountSearch = new();
    private readonly SearchQueryState _recentSearch = new();

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (LauncherPanel.Visibility != Visibility.Visible)
            return;

        var contextSearchBox = GetContextSearchBox();
        var clearSearchBox = Keyboard.FocusedElement switch
        {
            TextBox textBox when ReferenceEquals(textBox, AccountSearchBox) =>
                AccountSearchBox,
            TextBox textBox when ReferenceEquals(textBox, RecentSearchBox) =>
                RecentSearchBox,
            _ => contextSearchBox
        };
        if (e.Key == Key.Escape &&
            Keyboard.Modifiers == ModifierKeys.None &&
            clearSearchBox.Text.Length > 0)
        {
            e.Handled = true;
            clearSearchBox.Clear();
            return;
        }

        if (e.Key != Key.F || Keyboard.Modifiers != ModifierKeys.Control)
            return;

        e.Handled = true;
        contextSearchBox.Focus();
        contextSearchBox.SelectAll();
    }

    private TextBox GetContextSearchBox() =>
        RecentTabPanel.Visibility == Visibility.Visible
            ? RecentSearchBox
            : AccountSearchBox;

    private void AccountSearchBox_TextChanged(
        object sender,
        TextChangedEventArgs e)
    {
        var changed = _accountSearch.Update(AccountSearchBox.Text);
        if (!changed || !IsInitialized || _operationLifetime.IsShuttingDown)
            return;

        RenderAccountList();
    }

    private void RecentSearchBox_TextChanged(
        object sender,
        TextChangedEventArgs e)
    {
        var changed = _recentSearch.Update(RecentSearchBox.Text);
        if (!changed || !IsInitialized || _operationLifetime.IsShuttingDown)
            return;

        RenderRecentExperiences();
    }
}
