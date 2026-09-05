using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace Lenovo_Fan_Controller;

// Explicit command-line diagnostic only. Exercises the same helper as Save & apply.
internal static class NormalFanDiagnostic
{
    public static async Task<int> RunAsync()
    {
        string log = Path.Combine(AppContext.BaseDirectory, "normal-fans-4000.log");
        void Record(string message) => File.AppendAllText(log, $"{DateTime.Now:O} {message}\n");
        using var session = new NormalFanSession();
        using var reader = new LegionFanTargets();
        bool started = false;
        try
        {
            var baseline = reader.ReadState();
            Record("Baseline " + baseline);
            if (baseline.mode is not (1 or 2 or 3) || baseline.cpu >= 75 || baseline.gpu >= 70 || baseline.pch >= 70)
                throw new InvalidOperationException("Baseline mode / temperature unsuitable for short test.");
            NormalFanCurvePolicy.Target(new FanConfig { CpuTempsRampUp = [30, 85], GpuTempsRampUp = [30, 80], FanRpmPoints = [4000, 4000] }, baseline.cpu, baseline.gpu, baseline.pch, 1700, 5300);
            var config = new FanConfig { CpuTempsRampUp = [30, 85], GpuTempsRampUp = [30, 80], FanRpmPoints = [4000, 4000] };
            started = true;
            await session.StartAsync(new Dictionary<int, FanConfig> { [baseline.mode] = config });
            int reached = 0;
            for (int i = 0; i < 12; i++)
            {
                session.Pulse();
                await Task.Delay(1000);
                var state = reader.ReadState();
                Record($"Sample {i + 1}: {state}; helper={session.Status}");
                if (!session.IsRunning || state.mode != baseline.mode || state.cpu >= 80 || state.gpu >= 75 || state.pch >= 75)
                    throw new InvalidOperationException("Control ended, mode changed or thermal test limit reached.");
                if (Math.Abs(state.cpuRpm - 4000) <= 100 && Math.Abs(state.gpuRpm - 4000) <= 100) reached++;
            }
            if (reached < 3) throw new InvalidOperationException("4000 RPM not verified for at least three samples.");
            Record("PASS: both measured fans reached 4000 ±100 RPM for at least three samples; mode unchanged.");
            return 0;
        }
        catch (Exception ex) { Record("FAIL: " + ex); return 1; }
        finally
        {
            session.RequestStop();
            try { await session.WaitForStopAsync(); Record("Stopped: " + session.Status); }
            finally
            {
                if (started) reader.RecoverLostSession();
            }
            for (int i = 0; i < 8; i++)
            {
                await Task.Delay(1000);
                Record("Automatic recovery " + reader.ReadState());
            }
        }
    }
}
