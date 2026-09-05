using Lenovo_Fan_Controller;
using LegionFanController.Hardware;

// Child processes in the offline integration tests always inject a fake device.
if (args.FirstOrDefault() == "--normal-fan-session")
{
    Environment.Exit(NormalFanSession.RunWorker(Environment.GetCommandLineArgs(), () => new FakeFanTargetDevice(args[3]), _ => []));
    return;
}

// No hardware writes. --probe additionally performs only WMI discovery and reads.
if (args.Contains("--probe"))
{
    using var firmware = new LegionFanTargets();
    var state = firmware.ReadState();
    Console.WriteLine($"WMI discovery OK: {firmware.Minimum}–{firmware.Maximum}; mode={state.mode}; CPU={state.cpuRpm}, GPU={state.gpuRpm} RPM; temperatures={state.cpu}/{state.gpu}/{state.pch}");
    if (firmware.WriteAttempted) throw new Exception("Read-only probe attempted a write.");
    return;
}

int checks = 0;
void Check(bool result, string name)
{
    if (!result) throw new Exception(name);
    checks++;
}
void Reject(Action action, string name)
{
    try { action(); } catch (ArgumentException) { checks++; return; }
    throw new Exception("Accepted invalid curve: " + name);
}
FanConfig Curve() => new() {
    CpuTempsRampUp = [35, 50, 65, 75, 85], GpuTempsRampUp = [35, 45, 60, 70, 80],
    FanRpmPoints = [1700, 2100, 2800, 3800, 5300]
};
var curve = Curve();
foreach (var newline in new[] { "\n", "\r\n", "\r" })
{
    var text = FanConfigFormat.ModernDefault.Replace("\r\n", "\n").Replace("\n", newline);
    var defaults = FanConfigFormat.ParseConfig(FanConfigFormat.Lines(text));
    NormalFanCurvePolicy.Validate(defaults, 1700, 5300);
    Check(defaults.FanRpmPoints[0] == 1700 && defaults.CpuTempsRampUp[0] == 35, "Modern default roundtrip with " + newline.Length + " character line ending");
}
var exactKeys = FanConfigFormat.ParseConfig(["fan_accl_values : 9 9", "fan_accl_value : 3"]);
Check(exactKeys.AccelerationValue == 3, "Scalar key must not match array key prefix");
NormalFanCurvePolicy.Validate(curve,1700,5300);
Check(NormalFanCurvePolicy.Target(curve,30,0,30,1700,5300) == 1700,"Sleep GPU and minimum");
Check(NormalFanCurvePolicy.Target(curve,50,0,40,1700,5300) == 2100,"Exact temperature point");
Check(NormalFanCurvePolicy.Target(curve,30,70,30,1700,5300) == 3800,"GPU demand must raise both fans");
Check(NormalFanCurvePolicy.Target(curve,30,0,75,1700,5300) == 3800,"PCH demand must raise both fans");
Check(NormalFanCurvePolicy.Target(curve,85,0,30,1700,5300) == 5300,"Maximum");
int previous = 0;
for (int temp = 1; temp < 90; temp++)
{
    int value = NormalFanCurvePolicy.Target(curve,temp,0,30,1700,5300);
    Check(value >= previous && value >= 1700 && value <= 5300 && value % 100 == 0,"Bounded monotonic quantization");
    previous = value;
}
foreach (var bad in new[] {(0,0,30),(-1,0,30),(90,0,30),(50,-1,30),(50,85,30),(50,0,0),(50,0,80)})
{
    try { NormalFanCurvePolicy.Target(curve,bad.Item1,bad.Item2,bad.Item3,1700,5300); }
    catch (InvalidOperationException) { checks++; continue; }
    throw new Exception("Unsafe sensor accepted.");
}
foreach (int badRpm in new[] {0,1,99,1699,5350,5400,18045})
{
    var bad = Curve(); bad.FanRpmPoints[0] = badRpm;
    Reject(() => NormalFanCurvePolicy.Validate(bad,1700,5300),"RPM " + badRpm);
}
var descending = Curve(); descending.FanRpmPoints[2] = 1800;
Reject(() => NormalFanCurvePolicy.Validate(descending,1700,5300),"Descending RPM");
var duplicates = Curve(); duplicates.GpuTempsRampUp[2] = duplicates.GpuTempsRampUp[1];
Reject(() => NormalFanCurvePolicy.Validate(duplicates,1700,5300),"Duplicate GPU temperature");
var mismatch = Curve(); mismatch.CpuTempsRampUp = [30,60];
Reject(() => NormalFanCurvePolicy.Validate(mismatch,1700,5300),"Mismatched length");
Reject(() => NormalFanCurvePolicy.Validate(Curve(),0,5300),"Invalid firmware minimum");

