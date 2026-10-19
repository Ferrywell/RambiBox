namespace RambiBox.Core.Devices;

internal sealed partial class Win32PortEnumerator
{
    private string? _modemKey;
    private long _modemTime;
    private IReadOnlyCollection<string> _modems = Array.Empty<string>();

    /// <summary>
    /// COM ports of present Modem-class devices (SetupAPI, GUID_DEVCLASS_MODEM). Cached like <see cref="ListPorts"/>:
    /// until the SERIALCOMM contents change or 10 s pass. Empty when SetupAPI fails.
    /// </summary>
    public IReadOnlyCollection<string> GetModemPortNames()
    {
        var present = ReadSerialComm();
        string key = string.Join("|", present.Select(p => p.Device + "=" + p.Port));
        lock (_lock)
        {
            if (key == _modemKey && Environment.TickCount64 - _modemTime < CacheTtlMs) return _modems;
        }

        var details = new Dictionary<string, (string Desc, string Instance)>(StringComparer.OrdinalIgnoreCase);
        try { QuerySetupApi(GuidDevClassModem, details); }
        catch (Exception ex) { Log.Warn($"SetupAPI modem enumeration failed: {ex.Message}"); }
        var set = new HashSet<string>(details.Keys, StringComparer.OrdinalIgnoreCase);

        lock (_lock)
        {
            _modems = set;
            _modemKey = key;
            _modemTime = Environment.TickCount64;
        }
        return set;
    }
}
