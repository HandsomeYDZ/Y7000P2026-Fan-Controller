using System;

namespace Lenovo_Fan_Controller;

internal sealed class FanControlLease(DateTime now)
{
    private DateTime _lastHeartbeat = now;
    public void Refresh(DateTime now) => _lastHeartbeat = now;
    public bool IsAlive(DateTime now, bool parentAlive, bool stopRequested)
    {
        var elapsed = now - _lastHeartbeat;
        return parentAlive && !stopRequested && elapsed >= TimeSpan.Zero && elapsed < TimeSpan.FromSeconds(6);
    }
}
