using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LibreHardwareMonitor.Hardware;
using Microsoft.Win32.SafeHandles;

namespace ECProbe2
{
    internal static class PawnIODriver
    {
        private const uint DEVICE_TYPE = 41394u << 16;
        private const uint IOCTL_PIO_LOAD_BINARY = DEVICE_TYPE | (0x821 << 2);
        private const uint IOCTL_PIO_EXECUTE_FN = DEVICE_TYPE | (0x841 << 2);
        private const int FN_NAME_LENGTH = 32;

        private static SafeFileHandle _deviceHandle;
        private static bool _initialized = false;
        public static bool IsInitialized => _initialized;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern SafeFileHandle CreateFile(
            string lpFileName, uint dwDesiredAccess, uint dwShareMode,
            IntPtr lpSecurityAttributes, uint dwCreationDisposition,
            uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(
            IntPtr hDevice, uint dwIoControlCode, byte[] lpInBuffer, uint nInBufferSize,
            byte[] lpOutBuffer, uint nOutBufferSize, out uint lpBytesReturned, IntPtr lpOverlapped);

        public static bool Initialize()
        {
            if (_initialized) return true;
            try
            {
                _deviceHandle = CreateFile(@"\\?\GLOBALROOT\Device\PawnIO", 0xC0000000, 0x00000003,
                    IntPtr.Zero, 3, 0x00000080, IntPtr.Zero);
                if (_deviceHandle.IsInvalid)
                {
                    Console.WriteLine($"Failed to open device: err {Marshal.GetLastWin32Error()}");
                    return false;
                }
                string modulePath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PawnIO", "LpcIO.bin");
                if (!File.Exists(modulePath)) { Console.WriteLine($"Module not found: {modulePath}"); return false; }

                byte[] module = File.ReadAllBytes(modulePath);
                if (!DeviceIoControl(_deviceHandle.DangerousGetHandle(), IOCTL_PIO_LOAD_BINARY,
                        module, (uint)module.Length, null, 0, out _, IntPtr.Zero))
                {
                    Console.WriteLine($"Load module failed: err {Marshal.GetLastWin32Error()}");
                    return false;
                }
                byte[] slotParam = new byte[8];
                BitConverter.GetBytes(1L).CopyTo(slotParam, 0);
                if (!ExecuteIoctl("ioctl_select_slot", slotParam, null)) { Console.WriteLine("Select slot failed"); return false; }
                if (!ExecuteIoctl("ioctl_find_bars", new byte[0], null)) { Console.WriteLine("Find bars failed"); return false; }
                _initialized = true;
                return true;
            }
            catch (Exception ex) { Console.WriteLine($"Init error: {ex.Message}"); return false; }
        }

        private static bool ExecuteIoctl(string name, byte[] inputParams, byte[] outputBuffer)
        {
            byte[] input = new byte[FN_NAME_LENGTH + inputParams.Length];
            byte[] nameBytes = Encoding.ASCII.GetBytes(name);
            Array.Copy(nameBytes, input, Math.Min(FN_NAME_LENGTH - 1, nameBytes.Length));
            if (inputParams.Length > 0) Array.Copy(inputParams, 0, input, FN_NAME_LENGTH, inputParams.Length);
            return DeviceIoControl(_deviceHandle.DangerousGetHandle(), IOCTL_PIO_EXECUTE_FN,
                input, (uint)input.Length, outputBuffer,
                outputBuffer != null ? (uint)outputBuffer.Length : 0, out _, IntPtr.Zero);
        }

        public static byte ReadIoPortByte(ushort port)
        {
            byte[] input = new byte[8];
            BitConverter.GetBytes((long)port).CopyTo(input, 0);
            byte[] output = new byte[8];
            if (!ExecuteIoctl("ioctl_pio_inb", input, output)) return 0;
            return output[0];
        }

        public static void WriteIoPortByte(ushort port, byte value)
        {
            byte[] input = new byte[16];
            BitConverter.GetBytes((long)port).CopyTo(input, 0);
            input[8] = value;
            ExecuteIoctl("ioctl_pio_outb", input, null);
        }

        public static void Shutdown() => _deviceHandle?.Close();
    }

    internal static class EC
    {
        private const ushort EC_ADDR_PORT = 0x4E;
        private const ushort EC_DATA_PORT = 0x4F;

