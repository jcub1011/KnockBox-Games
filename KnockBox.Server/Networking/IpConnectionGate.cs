using System.Collections.Concurrent;

namespace KnockBox.Server.Networking;

/// <summary>
/// Caps concurrent <c>/ws</c> connections per client IP so one machine can't squat every socket
/// slot. A cap ≤ 0 disables refusal. Callers must pair every successful
/// <see cref="TryEnter"/> with an <see cref="Exit"/> (typically via try/finally around the
/// connection's lifetime). Behind a reverse proxy this is only meaningful with
/// <c>KnockBox:ForwardedHeaders</c> enabled — otherwise every connection shares the proxy's IP.
/// </summary>
/// <remarks>
/// The cap is read from a delegate on every call, so an operator can tighten it from the admin portal
/// while a flood is in progress. Two consequences worth knowing:
/// <list type="bullet">
/// <item>Connections are counted even while the cap is disabled. Skipping the bookkeeping when disabled
/// was the original behaviour, and it cannot survive a live cap: connections admitted while it was off
/// would never be decremented, so turning it on would start from permanently inflated counts. An entry
/// is removed as soon as its count reaches zero, so an idle server tracks nothing.</item>
/// <item>Lowering the cap never disconnects anyone. It refuses the NEXT connection from an IP already at
/// or over the new limit; the ones already open live out their sessions.</item>
/// </list>
/// </remarks>
public sealed class IpConnectionGate
{
    private readonly Func<int> _maxPerIp;
    private readonly ConcurrentDictionary<string, int> _counts = new();

    /// <summary>A gate with a fixed cap.</summary>
    public IpConnectionGate(int maxPerIp) : this(() => maxPerIp) { }

    /// <summary>A gate whose cap is re-read on every call. See the class remarks.</summary>
    public IpConnectionGate(Func<int> maxPerIp) => _maxPerIp = maxPerIp;

    /// <summary>Distinct IPs currently holding at least one connection. Diagnostics only.</summary>
    public int TrackedIps => _counts.Count;

    public bool TryEnter(string ip)
    {
        var maxPerIp = _maxPerIp();
        while (true)
        {
            var current = _counts.GetOrAdd(ip, 0);
            if (maxPerIp > 0 && current >= maxPerIp) return false;
            if (_counts.TryUpdate(ip, current + 1, current)) return true;
        }
    }

    public void Exit(string ip)
    {
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
