using AssetManager.Core;

namespace AssetManager.Tests.Core;

/// <summary>Design D12: the window is ready once the layout has rendered and the first page has its data.</summary>
public class StartupSignalsTests
{
    [Fact]
    public void Window_is_ready_only_after_the_first_page_has_loaded()
    {
        var signals = new StartupSignals();
        var dashboard = signals.FirstLoad(); // the page starts loading during the first render

        signals.SignalWindowReady(); // the layout has rendered
        Assert.False(signals.WindowReady.IsCompleted);

        dashboard.Dispose();
        Assert.True(signals.WindowReady.IsCompleted);
    }

    [Fact]
    public void Two_loads_both_have_to_finish()
    {
        var signals = new StartupSignals();
        var grid = signals.FirstLoad();
        var filters = signals.FirstLoad();
        signals.SignalWindowReady();

        grid.Dispose();
        Assert.False(signals.WindowReady.IsCompleted);
        filters.Dispose();
        Assert.True(signals.WindowReady.IsCompleted);
    }

    [Fact]
    public void Page_without_data_and_loads_after_ready_change_nothing()
    {
        var signals = new StartupSignals();
        signals.SignalWindowReady(); // e.g. the Settings page: nothing to load
        Assert.True(signals.WindowReady.IsCompleted);

        using (signals.FirstLoad()) Assert.True(signals.WindowReady.IsCompleted); // a later page visit
    }
}
