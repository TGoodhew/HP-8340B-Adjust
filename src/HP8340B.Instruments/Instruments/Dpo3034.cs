using HP8340B.Instruments.Visa;

namespace HP8340B.Instruments;

/// <summary>
/// Tektronix DPO3034 — 300 MHz, four channels, 2.5 GS/s. Intended as the <c>tuning-scope</c> for
/// S4, the primary tuning view, displacing the DS1104Z.
///
/// <para>Sources: <b>DPO3034 User Manual</b> for the specifications and <b>MSO3000 and DPO3000
/// Series Programmer Manual</b> for every command below.</para>
///
/// <para><b>Why this one for S4.</b> Not bandwidth — the S4 signal is a detected envelope at sweep
/// rates, so 100 MHz was already far more than enough. The reasons are four channels, a selectable
/// 50 Ω input that takes the 10100C feedthrough out of the chain, an AUX In that can take the DUT's
/// sweep trigger without spending a channel, and a built-in Ethernet port so it automates exactly
/// as the Rigol does.</para>
///
/// <para><b>Not command-compatible with the TDS3014B</b>, despite both being Tektronix: the
/// preamble is <c>WFMOutpre</c> rather than <c>WFMPre</c>, termination is
/// <c>CH&lt;x&gt;:TERmination</c> rather than <c>CH&lt;x&gt;:IMPedance</c>, the horizontal scale
/// drops the <c>MAIn</c>, and XY is its own <c>DISplay:XY</c> rather than a
/// <c>DISplay:FORMat</c> mode.</para>
/// </summary>
public sealed class Dpo3034 : TektronixScope
{
    public override string Model => "Tektronix DPO3034";

    /// <summary>Specifications from the DPO3034 User Manual.</summary>
    public override ScopeCapability Capability { get; } = new(
        Model: "Tektronix DPO3034",
        Channels: 4,
        BandwidthHz: 300e6,
        MinVoltsPerDivision: 1e-3,
        MaxVoltsPerDivision: 10.0,
        InputImpedances:
        [
            ScopeInputImpedance.OneMegaohm,
            ScopeInputImpedance.FiftyOhm,
            ScopeInputImpedance.SeventyFiveOhm,
        ],
        HasExternalTrigger: true,
        HasXyMode: true,
        Derates: [],
        Source: "DPO3034 User Manual; MSO3000/DPO3000 Programmer Manual");

    protected override string PreambleRoot => "WFMOutpre";

    protected override string HorizontalScaleCommand => "HORizontal:SCAle";

    /// <summary>
    /// The Aux In connector. The programmer manual gives <c>AUX</c> (or <c>EXT</c>) as an external
    /// trigger using the Aux Input connector, and notes it is not valid as a <i>data</i> source —
    /// so it can trigger the scope but cannot itself be captured, which is all S4 needs.
    /// </summary>
    protected override string? ExternalTriggerSource => "AUX";

    public Dpo3034(IInstrumentLink link, string role = "tuning-scope")
        : base(link, role)
    {
    }

    /// <summary><c>CH&lt;x&gt;:TERmination {FIFty|SEVENTYFive|MEG}</c>.</summary>
    protected override void WriteInputImpedance(int channel, ScopeInputImpedance impedance) =>
        Write($"CH{channel}:TERmination {impedance switch
        {
            ScopeInputImpedance.FiftyOhm => "FIFty",
            ScopeInputImpedance.SeventyFiveOhm => "SEVENTYFive",
            ScopeInputImpedance.OneMegaohm => "MEG",
            _ => throw new ArgumentOutOfRangeException(nameof(impedance), impedance, null),
        }}");

    protected override ScopeInputImpedance ReadInputImpedance(int channel)
    {
        // Reported as a resistance in NR3 form rather than as the keyword that set it.
        var reported = Query($"CH{channel}:TERmination?").Trim();

        if (double.TryParse(reported, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var ohms))
        {
            return ohms switch
            {
                < 60 => ScopeInputImpedance.FiftyOhm,
                < 1000 => ScopeInputImpedance.SeventyFiveOhm,
                _ => ScopeInputImpedance.OneMegaohm,
            };
        }

        return reported.StartsWith("FIF", StringComparison.OrdinalIgnoreCase)
            ? ScopeInputImpedance.FiftyOhm
            : reported.StartsWith("SEV", StringComparison.OrdinalIgnoreCase)
                ? ScopeInputImpedance.SeventyFiveOhm
                : reported.StartsWith("MEG", StringComparison.OrdinalIgnoreCase)
                    ? ScopeInputImpedance.OneMegaohm
                    : throw new InvalidOperationException(
                        $"The DPO3034 reported CH{channel} termination as '{reported}'.");
    }

    /// <summary>
    /// XY on this family is its own control: <c>DISplay:XY {OFF|TRIGgered}</c>.
    ///
    /// <para>Note the interaction the user manual documents: input impedance is forced to 1 MΩ
    /// when a channel is AC coupled. S4 is a DC measurement of a detected envelope, so it should
    /// never bite — but <see cref="SetInputImpedance"/> would be silently overridden if it did,
    /// which is why coupling is set explicitly in the hook-up rather than left at whatever the
    /// instrument was last used for.</para>
    /// </summary>
    protected override void WriteTimebaseMode(TimebaseMode mode) =>
        Write($"DISplay:XY {mode switch
        {
            TimebaseMode.Normal => "OFF",
            TimebaseMode.XY => "TRIGgered",
            TimebaseMode.Roll => throw new NotSupportedException(
                "Roll is not a DISplay:XY setting on the DPO3034."),
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
        }}");

    /// <summary>
    /// Captures the screen as a PNG.
    ///
    /// <para><b>Unverified on hardware.</b> The DPO3000 family saves an image to a file and then
    /// transfers it, rather than returning it inline the way the Rigol's <c>:DISPlay:DATA?</c>
    /// does, and the exact sequence needs confirming against the instrument. Throwing is better
    /// than guessing a sequence: a wrong one would appear to work and write a corrupt or empty
    /// file into the session record, which is worse than not having the picture. See the
    /// needs-bench criterion on #83.
    /// </para>
    /// </summary>
    public override byte[] CaptureScreenPng(int maxBytes = 2_000_000) =>
        throw new NotSupportedException(
            "Screen capture for the DPO3034 is not implemented: the DPO3000 family saves to a file "
            + "and transfers it rather than returning the image inline, and the sequence has not "
            + "been confirmed against the instrument. Capturing the waveform data works and is "
            + "what the measurements use.");
}
