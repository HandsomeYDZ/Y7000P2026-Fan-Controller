using System;
using System.Linq;

namespace Lenovo_Fan_Controller;

internal static class NormalFanCurvePolicy
{
    public static void Validate(FanConfig config, int minimum, int maximum)
    {
        if (minimum < 100 || maximum < minimum || maximum > 12000)
            throw new ArgumentException("Invalid firmware RPM limits.");
        ValidateCurve(config.CpuTempsRampUp, config.FanRpmPoints, minimum, maximum);
        ValidateCurve(config.GpuTempsRampUp, config.FanRpmPoints, minimum, maximum);
    }

    private static void ValidateCurve(int[] temps, int[] rpms, int minimum, int maximum)
    {
        if (temps == null || rpms == null || temps.Length != rpms.Length || temps.Length < 2 || temps.Length > 10)
            throw new ArgumentException("Each sensor curve must have 2–10 matching temperature and RPM points.");
        for (int i = 0; i < temps.Length; i++)
        {
            if (temps[i] < 0 || temps[i] > 100 || rpms[i] < minimum || rpms[i] > maximum || rpms[i] % 100 != 0)
                throw new ArgumentException($"Use temperatures from 0–100 °C and RPM from {minimum}–{maximum} in steps of 100. Zero RPM is not a curve point.");
            if (i > 0 && (temps[i] <= temps[i - 1] || rpms[i] < rpms[i - 1]))
                throw new ArgumentException("Temperatures must increase and RPM must not decrease along a curve.");
        }
    }

    public static int Target(FanConfig config, int cpu, int gpu, int pch, int minimum, int maximum)
    {
        Validate(config, minimum, maximum);
        // Zero GPU temperature is the documented asleep reading. Other missing
        // sensors or high temperatures hand cooling back to the firmware.
        if (cpu < 1 || cpu >= 90 || gpu < 0 || gpu >= 85 || pch < 1 || pch >= 80)
            throw new InvalidOperationException("Sensor unavailable or thermal limit reached; restore automatic cooling.");
        int demand = Math.Max(Interpolate(config.CpuTempsRampUp, config.FanRpmPoints, cpu),
            Interpolate(config.CpuTempsRampUp, config.FanRpmPoints, pch));
        if (gpu > 0) demand = Math.Max(demand, Interpolate(config.GpuTempsRampUp, config.FanRpmPoints, gpu));
        // Both fans share the highest CPU/GPU/PCH demand, matching the coupled
        // editor. Round UP, and never turn a sub-minimum curve point into auto.
        return Math.Clamp(((demand + 99) / 100) * 100, minimum, maximum);
    }

    private static int Interpolate(int[] temps, int[] rpms, int temperature)
    {
        if (temperature <= temps[0]) return rpms[0];
        for (int i = 1; i < temps.Length; i++)
            if (temperature <= temps[i])
                return (int)Math.Ceiling(rpms[i - 1] + (double)(rpms[i] - rpms[i - 1]) *
                    (temperature - temps[i - 1]) / (temps[i] - temps[i - 1]));
        return rpms.Last();
    }
}
