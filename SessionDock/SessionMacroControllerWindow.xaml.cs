using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using SessionDock.Services;

namespace SessionDock;

internal sealed record SessionMacroPlaybackOutcome(
    bool Succeeded,
    string? Message = null,
    bool SuppressDialog = false);

internal sealed record SessionMacroControllerReadiness(
    bool CanPlay,
    int ValidAssignmentCount,
    string? Message = null);

public partial class SessionMacroControllerWindow : Window
{
    private readonly Func<
        SessionMacroLaunchSnapshot,
        double,
        CancellationToken,
        Task<SessionMacroPlaybackOutcome>> _play;
    private readonly Func<
        SessionMacroLaunchSnapshot,
        SessionMacroControllerReadiness> _prepareReadiness;
    private readonly Action<double> _speedChanged;
    private readonly AppLocalizationService _localization;
    private readonly DispatcherTimer _readinessTimer;
    private SessionMacroLaunchContext _context;
    private SessionMacroControllerReadiness _readiness = new(false, 0);
    private CancellationTokenSource? _playbackCancellation;
    private bool _allowClose;
    private bool _isClosed;
    private bool _isPlaying;

    internal SessionMacroControllerWindow(
        SessionMacroLaunchContext context,
        double initialSpeed,
        Func<
            SessionMacroLaunchSnapshot,
            double,
            CancellationToken,
            Task<SessionMacroPlaybackOutcome>> play,
        Func<
            SessionMacroLaunchSnapshot,
            SessionMacroControllerReadiness> prepareReadiness,
        Action<double> speedChanged)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _play = play ?? throw new ArgumentNullException(nameof(play));
        _prepareReadiness = prepareReadiness ??
            throw new ArgumentNullException(nameof(prepareReadiness));
        _speedChanged = speedChanged ??
            throw new ArgumentNullException(nameof(speedChanged));
        InitializeComponent();
        _localization = ((App)Application.Current).LocalizationService;
        _readinessTimer = new DispatcherTimer(
            TimeSpan.FromSeconds(5),
            DispatcherPriority.Background,
            (_, _) => UpdatePresentation(),
            Dispatcher);
        IReadOnlyList<SpeedOption> speedOptions = SpeedOptions;
        if (double.IsFinite(initialSpeed) &&
            initialSpeed is >= 0.1 and <=
                SessionTemplatePolicy.MaximumMacroPlaybackSpeed &&
            !speedOptions.Any(option =>
                Math.Abs(option.Multiplier - initialSpeed) < 0.000001))
        {
            speedOptions = speedOptions
                .Append(new SpeedOption(
                    initialSpeed,
                    $"{initialSpeed:0.###}\u00D7"))
                .OrderBy(option => option.Multiplier)
                .ToArray();
        }
        SpeedComboBox.ItemsSource = speedOptions;
        SpeedComboBox.SelectedItem = speedOptions
            .OrderBy(option => Math.Abs(option.Multiplier - initialSpeed))
            .First();
        _context.Changed += Context_Changed;
        Closed += (_, _) => _isClosed = true;
        Loaded += (_, _) =>
        {
            FitInitialPosition();
            UpdatePresentation();
            _readinessTimer.Start();
        };
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                UpdatePresentation();
                _readinessTimer.Start();
            }
            else
            {
                _readinessTimer.Stop();
            }
        };
        UpdatePresentation();
    }

    private static IReadOnlyList<SpeedOption> SpeedOptions { get; } =
    [
        new(0.25, "0.25\u00D7"),
        new(0.5, "0.5\u00D7"),
        new(0.75, "0.75\u00D7"),
        new(1, "1\u00D7"),
        new(1.25, "1.25\u00D7"),
        new(1.5, "1.5\u00D7"),
        new(2, "2\u00D7")
    ];

    internal void UpdateContext(SessionMacroLaunchContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (ReferenceEquals(_context, context))
            return;
        _context.Changed -= Context_Changed;
        _context = context;
        _context.Changed += Context_Changed;
        UpdatePresentation();
    }

    internal void Reopen(bool userInitiated)
    {
        if (!IsVisible)
            Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        if (userInitiated)
        {
            Activate();
            PlayButton.Focus();
        }
    }

    internal void ClosePermanently()
    {
        _allowClose = true;
        _playbackCancellation?.Cancel();
        _readinessTimer.Stop();
        _context.Changed -= Context_Changed;
        if (!_isClosed)
            Close();
    }

    private async void PlayButton_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (_isPlaying)
        {
            _playbackCancellation?.Cancel();
            UpdatePresentation();
            return;
        }

        UpdatePresentation();
        if (!_readiness.CanPlay)
        {
            return;
        }
        var snapshot = _context.Snapshot();

        var speed = SpeedComboBox.SelectedItem is SpeedOption selected
            ? selected.Multiplier
            : 1;
        var playbackCancellation = new CancellationTokenSource();
        _playbackCancellation = playbackCancellation;
        _isPlaying = true;
        UpdatePresentation();
        try
        {
            var outcome = await _play(
                snapshot,
                speed,
                playbackCancellation.Token);
            if (!playbackCancellation.IsCancellationRequested &&
                !outcome.Succeeded &&
                !outcome.SuppressDialog &&
                !string.IsNullOrWhiteSpace(outcome.Message))
            {
                MessageBox.Show(
                    this,
                    outcome.Message,
                    Localize("Macro.ControllerPlaybackIssueTitle"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
        catch (OperationCanceledException)
        {
            // Stop is user-controlled; batch replacement and shutdown link to
            // the same token in MainWindow and follow this quiet path too.
        }
        catch (Exception exception)
        {
            if (playbackCancellation.IsCancellationRequested)
                return;
            Trace.WriteLine(
                $"Macro controller playback failed safely: {exception.GetType().Name}.");
            MessageBox.Show(
                this,
                Localize("Macro.PlaybackFailure", exception.Message),
                Localize("Macro.ControllerPlaybackIssueTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        finally
        {
            if (ReferenceEquals(
                    _playbackCancellation,
                    playbackCancellation))
            {
                _playbackCancellation = null;
            }
            playbackCancellation.Dispose();
            _isPlaying = false;
            UpdatePresentation();
        }
    }

    private void SpeedComboBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (SpeedComboBox.SelectedItem is SpeedOption selected)
            _speedChanged(selected.Multiplier);
    }

    private void Context_Changed(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        if (Dispatcher.CheckAccess())
            UpdatePresentation();
        else
            _ = Dispatcher.BeginInvoke(UpdatePresentation);
    }

    private void UpdatePresentation()
    {
        if (!IsInitialized)
            return;
        var snapshot = _context.Snapshot();
        if (!_isPlaying)
            _readiness = EvaluateReadiness(snapshot);
        var assignmentCount = _readiness.ValidAssignmentCount;
        var stopRequested =
            _playbackCancellation?.IsCancellationRequested == true;
        PlayButton.IsEnabled = _isPlaying
            ? !stopRequested
            : _readiness.CanPlay;
        SpeedComboBox.IsEnabled = !_isPlaying;
        var actionText = _isPlaying
            ? Localize("Macro.Stop")
            : Localize("Macro.ControllerPlay");
        PlayButton.Content = actionText;
        AutomationProperties.SetName(PlayButton, actionText);
        PlayButton.ToolTip = _isPlaying
            ? actionText
            : !_readiness.CanPlay
            ? _readiness.Message ??
                Localize("Macro.ControllerNoValidAssignments")
            : _readiness.Message ??
                Localize("Macro.ControllerAssignmentCount", assignmentCount);
        Title = assignmentCount == 0
            ? Localize("Macro.ControllerTitleEmpty")
            : Localize("Macro.ControllerTitleCount", assignmentCount);
    }

    private SessionMacroControllerReadiness EvaluateReadiness(
        SessionMacroLaunchSnapshot snapshot)
    {
        try
        {
            return _prepareReadiness(snapshot);
        }
        catch (Exception exception)
        {
            Trace.WriteLine(
                $"Macro controller readiness failed safely: {exception.GetType().Name}.");
            return new SessionMacroControllerReadiness(
                false,
                0,
                Localize("Macro.ControllerNoValidAssignments"));
        }
    }

    private void FitInitialPosition()
    {
        var workArea = SystemParameters.WorkArea;
        Left = Math.Max(workArea.Left, workArea.Right - ActualWidth - 20);
        Top = Math.Max(workArea.Top, workArea.Top + 20);
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        _ = sender;
        if (_allowClose)
            return;
        // An indefinite macro run must never continue behind a controller the
        // user intentionally closed. The window remains reusable, but closing
        // it requests the same safe cancellation as the visible Stop button.
        _playbackCancellation?.Cancel();
        e.Cancel = true;
        Hide();
    }

    private string Localize(string key, params object?[] arguments) =>
        arguments.Length == 0
            ? _localization.GetString(key)
            : _localization.Format(key, arguments);

    private sealed record SpeedOption(
        double Multiplier,
        string DisplayName) : IDropdownLabel;
}
