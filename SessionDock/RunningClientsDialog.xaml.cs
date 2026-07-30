using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using SessionDock.Services;

namespace SessionDock;

public partial class RunningClientsDialog : Window
{
    private readonly RobloxClientService _clientService;
    private readonly RunningClientRegistry _registry;
    private readonly Func<bool> _ownerIsShuttingDown;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<Button> _closeButtons = [];
    private readonly AccessibilityLiveRegion _statusLiveRegion;
    private readonly AccessibilityLiveRegion _warningLiveRegion;
    private IReadOnlyList<RunningRobloxClient> _clients = [];
    private bool _busy;
    private bool _destructiveOperationBusy;

    internal RunningClientsDialog(
        RobloxClientService clientService,
        RunningClientRegistry registry,
        Func<bool>? ownerIsShuttingDown = null)
    {
        ArgumentNullException.ThrowIfNull(clientService);
        ArgumentNullException.ThrowIfNull(registry);
        InitializeComponent();
        _statusLiveRegion = new AccessibilityLiveRegion(StatusText);
        _warningLiveRegion = new AccessibilityLiveRegion(WarningText);
        Services.WindowLayoutService.FitToWorkArea(this);
        _clientService = clientService;
        _registry = registry;
        _ownerIsShuttingDown = ownerIsShuttingDown ?? (() => false);
        Loaded += RunningClientsDialog_Loaded;
        Closing += RunningClientsDialog_Closing;
        Closed += RunningClientsDialog_Closed;
    }

    public int ClosedClientCount { get; private set; }

    private async void RunningClientsDialog_Loaded(
        object sender,
        RoutedEventArgs e)
    {
        Loaded -= RunningClientsDialog_Loaded;
        await RefreshSafelyAsync(focusIndex: null);
        if (IsVisible)
            DoneButton.Focus();
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) =>
        await RefreshSafelyAsync(focusIndex: null);

    private async Task RefreshSafelyAsync(int? focusIndex, string? result = null)
    {
        if (_busy)
            return;

        SetBusy(true);
        try
        {
            var scan = await _clientService.GetRunningPlayersAsync(
                _lifetime.Token);
            _lifetime.Token.ThrowIfCancellationRequested();
            ApplyScan(scan, focusIndex);
            var baseStatus = result ?? GetRefreshStatus(scan.Clients.Count);
            SetStatus(
                AppendUnverifiedWarningLocalized(
                    baseStatus,
                    scan.UnverifiedCount),
                accessibleAnnouncement: baseStatus);
        }
        catch (OperationCanceledException) when (
            _lifetime.IsCancellationRequested)
        {
            // Closing the dialog cancels bounded local process inspection.
        }
        catch (Exception exception) when (
            IsExpectedProcessInspectionFailure(exception))
        {
            SetStatus(
                Localize("Clients.InspectFailed"),
                isError: true);
        }
        finally
        {
            if (IsVisible)
                SetBusy(false);
        }
    }

    private void ApplyScan(
        RunningRobloxClientsResult scan,
        int? focusIndex)
    {
        _clients = scan.Clients;
        _registry.Reconcile(
            _clients.Select(client => client.Identity),
            scanIsComplete: scan.UnverifiedCount == 0);
        RenderClients(focusIndex);

        WarningBorder.Visibility = scan.UnverifiedCount > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        SetWarning(scan.UnverifiedCount switch
        {
            0 => string.Empty,
            1 => Localize("Clients.UnverifiedWarningOne"),
            _ => Localize(
                "Clients.UnverifiedWarningMany",
                scan.UnverifiedCount)
        });
    }

    private void RenderClients(int? focusIndex)
    {
        ClientsList.Children.Clear();
        _closeButtons.Clear();
        EmptyStateText.Visibility = _clients.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        EmptyStateText.Text = Localize("Clients.Empty");
        ClientCountText.Text = _clients.Count == 1
            ? Localize("Clients.CountOne")
            : Localize("Clients.CountMany", _clients.Count);
        CloseAllButton.IsEnabled = !_busy && _clients.Count > 0;

        for (var index = 0; index < _clients.Count; index++)
            ClientsList.Children.Add(CreateClientCard(_clients[index], index));

        if (focusIndex is not int requestedIndex)
            return;

        if (_closeButtons.Count == 0)
        {
            _ = DoneButton.Dispatcher.BeginInvoke(() => DoneButton.Focus());
            return;
        }

        var targetIndex = Math.Clamp(
            requestedIndex,
            0,
            _closeButtons.Count - 1);
        var focusTarget = _closeButtons[targetIndex];
        _ = focusTarget.Dispatcher.BeginInvoke(() =>
        {
            if (focusTarget.IsVisible && focusTarget.IsEnabled)
            {
                focusTarget.Focus();
                focusTarget.BringIntoView();
            }
        });
    }

