using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace SessionDock.Tests;

public sealed class DropdownSelectedLabelResolverTests
{
    [Fact]
    public void ResolveLabel_UsesTheSharedOptionContract()
    {
        var option = new ContractOption("Reviewed runtime");

        Assert.Equal(
            "Reviewed runtime",
            DropdownSelectedLabelResolver.ResolveLabel(option, null));
    }

    [Fact]
    public void ResolveLabel_UnselectedItemUsesTheConfiguredPlaceholder()
    {
        Assert.Equal(
            "Choose an option",
            DropdownSelectedLabelResolver.ResolveLabel(
                selectedItem: null,
                displayMemberPath: null,
                selectionBoxItem: string.Empty,
                placeholder: "Choose an option"));
        Assert.Equal(
            "Choose an option",
            DropdownSelectedLabelResolver.ResolveLabel(
                System.Windows.DependencyProperty.UnsetValue,
                displayMemberPath: null,
                placeholder: "Choose an option"));
    }

    [Fact]
    public void ResolveLabel_UsesAnExplicitDisplayMemberPath()
    {
        var option = new PathOption(new PathLabel("Account alpha"));

        Assert.Equal(
            "Account alpha",
            DropdownSelectedLabelResolver.ResolveLabel(
                option,
                "Presentation.Label"));
    }

    [Fact]
    public void ResolveLabel_NeverFallsBackToObjectToString()
    {
        var option = new UnsafeOption();

        Assert.Equal(
            string.Empty,
            DropdownSelectedLabelResolver.ResolveLabel(option, null));
        Assert.False(option.ToStringCalled);
    }

    [Fact]
    public void ResolveLabel_InvalidItemFailsClosedToThePlaceholder()
    {
        var option = new UnsafeOption();

        Assert.Equal(
            "Choose an option",
            DropdownSelectedLabelResolver.ResolveLabel(
                option,
                displayMemberPath: null,
                placeholder: "Choose an option"));
        Assert.False(option.ToStringCalled);
    }

    [Fact]
    public void ResolveLabel_InvalidDisplayPathFailsClosed()
    {
        Assert.Equal(
            string.Empty,
            DropdownSelectedLabelResolver.ResolveLabel(
                new PathLabel("Hidden"),
                "Missing.Label"));
    }

    [Fact]
    public void ResolveLabel_UsesTheLiveSelectionBoxRepresentationFirst()
    {
        Assert.Equal(
            "Localized label",
            DropdownSelectedLabelResolver.ResolveLabel(
                new ContractOption("Old label"),
                nameof(IDropdownLabel.DisplayName),
                "Localized label"));
    }

    [Fact]
    public void ComboBoxLifecycle_SelectClearReplaceAndRemoveKeepsSafeLabels()
    {
        RunOnSta(() =>
        {
            const string placeholder = "Choose an option";
            var alpha = new PathOption(new PathLabel("Account alpha"));
            var beta = new PathOption(new PathLabel("Account beta"));
            var replacement = new PathOption(
                new PathLabel("Replacement account"));
            var comboBox = new ComboBox
            {
                DisplayMemberPath = "Presentation.Label",
                ItemsSource = new[] { alpha, beta }
            };

            comboBox.SelectedItem = alpha;
            Assert.Equal("Account alpha", ResolveLiveLabel(comboBox, placeholder));

            comboBox.SelectedItem = null;
            Assert.Equal(placeholder, ResolveLiveLabel(comboBox, placeholder));

            comboBox.ItemsSource = new[] { replacement };
            comboBox.SelectedIndex = 0;
            Assert.Equal(
                "Replacement account",
                ResolveLiveLabel(comboBox, placeholder));

            comboBox.ItemsSource = Array.Empty<PathOption>();
            Assert.Null(comboBox.SelectedItem);
            Assert.Equal(placeholder, ResolveLiveLabel(comboBox, placeholder));
        });
    }

    [Fact]
    public void ComboBoxLifecycle_BlankReplacementLabelFailsClosedToPlaceholder()
    {
        RunOnSta(() =>
        {
            const string placeholder = "Choose an option";
            var comboBox = new ComboBox
            {
                DisplayMemberPath = "Presentation.Label",
                ItemsSource = new[]
                {
                    new PathOption(new PathLabel("   "))
                },
                SelectedIndex = 0
            };

            Assert.Equal(placeholder, ResolveLiveLabel(comboBox, placeholder));
        });
    }

    [Fact]
    public void ComboBoxLifecycle_ReopeningFromPersistedIdRestoresLabelOrPlaceholder()
    {
        RunOnSta(() =>
        {
            const string placeholder = "Choose a macro";
            var options = new[]
            {
                new IdOption("macro-a", "Harvest loop"),
                new IdOption("macro-b", "Shop loop")
            };

            var firstOpen = OpenIdComboBox("macro-b", options);
            Assert.Equal(
                "Shop loop",
                ResolveLiveLabel(firstOpen, placeholder));

            var reopened = OpenIdComboBox("macro-b", options);
            Assert.Equal(
                "Shop loop",
                ResolveLiveLabel(reopened, placeholder));

            var removed = OpenIdComboBox("deleted-macro", options);
            Assert.Null(removed.SelectedItem);
            Assert.Equal(placeholder, ResolveLiveLabel(removed, placeholder));
        });
    }

    private static string ResolveLiveLabel(
        ComboBox comboBox,
        string placeholder) =>
        DropdownSelectedLabelResolver.ResolveLabel(
            comboBox.SelectedItem,
            comboBox.DisplayMemberPath,
            comboBox.SelectionBoxItem,
            placeholder);

    private static ComboBox OpenIdComboBox(
        string storedId,
        IReadOnlyList<IdOption> options)
    {
        var comboBox = new ComboBox
        {
            DisplayMemberPath = nameof(IDropdownLabel.DisplayName),
            ItemsSource = options
        };
        comboBox.SelectedItem = options.SingleOrDefault(option =>
            option.Id.Equals(storedId, StringComparison.Ordinal));
        return comboBox;
    }

    private sealed record ContractOption(string DisplayName) : IDropdownLabel;

    private sealed record IdOption(string Id, string DisplayName) : IDropdownLabel;

    private sealed record PathOption(PathLabel Presentation);

    private sealed record PathLabel(string Label);

    private sealed class UnsafeOption
    {
        internal bool ToStringCalled { get; private set; }

        public override string ToString()
        {
            ToStringCalled = true;
            return "unsafe";
        }
    }

    private static void RunOnSta(Action action)
    {
        Exception? exception = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception currentException)
            {
                exception = currentException;
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        })
        {
            IsBackground = true
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(
            thread.Join(TimeSpan.FromSeconds(15)),
            "The ComboBox lifecycle STA test did not finish within 15 seconds.");
        if (exception is not null)
            ExceptionDispatchInfo.Capture(exception).Throw();
    }
}
