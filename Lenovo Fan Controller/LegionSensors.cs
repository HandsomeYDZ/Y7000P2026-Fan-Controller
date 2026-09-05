using LegionFanController.Hardware;
using System;
using System.Diagnostics;
using System.Management;

namespace Lenovo_Fan_Controller
{
    /// <summary>
    /// Cross-generation sensor reader for Legion laptops.
    ///
    /// Older Legions (Gen 5/6, ITE 5570/8226 EC) expose fan RPM and temperatures
    /// directly in EC RAM (0xC5E0-0xC5E3, 0xC538/0xC539). Newer platforms (e.g.
    /// Y7000P 2026 with the ITE 5508 EC) moved those values and instead expose
    /// them through the firmware WMI interface <c>LENOVO_OTHER_METHOD.GetFeatureValue</c>
    /// — the same interface Lenovo Legion Toolkit's SensorsControllerV5 uses.
    ///
    /// Reading strategy:
    ///   1. Try WMI (works on every modern Legion, independent of EC memory layout).
    ///   2. Fall back to the legacy EC registers, but only when the EC chip is a
    ///      known Gen 5/6 part — on unknown chips those addresses contain unrelated
    ///      data (e.g. the old bug: 18045 RPM / 128 °C) and must not be shown.
    /// </summary>
    internal static class LegionSensors
    {
        // CapabilityIDs from LenovoLegionToolkit (SensorsControllerV5 / CapabilityID).
        private const uint ID_CPU_FAN_RPM = 0x04030001;
        private const uint ID_GPU_FAN_RPM = 0x04030002;
        private const uint ID_PCH_FAN_RPM = 0x04030004;
        private const uint ID_CPU_TEMP = 0x05040000;
        private const uint ID_GPU_TEMP = 0x05050000;
        private const uint ID_PCH_TEMP = 0x05010000;

        private const int FAN_RPM_MIN_VALID = 0;
        private const int FAN_RPM_MAX_VALID = 30000;
        private const int TEMP_MIN_VALID = 1;
        private const int TEMP_MAX_VALID = 125;

        private static readonly object _wmiLock = new object();
        private static ManagementObject _wmiOtherMethod;
        private static bool _wmiProbed;
        private static bool _wmiSupported;

        /// <summary>
        /// Gets a cached instance of LENOVO_OTHER_METHOD, or null when the class
        /// does not exist / is not accessible. Probing happens once.
        /// </summary>
        private static ManagementObject GetOtherMethodInstance()
        {
            lock (_wmiLock)
            {
                if (_wmiProbed)
                    return _wmiSupported ? _wmiOtherMethod : null;

                _wmiProbed = true;
                try
                {
                    var scope = new ManagementScope("\\\\.\\ROOT\\WMI");
                    var searcher = new ManagementObjectSearcher(scope,
                        new ObjectQuery("SELECT * FROM LENOVO_OTHER_METHOD"));
                    foreach (ManagementObject instance in searcher.Get())
                    {
                        _wmiOtherMethod = instance;
                        _wmiSupported = true;
                        Debug.WriteLine("LegionSensors: LENOVO_OTHER_METHOD found, WMI sensor path enabled");
                        return instance;
                    }
                    Debug.WriteLine("LegionSensors: LENOVO_OTHER_METHOD not present, using EC fallback");
                    return null;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"LegionSensors: WMI probe failed: {ex.Message}");
                    return null;
                }
            }
        }

        /// <summary>
        /// Calls LENOVO_OTHER_METHOD.GetFeatureValue(id). Returns -1 on any failure.
        /// </summary>
        private static int GetFeatureValue(uint id)
        {
            try
            {
                ManagementObject instance = GetOtherMethodInstance();
                if (instance == null)
                    return -1;

                lock (_wmiLock)
                {
                    using ManagementBaseObject inParams = instance.GetMethodParameters("GetFeatureValue");
                    inParams["IDs"] = id;
                    using ManagementBaseObject outParams = instance.InvokeMethod("GetFeatureValue", inParams, null);
                    if (outParams?["Value"] != null)
                        return Convert.ToInt32(outParams["Value"]);
                }
                return -1;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"LegionSensors: GetFeatureValue(0x{id:X8}) failed: {ex.Message}");
                return -1;
            }
        }

        private static int ReadWmiWithFallback(uint wmiId, Func<int> legacyEcReader, int minValid, int maxValid)
        {
            int value = GetFeatureValue(wmiId);
            if (value >= minValid && value <= maxValid)
                return value;

            // WMI unavailable or returned garbage — only trust legacy EC registers
            // on known Gen 5/6 EC chips.
            if (HardwareAccessPolicy.LegacyWritesAllowed && ECUtils.IsLegacyGen56Chip())
            {
                int ecValue = legacyEcReader();
                if (ecValue >= minValid && ecValue <= maxValid)
                    return ecValue;
            }

            return -1;
        }

        public static int ReadCpuTemp() =>
            ReadWmiWithFallback(ID_CPU_TEMP, ECUtils.ReadCpuTemp, TEMP_MIN_VALID, TEMP_MAX_VALID);

        public static int ReadGpuTemp() =>
            ReadWmiWithFallback(ID_GPU_TEMP, ECUtils.ReadGpuTemp, TEMP_MIN_VALID, TEMP_MAX_VALID);

        public static int ReadVrmTemp() =>
            ReadWmiWithFallback(ID_PCH_TEMP, ECUtils.ReadVrmTemp, TEMP_MIN_VALID, TEMP_MAX_VALID);

        public static int ReadFan1Rpm() =>
            ReadWmiWithFallback(ID_CPU_FAN_RPM, ECUtils.ReadFan1Rpm, FAN_RPM_MIN_VALID, FAN_RPM_MAX_VALID);

        public static int ReadFan2Rpm() =>
            ReadWmiWithFallback(ID_GPU_FAN_RPM, ECUtils.ReadFan2Rpm, FAN_RPM_MIN_VALID, FAN_RPM_MAX_VALID);
    }
}
