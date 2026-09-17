namespace HP8340B.Instruments.Visa;

/// <summary>
/// A cost model for simulated bus operations: a fixed cost per operation plus a cost per byte.
///
/// <para>Crude on purpose. It is here so the M0-30 rate framework has something to measure with no
/// hardware attached, not to predict what any real instrument does — and the two terms are split
/// only because they are the two that behave differently. A per-operation cost dominates short
/// exchanges like a serial poll or a marker query, where the round trip is nearly all of it; a
/// per-byte cost dominates a waveform transfer, which is the whole argument for BYTE over ASCII
/// (#87). A single figure could not tell those apart, and telling them apart is the point.</para>
///
/// <para><b>Nothing here is a measurement.</b> These numbers are whatever the caller chose. See
/// <see cref="SimulatedInstrumentLink.Latency"/>.</para>
/// </summary>
/// <param name="PerOperation">Fixed cost of one bus operation, regardless of size.</param>
/// <param name="PerByte">Additional cost for each byte transferred.</param>
public sealed record SimulatedLatency(TimeSpan PerOperation, TimeSpan PerByte)
{
    /// <summary>Free. The default, so the test suite stays fast.</summary>
    public static readonly SimulatedLatency None =
        new(TimeSpan.Zero, TimeSpan.Zero);

    /// <summary>The cost of one operation moving <paramref name="bytes"/> bytes.</summary>
    public TimeSpan For(int bytes) =>
        PerOperation + (bytes > 0 ? PerByte * bytes : TimeSpan.Zero);

    /// <summary>
    /// A model with a fixed round-trip cost and no size term — the shape of a slow, chatty link
    /// where the round trip dominates.
    /// </summary>
    public static SimulatedLatency RoundTrip(TimeSpan perOperation) =>
        new(perOperation, TimeSpan.Zero);
}
