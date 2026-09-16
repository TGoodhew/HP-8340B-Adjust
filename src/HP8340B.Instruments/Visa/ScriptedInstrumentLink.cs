namespace HP8340B.Instruments.Visa;

/// <summary>
/// A link whose status bytes come from a script, and which can be told to make a serial poll
/// <b>throw</b> rather than answer.
///
/// <para><b>Why this is separate from <see cref="SimulatedInstrumentLink"/>.</b> The simulator
/// models an instrument that works. That is the right thing for exercising a command builder, and
/// it is useless for the cases that actually bite: a poll that never reports ready, a poll that
/// fails part way through a wait, a bus that wedges. Those cannot be reached by substituting a
/// well-behaved instrument, because a well-behaved instrument never does them.</para>
///
/// <para>So this scripts the <i>transport</i> and leaves the driver under test. The pattern, and
/// the point that the adversarial cases are the ones that find things, come from the
/// HP-Attenuator session's <c>--cal-selftest</c> work on the same bench.</para>
/// </summary>
public sealed class ScriptedInstrumentLink : IInstrumentLink
{
    /// <summary>
    /// Script entry meaning "this poll throws". Chosen because a real status byte cannot be
    /// negative — which is itself the distinction the HP-Attenuator session found being lost
    /// elsewhere, where a failed poll and a status of 0x00 rendered identically.
    /// </summary>
    public const int PollFault = -1;

    private readonly Queue<int> _script;
    private readonly List<string> _history = [];
    private readonly List<InstrumentTransaction> _transactions = [];

    public string ResourceName { get; }
    public bool IsSimulated => true;
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(5);
    public IReadOnlyList<string> History => _history;
    public IReadOnlyList<InstrumentTransaction> Transactions => _transactions;

    /// <summary>
    /// What every poll returns once the script runs out. <see cref="PollFault"/> for "and then it
    /// stays broken", which is how a timeout is tested without scripting hundreds of entries.
    /// </summary>
    public int TrailingPoll { get; set; }

    /// <summary>How long each poll takes, for testing a deadline against a blocking transport.</summary>
    public TimeSpan PollTakes { get; set; } = TimeSpan.Zero;

    /// <summary>How many polls have been made, including the ones that threw.</summary>
    public int Polls { get; private set; }

    /// <summary>What a read returns.</summary>
    public string Response { get; set; } = string.Empty;

    public ScriptedInstrumentLink(
        IEnumerable<int>? statusBytes = null, string resourceName = "SCRIPT::instrument::INSTR")
    {
        _script = new Queue<int>(statusBytes ?? []);
        ResourceName = resourceName;
    }

    private void Log(BusOperation op, string text, byte? status = null, TimeSpan elapsed = default) =>
        _transactions.Add(new InstrumentTransaction(
            DateTime.UtcNow, ResourceName, op, text, status, elapsed));

    public void Clear() => Log(BusOperation.Clear, string.Empty);

    public void Write(string command)
    {
        ArgumentNullException.ThrowIfNull(command);
        _history.Add(command);
        Log(BusOperation.Write, command);
    }

    public string Read()
    {
        Log(BusOperation.Read, Response);
        return Response;
    }

    public string Query(string command)
    {
        Write(command);
        return Read();
    }

    public byte[] ReadBytes(int count) => new byte[count];

    public void WriteBytes(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        Log(BusOperation.Write, $"<{data.Length} bytes>");
    }

    public byte SerialPoll()
    {
        Polls++;

        if (PollTakes > TimeSpan.Zero) Thread.Sleep(PollTakes);

        var next = _script.Count > 0 ? _script.Dequeue() : TrailingPoll;

        if (next == PollFault)
        {
            // Logged before throwing, for the same reason the live transport logs it: a failed
            // poll is the most interesting thing that can happen on the bus and a log that omits
            // it is worse than no log.
            Log(BusOperation.SerialPoll, "poll FAILED");

            throw new TimeoutException(
                $"{ResourceName} did not answer a serial poll (scripted fault).");
        }

        Log(BusOperation.SerialPoll, string.Empty, (byte)next);
        return (byte)next;
    }

    public void Dispose() { }
}
