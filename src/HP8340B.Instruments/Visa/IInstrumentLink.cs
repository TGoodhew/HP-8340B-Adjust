namespace HP8340B.Instruments.Visa;

/// <summary>
/// Abstraction over the transport to an instrument so callers can drive either a real VISA
/// session or an in-memory simulator with the same code path. Shape borrowed from
/// HP-Attenuator's HpAttenuator.Visa.IInstrumentLink so drivers port across unchanged.
/// </summary>
public interface IInstrumentLink : IDisposable
{
    string ResourceName { get; }
    bool IsSimulated { get; }

    /// <summary>
    /// I/O timeout. Settable per instrument because the range across this bench is wide: a 3458A
    /// DCV read is milliseconds, while an 8902A averaging measurement can take 10 s and a 2 s DUT
    /// sweep plus settling is longer still. A single flat value would either fail slow
    /// instruments or hide a dead one for far too long.
    /// </summary>
    TimeSpan Timeout { get; set; }

    /// <summary>Sends a GPIB device clear (SDC) to reset the device's bus state.</summary>
    void Clear();

    /// <summary>Sends a raw command string (terminator added by the link).</summary>
    void Write(string command);

    /// <summary>Reads a response string from a talker instrument.</summary>
    string Read();

    /// <summary>Writes a command then reads the response (for query instruments).</summary>
    string Query(string command);

    /// <summary>
    /// GPIB serial poll: returns the device's status byte without a data transfer. The 8340B
    /// predates IEEE 488.2 and has no *OPC?, so settle-waiting is built on this — status byte
    /// bit 3 "RF settled", bit 4 "end of sweep". See <see cref="StatusPoller"/>.
    /// </summary>
    byte SerialPoll();

    /// <summary>The commands written so far, most recent last. Convenience over the log.</summary>
    IReadOnlyList<string> History { get; }

    /// <summary>
    /// Every bus transaction, timestamped, including serial polls and reads. This is what the
    /// session record carries (M1-02) and what satisfies repo rule 1's requirement that every
    /// write be logged.
    /// </summary>
    IReadOnlyList<InstrumentTransaction> Transactions { get; }
}
