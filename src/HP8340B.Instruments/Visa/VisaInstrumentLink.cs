using Ivi.Visa;

namespace HP8340B.Instruments.Visa;

/// <summary>
/// Live link to an instrument over VISA, using the vendor-neutral Ivi.Visa API. At runtime
/// this dispatches to the installed VISA.NET provider (NI-VISA 26.0 on this bench).
/// Write-only listen devices (e.g. the 11713A) simply never call <see cref="Read"/>.
/// Ported from HP-Attenuator's VisaInstrumentLink.
/// </summary>
public sealed class VisaInstrumentLink : IInstrumentLink
{
    private readonly IMessageBasedSession _session;
    private readonly List<string> _history = new();

    public string ResourceName { get; }
    public bool IsSimulated => false;
    public IReadOnlyList<string> History => _history;

    public VisaInstrumentLink(string resourceName, int timeoutMs = 5000)
    {
        ResourceName = resourceName;
        _session = (IMessageBasedSession)GlobalResourceManager.Open(resourceName);

        // Drive into a known terminator/timeout posture. The timeout must exceed the longest
        // measurement (the 8902A's 10 s averaging, a 2 s DUT sweep), so it is caller-set.
        _session.TimeoutMilliseconds = timeoutMs;
        _session.TerminationCharacterEnabled = true;
    }

    public void Clear()
    {
        // GPIB Selected Device Clear. Some listen-only devices ignore it; don't fail.
        try { _session.Clear(); } catch { /* device may not support clear */ }
    }

    public void Write(string command)
    {
        ArgumentNullException.ThrowIfNull(command);
        _session.RawIO.Write(command + "\n");
        _history.Add(command);
    }

    /// <summary>
    /// Reads up to the termination character (LF) / EOI. Classic HP talkers terminate their
    /// ASCII output with CR/LF + EOI.
    /// </summary>
    public string Read() => _session.RawIO.ReadString().Trim();

    public string Query(string command)
    {
        Write(command);
        return Read();
    }

    public byte SerialPoll() => (byte)_session.ReadStatusByte();

    /// <summary>Lists VISA INSTR resources visible to the resource manager.</summary>
    public static IEnumerable<string> FindResources()
    {
        try { return GlobalResourceManager.Find("?*INSTR").ToList(); }
        catch (Exception) { return Array.Empty<string>(); }
    }

    public void Dispose()
    {
        try { _session.Dispose(); } catch { /* ignore */ }
    }
}
