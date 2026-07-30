using System.Windows;
using SessionDock.Models;
using SessionDock.Services;

namespace SessionDock;

public partial class MainWindow
{
    private void RunningClientsButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (_operationBusy)
            return;

        var dialog = new RunningClientsDialog(
            _robloxClient,
            _runningClients,
            () => _operationLifetime.IsShuttingDown)
        {
            Owner = this
        };
        _ = dialog.ShowDialog();
        if (dialog.ClosedClientCount == 0)
            return;

        SetStatus(
            dialog.ClosedClientCount == 1
                ? Localize("Main.ClientClosedTitleOne")
                : Localize("Main.ClientClosedTitleMany"),
            dialog.ClosedClientCount == 1
                ? Localize("Main.ClientClosedDetailOne")
                : Localize(
                    "Main.ClientClosedDetailMany",
                    dialog.ClosedClientCount),
            Localize("Main.ClientsClosedBadge"),
            StatusTone.Success);
    }

    private void TrackLaunchedClient(
        RobloxClientProcessIdentity? identity,
        AccountProfile account,
        RecentExperience recent)
    {
        if (identity is null)
            return;

        _runningClients.Track(
            identity,
            new RunningClientAttribution(
                account.Key,
                account.UserId,
                account.Username,
                account.Label,
                account.ColorHex,
                recent.PlaceId,
                recent.CustomName ?? recent.Name,
                recent.LastLaunchedAt));
    }
}
