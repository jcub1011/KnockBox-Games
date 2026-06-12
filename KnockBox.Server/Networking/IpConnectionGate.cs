using System.Collections.Concurrent;

namespace KnockBox.Server.Networking;

/// <summary>
/// Caps concurrent <c>/ws</c> connections per client IP so one machine can't squat every socket
/// slot. <paramref name="maxPerIp"/> ≤ 0 disables the cap. Callers must pair every successful
/// <see cref="TryEnter"/> with an <see cref="Exit"/> (typically via try/finally around the
/// connection's lifetime). Behind a reverse proxy this is only meaningful with
/// <c>KnockBox:ForwardedHeaders</c> enabled — otherwise every connection shares the proxy's IP.
/// </summary>
public sealed class IpConnectionGate(int maxPerIp)
{
    private readonly ConcurrentDictionary<string, int> _counts = new();

    public bool TryEnter(string ip)
    {
        if (maxPerIp <= 0) return true;
        while (true)
        {
            var current = _counts.GetOrAdd(ip, 0);
            if (current >= maxPerIp) return false;
            if (_counts.TryUpdate(ip, current + 1, current)) return true;
        }
    }

    public void Exit(string ip)
    {
        if (maxPerIp <= 0) return;
        while (true)
        {
            if (!_counts.TryGetValue(ip, out var current)) return;
            if (current <= 1)
            {
                // Remove only if the value is still what we read, else retry.
                if (_counts.TryRemove(KeyValuePair.Create(ip, current))) return;
            }
            else if (_counts.TryUpdate(ip, current - 1, current)) return;
        }
    }
}
