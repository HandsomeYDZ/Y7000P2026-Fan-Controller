using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Lenovo_Fan_Controller;

// The helper owns targets and watches the GUI heartbeat. Closing/hanging the GUI
// therefore does not leave targets latched. No control is enabled at app startup.
internal sealed class NormalFanSession : IDisposable
{
    private readonly string _token = Guid.NewGuid().ToString("N");
    private readonly EventWaitHandle _pulse;
    private readonly EventWaitHandle _stop;
    private readonly EventWaitHandle _ready;
    private Process? _worker;
    private string? _requestPath;
    public bool IsRunning => _worker != null && !_worker.HasExited;
    public string Status => _requestPath != null && File.Exists(_requestPath + ".status")
        ? File.ReadAllText(_requestPath + ".status") : "Starting fan control…";

    public NormalFanSession()
    {
        _pulse = new EventWaitHandle(false, EventResetMode.AutoReset, Name(_token, "pulse"));
        _stop = new EventWaitHandle(false, EventResetMode.ManualReset, Name(_token, "stop"));
        _ready = new EventWaitHandle(false, EventResetMode.ManualReset, Name(_token, "ready"));
    }

    private static string Name(string token, string purpose) => @"Local\Y7000P2026-" + token + "-" + purpose;

    public async Task StartAsync(Dictionary<int, FanConfig> profiles, Func<string[]>? conflictCheck = null)
    {
        if (profiles.Count == 0) throw new InvalidOperationException("Save at least one normal-mode profile first.");
        foreach (var (mode, config) in profiles)
        {
            if (mode is not (1 or 2 or 3)) throw new ArgumentException("Only ordinary modes can have target curves.");
            NormalFanCurvePolicy.Validate(config, 1700, 5300);
        }
        var conflicts = conflictCheck?.Invoke() ?? LegionFanTargets.ConflictingApps(Environment.ProcessId);
        if (conflicts.Length > 0) throw new InvalidOperationException("Close other fan/mode applications first: " + string.Join(", ", conflicts));
        _requestPath = Path.Combine(AppContext.BaseDirectory, "Config", "session-" + _token + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(_requestPath)!);
        File.WriteAllText(_requestPath, JsonSerializer.Serialize(profiles));
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        start.ArgumentList.Add("--normal-fan-session");
        start.ArgumentList.Add(_token);
        start.ArgumentList.Add(Environment.ProcessId.ToString());
        start.ArgumentList.Add(_requestPath);
        _worker = Process.Start(start) ?? throw new InvalidOperationException("Could not start cooling helper.");
        // Keep this asynchronous: the UI must continue delivering its heartbeat.
        bool ready = await Task.Run(() => _ready.WaitOne(10000));
        if (!ready || _worker.HasExited)
        {
            RequestStop();
            throw new InvalidOperationException(Status);
        }
        Pulse();
    }

    public void Pulse() => _pulse.Set();
    public void RequestStop() => _stop.Set();
    public async Task WaitForStopAsync()
    {
        if (_worker != null && !await Task.Run(() => _worker.WaitForExit(10000)))
            throw new TimeoutException("Cooling helper has not stopped; mode change cancelled.");
    }

