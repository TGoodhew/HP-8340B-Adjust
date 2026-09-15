using System.Diagnostics;
using System.Text.RegularExpressions;

namespace HP8340B.Instruments.Visa;

/// <summary>
/// In-memory link used by <c>--sim</c>. Records everything written and answers queries from a
/// responder supplied by the owning simulator, so every driver and measurement is exercisable
/// with no hardware (repo rule 4).
/// </summary>
public sealed class SimulatedInstrumentLink : IInstrumentLink
{
    private readonly List<string> _history = new();
    private readonly List<InstrumentTransaction> _transactions = new();
    private readonly Func<string, string> _responder;

    public string ResourceName { get; }
    public bool IsSimulated => true;
    public IReadOnlyList<string> History => _history;
    public IReadOnlyList<InstrumentTransaction> Transactions => _transactions;

    /// <summary>Simulated I/O is instant, but the value round-trips so config is testable.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>The most recent command written, or an empty string if none.</summary>
    public string LastWrite => _history.Count > 0 ? _history[^1] : string.Empty;

    /// <summary>Status byte the next <see cref="SerialPoll"/> returns. Settable by simulators.</summary>
    public byte StatusByte { get; set; }

    /// <summary>
    /// Optional hook letting a simulator vary the status byte per poll — used to model an
    /// instrument that takes a few polls to settle, so settle-waits are genuinely exercised
    /// rather than always succeeding on the first try.
    /// </summary>
    public Func<int, byte>? StatusSequence { get; set; }

    private int _pollCount;

    /// <summary>How many times this link has been serial-polled.</summary>
    public int PollCount => _pollCount;

    public SimulatedInstrumentLink(string resourceName, Func<string, string>? responder = null)
    {
        ResourceName = resourceName;
        _responder = responder ?? (_ => string.Empty);
    }

    private void Log(BusOperation op, string text, byte? status = null, TimeSpan elapsed = default) =>
        _transactions.Add(new InstrumentTransaction(
            DateTime.UtcNow, ResourceName, op, text, status, elapsed));

    public void Clear()
    {
        _history.Clear();
        Log(BusOperation.Clear, string.Empty);
    }

    public void Write(string command)
    {
        ArgumentNullException.ThrowIfNull(command);
        _history.Add(command);
        Log(BusOperation.Write, command);
    }

    public string Read()
    {
        var response = _responder(LastWrite);
        Log(BusOperation.Read, response);
        return response;
    }

    public string Query(string command)
    {
        Write(command);
        var response = _responder(command);
        Log(BusOperation.Read, response);
        return response;
    }

    /// <summary>
    /// Binary data the next <see cref="ReadBytes"/> returns. Simulators set this to model a
    /// learn string or a status-byte pair.
    /// </summary>
    public byte[] NextBytes { get; set; } = Array.Empty<byte>();

    /// <summary>The most recent payload passed to <see cref="WriteBytes"/>.</summary>
    public byte[] LastBytesWritten { get; private set; } = Array.Empty<byte>();

    public byte[] ReadBytes(int count)
    {
        // Return exactly what was asked for, padding with zeros, so a driver that miscounts is
        // caught by its own assertions rather than by a ragged array.
        var data = new byte[count];
        Array.Copy(NextBytes, data, Math.Min(count, NextBytes.Length));
        Log(BusOperation.Read, $"<{count} bytes>");
        return data;
    }

    public void WriteBytes(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        LastBytesWritten = data;
        Log(BusOperation.Write, $"<{data.Length} bytes>");
    }

    public byte SerialPoll()
    {
        var status = StatusSequence is not null ? StatusSequence(_pollCount) : StatusByte;
        _pollCount++;
        Log(BusOperation.SerialPoll, string.Empty, status);
        return status;
    }

    /// <summary>True if any command sent so far matches <paramref name="pattern"/>.</summary>
    public bool Sent(string pattern) =>
        _history.Any(h => Regex.IsMatch(h, pattern, RegexOptions.IgnoreCase));

    public void Dispose() { }
}
