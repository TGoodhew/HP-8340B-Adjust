using System.Globalization;
using HP8340B.Instruments.Visa;

namespace HP8340B.Instruments;

/// <summary>
/// Simulated 8673B. Answers the read-back codes from what has actually been written to the link,
/// so a round-trip test proves the command builder and the response parser agree rather than
/// comparing a driver against a constant it could never disagree with.
///
/// <para>The reference state is settable, because the case that matters is the unhappy one: an
/// LO quietly running on its own crystal while the DUT is on the Z3805A. <c>probe --sim</c> should
/// be able to show what that looks like.</para>
/// </summary>
public sealed class SimulatedSource
{
    private SimulatedInstrumentLink? _link;

    /// <summary>
    /// Where the generator sits before anything is set. Table 3-3 (p. 3-34): a Clear message
    /// "Sets output to 3000.000 MHz at -70 dBm with sweep and modulation off".
    /// </summary>
    public const double ClearedHz = 3.0e9;
    public const double ClearedDbm = -70.0;

    /// <summary>Whether the rear-panel FREQ STANDARD switch is modelled as being in EXT.</summary>
    public bool ExternalReferenceSelected { get; set; } = true;

    /// <summary>Whether the generator is modelled as phase locked.</summary>
    public bool PhaseLocked { get; set; } = true;

    /// <summary>Whether the ALC is modelled as unleveled.</summary>
    public bool Unleveled { get; set; }

    /// <summary>Builds the link, wiring the responder to this model.</summary>
    public SimulatedInstrumentLink CreateLink(string resourceName = "SIM::lo-source::INSTR")
    {
        _link = new SimulatedInstrumentLink(resourceName, Respond)
        {
            // Source settled: a simulated generator is always settled, so a settle-wait returns
            // immediately instead of burning its timeout in tests.
            StatusByte = (byte)SourceStatusByte1.SourceSettled,
        };

        RefreshStatusBytes();
        return _link;
    }

    /// <summary>
    /// Updates the two bytes an <c>OS</c> read returns. Called after a property changes so a test
    /// can model a generator that has dropped off the house reference.
    /// </summary>
    public void RefreshStatusBytes()
    {
        if (_link is null) return;

        var extended = SourceStatusByte2.None;
        if (ExternalReferenceSelected) extended |= SourceStatusByte2.ExternalReference;
        if (!PhaseLocked) extended |= SourceStatusByte2.NotPhaseLocked;
        if (Unleveled) extended |= SourceStatusByte2.AlcUnleveled;

        _link.NextBytes = [(byte)SourceStatusByte1.SourceSettled, (byte)extended];
    }

    /// <summary>The frequency the model has been set to, in Hz, read back out of the history.</summary>
    public double FrequencyHz => LastValue("FR", "HZ") ?? ClearedHz;

    /// <summary>The level the model has been set to, in dBm.</summary>
    public double LevelDbm => LastValue("LE", "DM") ?? ClearedDbm;

    /// <summary>True once <c>RF1</c> has been sent more recently than <c>RF0</c>.</summary>
    public bool RfOn
    {
        get
        {
            var last = _link?.History.LastOrDefault(h =>
                h.Equals("RF1", StringComparison.OrdinalIgnoreCase)
                || h.Equals("RF0", StringComparison.OrdinalIgnoreCase));

            return last?.Equals("RF1", StringComparison.OrdinalIgnoreCase) == true;
        }
    }

    /// <summary>
    /// Finds the value of the most recent command of the form {prefix}{number}{units}, ignoring
    /// the read-back form {prefix}OA which carries no value.
    /// </summary>
    private double? LastValue(string prefix, string units)
    {
        var command = _link?.History.LastOrDefault(h =>
            h.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && h.EndsWith(units, StringComparison.OrdinalIgnoreCase)
            && h.Length > prefix.Length + units.Length);

        return command is null ? null : Hp8673B.ParseNumeric(command, "simulated setting");
    }

    private string Respond(string command)
    {
        var c = command.Trim();

        // Responses are shaped exactly as the manual describes them — program-code prefix and
        // units terminator included — so the driver's parser meets the real format. Note FROA
        // answers with a CF prefix, not FR: that is what p. 3-71 specifies.
        if (c.StartsWith("FROA", StringComparison.OrdinalIgnoreCase))
            return $"CF{FrequencyHz.ToString("0", CultureInfo.InvariantCulture)}HZ";

        if (c.StartsWith("OK", StringComparison.OrdinalIgnoreCase))
            return $"FR{FrequencyHz.ToString("0", CultureInfo.InvariantCulture)}HZ";

        if (c.StartsWith("LEOA", StringComparison.OrdinalIgnoreCase))
            return $"LE{LevelDbm.ToString("0.0", CultureInfo.InvariantCulture)}DM";

        return string.Empty;
    }
}
