using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Visibility = Microsoft.UI.Xaml.Visibility;

namespace AssetManager.WinUI;

/// <summary>
/// Window chrome (spec "App bar as the title bar", design D1): the page content extends into the title bar, the system
/// draws only its caption buttons (48 px tall, the dense app bar's height), and MAUI's own title row is collapsed.
/// </summary>
internal static class TitleBarSetup
{
    /// <summary>Returns false when the OS cannot customise the title bar; the window then keeps the standard one.</summary>
    /// <param name="laidOut">Called once the window's content is laid out (its XamlRoot exists).</param>
    public static bool Apply(Microsoft.UI.Xaml.Window window, Action laidOut)
    {
        if (!AppWindowTitleBar.IsCustomizationSupported() || window.AppWindow?.TitleBar is not { } titleBar)
            return false;

        titleBar.ExtendsContentIntoTitleBar = true;
        titleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        titleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;

        // MAUI sets the window's content (its root view) after OnWindowCreated and applies the root view's template
        // later still, so the title row is looked up after each layout pass until it exists.
        window.Activated += OnActivated;
        return true;

        void OnActivated(object sender, WindowActivatedEventArgs e)
        {
            if (window.Content is not FrameworkElement root) return;
            window.Activated -= OnActivated;
            root.LayoutUpdated += OnLayoutUpdated;
            OnLayoutUpdated(null, null);

            void OnLayoutUpdated(object? s, object? a)
            {
                if (!CollapseMauiTitleRow(root, titleBar) || root.XamlRoot is null) return;
                root.LayoutUpdated -= OnLayoutUpdated;
                laidOut();
            }
        }
    }

    /// <summary>
    /// MAUI has no public switch for its title row on Windows: a template part named "AppTitleBarContainer" shows the
    /// window title over the page, and the page's NavigationView content ("ContentGrid") gets a top margin of that
    /// row's height. The row is collapsed and the margin pinned to 0, since MAUI sets it again on window style changes
    /// such as maximize.
    /// </summary>
    private static bool CollapseMauiTitleRow(DependencyObject root, AppWindowTitleBar titleBar)
    {
        if (Find(root, "AppTitleBarContainer") is not FrameworkElement row
            || Find(root, "ContentGrid") is not FrameworkElement content) return false;
        row.Visibility = Visibility.Collapsed;
        content.Margin = default;
        content.RegisterPropertyChangedCallback(FrameworkElement.MarginProperty, (_, _) =>
        {
            if (content.Margin != default) content.Margin = default;
        });
        titleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        return true;
    }

    private static DependencyObject? Find(DependencyObject parent, string name)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is FrameworkElement fe && fe.Name == name) return child;
            if (Find(child, name) is { } hit) return hit;
        }
        return null;
    }
}
