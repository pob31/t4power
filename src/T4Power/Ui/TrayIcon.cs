using System.Drawing;
using System.Windows;
using T4Power.Core.Ipc;
using T4Power.Core.Model;
using WinForms = System.Windows.Forms;

namespace T4Power.Ui;

/// <summary>
/// The tray presence: icon, tooltip, and a context menu offering the profiles the service
/// actually reports for each GPU. Rebuilt whenever state changes so the menu never offers a
/// profile that no longer exists.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    readonly WinForms.NotifyIcon _icon;
    readonly ServiceLink _link;
    readonly Func<MainWindow> _window;

    IReadOnlyList<GpuStateDto> _state = [];
    Icon? _current;

    public TrayIcon(ServiceLink link, Func<MainWindow> window)
    {
        _link = link;
        _window = window;

        _icon = new WinForms.NotifyIcon
        {
            Text = "T4Power",
            Visible = true,
            Icon = TrayIconFactory.Create(null, available: false),
            ContextMenuStrip = new WinForms.ContextMenuStrip(),
        };

        _icon.DoubleClick += (_, _) => ShowWindow();
        _icon.ContextMenuStrip.Opening += (_, _) => BuildMenu();

        _link.StateChanged += OnStateChanged;
        _link.AvailabilityChanged += (_, _) => Refresh();
    }

    void OnStateChanged(IReadOnlyList<GpuStateDto> state)
    {
        _state = state;
        Refresh();
    }

    void Refresh()
    {
        // Prefer the T4 for the icon: it is what the app exists to manage.
        var primary = _state.FirstOrDefault(g => g.Managed) ?? _state.FirstOrDefault();

        double? load = null;
        if (primary?.Telemetry is { PowerDrawW: { } draw, PowerLimitW: { } cap } && cap > 0)
            load = draw / cap;

        var next = TrayIconFactory.Create(load, _link.IsAvailable);
        _icon.Icon = next;

        // The NotifyIcon does not take ownership, so the previous icon must be freed by hand.
        _current?.Dispose();
        _current = next;

        _icon.Text = BuildTooltip(primary);
    }

    string BuildTooltip(GpuStateDto? gpu)
    {
        if (!_link.IsAvailable) return "T4Power — service not running";
        if (gpu is null) return "T4Power";

        var t = gpu.Telemetry;
        var text = $"{gpu.Name}\n{t?.PowerDrawW:0.0} W / {t?.PowerLimitW:0.#} W  ·  {t?.SmClockMhz} MHz  ·  {t?.TemperatureC} °C";
        if (gpu.ActiveProfile is not null) text += $"\nProfile: {gpu.ActiveProfile}";

        // NotifyIcon.Text is capped at 63 characters on older shells; keep it safe.
        return text.Length > 127 ? text[..127] : text;
    }

    void BuildMenu()
    {
        var menu = _icon.ContextMenuStrip!;
        menu.Items.Clear();

        if (!_link.IsAvailable)
        {
            menu.Items.Add(new WinForms.ToolStripMenuItem("Service not running") { Enabled = false });
            menu.Items.Add(new WinForms.ToolStripSeparator());
        }

        foreach (var gpu in _state)
        {
            var header = new WinForms.ToolStripMenuItem(gpu.Name) { Enabled = false };
            menu.Items.Add(header);

            if (!gpu.Managed)
            {
                menu.Items.Add(new WinForms.ToolStripMenuItem("  not managed") { Enabled = false });
                menu.Items.Add(new WinForms.ToolStripSeparator());
                continue;
            }

            foreach (var profile in gpu.Profiles)
            {
                var item = new WinForms.ToolStripMenuItem($"  {profile.Name}")
                {
                    Checked = string.Equals(profile.Name, gpu.ActiveProfile, StringComparison.OrdinalIgnoreCase),
                    ToolTipText = profile.Describe(),
                };
                var uuid = gpu.Uuid;
                var name = profile.Name;
                item.Click += async (_, _) => await _link.ApplyProfileAsync(uuid, name).ConfigureAwait(true);
                menu.Items.Add(item);
            }

            var boost = new WinForms.ToolStripMenuItem("  Boost for 30 min");
            var boostUuid = gpu.Uuid;
            boost.Click += async (_, _) =>
                await _link.ApplyProfileAsync(boostUuid, Profile.Max, TimeSpan.FromMinutes(30)).ConfigureAwait(true);
            menu.Items.Add(boost);

            var auto = new WinForms.ToolStripMenuItem("  Back to automatic")
            {
                Enabled = gpu.Override is not null,
            };
            var autoUuid = gpu.Uuid;
            auto.Click += async (_, _) => await _link.ReturnToAutoAsync(autoUuid).ConfigureAwait(true);
            menu.Items.Add(auto);

            menu.Items.Add(new WinForms.ToolStripSeparator());
        }

        var open = new WinForms.ToolStripMenuItem("Open T4Power");
        open.Click += (_, _) => ShowWindow();
        menu.Items.Add(open);

        var exit = new WinForms.ToolStripMenuItem("Exit");
        exit.Click += (_, _) =>
        {
            // Only the UI exits. The service keeps managing the GPU, which is the point of
            // having a service at all.
            _icon.Visible = false;
            Application.Current.Shutdown();
        };
        menu.Items.Add(exit);
    }

    void ShowWindow()
    {
        var window = _window();
        window.Show();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _current?.Dispose();
    }
}
