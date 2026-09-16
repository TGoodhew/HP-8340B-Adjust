using System.Globalization;
using HP8340B.Instruments.Visa;

namespace HP8340B.Instruments;

/// <summary>
/// Simulated 8563E. Answers the 8560-series queries the driver sends and synthesises a trace, so
/// the squegging scanner (M1-04/M1-05), the envelope view and the point tuning meter are all
/// developable and testable with no hardware (repo rule 4).
///
/// <para>The trace model is deliberately simple but not flat: a noise floor, a carrier at the
/// centre, and optional spurs. A flat trace would let a broken peak-search or a broken
/// minimum-finder pass.</para>
/// </summary>
public sealed class SimulatedAnalyzer
{
    private readonly Random _random;

    /// <summary>Noise floor in dBm.</summary>
    public double NoiseFloorDbm { get; set; } = -90.0;

    /// <summary>Carrier level in dBm, placed at the centre of the span.</summary>
    public double CarrierDbm { get; set; } = 0.0;

    /// <summary>
    /// Spurs to inject, as (offset from centre in trace points, level in dBm). The squegging
    /// scanner looks for exactly this: a non-harmonic response well above the floor.
    /// </summary>
    public List<(int OffsetPoints, double Dbm)> Spurs { get; } = new();

    /// <summary>Whether the model answers FREF? with EXT. See <see cref="Respond"/>.</summary>
    public bool ExternalReference { get; set; } = true;

    /// <summary>
    /// Shared bench state. When set, the marker reports the level the DUT was actually set to,
    /// which is what makes a wiring self-check between the two mean anything.
    /// </summary>
    public SimulatedBenchState? Bench { get; set; }

    public SimulatedAnalyzer(int seed = 8563) => _random = new Random(seed);

    /// <summary>Builds the link, wiring the responder to this model.</summary>
    public SimulatedInstrumentLink CreateLink(string resourceName = "SIM::spectrum-analyzer::INSTR")
    {
        var link = new SimulatedInstrumentLink(resourceName, Respond);
        // Bit 4 = end of sweep in the 8560 status model, per GpibMcp's 8563E database.
        link.StatusByte = 0x10;
        return link;
    }

    private string Respond(string command)
    {
        var c = command.Trim();

        if (c.StartsWith("ID?", StringComparison.OrdinalIgnoreCase))
            return "HP8563E,SIMULATED";

        // The simulated analyzer is on the house reference, like the real one. Settable so a test
        // can model the analyzer having been left on its own crystal after an IP.
        if (c.StartsWith("FREF?", StringComparison.OrdinalIgnoreCase))
            return ExternalReference ? "EXT" : "INT";

        if (c.StartsWith("TRA?", StringComparison.OrdinalIgnoreCase))
            return string.Join(",", BuildTrace().Select(v =>
                v.ToString("0.##", CultureInfo.InvariantCulture)));

        // MKF? and MKA? answer from the synthesised trace, so a peak search returns something
        // consistent with what a trace read would show.
        if (c.StartsWith("MKF?", StringComparison.OrdinalIgnoreCase))
            return (Hp8563E.TracePoints / 2).ToString(CultureInfo.InvariantCulture);

        if (c.StartsWith("MKA?", StringComparison.OrdinalIgnoreCase))
            return (Bench?.LevelDbm ?? BuildTrace().Max()).ToString("0.##", CultureInfo.InvariantCulture);

        // A plot request returns HP-GL. Enough structure that a renderer or the 7090A forwarder
        // can be exercised without hardware.
        if (c.StartsWith("PLOT", StringComparison.OrdinalIgnoreCase))
            return "IN;SP1;PU550,279;PD9750,279;PD9750,7479;PD550,7479;PD550,279;PU;SP0;";

        return string.Empty;
    }

    /// <summary>The trace this model would return. Public so tests can assert against it.</summary>
    public double[] BuildTrace()
    {
        var trace = new double[Hp8563E.TracePoints];
        var centre = Hp8563E.TracePoints / 2;

        for (var i = 0; i < trace.Length; i++)
        {
            // A little noise so nothing depends on an artificially flat floor.
            trace[i] = NoiseFloorDbm + _random.NextDouble() * 2.0 - 1.0;
        }

        // Carrier: a narrow peak at the centre, falling away over a few points.
        for (var d = -3; d <= 3; d++)
        {
            var i = centre + d;
            if (i < 0 || i >= trace.Length) continue;
            trace[i] = Math.Max(trace[i], CarrierDbm - Math.Abs(d) * 12.0);
        }

        foreach (var (offset, dbm) in Spurs)
        {
            var i = centre + offset;
            if (i < 0 || i >= trace.Length) continue;
            trace[i] = Math.Max(trace[i], dbm);
        }

        return trace;
    }
}
