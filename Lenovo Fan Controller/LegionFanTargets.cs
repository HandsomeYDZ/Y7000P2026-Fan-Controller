using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Threading;

namespace Lenovo_Fan_Controller;

internal interface IFanTargetDevice : IDisposable
{
    int Minimum { get; }
    int Maximum { get; }
    (int mode, int cpu, int gpu, int pch, int cpuRpm, int gpuRpm) ReadState();
    void SetTarget(int rpm, Func<bool> sessionAlive);
    void RestoreAutomatic();
}

// Firmware-backed RPM targets; no PawnIO, EC offsets, fan tables, or mode writes.
// Audited DSDT: WMAE(0x12), IDs 0x04030001/2 -> LECR(D1, fan, RPM/100, 2).
// Linux wmi-other.c documents target=0 as auto (NOT a Fan_Set_Table step=0).
internal sealed class LegionFanTargets : IFanTargetDevice
{
    private readonly ManagementObject _other;
    private readonly ManagementObject _gamezone;
    public int Minimum { get; }
    public int Maximum { get; }
    public bool WriteAttempted { get; private set; }

    public LegionFanTargets()
    {
        using var bios = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
        if ((string?)bios?.GetValue("SystemProductName") != "83F3" ||
            (string?)bios?.GetValue("BIOSVersion") != "Q6CN79WW")
            throw new NotSupportedException("RPM control is currently limited to audited 83F3 / Q6CN79WW firmware.");

        using var capsSearch = new ManagementObjectSearcher(@"root\WMI", "SELECT * FROM LENOVO_CAPABILITY_DATA_00");
        using var caps = capsSearch.Get();
        var supported = new HashSet<uint>();
        foreach (ManagementObject row in caps)
        {
            using (row)
                if ((Convert.ToUInt32(row["Capability"]) & 7) == 7) supported.Add(Convert.ToUInt32(row["IDs"]));
        }
        if (!supported.Contains(0x04030001) || !supported.Contains(0x04030002))
            throw new NotSupportedException("Firmware does not advertise readable and writable fan targets.");

        using var limits = First("LENOVO_FAN_TEST_DATA");
        var ids = (uint[])limits["FanId"];
        var mins = (uint[])limits["FanMinSpeed"];
        var maxs = (uint[])limits["FanMaxSpeed"];
        if (Convert.ToInt32(limits["NumOfFans"]) != 2 || !ids.SequenceEqual(new uint[] { 1, 2 }) || mins.Length != 2 || maxs.Length != 2)
            throw new NotSupportedException("Unexpected fan identities or limits.");
        Minimum = checked((int)mins.Max());
        Maximum = checked((int)maxs.Min());
        if (Minimum != 1700 || Maximum != 5300)
            throw new NotSupportedException("Firmware RPM limits changed; re-audit required.");
        _other = First("LENOVO_OTHER_METHOD");
        try { _gamezone = First("LENOVO_GAMEZONE_DATA"); }
        catch { _other.Dispose(); throw; }
    }

    private static ManagementObject First(string className)
    {
        using var search = new ManagementObjectSearcher(@"root\WMI", $"SELECT * FROM {className}");
        using var rows = search.Get();
        foreach (ManagementObject row in rows) return row;
        throw new InvalidOperationException($"No WMI instance: {className}");
    }

    private int Read(uint id)
    {
        using var input = _other.GetMethodParameters("GetFeatureValue");
        input["IDs"] = id;
        using var output = _other.InvokeMethod("GetFeatureValue", input, null);
        if (output?["Value"] == null) throw new InvalidOperationException("Missing WMI sensor response.");
        return checked((int)Convert.ToUInt32(output["Value"]));
    }

    public (int mode, int cpu, int gpu, int pch, int cpuRpm, int gpuRpm) ReadState()
    {
        using var result = _gamezone.InvokeMethod("GetSmartFanMode", null, null);
        if (result?["Data"] == null) throw new InvalidOperationException("Power mode unavailable.");
        return (Convert.ToInt32(result["Data"]), Read(0x05040000), Read(0x05050000), Read(0x05010000), Read(0x04030001), Read(0x04030002));
    }

    public void SetTarget(int rpm, Func<bool> sessionAlive)
    {
        if (rpm < Minimum || rpm > Maximum || rpm % 100 != 0)
            throw new ArgumentOutOfRangeException(nameof(rpm));
        WriteAttempted = true; // A thrown invocation can still have reached hardware.
        Write(0x04030001, rpm, sessionAlive);
        Write(0x04030002, rpm, sessionAlive);
    }

    public void RestoreAutomatic()
    {
        if (!WriteAttempted) return;
        FanTargetRecovery.Restore((id, rpm) => Write(id, rpm, () => true));
        WriteAttempted = false;
    }

    public void RecoverLostSession()
    {
        WriteAttempted = true;
        RestoreAutomatic();
    }

    private void Write(uint id, int rpm, Func<bool> sessionAlive)
    {
        using var mutex = new Mutex(false, @"Global\Y7000P2026-Fan-WmiWrite");
        bool locked = false;
        try
        {
            try { locked = mutex.WaitOne(5000); } catch (AbandonedMutexException) { locked = true; }
            if (!locked) throw new TimeoutException("Fan WMI write lock timed out.");
            if (!sessionAlive()) throw new OperationCanceledException("Fan session ended before write.");
            using var input = _other.GetMethodParameters("SetFeatureValue");
            input["IDs"] = id;
            input["Value"] = (uint)rpm;
            using var output = _other.InvokeMethod("SetFeatureValue", input, null);
            var returned = output?.Properties.Cast<PropertyData>().FirstOrDefault(p => p.Name == "ReturnValue")?.Value;
            if (returned != null && Convert.ToUInt32(returned) > 1)
                throw new InvalidOperationException("WMI rejected fan target.");
        }
        finally { if (locked) mutex.ReleaseMutex(); }
    }

    public static string[] ConflictingApps(params int[] allowedPids)
    {
        var conflicts = new List<string>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                if (allowedPids.Contains(process.Id)) continue;
                if (process.ProcessName is "Lenovo Fan Controller" or "Lenovo Legion Toolkit" or
                    "LenovoTray" or "FanControl" or "LegionFanControl" or "LenovoVantage" or "LegionZone" or "LegionSpace")
                    conflicts.Add(process.ProcessName);
            }
        }
        return conflicts.Distinct().ToArray();
    }

    public void Dispose()
    {
        try { RestoreAutomatic(); }
        finally { _other.Dispose(); _gamezone.Dispose(); }
    }
}
