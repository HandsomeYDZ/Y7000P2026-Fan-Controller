using System;
using System.Collections.Generic;

namespace Lenovo_Fan_Controller;

internal static class FanTargetRecovery
{
    public static void Restore(Action<uint, int> write)
    {
        var errors = new List<Exception>();
        // A failure on one fan must not skip recovery of the other.
        foreach (uint id in new uint[] { 0x04030001, 0x04030002 })
            try { write(id, 0); } catch (Exception ex) { errors.Add(ex); }
        if (errors.Count > 0) throw new AggregateException("Automatic cooling recovery failed.", errors);
    }
}
