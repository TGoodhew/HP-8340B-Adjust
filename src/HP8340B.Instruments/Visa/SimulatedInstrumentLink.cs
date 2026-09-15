using System.Text.RegularExpressions;

namespace HP8340B.Instruments.Visa;

/// <summary>
/// In-memory link used by <c>--sim</c>. Records everything written and answers queries
/// from a responder supplied by the owning simulator, so every driver and measurement is
/// exercisable with no hardware (repo rule 4).
/// </summary>
public sealed class SimulatedInstrumentLink : IInstrumentLink
{
    private readonly List<string> _history = new();
    private readonly Func<string, string> _responder;

    public string ResourceName { get; }
    public bool IsSimulated => true;
    public IReadOnlyList<string> History => _history;

    /// <summary>The most recent command written, or an empty string if none.</summary>
    public string LastWrite => _history.Count > 0 ? _history[^1] : string.Empty;

    /// <summary>Status byte the next <see cref="SerialPoll"/> returns. Settable by simulators.</summary>
    public byte StatusByte { get; set; }

    public SimulatedInstrumentLink(string resourceName, Func<string, string>? responder = null)
    {
        ResourceName = resourceName;
        _responder = responder ?? (_ => string.Empty);
    }

    public void Clear() => _history.Clear();

    public void Write(string command)
    {
        ArgumentNullException.ThrowIfNull(command);
        _history.Add(command);
    }

    public string Read() => _responder(LastWrite);

    public string Query(string command)
    {
        Write(command);
        return _responder(command);
    }

    public byte SerialPoll() => StatusByte;

    /// <summary>True if any command sent so far matches <paramref name="pattern"/>.</summary>
    public bool Sent(string pattern) =>
        _history.Any(h => Regex.IsMatch(h, pattern, RegexOptions.IgnoreCase));

    public void Dispose() { }
}