        public static byte ReadECByte(ushort addr)
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

    internal static class Program
    {
        // Candidate bytes from round 1
        private static readonly ushort[] WatchBytes = {
            0xC320,0xC321,0xC322,0xC323,0xC324,0xC325,0xC326,0xC327,0xC328,0xC329,0xC32A,0xC32B,
            0xC364,0xC365,
            0xC5E0,0xC5E1,0xC5E2,0xC5E3,
            0xC538,0xC539,0xC53A,
            0xC557,0xC558,0xC559,
            0xC211,0xC212,0xC213,
            0xC4C6,0xC4C7,0xC4B6,0xC4B7,
            0xC63E,0xC565,0xCC00,0xCC01,0xCC02,
            0xC160,0xC161,0xC162,0xC163,
            0xC354,0xC355,0xC356,
            0xC2CF,0xC2E4,0xC2F5,0xC2FA,0xC2FB,0xC307,0xC308,0xC309,
            0xC3D0,0xC3D1,0xC385,0xC042,0xC044,
            0xC5C5,0xC5C9,0xC5CC,0xC5AF,0xC5B0,0xC5B3,
            0xC068,
        };

        private static bool IsAdmin()
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }

        private static async Task<int> Main(string[] args)
        {
            string outDir = Path.Combine(AppContext.BaseDirectory, "out");
            Directory.CreateDirectory(outDir);

            if (!IsAdmin())
            {
                Console.WriteLine("Not elevated - requesting UAC elevation...");
                string dllPath = Path.Combine(AppContext.BaseDirectory, "ECProbe2.dll");
                var psi = new ProcessStartInfo
                {
                    FileName = Environment.ProcessPath,
                    Arguments = $"--elevated \"{dllPath}\"",
                    UseShellExecute = true,
                    Verb = "runas",
                    WorkingDirectory = AppContext.BaseDirectory,
                };
                try
                {
                    var p = Process.Start(psi);
                    p.WaitForExit();
                    string logFile = Path.Combine(outDir, "ecprobe2.log");
                    if (File.Exists(logFile)) Console.WriteLine(File.ReadAllText(logFile));
                    else Console.WriteLine("Elevated run produced no log (user declined UAC?).");
                    return p.ExitCode;
                }
                catch (Exception ex) { Console.WriteLine($"Elevation failed: {ex.Message}"); return 1; }
            }

            return Run(outDir);
        }

        private static Computer BuildComputer()
        {
            return new Computer
            {
                IsCpuEnabled = true,
                IsGpuEnabled = true,
                IsMemoryEnabled = false,
                IsMotherboardEnabled = true,
                IsControllerEnabled = true,
                IsNetworkEnabled = false,
                IsStorageEnabled = false,
            };
        }

