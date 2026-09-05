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
using Microsoft.Win32.SafeHandles;

namespace ECProbe
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
                if (!File.Exists(modulePath))
                {
                    Console.WriteLine($"Module not found: {modulePath}");
                    return false;
                }

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
            catch (Exception ex)
            {
                Console.WriteLine($"Init error: {ex.Message}");
                return false;
            }
        }

        private static bool ExecuteIoctl(string name, byte[] inputParams, byte[] outputBuffer)
        {
            byte[] input = new byte[FN_NAME_LENGTH + inputParams.Length];
            byte[] nameBytes = Encoding.ASCII.GetBytes(name);
            Array.Copy(nameBytes, input, Math.Min(FN_NAME_LENGTH - 1, nameBytes.Length));
            if (inputParams.Length > 0)
                Array.Copy(inputParams, 0, input, FN_NAME_LENGTH, inputParams.Length);

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

        public static void Shutdown()
        {
            _deviceHandle?.Close();
            _initialized = false;
        }
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
        private const int EC_BASE = 0xC000;
        private const int EC_SIZE = 0x1000; // 0xC000 .. 0xCFFF
        private static string _outDir;
        private static StringBuilder _log = new StringBuilder();

        private static void Log(string s)
        {
            Console.WriteLine(s);
            _log.AppendLine(s);
        }

        private static bool IsAdmin()
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }

        private static async Task<int> Main(string[] args)
        {
            _outDir = Path.Combine(AppContext.BaseDirectory, "out");
            Directory.CreateDirectory(_outDir);
            File.WriteAllText(Path.Combine(_outDir, "started.marker"),
                $"started at {DateTime.Now:O} admin={IsAdmin()} args=[{string.Join(" ", args)}] cwd={Environment.CurrentDirectory}");

            if (!IsAdmin())
            {
                // Re-launch elevated (UAC prompt), parent waits and prints the saved log.
                // NOTE: the elevated process gets C:\Windows\System32 as its working
                // directory, so every path passed to it must be absolute.
                Console.WriteLine("Not elevated - requesting UAC elevation...");
                string dllPath = Path.Combine(AppContext.BaseDirectory, "ECProbe.dll");
                string extraArgs = string.Join(" ", Environment.GetCommandLineArgs().Skip(1)
                    .Where(a => a != dllPath).Select(a => a.Contains(' ') ? $"\"{a}\"" : a));
                var psi = new ProcessStartInfo
                {
                    FileName = Environment.ProcessPath,
                    Arguments = $"--elevated \"{dllPath}\" {extraArgs}",
                    UseShellExecute = true,
                    Verb = "runas",
                    WorkingDirectory = AppContext.BaseDirectory,
                };
                try
                {
                    var p = Process.Start(psi);
                    p.WaitForExit();
                    string logFile = Path.Combine(_outDir, "ecprobe.log");
                    if (File.Exists(logFile))
                        Console.WriteLine(File.ReadAllText(logFile));
                    else
                        Console.WriteLine("Elevated run produced no log (user declined UAC?).");
                    return p.ExitCode;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Elevation failed: {ex.Message}");
                    return 1;
                }
            }

            // Elevated path
            try
            {
                return RunProbe();
            }
            finally
            {
                File.WriteAllText(Path.Combine(_outDir, "ecprobe.log"), _log.ToString());
            }
        }

        private static int RunProbe()
        {
            if (!PawnIODriver.Initialize())
            {
                Log("PawnIO init FAILED");
                return 1;
            }
            Log("PawnIO initialized OK");

            bool dumpOnly = Environment.GetCommandLineArgs().Any(a => a == "--dump-only");

            // Chip / FW identification
            byte id0 = EC.ReadECByte(0x2000), id1 = EC.ReadECByte(0x2001);
            ushort chipId = (ushort)((id0 << 8) | id1);
            byte chipVer = EC.ReadECByte(0x2002);
            Log($"EC ChipID 0x2000/2001: 0x{id0:X2} 0x{id1:X2} -> 0x{chipId:X4}, version reg 0x{chipVer:X2}");
            Log($"Legacy temp regs: C538={EC.ReadECByte(0xC538):X2}({EC.ReadECByte(0xC538)}d) C539={EC.ReadECByte(0xC539):X2} C53A={EC.ReadECByte(0xC53A):X2}");
            Log($"Legacy RPM regs: C5E0={EC.ReadECByte(0xC5E0):X2} C5E1={EC.ReadECByte(0xC5E1):X2} C5E2={EC.ReadECByte(0xC5E2):X2} C5E3={EC.ReadECByte(0xC5E3):X2}");
            Log("");

            // Phase 1: full hexdump of 0xC000-0xCFFF
            byte[] dump = new byte[EC_SIZE];
            for (int i = 0; i < EC_SIZE; i++) dump[i] = EC.ReadECByte((ushort)(EC_BASE + i));
            string hexPath = Path.Combine(_outDir, "ec-dump-full.txt");
            var sb = new StringBuilder();
            for (int row = 0; row < EC_SIZE; row += 16)
            {
                sb.Append($"{(EC_BASE + row):X4}: ");
                var line = new StringBuilder();
                var ascii = new StringBuilder();
                for (int j = 0; j < 16; j++)
                {
                    byte b = dump[row + j];
                    line.Append($"{b:X2} ");
                    ascii.Append(b >= 32 && b < 127 ? (char)b : '.');
                }
                sb.Append(line.ToString().PadRight(48));
                sb.AppendLine($" |{ascii}|");
            }
            File.WriteAllText(hexPath, sb.ToString());
            Log($"Full hexdump written to {hexPath}");

            if (dumpOnly)
            {
                Log("");
                Log("=== Fan table region 0xC530-0xC660 ===");
                for (int row = 0xC530; row < 0xC660; row += 16)
                {
                    var line = new StringBuilder();
                    for (int j = 0; j < 16; j++) line.Append($"{dump[row - EC_BASE + j]:X2} ");
                    Log($"{row:X4}: {line}");
                }
                return 0;
            }

            // Phase 2: sampling. Rounds: idle -> load -> cool.
            // Track per-byte min/max/changeCount and per-round snapshots.
            var samples = new List<byte[]>();
            int rounds = 0;

            void Sample(int count, int intervalMs, string label)
            {
                Log($"Sampling '{label}': {count} rounds @ {intervalMs}ms");
                for (int r = 0; r < count; r++)
                {
                    var sw = Stopwatch.StartNew();
                    var buf = new byte[EC_SIZE];
                    for (int i = 0; i < EC_SIZE; i++) buf[i] = EC.ReadECByte((ushort)(EC_BASE + i));
                    sw.Stop();
                    samples.Add(buf);
                    rounds++;
                    Log($"  round {rounds}: {sw.ElapsedMilliseconds}ms (buf[0x5E0]=0x{buf[0x5E0]:X2})");
                    if (r < count - 1) Thread.Sleep(intervalMs);
                }
            }

            // idle phase
            Sample(4, 500, "idle");

            // load phase: spin CPU threads while sampling
            Log(">>> Starting CPU load (all cores) for ~14s");
            int threads = Math.Max(2, Environment.ProcessorCount);
            var stopLoad = new CancellationTokenSource();
            var loadTasks = new List<Task>();
            for (int t = 0; t < threads; t++)
            {
                loadTasks.Add(Task.Run(() =>
                {
                    double x = 1.0000001;
                    var sw = Stopwatch.StartNew();
                    while (!stopLoad.IsCancellationRequested && sw.ElapsedMilliseconds < 30000)
                    {
                        for (int i = 0; i < 200000; i++) x = Math.Sqrt(x * 1.0000001 + 0.5) + Math.Sin(x);
                    }
                }));
            }
            Sample(8, 700, "cpu-load");
            stopLoad.Cancel();
            Task.WaitAll(loadTasks.ToArray());
            Log(">>> CPU load stopped");

            // cool phase
            Sample(6, 800, "cool-down");

            // Phase 3: analysis
            var changes = new List<(int addr, int min, int max, int cnt, int last)>();
            for (int i = 0; i < EC_SIZE; i++)
            {
                int min = 255, max = 0, cnt = 0;
                for (int s = 1; s < samples.Count; s++)
                {
                    byte v = samples[s][i];
                    if (v < min) min = v;
                    if (v > max) max = v;
                    if (v != samples[s - 1][i]) cnt++;
                }
                if (cnt > 0)
                    changes.Add((EC_BASE + i, min, max, cnt, samples[^1][i]));
            }
            changes.Sort((a, b) => b.cnt.CompareTo(a.cnt));

            var a = new StringBuilder();
            a.AppendLine("=== CHANGING BYTES (sorted by change count) ===");
            a.AppendLine("addr  min  max  chgCnt last");
            foreach (var c in changes.Take(120))
                a.AppendLine($"{c.addr:X4}  {c.min:X2}   {c.max:X2}   {c.cnt}     0x{c.last:X2} ({c.last}d)");
            a.AppendLine($"total changing bytes: {changes.Count}");

            a.AppendLine();
            a.AppendLine("=== RPM WORD CANDIDATES (pairs of adjacent changing bytes, word value 300..9000) ===");
            for (int i = 0; i < EC_SIZE - 1; i++)
            {
                int a0 = EC_BASE + i, a1 = EC_BASE + i + 1;
                bool c0 = changes.Any(c => c.addr == a0);
                bool c1 = changes.Any(c => c.addr == a1);
                if (!c0 && !c1) continue;
                int cnt = Math.Max(changes.FirstOrDefault(c => c.addr == a0).cnt, changes.FirstOrDefault(c => c.addr == a1).cnt);
                var wLE = new List<int>();  // lsb at a0
                var wBE = new List<int>();
                foreach (var s in samples)
                {
                    wLE.Add(s[i] | (s[i + 1] << 8));
                    wBE.Add((s[i] << 8) | s[i + 1]);
                }
                void Report(string order, List<int> words)
                {
                    int mn = words.Min(), mx = words.Max();
                    if (mn >= 300 && mx <= 9000 && words.Distinct().Count() > 1)
                    {
                        a.AppendLine($"  {a0:X4}-{a1:X4} {order}: range {mn}-{mx}  values: {string.Join(",", words.Select(w => w.ToString()))}");
                    }
                }
                Report("LE(lsb@a0)", wLE);
                Report("BE(msb@a0)", wBE);
            }

            a.AppendLine();
            a.AppendLine("=== TEMP BYTE CANDIDATES (changing, value range 15..115) ===");
            foreach (var c in changes.Where(c => c.min >= 15 && c.max <= 115 && (c.max - c.min) >= 3))
            {
                var vals = samples.Select(s => s[c.addr - EC_BASE]).Distinct().ToList();
                a.AppendLine($"  {c.addr:X4}: min {c.min} max {c.max} last {c.last}  distinct:{string.Join(",", vals)}");
            }

            // Phase 4: static pattern analysis over the full dump (fan tables etc.)
            a.AppendLine();
            a.AppendLine("=== STATIC PATTERNS ===");
            FindEqualRuns(a, dump, 10, 1, 15, "ACC/DEC-like 10-byte equal run");
            FindMonoRuns(a, dump, 9, 0, 80, "RPM-table-like monotonic non-decreasing run");
            FindTempTableRuns(a, dump, "temp-table-like run (10 bytes, 10..100)");
            a.AppendLine();
            a.AppendLine("=== Legacy layout hexdump 0xC500-0xC660 ===");
            for (int row = 0xC500; row < 0xC660; row += 16)
            {
                var line = new StringBuilder();
                for (int j = 0; j < 16; j++) line.Append($"{dump[row - EC_BASE + j]:X2} ");
                a.AppendLine($"{row:X4}: {line}");
            }

            string analysisPath = Path.Combine(_outDir, "analysis.txt");
            File.WriteAllText(analysisPath, a.ToString());
            Log("");
            Log(a.ToString());
            return 0;
        }

        private static void FindEqualRuns(StringBuilder sb, byte[] dump, int len, int lo, int hi, string label)
        {
            for (int i = 0; i <= dump.Length - len; i++)
            {
                int v = dump[i];
                if (v < lo || v > hi) continue;
                bool all = true;
                for (int j = 1; j < len; j++) if (dump[i + j] != v) { all = false; break; }
                if (all)
                {
                    // extend
                    int end = i + len;
                    while (end < dump.Length && dump[end] == v) end++;
                    sb.AppendLine($"  {label}: 0x{EC_BASE + i:X4}..0x{EC_BASE + end - 1:X4} value={v}");
                    i = end - 1;
                }
            }
        }

        private static void FindMonoRuns(StringBuilder sb, byte[] dump, int len, int lo, int hi, string label)
        {
            for (int i = 0; i <= dump.Length - len; i++)
            {
                bool ok = dump[i] >= lo && dump[i] <= hi;
                for (int j = 1; j < len && ok; j++)
                    ok = dump[i + j] >= dump[i + j - 1] && dump[i + j] <= hi;
                if (!ok) continue;
                int end = i + len;
                while (end < dump.Length && dump[end] >= dump[end - 1] && dump[end] <= hi) end++;
                sb.AppendLine($"  {label}: 0x{EC_BASE + i:X4}..0x{EC_BASE + end - 1:X4} vals [{string.Join(" ", dump.Skip(i).Take(end - i).Select(b => b.ToString()))}]");
                i = end - 1;
            }
        }

        private static void FindTempTableRuns(StringBuilder sb, byte[] dump, string label)
        {
            // Look for 3 groups of 10 bytes where values are in 10..100 or 0x7F
            for (int i = 0; i <= dump.Length - 30; i++)
            {
                bool ok = true;
                int groups = 0;
                for (int g = 0; g < 3 && ok; g++)
                {
                    int start = i + g * 10;
                    bool validGroup = true;
                    for (int j = 0; j < 10; j++)
                    {
                        int v = dump[start + j];
                        if (!((v >= 10 && v <= 100) || v == 0x7F)) { validGroup = false; break; }
                    }
                    if (!validGroup) { ok = false; break; }
                    groups++;
                }
                if (ok && groups == 3)
                {
                    sb.AppendLine($"  {label}: 0x{EC_BASE + i:X4}");
                    for (int g = 0; g < 3; g++)
                    {
                        int start = i + g * 10;
                        sb.AppendLine($"      group{g}: [{string.Join(" ", dump.Skip(start).Take(10).Select(b => b.ToString()))}]");
                    }
                    i += 29;
                }
            }
        }
    }
}
