using System;
using System.Diagnostics;
using System.Management;

/// <summary>
/// Listens for power-mode changes made via Fn+Q by subscribing to Lenovo's WMI
/// thermal-mode event, so the UI can stay in sync instead of showing a stale value.
/// </summary>
public sealed class PowerModeListener : IDisposable
{
    // WMI event Lenovo fires when the power/thermal mode is switched with Fn+Q.
    private const string EventClass = "LENOVO_GAMEZONE_THERMAL_MODE_EVENT";

    private ManagementEventWatcher _watcher;

    /// <summary>
    /// Raised when an Fn+Q power-mode change is detected. The argument is the
    /// freshly re-read current mode (queried via WMI, not parsed from the event
    /// payload). Handlers run on a thread-pool thread — marshal to the UI thread
    /// before touching UI.
    /// </summary>
    public event EventHandler<PowerModeHelper.LegionPowerMode> PowerModeChanged;

    public void Start()
    {
        if (_watcher != null) return;

        try
        {
            var scope = new ManagementScope("\\\\.\\ROOT\\WMI");
            _watcher = new ManagementEventWatcher(scope, new WqlEventQuery($"SELECT * FROM {EventClass}"));
            _watcher.EventArrived += OnEventArrived;
            _watcher.Start();
            Debug.WriteLine($"PowerModeListener: subscribed to {EventClass}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"PowerModeListener: could not subscribe to {EventClass}: {ex.Message}");
            _watcher = null;
        }
    }

    private void OnEventArrived(object sender, EventArrivedEventArgs e)
    {
        // Use the event purely as a trigger and read the authoritative value back,
        // so we don't depend on the generation-specific event payload layout.
        try
        {
            PowerModeChanged?.Invoke(this, PowerModeHelper.GetCurrentPowerMode());
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"PowerModeListener: failed to read mode after event: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_watcher == null) return;

        try
        {
            _watcher.EventArrived -= OnEventArrived;
            _watcher.Stop();
            _watcher.Dispose();
        }
        catch { /* best-effort cleanup */ }

        _watcher = null;
    }
}
