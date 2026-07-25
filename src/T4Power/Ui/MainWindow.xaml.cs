using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using T4Power.Core;
using T4Power.Core.Fans;
using T4Power.Core.Model;

namespace T4Power.Ui;

public partial class MainWindow : Window
{
    readonly MainViewModel _model;
    readonly ServiceLink _link;

    internal MainWindow(MainViewModel model, ServiceLink link)
    {
        _model = model;
        _link = link;
        DataContext = model;
        InitializeComponent();
    }

    /// <summary>Closing hides rather than exits — this is a tray app.</summary>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
        base.OnClosing(e);
    }

    // ---- button handlers -------------------------------------------------------------

    async void Preset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: string profile } button) return;
        if (FindGpu(button) is not { } gpu) return;

        await ReportAsync(await gpu.ApplyProfileAsync(profile).ConfigureAwait(true));
    }

    async void Boost_Click(object sender, RoutedEventArgs e)
    {
        if (FindGpu(sender) is not { } gpu) return;
        await ReportAsync(await gpu.BoostAsync(TimeSpan.FromMinutes(30)).ConfigureAwait(true));
    }

    async void Auto_Click(object sender, RoutedEventArgs e)
    {
        if (FindGpu(sender) is not { } gpu) return;
        await ReportAsync(await gpu.ReturnToAutoAsync().ConfigureAwait(true));
    }

    async void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (FindGpu(sender) is not { } gpu) return;
        await ReportAsync(await gpu.RestoreDefaultsAsync().ConfigureAwait(true));
    }

    async void ManageGpu_Click(object sender, RoutedEventArgs e)
    {
        if (FindGpu(sender) is not { } gpu) return;

        var answer = MessageBox.Show(
            $"Let T4Power set power limits and clocks on {gpu.Name}?\n\n" +
            "It is left alone by default because clamping a display adapter can hurt desktop " +
            "responsiveness. The Tesla T4 is managed out of the box.",
            "Manage this GPU", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (answer != MessageBoxResult.OK) return;

        // Round-trip the stored config rather than inventing one, so profiles and rules survive.
        var store = new ConfigStore();
        var config = store.Load().Find(gpu.Uuid);
        if (config is null)
        {
            _model.LastMessage = "could not read the stored configuration for this GPU";
            return;
        }

        await ReportAsync(await _link.SaveConfigAsync(config with { Managed = true }).ConfigureAwait(true));
    }

    // ---- slider / checkbox gestures --------------------------------------------------
    //
    // Every commit below is driven by a real user action. DragStarted/DragCompleted and Click
    // cannot be raised by the poll loop updating a bound property, which is what keeps the UI
    // from writing the service's own state back at it as a manual override.

    void Slider_DragStarted(object sender, RoutedEventArgs e)
    {
        if (FindGpu(sender) is { } gpu) gpu.IsUserAdjusting = true;
    }

    async void Slider_DragCompleted(object sender, RoutedEventArgs e)
    {
        if (FindGpu(sender) is not { } gpu) return;
        gpu.IsUserAdjusting = false;
        await ReportAsync(await gpu.CommitAsync().ConfigureAwait(true));
    }

    async void Slider_KeyUp(object sender, System.Windows.Input.KeyEventArgs e)
    {
        // Arrow/page/home/end nudge the value without ever touching the thumb.
        if (e.Key is not (Key.Left or Key.Right or Key.Up or Key.Down
            or Key.PageUp or Key.PageDown or Key.Home or Key.End)) return;

        if (FindGpu(sender) is not { } gpu) return;
        await ReportAsync(await gpu.CommitAsync().ConfigureAwait(true));
    }

    async void ClockLock_Click(object sender, RoutedEventArgs e)
    {
        if (FindGpu(sender) is not { } gpu) return;
        gpu.OnPinToggled();
        await ReportAsync(await gpu.CommitAsync().ConfigureAwait(true));
    }

    // ---- watchlist -------------------------------------------------------------------

    async void AddWatch_Click(object sender, RoutedEventArgs e)
    {
        if (FindGpu(sender) is not { } gpu) return;
        await ReportAsync(await gpu.AddWatchAsync().ConfigureAwait(true));
    }

    async void NewApp_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (FindGpu(sender) is not { } gpu) return;
        await ReportAsync(await gpu.AddWatchAsync().ConfigureAwait(true));
    }

    async void RemoveWatch_Click(object sender, RoutedEventArgs e)
    {
        // The chip's DataContext is the executable name; the GPU is further up the tree.
        if (sender is not FrameworkElement { DataContext: string exe }) return;
        if (FindGpu(sender) is not { } gpu) return;
        await ReportAsync(await gpu.RemoveWatchAsync(exe).ConfigureAwait(true));
    }

    // ---- fan control -----------------------------------------------------------------
    //
    // The same discipline as the GPU sliders above: every commit below is driven by a real user
    // action, and the curve editor raises CurveCommitted only on gesture boundaries. Nothing here
    // can be triggered by the poll loop updating a bound property.

    async void AdoptFan_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: FanChannel channel }) return;
        if (_model.FanSourceGpu is not { } gpu) return;

        var answer = MessageBox.Show(
            $"Let T4Power drive {channel.Describe()} from {gpu.Name}'s temperature?\n\n" +
            "The header will briefly run at full speed to confirm it responds.\n\n" +
            "Make sure nothing else is driving this header, and that the BIOS curve for it is " +
            "safe — that is what takes over whenever T4Power is not running.",
            "Adopt this fan header", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (answer != MessageBoxResult.OK) return;

        _model.LastMessage = "checking the header responds...";
        await ReportAsync(await _link.AdoptFanAsync(channel.Identifier, gpu.Uuid).ConfigureAwait(true));
    }

    /// <summary>Spins an unadopted header so the user can hear which fan it is. Goes straight to
    /// the service rather than through a view model, because nothing owns this header yet.</summary>
    async void IdentifyChannel_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: FanChannel channel }) return;

        _model.LastMessage = $"spinning {channel.Name} up for ten seconds...";
        await ReportAsync(await _link
            .SetFanPercentAsync(channel.Identifier, 100, TimeSpan.FromSeconds(10))
            .ConfigureAwait(true));
    }

    async void IdentifyFan_Click(object sender, RoutedEventArgs e)
    {
        if (FindFan(sender) is not { } fan) return;
        await ReportAsync(await fan.IdentifyAsync().ConfigureAwait(true));
    }

    async void FanAuto_Click(object sender, RoutedEventArgs e)
    {
        if (FindFan(sender) is not { } fan) return;
        await ReportAsync(await fan.ReturnToCurveAsync().ConfigureAwait(true));
    }

    async void ReleaseFan_Click(object sender, RoutedEventArgs e)
    {
        if (FindFan(sender) is not { } fan) return;

        var answer = MessageBox.Show(
            $"Hand {fan.Name} back to the BIOS and stop managing it?\n\n" +
            "The motherboard's own fan curve takes over immediately.",
            "Release this fan header", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (answer != MessageBoxResult.OK) return;

        await ReportAsync(await fan.ReleaseAsync().ConfigureAwait(true));
    }

    void FanSlider_DragStarted(object sender, RoutedEventArgs e)
    {
        if (FindFan(sender) is { } fan) fan.IsUserAdjusting = true;
    }

    async void FanSlider_DragCompleted(object sender, RoutedEventArgs e)
    {
        if (FindFan(sender) is not { } fan) return;
        fan.IsUserAdjusting = false;
        await ReportAsync(await fan.CommitDutyAsync().ConfigureAwait(true));
    }

    async void FanSlider_KeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Left or Key.Right or Key.Up or Key.Down
            or Key.PageUp or Key.PageDown or Key.Home or Key.End)) return;

        if (FindFan(sender) is not { } fan) return;
        await ReportAsync(await fan.CommitDutyAsync().ConfigureAwait(true));
    }

    /// <summary>
    /// Wires a curve editor to its view model as each card is realised.
    ///
    /// Done here rather than with bindings because the editor's contract is event-based on
    /// purpose: PointsChanged is local-only and fires throughout a drag, while CurveCommitted is
    /// the single path to the service. Expressing that as a two-way binding is exactly the
    /// mistake the sliders above document.
    /// </summary>
    void CurveEditor_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FanCurveEditor editor) return;
        if (editor.DataContext is not FanViewModel fan) return;

        // Idempotent: a virtualised card can raise Loaded more than once for the same instance.
        editor.PointsChanged -= fan.ReplacePointsLocally;
        editor.PointsChanged += fan.ReplacePointsLocally;

        editor.IsUserAdjusting -= OnCurveAdjusting;
        editor.IsUserAdjusting += OnCurveAdjusting;

        editor.CurveCommitted -= OnCurveCommitted;
        editor.CurveCommitted += OnCurveCommitted;

        void OnCurveAdjusting(bool adjusting) => fan.IsUserAdjusting = adjusting;

        async void OnCurveCommitted() =>
            await ReportAsync(await fan.CommitCurveAsync().ConfigureAwait(true));
    }

    void GetPawnIo_Click(object sender, RoutedEventArgs e)
    {
        if (_model.FanHelpUrl is not { } url) return;

        try
        {
            Shell.OpenUrl(url);
            _model.LastMessage = "opened the PawnIO download page — install it, then restart the T4Power service";
        }
        catch (Exception ex)
        {
            // Falling back to showing the URL beats a dead button.
            _model.LastMessage = $"could not open the browser ({ex.Message}) — the address is {url}";
        }
    }

    void InstallService_Click(object sender, RoutedEventArgs e)
    {
        // Runs the install verb, which raises the one UAC prompt this design costs.
        try
        {
            Process.Start(new ProcessStartInfo(Paths.ExecutablePath, "--install-service")
            {
                UseShellExecute = true,
            });
            _model.LastMessage = "installing the service — approve the elevation prompt";
        }
        catch (Exception ex)
        {
            _model.LastMessage = $"could not start the installer: {ex.Message}";
        }
    }

    // ---- helpers ---------------------------------------------------------------------

    /// <summary>Finds the card a clicked button belongs to. Buttons inside the preset
    /// ItemsControl have a string DataContext, so walk up to the GPU.</summary>
    static GpuViewModel? FindGpu(object sender)
    {
        var element = sender as FrameworkElement;
        while (element is not null)
        {
            if (element.DataContext is GpuViewModel gpu) return gpu;
            element = System.Windows.Media.VisualTreeHelper.GetParent(element) as FrameworkElement;
        }
        return null;
    }

    /// <summary>The fan-card equivalent of <see cref="FindGpu"/>.</summary>
    static FanViewModel? FindFan(object sender)
    {
        var element = sender as FrameworkElement;
        while (element is not null)
        {
            if (element.DataContext is FanViewModel fan) return fan;
            element = System.Windows.Media.VisualTreeHelper.GetParent(element) as FrameworkElement;
        }
        return null;
    }

    Task ReportAsync((bool Ok, string? Message) result)
    {
        _model.LastMessage = result.Message ?? (result.Ok ? "done" : "failed");
        return Task.CompletedTask;
    }
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Visibility.Visible;
}

public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not Visibility.Visible;
}
