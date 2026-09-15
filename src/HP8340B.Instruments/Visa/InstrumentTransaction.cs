namespace HP8340B.Instruments.Visa;

/// <summary>What happened on the bus.</summary>
public enum BusOperation
{
    /// <summary>GPIB device clear (SDC).</summary>
    Clear,
    /// <summary>A command sent to the instrument.</summary>
    Write,
    /// <summary>A response read back.</summary>
    Read,
    /// <summary>A serial poll — no data transfer, just the status byte.</summary>
    SerialPoll,
}

/// <summary>
/// One bus transaction, timestamped. Repo rule 1 requires every write to the 8340B to be logged
/// with a timestamp and its old and new value; this is the transport-level half of that, and the
/// session record (M1-02) carries it verbatim.
///
/// Serial polls are logged too. They are how settling is detected, so when a measurement turns out
/// to have been taken before the instrument settled, the poll history is the evidence.
/// </summary>
/// <param name="At">When it happened, UTC.</param>
/// <param name="Resource">VISA resource name, so one log can carry several instruments.</param>
/// <param name="Operation">What was done.</param>
/// <param name="Text">Command or response text; empty for a clear.</param>
/// <param name="StatusByte">The status byte, for <see cref="BusOperation.SerialPoll"/> only.</param>
/// <param name="Elapsed">How long the call took, useful when a timeout is suspected.</param>
public sealed record InstrumentTransaction(
    DateTime At,
    string Resource,
    BusOperation Operation,
    string Text,
    byte? StatusByte = null,
    TimeSpan Elapsed = default)
{
    /// <summary>One log line, for the session's Markdown summary.</summary>
    public override string ToString()
    {
        var detail = Operation switch
        {
            BusOperation.SerialPoll => $"status 0x{StatusByte ?? 0:X2}",
            BusOperation.Clear => "device clear",
            _ => Text,
        };

        return $"{At:HH:mm:ss.fff} {Resource} {Operation,-10} {detail}";
    }
}