    public static int RunWorker(string[] args, Func<IFanTargetDevice>? deviceFactory = null,
        Func<int[], string[]>? conflictCheck = null)
    {
        if (args.Length != 5 || !Guid.TryParseExact(args[2], "N", out _) || !int.TryParse(args[3], out int parentId)) return 1;
        string token = args[2], requestPath = args[4];
        string statusPath = requestPath + ".status";
        void Status(string text)
        {
            try { File.WriteAllText(statusPath, text); }
            catch (IOException) { Debug.WriteLine(text); }
            catch (UnauthorizedAccessException) { Debug.WriteLine(text); }
        }
        IFanTargetDevice? firmware = null;
        bool ownsControl = false;
        string endReason = "Session ended; enable curves to start again.";
        using var owner = new Mutex(false, @"Global\Y7000P2026-NormalFanSession");
        try
        {
            using var parent = Process.GetProcessById(parentId);
            if (parent.MainModule?.FileName != Environment.ProcessPath) throw new InvalidOperationException("Unexpected parent process.");
            using var pulse = EventWaitHandle.OpenExisting(Name(token, "pulse"));
            using var stop = EventWaitHandle.OpenExisting(Name(token, "stop"));
            using var ready = EventWaitHandle.OpenExisting(Name(token, "ready"));
            try { ownsControl = owner.WaitOne(0); } catch (AbandonedMutexException) { ownsControl = true; }
            if (!ownsControl) throw new InvalidOperationException("Another cooling helper owns control.");
            var profiles = JsonSerializer.Deserialize<Dictionary<int, FanConfig>>(File.ReadAllText(requestPath))
                ?? throw new InvalidOperationException("No saved profiles.");
            firmware = deviceFactory?.Invoke() ?? new LegionFanTargets();
            foreach (var (mode, config) in profiles)
            {
                if (mode is not (1 or 2 or 3)) throw new ArgumentException("Unsupported profile mode.");
                NormalFanCurvePolicy.Validate(config, firmware.Minimum, firmware.Maximum);
            }
            var conflicts = conflictCheck?.Invoke([parentId, Environment.ProcessId]) ?? LegionFanTargets.ConflictingApps(parentId, Environment.ProcessId);
            if (conflicts.Length > 0) throw new InvalidOperationException("Conflicting fan/mode application: " + string.Join(", ", conflicts));
            Status("Ready; waiting for GUI heartbeat.");
            ready.Set();
            if (!pulse.WaitOne(10000) || stop.WaitOne(0)) return 0;
            var lease = new FanControlLease(DateTime.UtcNow);
            bool Alive()
            {
                if (pulse.WaitOne(0)) lease.Refresh(DateTime.UtcNow);
                return lease.IsAlive(DateTime.UtcNow, !parent.HasExited, stop.WaitOne(0));
            }
            int lastMode = -1, lastTarget = 0;
            DateTime lastWrite = DateTime.MinValue;
            while (Alive())
            {
                conflicts = conflictCheck?.Invoke([parentId, Environment.ProcessId]) ?? LegionFanTargets.ConflictingApps(parentId, Environment.ProcessId);
                if (conflicts.Length > 0) throw new InvalidOperationException("Another fan/mode app started; returning to automatic cooling.");
                var state = firmware.ReadState();
                if (state.mode != lastMode)
                {
                    firmware.RestoreAutomatic();
                    lastTarget = 0;
                    lastMode = state.mode;
                    // Allow the firmware's mode transition to settle, without a mode write.
                    Thread.Sleep(500);
                    continue;
                }
                if (!profiles.TryGetValue(state.mode, out var config))
                {
                    firmware.RestoreAutomatic();
                    lastTarget = 0;
                    Status($"Mode {state.mode}: firmware automatic (no enabled profile).");
                }
                else
                {
                    int target = NormalFanCurvePolicy.Target(config, state.cpu, state.gpu, state.pch, firmware.Minimum, firmware.Maximum);
                    if (state.cpuRpm < 0 || state.gpuRpm < 0 || state.cpuRpm > 12000 || state.gpuRpm > 12000 ||
                        (lastTarget > 0 && DateTime.UtcNow - lastWrite > TimeSpan.FromSeconds(5) && (state.cpuRpm == 0 || state.gpuRpm == 0)))
                        throw new InvalidOperationException("Invalid or stopped fan feedback.");
                    // Slow reductions; rising demand is immediate. Firmware still ramps mechanically.
                    if (lastTarget > 0 && target < lastTarget) target = Math.Max(target, lastTarget - 100);
                    if (target != lastTarget)
                    {
                        // Re-read authoritative mode immediately before a target update.
                        if (firmware.ReadState().mode != state.mode) continue;
                        firmware.SetTarget(target, Alive);
                        lastTarget = target;
                        lastWrite = DateTime.UtcNow;
                    }
                    Status($"Mode {state.mode}: target {target} RPM; CPU {state.cpuRpm}, GPU {state.gpuRpm} RPM.");
                }
                Thread.Sleep(1000);
            }
            Status("Control stopped; restoring firmware automatic cooling.");
            return 0;
        }
        catch (Exception ex)
        {
            endReason = ex.Message;
            Status("Control stopped: " + ex.Message);
            return 1;
        }
        finally
        {
            if (firmware != null)
            {
                bool restored = false;
                for (int i = 0; i < 3 && !restored; i++)
                {
                    try { firmware.RestoreAutomatic(); restored = true; }
                    catch (Exception ex) { Status("Automatic recovery failed: " + ex.Message); Thread.Sleep(200); }
                }
                if (restored) Status("Automatic cooling requested. " + endReason);
                try { firmware.Dispose(); } catch (Exception ex) { Status("Recovery requires attention: " + ex.Message); }
            }
            if (ownsControl) owner.ReleaseMutex();
        }
    }

    public void Dispose()
    {
        RequestStop();
        _worker?.Dispose();
        _pulse.Dispose();
        _stop.Dispose();
        _ready.Dispose();
    }
}