    private Border CreateClientCard(
        RunningRobloxClient client,
        int index)
    {
        _registry.TryGet(client.Identity, out var attribution);
        var knownAccount = attribution is not null;
        var accountTitle = attribution is null
            ? Localize("Clients.UnknownAccount")
            : string.IsNullOrWhiteSpace(attribution.AccountLabel)
                ? $"@{attribution.AccountUsername}"
                : $"{attribution.AccountLabel} (@{attribution.AccountUsername})";
        var experience = attribution?.ExperienceName;
        if (string.IsNullOrWhiteSpace(experience) &&
            !string.Equals(
                client.WindowTitle,
                "Roblox",
                StringComparison.OrdinalIgnoreCase))
        {
            experience = client.WindowTitle;
        }
        experience = string.IsNullOrWhiteSpace(experience)
            ? "Roblox Player"
            : experience;
        var processId = client.Identity.ProcessId.ToString(
            CultureInfo.InvariantCulture);

        var card = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(11, 9, 9, 9),
            Margin = new Thickness(0, 0, 0, 6),
            ToolTip = Localize(
                "Clients.ProcessTooltip",
                processId)
        };
        card.SetResourceReference(
            Border.BackgroundProperty,
            "CardSurfaceBrush");
        card.SetResourceReference(
            Border.BorderBrushProperty,
            "CardBorderBrush");
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(38)
        });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = GridLength.Auto
        });

        var iconShell = new Border
        {
            Width = 32,
            Height = 32,
            CornerRadius = new CornerRadius(8),
            VerticalAlignment = VerticalAlignment.Top
        };
        Color? accountColor = null;
        if (knownAccount)
        {
            accountColor = (Color)ColorConverter.ConvertFromString(
                attribution!.AccountColorHex ?? "#326FD1");
            iconShell.Background = new SolidColorBrush(accountColor.Value);
        }
        else
        {
            iconShell.SetResourceReference(
                Border.BackgroundProperty,
                "UnknownAccountSurfaceBrush");
        }
        var icon = new Path
        {
            Data = (Geometry)FindResource(
                knownAccount ? "IconAccount" : "IconClients"),
            Style = (Style)FindResource("ButtonIcon"),
            Width = 15,
            Height = 15,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        if (accountColor is { } color)
        {
            icon.Stroke = new SolidColorBrush(
                MainWindow.GetContrastingAccountForeground(color));
        }
        else
        {
            icon.SetResourceReference(Shape.StrokeProperty, "OnAccentTextBrush");
        }
        iconShell.Child = icon;
        grid.Children.Add(iconShell);

        var labels = new StackPanel
        {
            Margin = new Thickness(9, 0, 12, 0)
        };
        var accountTitleText = new TextBlock
        {
            Text = accountTitle,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        accountTitleText.SetResourceReference(
            TextBlock.ForegroundProperty,
            "TextBrush");
        labels.Children.Add(accountTitleText);
        var attributionText = new TextBlock
        {
            Text = knownAccount
                ? Localize(
                    "Clients.AttributionKnown",
                    experience,
                    attribution!.AccountUsername)
                : Localize("Clients.AttributionUnknown"),
            FontSize = 11,
            Margin = new Thickness(0, 3, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        attributionText.SetResourceReference(
            TextBlock.ForegroundProperty,
            "MutedBrush");
        labels.Children.Add(attributionText);
        var startedAt = DateTime.SpecifyKind(
            client.Identity.StartTimeUtc,
            DateTimeKind.Utc).ToLocalTime();
        var startedAtText = startedAt.ToString(
            "t",
            Localization.EffectiveCulture);
        var processText = new TextBlock
        {
            Text = client.HasVisibleWindow
                ? Localize(
                    "Clients.ProcessVisible",
                    startedAtText,
                    processId)
                : Localize(
                    "Clients.ProcessBackground",
                    startedAtText,
                    processId),
            FontSize = 11,
            Margin = new Thickness(0, 3, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        processText.SetResourceReference(
            TextBlock.ForegroundProperty,
            "SubtleBrush");
        labels.Children.Add(processText);
        Grid.SetColumn(labels, 1);
        grid.Children.Add(labels);

        var closeButton = new Button
        {
            Tag = new ClientCloseTarget(client, index, accountTitle, experience),
            Content = Localize("Common.Close"),
            Padding = new Thickness(13, 8, 13, 8),
            VerticalAlignment = VerticalAlignment.Center
        };
        closeButton.SetResourceReference(
            Control.BackgroundProperty,
            "ErrorSurfaceBrush");
        closeButton.SetResourceReference(
            Control.ForegroundProperty,
            "ErrorMutedTextBrush");
        AutomationProperties.SetName(
            closeButton,
            knownAccount
                ? Localize(
                    "Clients.CloseKnownAutomation",
                    accountTitle,
                    experience)
                : Localize(
                    "Clients.CloseUnknownAutomation",
                    startedAtText));
        closeButton.Click += CloseClientButton_Click;
        Grid.SetColumn(closeButton, 2);
        grid.Children.Add(closeButton);
        _closeButtons.Add(closeButton);

        card.Child = grid;
        return card;
    }

    private async void CloseClientButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (_busy || sender is not Button
            {
                Tag: ClientCloseTarget target
            })
        {
            return;
        }

        var confirmationText = target.Client.HasVisibleWindow
            ? _registry.TryGet(target.Client.Identity, out _)
                ? Localize(
                    "Clients.CloseKnownConfirm",
                    target.AccountTitle,
                    target.ExperienceName)
                : Localize("Clients.CloseUnknownConfirm")
            : Localize("Clients.CloseBackgroundConfirm");
        var confirmation = MessageBox.Show(
            this,
            confirmationText,
            Localize("Clients.CloseConfirmTitle"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (confirmation != MessageBoxResult.Yes)
            return;

        SetBusy(true, destructive: true);
        SetStatus(Localize(
            "Clients.ClosingProcess",
            target.Client.Identity.ProcessId.ToString(
                CultureInfo.InvariantCulture)));
        try
        {
            var closeResult = await _clientService.ClosePlayerAsync(
                target.Client.Identity,
                _lifetime.Token);
            _lifetime.Token.ThrowIfCancellationRequested();
            var resultText = closeResult.Status switch
            {
                CloseRobloxClientStatus.Closed =>
                    Localize("Clients.CloseResultClosed"),
                CloseRobloxClientStatus.AlreadyExited =>
                    Localize("Clients.CloseResultExited"),
                CloseRobloxClientStatus.IdentityMismatch =>
                    Localize("Clients.CloseResultIdentityChanged"),
                _ =>
                    Localize("Clients.CloseResultFailed")
            };
            if (closeResult.Removed)
            {
                _registry.Remove(target.Client.Identity);
                _clients = _clients
                    .Where(client => !RobloxClientProcessIdentityComparer
                        .Instance.Equals(
                            client.Identity,
                            target.Client.Identity))
                    .ToArray();
                RenderClients(target.Index);
            }
            if (closeResult.Status == CloseRobloxClientStatus.Closed)
                ClosedClientCount++;

            await RefreshAfterActionAsync(resultText, target.Index);
        }
        catch (OperationCanceledException) when (
            _lifetime.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception) when (
            IsExpectedProcessInspectionFailure(exception))
        {
            SetStatus(
                Localize("Clients.SelectedInspectFailed"),
                isError: true);
        }
        finally
        {
            if (IsVisible)
            {
                SetBusy(false);
                RestoreFocusAfterClose(target.Index);
            }
        }
    }

    private async void CloseAllButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _clients.Count == 0)
            return;

        var targets = _clients.ToArray();
        var confirmationText = targets.Length == 1
            ? Localize("Clients.CloseAllConfirmOne")
            : Localize("Clients.CloseAllConfirmMany", targets.Length);
        var confirmation = MessageBox.Show(
            this,
            confirmationText,
            Localize("Clients.CloseAllConfirmTitle"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (confirmation != MessageBoxResult.Yes)
            return;

        SetBusy(true, destructive: true);
        SetStatus(Localize("Clients.ClosingDisplayed"));
        try
        {
            var results = await CloseIdentitySnapshotAsync(
                targets.Select(target => target.Identity).ToArray(),
                CloseDisplayedIdentityAsync);
            var outcomes = targets
                .Zip(
                    results,
                    (client, result) => new ClientCloseOutcome(client, result))
                .ToArray();
            _lifetime.Token.ThrowIfCancellationRequested();
            ClosedClientCount += outcomes.Count(outcome =>
                outcome.Result.Status == CloseRobloxClientStatus.Closed);

            var removed = outcomes
                .Where(outcome => outcome.Result.Removed)
                .Select(outcome => outcome.Client.Identity)
                .ToHashSet(RobloxClientProcessIdentityComparer.Instance);
            foreach (var identity in removed)
                _registry.Remove(identity);
            _clients = _clients
                .Where(client => !removed.Contains(client.Identity))
                .ToArray();
            RenderClients(focusIndex: 0);

            await RefreshAfterActionAsync(
                GetCloseDisplayedStatusLocalized(
                    outcomes.Select(outcome => outcome.Result.Status)),
                focusIndex: 0);
        }
        catch (OperationCanceledException) when (
            _lifetime.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception) when (
            IsExpectedProcessInspectionFailure(exception))
        {
            SetStatus(
                Localize("Clients.CloseDisplayedFailed"),
                isError: true);
        }
        finally
        {
            if (IsVisible)
            {
                SetBusy(false);
                RestoreFocusAfterClose(requestedIndex: 0);
            }
        }
    }

    internal static Task<CloseRobloxClientResult[]> CloseIdentitySnapshotAsync(
        IReadOnlyCollection<RobloxClientProcessIdentity> identities,
        Func<RobloxClientProcessIdentity, Task<CloseRobloxClientResult>> close)
    {
        ArgumentNullException.ThrowIfNull(identities);
        ArgumentNullException.ThrowIfNull(close);
        return Task.WhenAll(identities.Select(close).ToArray());
    }

    private async Task<CloseRobloxClientResult> CloseDisplayedIdentityAsync(
        RobloxClientProcessIdentity identity)
    {
        try
        {
            return await _clientService.ClosePlayerAsync(
                identity,
                _lifetime.Token);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            IsExpectedProcessInspectionFailure(exception))
        {
            return new CloseRobloxClientResult(
                CloseRobloxClientStatus.Failed);
        }
    }

    private async Task RefreshAfterActionAsync(
        string completedActionStatus,
        int? focusIndex)
    {
        try
        {
            var scan = await _clientService.GetRunningPlayersAsync(
                _lifetime.Token);
            _lifetime.Token.ThrowIfCancellationRequested();
            ApplyScan(scan, focusIndex);
            SetStatus(
                AppendUnverifiedWarningLocalized(
                    completedActionStatus,
                    scan.UnverifiedCount),
                accessibleAnnouncement: completedActionStatus);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            IsExpectedProcessInspectionFailure(exception))
        {
            SetStatus(
                Localize(
                    "Clients.RefreshAfterActionFailed",
                    completedActionStatus),
                isError: true);
        }
    }

    private void SetBusy(bool busy, bool destructive = false)
    {
        _busy = busy;
        _destructiveOperationBusy = busy && destructive;
        RefreshButton.IsEnabled = !busy;
        CloseAllButton.IsEnabled = !busy && _clients.Count > 0;
        DoneButton.IsEnabled = !_destructiveOperationBusy;
        foreach (var closeButton in _closeButtons)
            closeButton.IsEnabled = !busy;
    }

    private void SetStatus(
        string text,
        bool isError = false,
        string? accessibleAnnouncement = null) =>
        _statusLiveRegion.Update(
            text,
            accessibleAnnouncement,
            severity: isError
                ? AccessibilityLiveRegionSeverity.Assertive
                : AccessibilityLiveRegionSeverity.Polite);

    private void SetWarning(string text) =>
        _warningLiveRegion.Update(
            text,
            severity: AccessibilityLiveRegionSeverity.Assertive);

    private void RestoreFocusAfterClose(int requestedIndex)
    {
        Button focusTarget = _closeButtons.Count == 0
            ? DoneButton
            : _closeButtons[Math.Clamp(
                requestedIndex,
                0,
                _closeButtons.Count - 1)];
        _ = focusTarget.Dispatcher.BeginInvoke(() =>
        {
            if (focusTarget.IsVisible && focusTarget.IsEnabled)
            {
                focusTarget.Focus();
                focusTarget.BringIntoView();
            }
        });
    }

    private string GetRefreshStatus(int clientCount) => clientCount switch
    {
        0 => Localize("Clients.RefreshNone"),
        1 => Localize("Clients.RefreshOne"),
        _ => Localize("Clients.RefreshMany", clientCount)
    };

    internal static string GetCloseDisplayedStatus(
        IEnumerable<CloseRobloxClientStatus> statuses) =>
        CreateCloseDisplayedStatus(statuses, EnglishLocalize);

    private string GetCloseDisplayedStatusLocalized(
        IEnumerable<CloseRobloxClientStatus> statuses) =>
        CreateCloseDisplayedStatus(
            statuses,
            (key, arguments) => Localize(key, arguments));

    private static string CreateCloseDisplayedStatus(
        IEnumerable<CloseRobloxClientStatus> statuses,
        Func<string, object?[], string> localize)
    {
        ArgumentNullException.ThrowIfNull(statuses);
        ArgumentNullException.ThrowIfNull(localize);
        var results = statuses.ToArray();
        var messages = new List<string>();
        AddCountMessage(
            messages,
            results.Count(status => status == CloseRobloxClientStatus.Closed),
            "Clients.ClosedCountOne",
            "Clients.ClosedCountMany",
            localize);
        AddCountMessage(
            messages,
            results.Count(status => status == CloseRobloxClientStatus.AlreadyExited),
            "Clients.ExitedCountOne",
            "Clients.ExitedCountMany",
            localize);
        AddCountMessage(
            messages,
            results.Count(status => status == CloseRobloxClientStatus.IdentityMismatch),
            "Clients.IdentityChangedCountOne",
            "Clients.IdentityChangedCountMany",
            localize);
        AddCountMessage(
            messages,
            results.Count(status => status == CloseRobloxClientStatus.Failed),
            "Clients.FailedCountOne",
            "Clients.FailedCountMany",
            localize);
        return messages.Count == 0
            ? localize("Clients.NoneNeededClosing", [])
            : string.Join(" ", messages);
    }

    internal static string AppendUnverifiedWarning(
        string status,
        int unverifiedCount) =>
        AppendUnverifiedWarning(
            status,
            unverifiedCount,
            EnglishLocalize);

    private string AppendUnverifiedWarningLocalized(
        string status,
        int unverifiedCount) =>
        AppendUnverifiedWarning(
            status,
            unverifiedCount,
            (key, arguments) => Localize(key, arguments));

    private static string AppendUnverifiedWarning(
        string status,
        int unverifiedCount,
        Func<string, object?[], string> localize)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(status);
        ArgumentNullException.ThrowIfNull(localize);
        return unverifiedCount switch
        {
            <= 0 => status,
            1 => localize("Clients.AppendUnverifiedOne", [status]),
            _ => localize(
                "Clients.AppendUnverifiedMany",
                [status, unverifiedCount])
        };
    }

    private static void AddCountMessage(
        ICollection<string> messages,
        int count,
        string singularKey,
        string pluralKey,
        Func<string, object?[], string> localize)
    {
        if (count == 1)
            messages.Add(localize(singularKey, []));
        else if (count > 1)
            messages.Add(localize(pluralKey, [count]));
    }

    private static bool IsExpectedProcessInspectionFailure(
        Exception exception) =>
        exception is System.ComponentModel.Win32Exception or
            UnauthorizedAccessException or NotSupportedException;

    private void RunningClientsDialog_Closing(
        object? sender,
        CancelEventArgs e)
    {
        if (!_destructiveOperationBusy || _ownerIsShuttingDown())
            return;

        e.Cancel = true;
        SetStatus(
            Localize("Clients.WaitForClose"),
            isError: true);
    }

    private void RunningClientsDialog_Closed(object? sender, EventArgs e)
    {
        Closing -= RunningClientsDialog_Closing;
        Closed -= RunningClientsDialog_Closed;
        _lifetime.Cancel();
    }

    private AppLocalizationService Localization =>
        ((App)Application.Current).LocalizationService;

    private string Localize(string key) => Localization.GetString(key);

    private string Localize(string key, params object?[] arguments) =>
        Localization.Format(key, arguments);

    private static string EnglishLocalize(
        string key,
        params object?[] arguments)
    {
        var template = EnglishResources.Value[key] as string ?? key;
        return LocalizationCulture.Format(
            CultureInfo.GetCultureInfo("en-US"),
            template,
            arguments);
    }

    private static readonly Lazy<ResourceDictionary> EnglishResources = new(
        () => (ResourceDictionary)Application.LoadComponent(
            new Uri(
                "/SessionDock;component/Localization/Strings.en-US.xaml",
                UriKind.Relative)));

    private sealed record ClientCloseTarget(
        RunningRobloxClient Client,
        int Index,
        string AccountTitle,
        string ExperienceName);

    private sealed record ClientCloseOutcome(
        RunningRobloxClient Client,
        CloseRobloxClientResult Result);
}
