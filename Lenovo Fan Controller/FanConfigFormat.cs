using System;
using System.Linq;
namespace Lenovo_Fan_Controller;
internal static class FanConfigFormat
{
public const string ModernDefault = @"legion_gen : 0
fan_curve_points : 5
fan_accl_value : 2
fan_deccl_value : 2
fan_rpm_points : 1700 2100 2800 3800 5300
cpu_temps_ramp_up : 35 50 65 75 85
cpu_temps_ramp_down : 32 47 62 72 82
gpu_temps_ramp_up : 35 45 60 70 80
gpu_temps_ramp_down : 32 42 57 67 77
hst_temps_ramp_up : 35 50 65 75 85
hst_temps_ramp_down : 32 47 62 72 82";
public static string[] Lines(string text) => text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        public static FanConfig ParseConfig(string[] lines)
        {
            int legionGen = GetConfigValue(lines, "legion_gen", 5);
            int accelerationValue = GetConfigValue(lines, "fan_accl_value", 2);
            int decelerationValue = GetConfigValue(lines, "fan_deccl_value", 2);
            int[] fan1RpmPoints = GetConfigArray(lines, "fan_rpm_points", new[] { 0, 1500, 2200, 3600, 3900 });

            return new FanConfig
            {
                LegionGeneration = legionGen,
                FanCurvePoints = GetConfigValue(lines, "fan_curve_points", 5),
                AccelerationValue = accelerationValue,
                DecelerationValue = decelerationValue,
                FanRpmPoints = fan1RpmPoints,
                Fan2RpmPoints = GetConfigArray(lines, "fan2_rpm_points", fan1RpmPoints),
                FanAccelerationValues = GetConfigArray(lines, "fan_accl_values",
                    Enumerable.Repeat(accelerationValue, legionGen == 5 ? 2 : 10).ToArray()),
                FanDecelerationValues = GetConfigArray(lines, "fan_deccl_values",
                    Enumerable.Repeat(decelerationValue, legionGen == 5 ? 2 : 10).ToArray()),
                CpuTempsRampUp = GetConfigArray(lines, "cpu_temps_ramp_up", new[] { 30, 45, 55, 60, 65 }),
                CpuTempsRampDown = GetConfigArray(lines, "cpu_temps_ramp_down", new[] { 28, 43, 53, 58, 63 }),
                GpuTempsRampUp = GetConfigArray(lines, "gpu_temps_ramp_up", new[] { 30, 50, 55, 60, 63 }),
                GpuTempsRampDown = GetConfigArray(lines, "gpu_temps_ramp_down", new[] { 28, 48, 53, 58, 61 }),
                HstTempsRampUp = GetConfigArray(lines, "hst_temps_ramp_up", new[] { 30, 50, 55, 65, 70 }),
                HstTempsRampDown = GetConfigArray(lines, "hst_temps_ramp_down", new[] { 28, 48, 53, 63, 68 }),
                Hysteresis = GetConfigValue(lines, "hysteresis", 3)
            };
        }

        private static int GetConfigValue(string[] lines, string key, int defaultValue)
        {
            try
            {
                var line = lines.FirstOrDefault(l => l.Split(':')[0].Trim() == key);
                if (line != null)
                {
                    var value = line.Split(':')[1].Trim();
                    return int.Parse(value);
                }
                return defaultValue;
            }
            catch
            {
                return defaultValue;
            }
        }

        private static int[] GetConfigArray(string[] lines, string key, int[] defaultValue)
        {
            try
            {
                var line = lines.FirstOrDefault(l => l.Split(':')[0].Trim() == key);
                if (line != null)
                {
                    var values = line.Split(':')[1].Trim()
                        .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    return values.Select(int.Parse).ToArray();
                }
                return defaultValue;
            }
            catch
            {
                return defaultValue;
            }
        }


}
