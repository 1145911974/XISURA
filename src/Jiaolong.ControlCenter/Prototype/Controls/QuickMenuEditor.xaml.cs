using System.Collections.ObjectModel;
using Jiaolong_ControlCenter.Prototype.QuickMenu;
using Jiaolong.Contracts.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace Jiaolong_ControlCenter.Prototype.Controls;

public sealed partial class QuickMenuEditor : UserControl
{
    private QuickMenuLayout layout = QuickMenuLayout.Default;
    private bool suppressOptionChanged;
    private int maxItems = QuickMenuLayout.MaxEnabledItems;

    public ObservableCollection<QuickMenuOption> Options { get; private set; } = [];
    public ObservableCollection<QuickMenuPreviewSlot> PreviewItems { get; private set; } = [];
    public QuickMenuLayout Layout => layout;
    public event Action<QuickMenuLayout>? LayoutChanged;

    public QuickMenuEditor()
    {
        InitializeComponent();
        OptionsRepeater.ItemsSource = Options;
        PreviewGrid.ItemsSource = PreviewItems;
    }

    public void ApplyLayout(QuickMenuLayout value, IReadOnlyDictionary<QuickSettingKind, bool>? availability = null,
        int maxItems = QuickMenuLayout.MaxEnabledItems)
    {
        if (maxItems is < 1 or > QuickMenuLayout.MaxEnabledItems) throw new ArgumentOutOfRangeException(nameof(maxItems));
        this.maxItems = maxItems;
        SelectionLimitText.Text = $"最多选择 {maxItems} 项";
        PreviewSurface.Height = PreviewGrid.Height = maxItems <= 6 ? 164 : 238;
        layout = new QuickMenuLayout(value.EnabledOrder.Distinct().Take(maxItems).ToArray());
        var nextOptions = new ObservableCollection<QuickMenuOption>();
        foreach (var item in QuickMenuCatalog.CreateDefault())
        {
            var option = new QuickMenuOption(item)
            {
                IsAvailable = QuickMenuCatalog.IsLocal(item.Kind) || item.IsAction || availability?.TryGetValue(item.Kind, out var available) == true && available,
                IsEnabled = layout.EnabledOrder.Contains(item.Kind)
            };
            nextOptions.Add(option);
        }
        Options = nextOptions;
        OptionsRepeater.ItemsSource = Options;
        RebuildPreview();
        RefreshSelectionAvailability();
    }

    public void SetAvailability(QuickSettingKind kind, bool available)
    {
        var option = Options.FirstOrDefault(item => item.Item.Kind == kind);
        if (option is not null && !option.Item.IsAction)
        {
            option.IsAvailable = QuickMenuCatalog.IsLocal(kind) || available;
            RefreshSelectionAvailability();
        }
    }

    private void OnOptionChanged(object sender, RoutedEventArgs args)
    {
        if (suppressOptionChanged || sender is not ToggleButton { Tag: QuickSettingKind kind } toggle) return;
        var option = Options.FirstOrDefault(candidate => candidate.Item.Kind == kind);
        if (option is null) return;
        var enabled = toggle.IsChecked == true;
        SynchronizeOptionToggle(toggle, useTransitions: true);
        if (layout.EnabledOrder.Contains(kind) == enabled) return;
        option.IsEnabled = enabled;
        layout = layout.Toggle(kind, enabled, maxItems);
        suppressOptionChanged = true;
        try
        {
            foreach (var candidate in Options)
                candidate.IsEnabled = layout.EnabledOrder.Contains(candidate.Item.Kind);
        }
        finally
        {
            suppressOptionChanged = false;
        }
        RefreshSelectionAvailability();
        RebuildPreview();
        LayoutChanged?.Invoke(layout);
    }

    private void OnOptionToggleLoaded(object sender, RoutedEventArgs args)
    {
        if (sender is ToggleButton toggle)
            SynchronizeOptionToggle(toggle, useTransitions: false);
    }

    private static void SynchronizeOptionToggle(ToggleButton toggle, bool useTransitions) =>
        VisualStateManager.GoToState(toggle, toggle.IsChecked == true ? "Checked" : "Unchecked", useTransitions);

    private void OnPreviewDragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        layout = QuickMenuPreviewLayout.ToLayout(PreviewItems, maxItems);
        suppressOptionChanged = true;
        try
        {
            foreach (var option in Options)
                option.IsEnabled = layout.EnabledOrder.Contains(option.Item.Kind);
        }
        finally
        {
            suppressOptionChanged = false;
        }
        RefreshSelectionAvailability();
        LayoutChanged?.Invoke(layout);
    }

    private void OnPreviewGridLoaded(object sender, RoutedEventArgs args) => UpdatePreviewGridLayout();

    private void RefreshSelectionAvailability()
    {
        var hasRoom = layout.EnabledOrder.Count < maxItems;
        foreach (var option in Options)
            option.IsSelectable = option.IsAvailable && (option.IsEnabled || hasRoom);
    }

    private void RebuildPreview()
    {
        var nextPreviewItems = new ObservableCollection<QuickMenuPreviewSlot>(QuickMenuPreviewLayout.Create(layout, Options, maxItems));
        PreviewItems = nextPreviewItems;
        PreviewGrid.ItemsSource = PreviewItems;
        UpdatePreviewGridLayout();
    }

    private void UpdatePreviewGridLayout()
    {
        if (PreviewGrid.ItemsPanelRoot is not ItemsWrapGrid panel) return;
        panel.MaximumRowsOrColumns = 3;
    }
}
