using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using SessionDock.Models;
using SessionDock.Services;

namespace SessionDock;

public partial class MainWindow
{
    private RecentTypeFilter _recentTypeFilter;
    private long _recentAccountFilter;
    private bool _updatingRecentFilters;

    private void LaunchTabButton_Click(object sender, RoutedEventArgs e) =>
        ShowLauncherTab();

    private void RecentTabButton_Click(object sender, RoutedEventArgs e)
    {
        LaunchTabPanel.Visibility = Visibility.Collapsed;
        RecentTabPanel.Visibility = Visibility.Visible;
        LaunchTabButton.IsChecked = false;
        RecentTabButton.IsChecked = true;
        RenderRecentExperiences();
    }

    private void ShowLauncherTab()
    {
        LaunchTabPanel.Visibility = Visibility.Visible;
        RecentTabPanel.Visibility = Visibility.Collapsed;
        RecentTabButton.IsChecked = false;
        LaunchTabButton.IsChecked = true;
    }

    private void RenderRecentExperiences()
    {
        var restoreKeyboardFocus =
            RecentExperiencesList.IsKeyboardFocusWithin;
        var focusedButton = Keyboard.FocusedElement as Button;
        var focusedRecent = focusedButton?.Tag as RecentExperience;
        var focusedDestinationKey = focusedRecent is null
            ? null
            : RecentDestinationIdentity.CreateKey(focusedRecent);
        var focusedAccountUserId = focusedRecent?.AccountUserId ?? 0;
        var focusedActionIndex = focusedButton?.Parent is Grid focusedGrid
            ? focusedGrid.Children.IndexOf(focusedButton)
            : -1;

        PopulateAccountFilter();
        UpdateClearHistoryButton();
        RecentExperiencesList.Children.Clear();
        var filtered = _settings.RecentExperiences
            .Where(MatchesRecentFilters)
            .Where(_recentSearch.MatchesRecent)
            .OrderByDescending(item => item.IsPinned)
            .ThenByDescending(item => item.LastLaunchedAt)
            .ToList();
        AutomationProperties.SetItemStatus(
            RecentSearchBox,
            _recentSearch.IsActive
                ? $"{filtered.Count} of {_settings.RecentExperiences.Count(MatchesRecentFilters)} saved experiences shown"
                : $"{filtered.Count} saved experiences shown");

        if (filtered.Count == 0)
        {
            var emptyState = new TextBlock
            {
                Text = _settings.RecentExperiences.Count == 0
                    ? "Experiences you successfully launch will appear here."
                    : _recentSearch.IsActive
                        ? "No Recent or Favorite items match your search and filters."
                    : "No saved experiences match these filters.",
                FontSize = 13,
                Margin = new Thickness(2, 18, 0, 0)
            };
            emptyState.SetResourceReference(
                TextBlock.ForegroundProperty,
                "SubtleBrush");
            RecentExperiencesList.Children.Add(emptyState);
            RestoreRecentKeyboardFocus(
                restoreKeyboardFocus,
                focusedDestinationKey,
                focusedAccountUserId,
                focusedActionIndex);
            return;
        }

        var favorites = filtered.Where(item => item.IsPinned).ToList();
        var recent = filtered.Where(item => !item.IsPinned).ToList();
        AddRecentSection("Favorites", favorites);
        AddRecentSection("Recent", recent);
        RestoreRecentKeyboardFocus(
            restoreKeyboardFocus,
            focusedDestinationKey,
            focusedAccountUserId,
            focusedActionIndex);
    }