// Exercise the actual lowest-level writer: no driver initialization and no I/O.
Check(!HardwareAccessPolicy.LegacyWritesAllowed,"Default deny");
foreach (Action write in new Action[] {
    () => ECWriter.WriteECByte(0xC551,26), () => ECWriter.BeginFanTableUpdate(),
    () => ECWriter.WriteFanRpmPoints([26],[26]), () => ECWriter.ResetFanCurveState(),
    () => ECWriter.WriteTemperatureRamp([40],[35],0xC580,0xC591),
    () => ECWriter.WriteFanAcclDeccl(6,[2],[2]), () => ECWriter.WriteStopRgbFanWake()
})
{
    try { write(); } catch (InvalidOperationException) { checks++; continue; }
    throw new Exception("Legacy EC write escaped guard.");
}
Check(!PawnIODriver.IsInitialized,"No low-level driver was opened by tests");
var now = DateTime.UtcNow;
var lease = new FanControlLease(now);
Check(lease.IsAlive(now.AddSeconds(5),true,false),"Heartbeat grace window");
Check(!lease.IsAlive(now.AddSeconds(6),true,false),"Expired heartbeat");
Check(!lease.IsAlive(now,true,true),"Explicit stop");
Check(!lease.IsAlive(now,false,false),"Parent crash");
Check(!lease.IsAlive(now.AddHours(1),true,false),"Resume after sleep");
Check(!lease.IsAlive(now.AddSeconds(-1),true,false),"Clock moved backward");
lease.Refresh(now.AddSeconds(4));
Check(lease.IsAlive(now.AddSeconds(9),true,false),"Heartbeat refresh");
var recovery = new List<(uint id,int rpm)>();
FanTargetRecovery.Restore((id,rpm) => recovery.Add((id,rpm)));
Check(recovery.SequenceEqual(new[] {(0x04030001u,0),(0x04030002u,0)}),"Auto recovery command semantics");
recovery.Clear();
try {
    FanTargetRecovery.Restore((id,rpm) => { recovery.Add((id,rpm)); throw new IOException("Injected failure"); });
    throw new Exception("Recovery failure not reported");
} catch (AggregateException ex) {
    Check(ex.InnerExceptions.Count == 2 && recovery.Count == 2,"Recover both fans despite failures");
}
foreach (string scenario in new[] { "heartbeat-loss", "stop", "sensor-failure", "mode-change" })
{
    Environment.SetEnvironmentVariable("FAN_SAFETY_TEST_SCENARIO", scenario);
    using var session = new NormalFanSession();
    var constant = Curve(); constant.FanRpmPoints = [2600,2600,2600,2600,2600];
    var profiles = new Dictionary<int,FanConfig> { [2] = constant };
    if (scenario == "mode-change")
    {
        var quiet = Curve(); quiet.FanRpmPoints = [2400,2400,2400,2400,2400];
        var performance = Curve(); performance.FanRpmPoints = [2800,2800,2800,2800,2800];
        profiles[1] = quiet; profiles[3] = performance;
    }
    await session.StartAsync(profiles, () => []);
    string started = session.Status;
    for (int tick = 0; tick < 24 && session.IsRunning; tick++)
    {
        await Task.Delay(500);
        if (scenario == "stop" && tick < 3) session.Pulse();
        if (scenario == "stop" && tick == 3) session.RequestStop();
    }
    Check(!session.IsRunning,"Helper stopped: " + scenario);
    Check(session.Status.StartsWith("Automatic cooling requested."),"Helper recovery: " + scenario);
    var record = Directory.GetFiles(Path.Combine(AppContext.BaseDirectory,"Config"),"*.fake-writes")
        .OrderByDescending(File.GetLastWriteTimeUtc).First();
    var commands = File.ReadAllLines(record);
    Check((scenario == "mode-change" ? commands.Contains("2400") && commands.Contains("2800") && !commands.Contains("2600") : commands.Contains("2600"))
        && commands.Last() == "auto", "Target applied and released using fake firmware: " + scenario);
}
Environment.SetEnvironmentVariable("FAN_SAFETY_TEST_SCENARIO", null);
Console.WriteLine($"PASS: {checks} safety checks, including real helper processes with fake firmware. No hardware writes or EC access.");

sealed class FakeFanTargetDevice(string requestPath) : IFanTargetDevice
{
    private int _target;
    private readonly System.Diagnostics.Stopwatch _time = System.Diagnostics.Stopwatch.StartNew();
    private readonly string _record = requestPath + ".fake-writes";
    public int Minimum => 1700;
    public int Maximum => 5300;
    public (int mode,int cpu,int gpu,int pch,int cpuRpm,int gpuRpm) ReadState()
    {
        if (_target != 0 && Environment.GetEnvironmentVariable("FAN_SAFETY_TEST_SCENARIO") == "sensor-failure")
            throw new IOException("Injected sensor failure");
        int mode = Environment.GetEnvironmentVariable("FAN_SAFETY_TEST_SCENARIO") == "mode-change"
            ? _time.Elapsed.TotalSeconds < 2 ? 1 : _time.Elapsed.TotalSeconds < 4 ? 3 : 255 : 2;
        return (mode,45,0,40,_target == 0 ? 1900 : _target,_target == 0 ? 1900 : _target);
    }
    public void SetTarget(int rpm, Func<bool> sessionAlive)
    {
        if (!sessionAlive()) throw new OperationCanceledException();
        _target = rpm;
        File.AppendAllLines(_record,[rpm.ToString()]);
    }
    public void RestoreAutomatic()
    {
        if (_target == 0) return;
        _target = 0;
        File.AppendAllLines(_record,["auto"]);
    }
    public void Dispose() => RestoreAutomatic();
}
