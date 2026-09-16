using System.Diagnostics;
using Ivi.Visa;

namespace HP8340B.Instruments.Visa;

/// <summary>
/// Live link to an instrument over VISA, using the vendor-neutral Ivi.Visa API. At runtime this
/// dispatches to the installed VISA.NET provider (NI-VISA 26.0 on this bench).
/// Write-only listen devices (e.g. the 11713A) simply never call <see cref="Read"/>.
/// Ported from HP-Attenuator's VisaInstrumentLink.
/// </summary>
public sealed class VisaInstrumentLink : IInstrumentLink
{
    /// <summary>
    /// Default I/O timeout. Deliberately generous: an 8902A averaging measurement can take 10 s.
    /// Per-instrument values come from bench.json (M0-02).
    /// </summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    private readonly IMessageBasedSession _session;
    private readonly List<string> _history = new();
    private readonly List<InstrumentTransaction> _transactions = new();

    public string ResourceName { get; }
    public bool IsSimulated => false;
    public IReadOnlyList<string> History => _history;
    public IReadOnlyList<InstrumentTransaction> Transactions => _transactions;

    public TimeSpan Timeout
    {
        get => TimeSpan.FromMilliseconds(_session.TimeoutMilliseconds);
        set => _session.TimeoutMilliseconds = (int)value.TotalMilliseconds;
    }

    public VisaInstrumentLink(string resourceName, TimeSpan? timeout = null)
    {
        ResourceName = resourceName;
        _session = (IMessageBasedSession)GlobalResourceManager.Open(resourceName);

        // Drive into a known terminator/timeout posture. A trailing newline on writes is harmless
        // and matches classic controllers.
        Timeout = timeout ?? DefaultTimeout;
        _session.TerminationCharacterEnabled = true;
    }

    private void Log(BusOperation op, string text, byte? status, TimeSpan elapsed) =>
        _transactions.Add(new InstrumentTransaction(
            DateTime.UtcNow, ResourceName, op, text, status, elapsed));

    public void Clear()
    {
        var sw = Stopwatch.StartNew();
        // GPIB Selected Device Clear. Some listen-only devices ignore it; don't fail.
        try { _session.Clear(); } catch { /* device may not support clear */ }
        Log(BusOperation.Clear, string.Empty, null, sw.Elapsed);
    }

    public void Write(string command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var sw = Stopwatch.StartNew();
        _session.RawIO.Write(command + "\n");
        _history.Add(command);
        Log(BusOperation.Write, command, null, sw.Elapsed);
    }

    /// <summary>
    /// Reads up to the termination character (LF) / EOI. Classic HP talkers terminate their
    /// ASCII output with CR/LF + EOI.
    /// </summary>
    public string Read()
    {
        var sw = Stopwatch.StartNew();
        var response = _session.RawIO.ReadString().Trim();
        Log(BusOperation.Read, response, null, sw.Elapsed);
        return response;
    }

    public string Query(string command)
    {
        Write(command);
        return Read();
    }

    public byte[] ReadBytes(int count)
    {
        var sw = Stopwatch.StartNew();
        var data = _session.RawIO.Read(count);
        Log(BusOperation.Read, $"<{data.Length} bytes>", null, sw.Elapsed);
        return data;
    }

    public void WriteBytes(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var sw = Stopwatch.StartNew();
        // No terminator: the instrument counts the bytes it expects, and a trailing newline
        // would be read as data.
        _session.RawIO.Write(data);
        Log(BusOperation.Write, $"<{data.Length} bytes>", null, sw.Elapsed);
    }

    /// <summary>
    /// Reads the status byte.
    ///
    /// <para>A failure propagates rather than being reported as a status of zero. Those two are
    /// not the same thing — one is a wedged bus and the other a quiet instrument — and collapsing
    /// them turns a bus fault into a measurement that merely looks slow. The HP-Attenuator session
    /// found exactly that failure mode in its own harness.</para>
    ///
    /// <para>The failed poll is logged before it is rethrown. It is the most interesting event
    /// that can happen on this bus and an audit log that omits it is worse than no log, because it
    /// shows an unbroken run of successful traffic right up to the point where everything stopped.
    /// </para>
    /// </summary>
    public byte SerialPoll()
    {
        var sw = Stopwatch.StartNew();

        try
        {
            var status = (byte)_session.ReadStatusByte();
            Log(BusOperation.SerialPoll, string.Empty, status, sw.Elapsed);
            return status;
        }
        catch (Exception ex)
        {
            Log(BusOperation.SerialPoll, $"poll FAILED: {ex.Message}", null, sw.Elapsed);
            throw;
        }
    }

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