    private void RestoreRecentKeyboardFocus(
        bool shouldRestore,
        string? destinationKey,
        long accountUserId,
        int actionIndex)
    {
        if (!shouldRestore || destinationKey is null)
            return;

        Control? focusTarget = null;
        foreach (var card in RecentExperiencesList.Children.OfType<Border>())
        {
            if (card.Child is not Grid grid)
                continue;

            var cardRecent = grid.Children
                .OfType<Button>()
                .Select(button => button.Tag)
                .OfType<RecentExperience>()
                .FirstOrDefault();
            if (cardRecent is null ||
                cardRecent.AccountUserId != accountUserId ||
                !RecentDestinationIdentity.CreateKey(cardRecent).Equals(
                    destinationKey,
                    StringComparison.Ordinal))
            {
                continue;
            }

            if (actionIndex >= 0 &&
                actionIndex < grid.Children.Count &&
                grid.Children[actionIndex] is Button actionButton)
            {
                focusTarget = actionButton;
            }
            break;
        }

        RestoreKeyboardFocus(
            focusTarget ??
            (_recentSearch.IsActive ? RecentSearchBox : RecentTabButton));
    }

    private void AddRecentSection(string title, IReadOnlyList<RecentExperience> items)
    {
        if (items.Count == 0)
            return;

        var sectionTitle = new TextBlock
        {
            Text = title,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(2, 4, 0, 8)
        };
        sectionTitle.SetResourceReference(
            TextBlock.ForegroundProperty,
            "BadgeTextBrush");
        RecentExperiencesList.Children.Add(sectionTitle);
        foreach (var item in items)
            RecentExperiencesList.Children.Add(CreateRecentExperienceCard(item));
    }

