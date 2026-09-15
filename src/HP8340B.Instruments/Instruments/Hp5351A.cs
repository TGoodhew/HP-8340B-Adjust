using System.Globalization;
using HP8340B.Instruments.Visa;

namespace HP8340B.Instruments;

/// <summary>Counter input and measurement mode. 5350A/5351A/5352A manual, paragraphs 3-308 to 3-311.</summary>
public enum CounterMode
{
    /// <summary>INPUT 1, automatic — the microwave input. What 4-2 uses across the DUT's range.</summary>
    Automatic,
    /// <summary>INPUT 1, manual, with a centre frequency supplied.</summary>
    Manual,
    /// <summary>INPUT 2, 50 ohm.</summary>
    LowZ,
    /// <summary>INPUT 2, 1 Mohm.</summary>
    HighZ,
}

/// <summary>
/// Driver for the HP 5351A microwave frequency counter, to 26.5 GHz.
///
/// <para>Source: <b>HP 5350A/5351A/5352A Operating and Programming manual</b> (`5351A.pdf` in the
/// local manual library), paragraphs 3-305 to 3-330. The command set is word-based — RESET, AUTO,
/// MANUAL and so on — with <c>;</c> between commands and <c>,</c> between a command and its data.
/// The counter is a talker: a measurement is read by addressing it, not by sending a query.</para>
///
/// <para>Used by 4-2 (frequency range and CW accuracy) and 4-4 (swept frequency spot checks).
/// <b>4-2 is meaningless unless the counter and the DUT share the Z3805A</b>, which is why the
/// reference check below matters more than the frequency read.</para>
/// </summary>
public sealed class Hp5351A : IInstrument
{
    /// <summary>
    /// Maximum input this project will drive the counter to without a declared pad.
    /// Repo rule 5: warn above +10 dBm.
    /// </summary>
    public const double WarnAboveDbm = 10.0;

    private readonly IInstrumentLink _link;

    public string Role { get; }
    public string Model => "HP 5351A";
    public IInstrumentLink Link => _link;

    /// <summary>Pad in front of the counter input, dB, declared in config.</summary>
    public double PadDb { get; set; }

    public Hp5351A(IInstrumentLink link, string role = "counter")
    {
        _link = link ?? throw new ArgumentNullException(nameof(link));
        Role = role;
    }

    /// <summary>
    /// Instrument reset — paragraph 3-305. The manual is explicit that RESET, CLR and INIT clear
    /// the input buffers and so must be sent <b>alone</b>, not appended to other commands.
    /// </summary>
    public void Reset() => _link.Write("RESET");

    /// <summary>Instrument initialisation — paragraph 3-307. Sent alone, as above.</summary>
    public void Initialize() => _link.Write("INIT");

    /// <summary>
    /// Selects the input and measurement mode. <see cref="CounterMode.Manual"/> takes a centre
    /// frequency in Hz; without one the counter uses the last measurement (paragraph 3-309).
    /// </summary>
    public void SetMode(CounterMode mode, double? manualCentreHz = null)
    {
        switch (mode)
        {
            case CounterMode.Automatic:
                _link.Write("AUTO");
                break;

            case CounterMode.Manual:
                _link.Write(manualCentreHz is { } hz
                    ? $"MANUAL,{hz.ToString("0.###", CultureInfo.InvariantCulture)}"
                    : "MANUAL");
                break;

            case CounterMode.LowZ:
                _link.Write("LOWZ");
                break;

            case CounterMode.HighZ:
                _link.Write("HIGHZ");
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(mode), mode, null);
        }
    }

    /// <summary>Starts a measurement — paragraph 3-312, TRIGGER/TRG.</summary>
    public void Trigger() => _link.Write("TRG");

    /// <summary>
    /// Reads the current measurement in Hz. The counter is a talker, so this is a bare read
    /// rather than a query.
    /// </summary>
    public double ReadFrequencyHz()
    {
        var text = _link.Read().Trim();

        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var hz))
            throw new InvalidOperationException(
                $"5351A returned '{text}', which is not a frequency. The counter is a talker and "
                + "returns its measurement directly; a non-numeric response usually means it has "
                + "no signal or is still settling.");

        return hz;
    }

    /// <summary>
    /// Checks a planned DUT output level against the counter's input, accounting for the declared
    /// pad. Returns a warning, or null if safe. Repo rule 5.
    /// </summary>
    public string? CheckInputLevel(double dutOutputDbm)
    {
        var atInput = dutOutputDbm - PadDb;
        if (atInput <= WarnAboveDbm) return null;

        return $"{atInput:+0.0;-0.0} dBm would reach the 5351A input "
               + $"(DUT at {dutOutputDbm:+0.0;-0.0} dBm, pad {PadDb:0.#} dB), above the "
               + $"{WarnAboveDbm:+0.0} dBm this project warns at. Add or declare a pad.";
    }

    public ProbeResult Probe()
    {
        try
        {
            // The counter has no identification query in the manual's command list, so proof of
            // life is a serial poll.
            var status = _link.SerialPoll();

            return new ProbeResult(
                Role, Model, _link.ResourceName,
                Responded: true,
                Identity: $"status byte 0x{status:X2}",
                // The manual (paragraph 3-127) says the counter switches to an external reference
                // AUTOMATICALLY when one is applied, and lights the EXT REF annunciator. No
                // command in the 3-305..3-330 list reads that state back, so this cannot be
                // confirmed over the bus. Reporting null rather than a guess: 4-2 is meaningless
                // if the counter is not on the Z3805A, so this must be checked at the front panel.
                ExternalReference: null,
                Detail: "EXT REF is automatic and has no documented query — confirm the EXT REF "
                        + "annunciator is lit at the front panel before trusting 4-2.",
                ReferenceApplies: true);
        }
        catch (Exception ex)
        {
            return new ProbeResult(Role, Model, _link.ResourceName, Responded: false, Detail: ex.Message);
        }
    }

    public void Dispose() => _link.Dispose();
}
