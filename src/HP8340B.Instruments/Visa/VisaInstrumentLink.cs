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

    /// <summary>
    /// Sends a command.
    ///
    /// <para><b>A failed write is logged before the exception propagates.</b> Repo rule 1 requires
    /// every write to the DUT to be recorded with its timestamp, and a write that threw is still a
    /// write that may have reached the instrument — a partial transfer is not a non-event. Leaving
    /// it out of the log breaks that rule in the one case where the record matters most.</para>
    ///
    /// <para>It does <b>not</b> go into <see cref="History"/>. That list means "commands that went
    /// out", and drivers assert against its last entry; putting a failure there would claim
    /// something was sent that may not have been. The transaction log is the audit record, and it
    /// marks the attempt as failed. See <see cref="Transactions"/>.</para>
    /// </summary>
    public void Write(string command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var sw = Stopwatch.StartNew();

        try
        {
            _session.RawIO.Write(command + "\n");
        }
        catch (Exception ex)
        {
            Log(BusOperation.Write, $"WRITE FAILED: {command} ({ex.Message})", null, sw.Elapsed);
            throw;
        }

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

        try
        {
            var response = _session.RawIO.ReadString().Trim();
            Log(BusOperation.Read, response, null, sw.Elapsed);
            return response;
        }
        catch (Exception ex)
        {
            Log(BusOperation.Read, $"READ FAILED: {ex.Message}", null, sw.Elapsed);
            throw;
        }
    }

    public string Query(string command)
    {
        Write(command);
        return Read();
    }

    public byte[] ReadBytes(int count)
    {
        var sw = Stopwatch.StartNew();

        try
        {
            var data = _session.RawIO.Read(count);
            Log(BusOperation.Read, $"<{data.Length} bytes>", null, sw.Elapsed);
            return data;
        }
        catch (Exception ex)
        {
            Log(BusOperation.Read, $"READ FAILED after {count} bytes requested: {ex.Message}",
                null, sw.Elapsed);
            throw;
        }
    }

    /// <summary>
    /// Sends raw bytes — the learn string, and nothing else on this bench.
    ///
    /// <para>A failure here matters more than most: a learn-string write is 123 bytes of
    /// instrument state, and a partial one leaves the DUT in a condition nobody chose. Logged
    /// before the exception propagates, per rule 1.</para>
    /// </summary>
    public void WriteBytes(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var sw = Stopwatch.StartNew();

        try
        {
            // No terminator: the instrument counts the bytes it expects, and a trailing newline
            // would be read as data.
            _session.RawIO.Write(data);
        }
        catch (Exception ex)
        {
            Log(BusOperation.Write,
                $"WRITE FAILED: <{data.Length} bytes> ({ex.Message}). A partial binary write "
                + "leaves the instrument in a state nobody chose.", null, sw.Elapsed);
            throw;
        }

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

    /// <summary>
    /// Lists VISA INSTR resources visible to the resource manager.
    ///
    /// <para><b>An empty list means the bus is empty. A failure throws.</b> Those are different
    /// answers and the earlier version returned the same value for both — so a broken VISA
    /// install, a missing provider or a dead interface all reported as "no instruments found",
    /// which sends somebody to check GPIB cabling when the problem is on this side of the
    /// connector entirely.</para>
    ///
    /// <para>Use <see cref="TryFindResources"/> where a caller genuinely wants to carry on
    /// either way — it says which happened rather than collapsing them.</para>
    /// </summary>
    public static IEnumerable<string> FindResources() =>
        GlobalResourceManager.Find("?*INSTR").ToList();

    /// <summary>
    /// Lists VISA resources, distinguishing "none present" from "could not ask".
    /// </summary>
    /// <returns>
    /// The resources found and a null error, or an empty list and the reason the resource manager
    /// could not be reached.
    /// </returns>
    public static (IReadOnlyList<string> Resources, string? Error) TryFindResources()
    {
        try
        {
            return (GlobalResourceManager.Find("?*INSTR").ToList(), null);
        }
        catch (Exception ex)
        {
            return ([],
                $"The VISA resource manager could not be reached: {ex.Message} This is not an "
                + "empty bus — nothing was asked. Check that a VISA provider is installed and "
                + "that its interface is configured.");
        }
    }

    public void Dispose()
    {
        try { _session.Dispose(); } catch { /* ignore */ }
    }
}