    private Border CreateRecentExperienceCard(RecentExperience recent)
    {
        var title = recent.CustomName
            ?? recent.Name
            ?? $"Place {recent.PlaceId}";
        var timestamp = LocalizationCulture.FormatLocalDateTime(
            recent.LastLaunchedAt,
            Localization.EffectiveCulture);
        var type = recent.IsPrivateServer ? "Private server" : "Public";
        var account = string.IsNullOrWhiteSpace(recent.AccountUsername)
            ? "Unknown account"
            : $"@{recent.AccountUsername}";

        var card = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Margin = new Thickness(0, 0, 0, 6),
            Padding = new Thickness(6)
        };
        card.SetResourceReference(
            Border.BackgroundProperty,
            "CardSurfaceBrush");
        card.SetResourceReference(
            Border.BorderBrushProperty,
            recent.IsPinned ? "AccentBrush" : "CardBorderBrush");
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var useButton = new Button
        {
            Tag = recent,
            Background = Brushes.Transparent,
            Padding = new Thickness(6),
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        AutomationProperties.SetName(
            useButton,
            $"Use {title} with the selected account");
        var labels = new StackPanel();
        var titleText = new TextBlock
        {
            Text = title,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        titleText.SetResourceReference(
            TextBlock.ForegroundProperty,
            "TextBrush");
        labels.Children.Add(titleText);
        var metadataText = new TextBlock
        {
            Text = $"{type}  •  {account}  •  {timestamp}",
            FontSize = 11,
            Margin = new Thickness(0, 3, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        metadataText.SetResourceReference(
            TextBlock.ForegroundProperty,
            "MutedBrush");
        labels.Children.Add(metadataText);
        if (recent.ServerJobId is { Length: > 8 } serverJobId)
        {
            var serverText = new TextBlock
            {
                Text = $"Tracked server {serverJobId[..8]}…",
                ToolTip = $"Roblox server JobId\n{serverJobId}",
                FontSize = 11,
                Margin = new Thickness(0, 3, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            serverText.SetResourceReference(
                TextBlock.ForegroundProperty,
                "SubtleBrush");
            labels.Children.Add(serverText);
        }
        useButton.Content = labels;
        useButton.ToolTip = recent.ServerJobId is null
            ? $"Use this destination\n{recent.Destination}"
            : $"Use this destination\n{recent.Destination}\n\nServer JobId\n{recent.ServerJobId}";
        useButton.Click += RecentExperienceButton_Click;
        grid.Children.Add(useButton);

        var actionColumn = 1;
        if (Guid.TryParse(recent.ServerJobId, out _))
        {
            var serverButton = CreateHistoryActionButton(
                "IconServerJoin",
                "Use this tracked Server JobId",
                recent,
                UseRecentServerButton_Click);
            Grid.SetColumn(serverButton, actionColumn++);
            grid.Children.Add(serverButton);
        }

        var pinButton = CreateHistoryActionButton(
            "IconStar",
            recent.IsPinned ? "Remove from Favorites" : "Add to Favorites",
            recent,
            PinRecentButton_Click,
            recent.IsPinned);
        Grid.SetColumn(pinButton, actionColumn++);
        grid.Children.Add(pinButton);

        var renameButton = CreateHistoryActionButton(
            "IconEdit",
            "Set a local custom name",
            recent,
            RenameRecentButton_Click);
        Grid.SetColumn(renameButton, actionColumn++);
        grid.Children.Add(renameButton);

        var removeButton = CreateHistoryActionButton(
            "IconTrash",
            "Remove from history",
            recent,
            RemoveRecentButton_Click);
        Grid.SetColumn(removeButton, actionColumn);
        grid.Children.Add(removeButton);
        card.Child = grid;
        return card;
    }

    private static Button CreateHistoryActionButton(
        string iconResourceKey,
        string tooltip,
        RecentExperience recent,
        RoutedEventHandler handler,
        bool fillIcon = false)
    {
        var icon = new Path
        {
            Data = (Geometry)Application.Current.FindResource(iconResourceKey),
            Style = (Style)Application.Current.FindResource("ButtonIcon")
        };
        if (fillIcon)
        {
            icon.SetResourceReference(Shape.FillProperty, "InfoTextBrush");
            icon.SetResourceReference(Shape.StrokeProperty, "InfoTextBrush");
        }

        var button = new Button
        {
            Tag = recent,
            Content = icon,
            ToolTip = tooltip,
            Width = 34,
            MinHeight = 34,
            Margin = new Thickness(4, 0, 0, 0),
            Padding = new Thickness(4)
        };
        button.SetResourceReference(
            Control.BackgroundProperty,
            "ControlSurfaceBrush");
        button.SetResourceReference(
            Control.ForegroundProperty,
            "ControlTextBrush");
        AutomationProperties.SetName(button, tooltip);
        if (iconResourceKey == "IconStar")
        {
            AutomationProperties.SetItemStatus(
                button,
                recent.IsPinned ? "Selected" : "Not selected");
        }
        button.Click += handler;
        return button;
    }

    private async void RecentExperienceButton_Click(
        object sender,
        RoutedEventArgs e) =>
        await RunWindowOperationAsync(_ =>
            RecentExperienceButtonClickAsync(sender));

    private async Task RecentExperienceButtonClickAsync(object sender)
    {
        if (sender is not Button { Tag: RecentExperience recent })
            return;

        if (_activeProfile is not { } activeProfile ||
            _pendingProfile is not null)
        {
            return;
        }

        var activeProfileKey = activeProfile.Key;
        var destination = recent.Destination;
        var mutationApplied = false;
        _destinationPersistence.Cancel();
        if (!await TryCommitSettingsMutationAsync(
                () =>
                {
                    var currentProfile = _settings.Accounts.FirstOrDefault(account =>
                        account.Key.Equals(
                            activeProfileKey,
                            StringComparison.OrdinalIgnoreCase));
                    if (currentProfile is null ||
                        !string.Equals(
                            _settings.ActiveAccountKey,
                            activeProfileKey,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }
                    currentProfile.Destination = destination;
                    mutationApplied = true;
                },
                "Recent destination could not be saved",
                "HISTORY SAVE ERROR",
                onCommitted: () =>
                {
                    if (!mutationApplied ||
                        !string.Equals(
                            _activeProfile?.Key,
                            activeProfileKey,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }
                    activeProfile = _settings.Accounts.First(account =>
                        account.Key.Equals(
                            activeProfileKey,
                            StringComparison.OrdinalIgnoreCase));
                    ShowDestinationForProfile(activeProfile);
                    ShowLauncherTab();
                    PlaceIdBox.Focus();
                    ResetDestinationViewport();
                }))
        {
            return;
        }

    }

    private async void UseRecentServerButton_Click(
        object sender,
        RoutedEventArgs e) =>
        await RunWindowOperationAsync(_ =>
            UseRecentServerButtonClickAsync(sender));

    private async Task UseRecentServerButtonClickAsync(object sender)
    {
        if (sender is not Button { Tag: RecentExperience recent } ||
            !Guid.TryParse(recent.ServerJobId, out var serverJobId))
        {
            return;
        }

        if (_activeProfile is not { } activeProfile ||
            _pendingProfile is not null)
        {
            return;
        }

        var destination = serverJobId.ToString("D");
        var activeProfileKey = activeProfile.Key;
        var placeId = recent.PlaceId;
        var mutationApplied = false;
        _destinationPersistence.Cancel();
        if (!await TryCommitSettingsMutationAsync(
                () =>
                {
                    var currentProfile = _settings.Accounts.FirstOrDefault(account =>
                        account.Key.Equals(
                            activeProfileKey,
                            StringComparison.OrdinalIgnoreCase));
                    if (currentProfile is null ||
                        !string.Equals(
                            _settings.ActiveAccountKey,
                            activeProfileKey,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }
                    currentProfile.Destination = destination;
                    mutationApplied = true;
                },
                "Tracked server could not be saved",
                "HISTORY SAVE ERROR",
                onCommitted: () =>
                {
                    if (!mutationApplied ||
                        !string.Equals(
                            _activeProfile?.Key,
                            activeProfileKey,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }
                    activeProfile = _settings.Accounts.First(account =>
                        account.Key.Equals(
                            activeProfileKey,
                            StringComparison.OrdinalIgnoreCase));
                    ShowDestinationForProfile(activeProfile);
                    ShowLauncherTab();
                    PlaceIdBox.Focus();
                    ResetDestinationViewport();
                }))
        {
            return;
        }

        if (!mutationApplied ||
            !string.Equals(
                _activeProfile?.Key,
                activeProfileKey,
                StringComparison.OrdinalIgnoreCase))
            return;
        SetStatus(
            "Tracked server selected",
            $"Launch will try server {serverJobId.ToString("D")[..8]}… for Place {placeId}.",
            "SERVER READY");
    }

    private async void PinRecentButton_Click(object sender, RoutedEventArgs e) =>
        await RunWindowOperationAsync(_ => PinRecentButtonClickAsync(sender));

    private async Task PinRecentButtonClickAsync(object sender)
    {
        if (sender is not Button { Tag: RecentExperience recent })
            return;
        var favoriteLimitReached = false;
        await SaveRecentMetadataAsync(() =>
        {
            if (!_settings.RecentExperiences.Contains(recent))
                return;
            if (!recent.IsPinned &&
                _settings.RecentExperiences.Count(item => item.IsPinned) >= 50)
            {
                favoriteLimitReached = true;
                return;
            }
            recent.IsPinned = !recent.IsPinned;
        });
        if (favoriteLimitReached)
        {
            SetStatus(
                "Favorites limit reached",
                "Remove one Favorite before pinning another. Recent history is unchanged.",
                "FAVORITES FULL");
        }
    }

    private async void RenameRecentButton_Click(object sender, RoutedEventArgs e) =>
        await RunWindowOperationAsync(_ => RenameRecentButtonClickAsync(sender));

    private async Task RenameRecentButtonClickAsync(object sender)
    {
        if (sender is not Button { Tag: RecentExperience recent })
            return;

        var dialog = new TextPromptDialog(
            "Rename saved experience",
            "This local name applies to this destination for every account. Leave it blank to use Roblox's name.",
            recent.CustomName)
        {
            Owner = this
        };
        if (dialog.ShowDialog() != true)
            return;

        var destinationKey = RecentDestinationIdentity.CreateKey(recent);
        var customName = dialog.Value;
        await SaveRecentMetadataAsync(() =>
        {
            if (!_settings.RecentExperiences.Contains(recent))
                return;
            foreach (var matchingEntry in _settings.RecentExperiences.Where(item =>
                         RecentDestinationIdentity.CreateKey(item).Equals(
                             destinationKey,
                             StringComparison.Ordinal)))
            {
                matchingEntry.CustomName = customName;
            }
        });
    }

    private async void RemoveRecentButton_Click(object sender, RoutedEventArgs e) =>
        await RunWindowOperationAsync(_ => RemoveRecentButtonClickAsync(sender));

    private async Task RemoveRecentButtonClickAsync(object sender)
    {
        if (sender is not Button { Tag: RecentExperience recent })
            return;
        var result = MessageBox.Show(
            $"Remove “{recent.CustomName ?? recent.Name ?? $"Place {recent.PlaceId}"}” from saved experiences?",
            "Remove saved experience",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes)
            return;
        await SaveRecentMetadataAsync(() =>
            _settings.RecentExperiences.Remove(recent));
    }

    private async void ClearHistoryButton_Click(object sender, RoutedEventArgs e) =>
        await RunWindowOperationAsync(_ => ClearHistoryButtonClickAsync());

    private async Task ClearHistoryButtonClickAsync()
    {
        var removableCount = _settings.RecentExperiences.Count(
            MatchesClearHistoryScope);
        if (removableCount == 0)
            return;

        var scope = _recentTypeFilter switch
        {
            RecentTypeFilter.Public => "public-server history",
            RecentTypeFilter.Private => "private-server history",
            _ => "history"
        };
        var entryText = removableCount == 1
            ? "1 non-favorite entry"
            : $"{removableCount} non-favorite entries";
        var accountScope = GetRecentAccountScopeDescription();
        var result = MessageBox.Show(
            $"Clear {scope} {accountScope}? This removes {entryText}. Favorites will stay saved.",
            $"Clear {scope}",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes)
            return;

        var recentType = _recentTypeFilter switch
        {
            RecentTypeFilter.Public => RecentServerType.Public,
            RecentTypeFilter.Private => RecentServerType.Private,
            _ => RecentServerType.All
        };
        var accountFilter = _recentAccountFilter;
        await SaveRecentMetadataAsync(() =>
            _settings.RecentExperiences.RemoveAll(item =>
                RecentHistoryScope.CanClear(
                    item,
                    recentType,
                    accountFilter)));
    }

    private async Task SaveRecentExperienceAsync(RecentExperience recent)
    {
        await SaveRecentMetadataAsync(() =>
        {
            var matchingDestinationEntries = _settings.RecentExperiences
                .Where(item => RecentDestinationIdentity.Matches(item, recent))
                .ToList();
            var sharedCustomName = matchingDestinationEntries
                .Where(item => item.CustomName is not null)
                .OrderByDescending(item => item.LastLaunchedAt)
                .Select(item => item.CustomName)
                .FirstOrDefault();
            var existing = _settings.RecentExperiences.FirstOrDefault(item =>
                item.AccountUserId == recent.AccountUserId &&
                RecentDestinationIdentity.Matches(item, recent));
            if (existing is not null)
            {
                recent.IsPinned = existing.IsPinned;
                _settings.RecentExperiences.Remove(existing);
            }
            recent.CustomName = sharedCustomName;

            _settings.RecentExperiences.Insert(0, recent);
            if (_settings.RecentExperiences.Count(item => !item.IsPinned) > 50)
            {
                var removable = _settings.RecentExperiences
                    .Where(item => !item.IsPinned)
                    .OrderBy(item => item.LastLaunchedAt)
                    .FirstOrDefault();
                if (removable is not null)
                    _settings.RecentExperiences.Remove(removable);
            }
        }, showError: false);
    }

    private async Task<bool> SaveRecentMetadataAsync(
        Action mutation,
        bool showError = true)
    {
        var committed = await TryCommitSettingsMutationAsync(
            mutation,
            "Local metadata could not be saved",
            "HISTORY ERROR",
            showFailure: showError);
        RenderRecentExperiences();
        return committed;
    }

    private bool MatchesRecentFilters(RecentExperience item)
    {
        return MatchesRecentType(item) &&
               (_recentAccountFilter == 0 ||
                item.AccountUserId == _recentAccountFilter);
    }

    private bool MatchesRecentType(RecentExperience item) =>
        RecentHistoryScope.MatchesType(
            item,
            _recentTypeFilter switch
            {
                RecentTypeFilter.Public => RecentServerType.Public,
                RecentTypeFilter.Private => RecentServerType.Private,
                _ => RecentServerType.All
            });

    private bool MatchesClearHistoryScope(RecentExperience item) =>
        RecentHistoryScope.CanClear(
            item,
            _recentTypeFilter switch
            {
                RecentTypeFilter.Public => RecentServerType.Public,
                RecentTypeFilter.Private => RecentServerType.Private,
                _ => RecentServerType.All
            },
            _recentAccountFilter);

    private string GetRecentAccountScopeDescription()
    {
        if (_recentAccountFilter == 0)
            return "across all accounts";

        var username = _settings.RecentExperiences
            .Where(item => item.AccountUserId == _recentAccountFilter)
            .Select(item => item.AccountUsername)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        return username is null
            ? $"for account {_recentAccountFilter}"
            : $"for @{username}";
    }

    private void UpdateClearHistoryButton()
    {
        var accessibleName = _recentTypeFilter switch
        {
            RecentTypeFilter.Public =>
                "Clear public history",
            RecentTypeFilter.Private =>
                "Clear private history",
            _ => "Clear all history"
        };
        var accountScope = GetRecentAccountScopeDescription();
        AutomationProperties.SetName(
            ClearHistoryButton,
            $"{accessibleName} {accountScope}");
        ClearHistoryButton.ToolTip =
            $"{accessibleName} {accountScope}; Favorites stay saved";
        ClearHistoryButton.IsEnabled =
            !_operationBusy &&
            _settings.RecentExperiences.Any(MatchesClearHistoryScope);
    }

    private void PopulateAccountFilter()
    {
        var accountIds = _settings.RecentExperiences
            .Where(item => item.AccountUserId > 0)
            .GroupBy(item => item.AccountUserId)
            .Select(group => new
            {
                UserId = group.Key,
                Username = group.Select(item => item.AccountUsername)
                    .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name))
                    ?? group.Key.ToString()
            })
            .OrderBy(account => account.Username)
            .ToList();
        if (_recentAccountFilter != 0 &&
            accountIds.All(account => account.UserId != _recentAccountFilter))
        {
            _recentAccountFilter = 0;
        }

        _updatingRecentFilters = true;
        AccountFilterCombo.Items.Clear();
        AccountFilterCombo.Items.Add(new ComboBoxItem
        {
            Content = Localize("Main.AllAccounts"),
            Tag = 0L
        });
        foreach (var account in accountIds)
        {
            AccountFilterCombo.Items.Add(new ComboBoxItem
            {
                Content = $"@{account.Username}",
                Tag = account.UserId
            });
        }

        AccountFilterCombo.SelectedItem = AccountFilterCombo.Items
            .Cast<ComboBoxItem>()
            .First(item => (long)item.Tag == _recentAccountFilter);
        _updatingRecentFilters = false;
    }

    private void AccountFilterCombo_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_updatingRecentFilters ||
            AccountFilterCombo.SelectedItem is not ComboBoxItem { Tag: long userId })
        {
            return;
        }

        _recentAccountFilter = userId;
        RenderRecentExperiences();
    }

    private void AllTypeFilterButton_Click(object sender, RoutedEventArgs e) =>
        SetRecentTypeFilter(RecentTypeFilter.All);

    private void PublicFilterButton_Click(object sender, RoutedEventArgs e) =>
        SetRecentTypeFilter(RecentTypeFilter.Public);

    private void PrivateFilterButton_Click(object sender, RoutedEventArgs e) =>
        SetRecentTypeFilter(RecentTypeFilter.Private);

    private void SetRecentTypeFilter(RecentTypeFilter filter)
    {
        _recentTypeFilter = filter;
        AllTypeFilterButton.IsChecked = filter == RecentTypeFilter.All;
        PublicFilterButton.IsChecked = filter == RecentTypeFilter.Public;
        PrivateFilterButton.IsChecked = filter == RecentTypeFilter.Private;
        RenderRecentExperiences();
    }

    private enum RecentTypeFilter
    {
        All,
        Public,
        Private
    }
}
