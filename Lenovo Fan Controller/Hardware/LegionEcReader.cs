using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace LegionFanController.Hardware
{
    internal static class PawnIODriver
    {
        private const uint DEVICE_TYPE = 41394u << 16;  // 0xA1B20000
        private const uint IOCTL_PIO_LOAD_BINARY = DEVICE_TYPE | (0x821 << 2);  // 0xA1B22084
        private const uint IOCTL_PIO_EXECUTE_FN = DEVICE_TYPE | (0x841 << 2);   // 0xA1B22104
        private const int FN_NAME_LENGTH = 32;

        private static SafeFileHandle _deviceHandle;
        private static bool _initialized = false;
        public static bool IsInitialized => _initialized;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern SafeFileHandle CreateFile(
            string lpFileName,
            uint dwDesiredAccess,
            uint dwShareMode,
            IntPtr lpSecurityAttributes,
            uint dwCreationDisposition,
            uint dwFlagsAndAttributes,
            IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(
            IntPtr hDevice,
            uint dwIoControlCode,
            byte[] lpInBuffer,
            uint nInBufferSize,
            byte[] lpOutBuffer,
            uint nOutBufferSize,
            out uint lpBytesReturned,
            IntPtr lpOverlapped);

        public static bool Initialize()
        {
            if (_initialized) return true;

            try
            {
                // Open device
                _deviceHandle = CreateFile(
                    @"\\?\GLOBALROOT\Device\PawnIO",
                    0xC0000000,
                    0x00000003,
                    IntPtr.Zero,
                    3,
                    0x00000080,
                    IntPtr.Zero);

                if (_deviceHandle.IsInvalid)
                {
                    Debug.WriteLine($"Failed to open device: {Marshal.GetLastWin32Error()}");
                    return false;
                }

                // Load module
                string modulePath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    "PawnIO", "LpcIO.bin");

                if (!File.Exists(modulePath))
                {
                    Debug.WriteLine($"Module not found: {modulePath}");
                    return false;
                }

                byte[] module = File.ReadAllBytes(modulePath);

                bool success = DeviceIoControl(
                    _deviceHandle.DangerousGetHandle(),
                    IOCTL_PIO_LOAD_BINARY,
                    module,
                    (uint)module.Length,
                    null,
                    0,
                    out _,
                    IntPtr.Zero);

                if (!success)
                {
                    Debug.WriteLine($"Load module failed: {Marshal.GetLastWin32Error()}");
                    return false;
                }

                // Select slot 1 (0x4E/0x4F)
                byte[] slotParam = new byte[8];
                BitConverter.GetBytes(1L).CopyTo(slotParam, 0);
                if (!ExecuteIoctl("ioctl_select_slot", slotParam, null))
                {
                    Debug.WriteLine("Select slot failed");
                    return false;
                }

                // Find bars
                if (!ExecuteIoctl("ioctl_find_bars", new byte[0], null))
                {
                    Debug.WriteLine("Find bars failed");
                    return false;
                }

                _initialized = true;
                Debug.WriteLine("PawnIO initialized");
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Init error: {ex.Message}");
                return false;
            }
        }

        private static bool ExecuteIoctl(string name, byte[] inputParams, byte[] outputBuffer)
        {
            byte[] input = new byte[FN_NAME_LENGTH + inputParams.Length];
            byte[] nameBytes = System.Text.Encoding.ASCII.GetBytes(name);
            Array.Copy(nameBytes, input, Math.Min(FN_NAME_LENGTH - 1, nameBytes.Length));
            if (inputParams.Length > 0)
                Array.Copy(inputParams, 0, input, FN_NAME_LENGTH, inputParams.Length);

            return DeviceIoControl(
                _deviceHandle.DangerousGetHandle(),
                IOCTL_PIO_EXECUTE_FN,
                input,
                (uint)input.Length,
                outputBuffer,
                outputBuffer != null ? (uint)outputBuffer.Length : 0,
                out _,
                IntPtr.Zero);
        }

        public static byte ReadIoPortByte(ushort port)
        {
            if (!_initialized) return 0;

            byte[] input = new byte[8];
            BitConverter.GetBytes((long)port).CopyTo(input, 0);
            byte[] output = new byte[8];

            if (!ExecuteIoctl("ioctl_pio_inb", input, output))
                return 0;

            return output[0];
        }

        public static void WriteIoPortByte(ushort port, byte value)
        {
            if (!_initialized) return;

            byte[] input = new byte[16];
            BitConverter.GetBytes((long)port).CopyTo(input, 0);
            input[8] = value;

            ExecuteIoctl("ioctl_pio_outb", input, null);
        }

        public static void Shutdown()
        {
            _deviceHandle?.Close();
            _initialized = false;
        }

        /// <summary>
        /// Tears down the current device handle and opens a fresh one, re-loading
        /// the module. Used after the system resumes from sleep/hibernate, where the
        /// previously opened PawnIO handle may have been invalidated.
        /// </summary>
        public static bool Reinitialize()
        {
            Shutdown();
            return Initialize();
        }
    }

    internal static class ECUtils
    {
        private const ushort EC_ADDR_PORT = 0x4E;
        private const ushort EC_DATA_PORT = 0x4F;
        private static bool _initialized = false;
        public static bool DEBUG_MODE = false;

        public static bool Init()
        {
            if (_initialized) return true;
            _initialized = PawnIODriver.Initialize();
            return _initialized;
        }

        /// <summary>
        /// Forces the underlying driver connection to be rebuilt, regardless of the
        /// current initialized state. Call this on resume from sleep/hibernate before
        /// re-applying the fan curve, since the EC and PawnIO handle may have reset.
        /// </summary>
        public static bool Reinit()
        {
            _initialized = PawnIODriver.Reinitialize();
            return _initialized;
        }

        public static void Cleanup() => PawnIODriver.Shutdown();

        public static byte ReadECByte(ushort addr)
        {
            if (!PawnIODriver.IsInitialized) return 0;

            lock (typeof(ECUtils))
            {
                PawnIODriver.WriteIoPortByte(EC_ADDR_PORT, 0x2E);
                PawnIODriver.WriteIoPortByte(EC_DATA_PORT, 0x11);
                PawnIODriver.WriteIoPortByte(EC_ADDR_PORT, 0x2F);
                PawnIODriver.WriteIoPortByte(EC_DATA_PORT, (byte)((addr >> 8) & 0xFF));

                PawnIODriver.WriteIoPortByte(EC_ADDR_PORT, 0x2E);
                PawnIODriver.WriteIoPortByte(EC_DATA_PORT, 0x10);
                PawnIODriver.WriteIoPortByte(EC_ADDR_PORT, 0x2F);
                PawnIODriver.WriteIoPortByte(EC_DATA_PORT, (byte)(addr & 0xFF));

                PawnIODriver.WriteIoPortByte(EC_ADDR_PORT, 0x2E);
                PawnIODriver.WriteIoPortByte(EC_DATA_PORT, 0x12);
                PawnIODriver.WriteIoPortByte(EC_ADDR_PORT, 0x2F);

                return PawnIODriver.ReadIoPortByte(EC_DATA_PORT);
            }
        }

        private static ushort ReadECWord(ushort lsbAddr, ushort msbAddr)
        {
            byte lsb = ReadECByte(lsbAddr);
            byte msb = ReadECByte(msbAddr);
            return (ushort)((msb << 8) | lsb);
        }

        public static int ReadFan1Rpm()
        {
            ushort raw = ReadECWord((ushort)ECRegister.FAN1_RPM_LSB, (ushort)ECRegister.FAN1_RPM_MSB);
            if (DEBUG_MODE) Debug.WriteLine($"Fan1 Raw: {raw}");
            return SanitizeRpm(raw);
        }

        public static int ReadFan2Rpm()
        {
            ushort raw = ReadECWord((ushort)ECRegister.FAN2_RPM_LSB, (ushort)ECRegister.FAN2_RPM_MSB);
            if (DEBUG_MODE) Debug.WriteLine($"Fan2 Raw: {raw}");
            return SanitizeRpm(raw);
        }

        private static int SanitizeRpm(ushort rpm)
        {
            // No real Legion fan spins above ~8000 RPM; anything beyond 12000 is
            // definitely not a tachometer value (old bug showed 16743/18045).
            if (rpm == 0x0000 || rpm == 0xFFFF || rpm > 12000)
                return 0;
            return rpm;
        }

        public static int ReadCpuTemp()
        {
            int val = ReadECByte((ushort)ECRegister.CPU_TEMP);
            if (DEBUG_MODE) Debug.WriteLine($"CPU Temp Raw: {val}");
            return val;
        }

        public static int ReadGpuTemp()
        {
            int val = ReadECByte((ushort)ECRegister.GPU_TEMP);
            if (DEBUG_MODE) Debug.WriteLine($"GPU Temp Raw: {val}");
            return val;
        }

        public static int ReadVrmTemp()
        {
            return ReadECByte((ushort)ECRegister.VRM_TEMP);
        }

        /// <summary>
        /// Reads the EC chip ID (0x2000/0x2001). Known parts:
        ///   0x5570/0x5571 -> ITE 5570 (Legion Gen 5)
        ///   0x8226/0x8227 -> ITE 8227 (Legion Gen 6)
        ///   Anything else (e.g. 0x5508 on newer models like the Y7000P 2026) is a
        ///   newer EC whose RAM layout differs — the legacy sensor registers must
        ///   NOT be trusted on those chips.
        /// </summary>
        public static ushort GetChipId()
        {
            byte idLow = ECUtils.ReadECByte(0x2000);
            byte idHigh = ECUtils.ReadECByte(0x2001);
            return (ushort)((idLow << 8) | idHigh);
        }

        /// <summary>
        /// True when the EC chip is a known Gen 5/6 part whose RAM layout matches
        /// the legacy sensor register map (0xC538/0xC539/0xC5E0-0xC5E3).
        /// </summary>
        public static bool IsLegacyGen56Chip()
        {
            ushort chipId = GetChipId();
            return chipId is 0x5570 or 0x5571 or 0x8226 or 0x8227;
        }

        public static int DetectLegionGen()
        {
            // Method 1: Read EC chip ID (read-only, no risk)
            ushort chipId = GetChipId();

            Debug.WriteLine($"EC chip ID: 0x{chipId:X4}");

            switch (chipId)
            {
                case 0x5570:
                case 0x5571:
                    Debug.WriteLine("Detected Gen5 by chip ID");
                    return 5;
                case 0x8226:
                case 0x8227:
                    Debug.WriteLine("Detected Gen6 by chip ID");
                    return 6;
                default:
                    // Newer EC (e.g. ITE 5508 on the Y7000P 2026). The legacy
                    // Gen 5/6 register map does not apply; sensor values must be
                    // read via WMI (LENOVO_OTHER_METHOD.GetFeatureValue).
                    // Return 0 = "modern platform, generation unknown" instead of
                    // guessing and risking writes to wrong registers.
                    Debug.WriteLine($"Unknown/new EC chip 0x{chipId:X4}: modern platform, sensor reads must use WMI");
                    return 0;
            }
        }

        private static int DetectLegionGenByRegisters()
        {
            // Legacy fallback retained for documentation only; DetectLegionGen()
            // now decides purely by chip ID and returns 0 for unknown platforms.
            return 0;
        }
    }

    internal enum ECRegister : ushort
    {
        CPU_TEMP = 0xC538,
        GPU_TEMP = 0xC539,
        VRM_TEMP = 0xC53A,
        FAN1_RPM_LSB = 0xC5E0,
        FAN1_RPM_MSB = 0xC5E1,
        FAN2_RPM_LSB = 0xC5E2,
        FAN2_RPM_MSB = 0xC5E3
    }
}