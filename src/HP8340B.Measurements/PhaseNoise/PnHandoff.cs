using HP8340B.Instruments;

namespace HP8340B.Measurements.PhaseNoise;

/// <summary>One step of the hand-off, recorded so the session says what happened and when.</summary>
/// <param name="What">The step.</param>
/// <param name="At">When.</param>
/// <param name="Detail">Anything worth keeping.</param>
public sealed record HandoffStep(string What, DateTime At, string? Detail = null);

/// <summary>How a hand-off ended.</summary>
/// <param name="BusReleased">True once this tool's analyzer session was closed.</param>
/// <param name="BusRecovered">True once the analyzer answered again afterwards.</param>
/// <param name="Steps">Everything that happened, in order.</param>
/// <param name="Trace">The imported trace, if one was.</param>
public sealed record PnHandoffResult(
    bool BusReleased,
    bool BusRecovered,
    IReadOnlyList<HandoffStep> Steps,
    PnTrace? Trace);

/// <summary>
/// Hands the 8563E over to KE5FX PN.EXE and takes it back afterwards.
///
/// <para><b>The rule this exists to enforce.</b> Repo rule 5: never hold a GPIB session on the
/// analyzer while PN is acquiring. Two controllers on one instrument corrupts both — PN's own
/// traffic and ours interleave, and what comes back is neither. So this closes our session
/// <b>before</b> telling anybody to start PN, and proves it closed rather than assuming.</para>
///
/// <para>The ordering is the whole point and it is enforced, not documented: the prompt is not
/// issued until the release has happened. A hand-off that printed "now run PN" and then closed the
/// session would leave a window in which both are live, which is exactly the failure being
/// avoided.</para>
/// </summary>
public static class PnHandoff
{
    /// <summary>
    /// Sets the DUT up, releases the analyzer, waits for the operator, then confirms the bus came
    /// back.
    /// </summary>
    /// <param name="dut">The DUT, which this tool keeps throughout — PN does not touch it.</param>
    /// <param name="carrierHz">Carrier for the measurement.</param>
    /// <param name="carrierDbm">Level for the measurement.</param>
    /// <param name="releaseAnalyzer">
    /// Closes this tool's analyzer session. Must actually dispose it: rule 5 is about there being
    /// one controller, not about politeness.
    /// </param>
    /// <param name="prompt">
    /// Tells the operator to run PN and returns when they say they are done. Called only after
    /// the release.
    /// </param>
    /// <param name="reprobeAnalyzer">
    /// Reopens the analyzer and probes it. Returns null if it cannot be reached.
    /// </param>
    /// <param name="settleTimeout">How long to allow the DUT to settle.</param>
    public static PnHandoffResult Run(
        Hp8340B dut,
        double carrierHz,
        double carrierDbm,
        Action releaseAnalyzer,
        Func<string, bool> prompt,
        Func<ProbeResult?> reprobeAnalyzer,
        TimeSpan settleTimeout)
    {
        ArgumentNullException.ThrowIfNull(dut);
        ArgumentNullException.ThrowIfNull(releaseAnalyzer);
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(reprobeAnalyzer);

        var steps = new List<HandoffStep>();

        void Record(string what, string? detail = null) =>
            steps.Add(new HandoffStep(what, DateTime.UtcNow, detail));

        // 1. The DUT first, while we still have the bus to ourselves.
        dut.SetCwGHz(carrierHz / 1e9);
        dut.SetPowerDbm(carrierDbm);

        var settled = dut.WaitSettled(settleTimeout);

        Record("DUT set",
            $"{carrierHz / 1e9:0.###} GHz at {carrierDbm:+0.0;-0.0} dBm, "
            + (settled ? "settled." : "DID NOT report settled within the timeout."));

        if (!settled)
            throw new InvalidOperationException(
                "The DUT did not settle, so the hand-off stops here. Starting a phase-noise "
                + "acquisition on an unsettled source measures the settling, not the source.");

        // 2. Release BEFORE prompting. The order is the rule.
        releaseAnalyzer();
        Record("Analyzer session closed",
            "Rule 5: PN must be the only controller on the 8563E while it acquires.");

        // 3. Now, and only now, hand over.
        var message =
            $"The 8563E is released. Run PN.EXE now and acquire at {carrierHz / 1e9:0.###} GHz. "
            + "Export the trace to .TXT or .CSV when it is done, then confirm here. "
            + "Do not let this tool touch the analyzer until you have.";

        var operatorDone = prompt(message);

        Record("Operator prompted", operatorDone ? "Confirmed complete." : "Abandoned.");

        if (!operatorDone)
            return new PnHandoffResult(BusReleased: true, BusRecovered: false, steps, null);

        // 4. Take it back, and prove it came back rather than assuming.
        var probe = reprobeAnalyzer();
        var recovered = probe is { Responded: true };

        Record("Analyzer re-probed",
            recovered
                ? $"Responded: {probe!.Identity}"
                : "DID NOT respond. PN may still hold the session, or the adapter needs "
                  + "reinitialising. Nothing further should touch the analyzer until it does.");

        return new PnHandoffResult(BusReleased: true, BusRecovered: recovered, steps, null);
    }

    /// <summary>
    /// The instruction for a bench where PN is driven through a Prologix adapter rather than
    /// NI-488.2.
    ///
    /// <para><b>Decision D-08 is narrowed but not closed.</b> The installed KE5FX toolkit has no
    /// <c>connect.ini</c>, only <c>default_connect.ini</c>, whose setting is
    /// <c>interface_settings GPIB0</c> — so as installed, PN would use the NI-488.2 interface and
    /// nothing has overridden that. A <c>prologix.exe</c> configurator ships alongside it, so the
    /// alternative exists and would be visible as a <c>connect.ini</c> naming a COM port or an IP
    /// address. Which adapter is physically on the bus still has to be looked at.</para>
    /// </summary>
    public static string AdapterNote(bool prologix) => prologix ? PrologixNote : NiNote;

    private const string NiNote =
        "NI-488.2: closing this tool's VISA session is enough — the driver arbitrates, and PN "
        + "opens its own handle to GPIB0 when it starts.";

    private const string PrologixNote =
        "Prologix: the adapter is a single serial port, so only one program can hold it at a "
        + "time. This tool's session must be closed AND its COM port free before PN starts, and "
        + "PN will not report a useful error if it is not — it will simply fail to find the "
        + "analyzer.";
}