        private static int Run(string outDir)
        {
            if (!PawnIODriver.Initialize())
            {
                Console.WriteLine("PawnIO init FAILED");
                File.WriteAllText(Path.Combine(outDir, "ecprobe2.log"), "PawnIO init FAILED");
                return 1;
            }
            Console.WriteLine("PawnIO initialized");

            // LHM reference
            Computer computer = null;
            string lhmError = null;
            try
            {
                computer = BuildComputer();
                computer.IsCpuEnabled = true;
                computer.IsGpuEnabled = true;
                computer.IsMotherboardEnabled = true;
                computer.Open();
            }
            catch (Exception ex) { lhmError = ex.Message; }

            // LHM sensor inventory
            var csv = new StringBuilder();
            csv.Append("round,timeMs,phase");
            foreach (var a in WatchBytes) csv.Append($",EC_{a:X4}");
            csv.Append(",lhmCpuTemp,lhmGpuTemp");
            if (computer != null)
            {
                foreach (var hw in computer.Hardware)
                {
                    try { hw.Update(); } catch { }
                    foreach (var sub in hw.SubHardware) { try { sub.Update(); } catch { } }
                    foreach (var s in hw.Sensors.Where(s => s.SensorType == SensorType.Temperature || s.SensorType == SensorType.Fan))
                    {
                        csv.Append($",lhm[{hw.HardwareType}:{hw.Name}:{s.Name}:{s.SensorType}]");
                    }
                }
            }
            csv.AppendLine();

            var swTotal = Stopwatch.StartNew();
            int round = 0;

            void OneRound(string phase, int intervalMs)
            {
                var t0 = Stopwatch.StartNew();
                round++;
                var row = new StringBuilder();
                row.Append($"{round},{swTotal.ElapsedMilliseconds},{phase}");
                foreach (var a in WatchBytes)
                {
                    row.Append($",{EC.ReadECByte(a)}");
                }
                if (computer != null)
                {
                    try
                    {
                        foreach (var hw in computer.Hardware)
                        {
                            try { hw.Update(); } catch { }
                            foreach (var sub in hw.SubHardware) { try { sub.Update(); } catch { } }
                        }
                        float cpuT = float.NaN, gpuT = float.NaN;
                        var fans = new List<float>();
                        var extras = new List<string>();
                        foreach (var hw in computer.Hardware)
                        {
                            foreach (var s in hw.Sensors)
                            {
                                if (!s.Value.HasValue) continue;
                                if (s.SensorType == SensorType.Temperature)
                                {
                                    if (hw.HardwareType == HardwareType.Cpu && (s.Name.Contains("Package") || s.Name.Contains("Core Average") || s.Name.Contains("CPU Package")))
                                    { if (float.IsNaN(cpuT)) cpuT = s.Value.Value; }
                                    else if (hw.HardwareType == HardwareType.GpuNvidia || hw.HardwareType == HardwareType.GpuAmd)
                                    { if (s.Name.Contains("Core") && float.IsNaN(gpuT)) gpuT = s.Value.Value; }
                                    extras.Add($"{hw.HardwareType}:{hw.Name}:{s.Name}={s.Value.Value:F0}");
                                }
                            }
                        }
                        row.Append($",{(float.IsNaN(cpuT) ? "" : cpuT.ToString("F0"))},{(float.IsNaN(gpuT) ? "" : gpuT.ToString("F0"))}");
                        row.Append($",[{string.Join("|", extras)}]");
                    }
                    catch { row.Append(",,"); }
                }
                else
                {
                    row.Append($",,");
                    row.Append($",[LHM_ERROR:{lhmError}]");
                }
                row.AppendLine();
                csv.Append(row.ToString());
                Console.WriteLine($"round {round} {phase} {t0.ElapsedMilliseconds}ms: " +
                    $"C320={EC.ReadECByte(0xC320)} C364={EC.ReadECByte(0xC364)} C5E0={EC.ReadECByte(0xC5E0)} " +
                    $"C557={EC.ReadECByte(0xC557)} C558={EC.ReadECByte(0xC558)}");
                int remain = intervalMs - (int)t0.ElapsedMilliseconds;
                if (remain > 0) Thread.Sleep(remain);
            }

            Console.WriteLine("Phase 1: idle 20s");
            for (int i = 0; i < 20; i++) OneRound("idle", 1000);

            Console.WriteLine("Phase 2: CPU load 30s (fans may spin up)");
            var stopLoad = new CancellationTokenSource();
            var loadTasks = new List<Task>();
            int threads = Math.Max(2, Environment.ProcessorCount);
            for (int t = 0; t < threads; t++)
            {
                loadTasks.Add(Task.Run(() =>
                {
                    double x = 1.0000001;
                    var sw = Stopwatch.StartNew();
                    while (!stopLoad.IsCancellationRequested && sw.ElapsedMilliseconds < 60000)
                        for (int i = 0; i < 200000; i++) x = Math.Sqrt(x * 1.0000001 + 0.5) + Math.Sin(x);
                }));
            }
            for (int i = 0; i < 30; i++) OneRound("load", 1000);
            stopLoad.Cancel();
            Task.WaitAll(loadTasks.ToArray());

            Console.WriteLine("Phase 3: cool-down 20s");
            for (int i = 0; i < 20; i++) OneRound("cool", 1000);

            computer?.Close();

            string csvPath = Path.Combine(outDir, "timeline.csv");
            File.WriteAllText(csvPath, csv.ToString());
            Console.WriteLine($"CSV written to {csvPath}");
            File.WriteAllText(Path.Combine(outDir, "ecprobe2.log"), $"done, {round} rounds, csv={csvPath}");
            return 0;
        }
    }
}
