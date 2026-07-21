using System;
using System.Diagnostics;
using System.Management;
using System.Threading.Tasks;

/// <summary>
/// Listens for power-mode changes made via Fn+Q by subscribing to Lenovo's WMI
/// thermal-mode event, so the UI can stay in sync instead of showing a stale value.
/// </summary>
public sealed class PowerModeListener : IDisposable
{
    // WMI event Lenovo fires when the power/thermal mode is switched with Fn+Q.
    private const string EventClass = "LENOVO_GAMEZONE_THERMAL_MODE_EVENT";

    private ManagementEventWatcher _watcher;

    /// <summary>True while the WMI subscription is active and delivering events.</summary>
    public bool IsRunning => _watcher != null;

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

    private async void OnEventArrived(object sender, EventArrivedEventArgs e)
    {
        // Use the event purely as a trigger and read the authoritative value back,
        // so we don't depend on the generation-specific event payload layout.
        try
        {
            await Task.Delay(250);
            PowerModeChanged?.Invoke(this, PowerModeHelper.GetCurrentPowerMode());
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"PowerModeListener: failed to read mode after event: {ex.Message}");
        }
    }

    /// <summary>
    /// Tears down and re-creates the WMI subscription. Use after the system
    /// resumes from sleep/hibernate, where the event watcher can silently stop
    /// delivering events. Check <see cref="IsRunning"/> afterwards to confirm the
    /// subscription was re-established (WMI may not be ready immediately on wake).
    /// </summary>
    public void Restart()
    {
        Stop();
        Start();
    }

    private void Stop()
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

    public void Dispose() => Stop();
}
