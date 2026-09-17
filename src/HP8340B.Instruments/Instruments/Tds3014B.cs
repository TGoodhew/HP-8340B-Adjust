using HP8340B.Instruments.Visa;

namespace HP8340B.Instruments;

/// <summary>
/// Tektronix TDS3014B — 100 MHz, four channels. Intended as the <c>testpoint-scope</c> for S5, so
/// that S4 and S5 can be instrumented at the same time during 5-14 and 5-16 rather than re-cabled
/// between views with the covers off.
///
/// <para>Sources: <b>TDS3014B Service Manual</b> for the specifications and <b>TDS3000,
/// TDS3000B and TDS3000C Series Programmer Manual</b> for every command below.</para>
///
/// <para><b>The B suffix matters.</b> The programmer manual states that all TDS3000B and TDS3000C
/// oscilloscopes have a built-in Ethernet port, so no communication module is needed to automate
/// this instrument. It also carries a caution worth repeating on the hook-up card: do not fit a
/// TDS3EM module to a B or C series scope, because doing so stops <i>both</i> the built-in
/// Ethernet port and the module's own port working.</para>
///
/// <para><b>It has no usable external trigger.</b> The programmer manual states that neither EXT
/// nor EXT10 is available on <b>4-channel</b> TDS3000 Series instruments, and the 3014B is
/// four-channel. The DUT's rear sweep trigger therefore has to go to one of the four channels and
/// be selected with <see cref="TektronixScope.SetChannelTrigger"/>. That costs a channel, which is
/// why <see cref="ScopeRole"/> counts it — three test points plus the trigger is exactly four, so
/// this scope fits S5 with nothing to spare.</para>
/// </summary>
public sealed class Tds3014B : TektronixScope
{
    public override string Model => "Tektronix TDS3014B";

    /// <summary>
    /// Specifications from the TDS3014B Service Manual.
    ///
    /// <para>The derate is real and documented: 100 MHz from 5 mV/div to 1 V/div and from
    /// 2 mV/div, but <b>90 MHz at 1 mV/div</b>. It never bites on this bench, because the S5 test
    /// points are DC or slow, but the figure belongs in the driver rather than in somebody's
    /// memory of a footnote.</para>
    /// </summary>
    public override ScopeCapability Capability { get; } = new(
        Model: "Tektronix TDS3014B",
        Channels: 4,
        BandwidthHz: 100e6,
        MinVoltsPerDivision: 1e-3,
        MaxVoltsPerDivision: 10.0,
        InputImpedances: [ScopeInputImpedance.OneMegaohm, ScopeInputImpedance.FiftyOhm],
        HasExternalTrigger: false,
        HasXyMode: true,
        Derates: [new BandwidthDerate(1.99e-3, 90e6)],
        Source: "TDS3014B Service Manual; TDS3000/B/C Programmer Manual");

    protected override string PreambleRoot => "WFMPre";

    protected override string HorizontalScaleCommand => "HORizontal:MAIn:SCAle";

    /// <summary>Null: EXT and EXT10 are unavailable on 4-channel TDS3000 Series instruments.</summary>
    protected override string? ExternalTriggerSource => null;

    public Tds3014B(IInstrumentLink link, string role = "testpoint-scope")
        : base(link, role)
    {
    }

    /// <summary><c>CH&lt;x&gt;:IMPedance { FIFty | MEG }</c>.</summary>
    protected override void WriteInputImpedance(int channel, ScopeInputImpedance impedance) =>
        Write($"CH{channel}:IMPedance {impedance switch
        {
            ScopeInputImpedance.FiftyOhm => "FIFty",
            ScopeInputImpedance.OneMegaohm => "MEG",
            _ => throw new NotSupportedException(
                $"The TDS3014B supports 50 ohm and 1 Mohm only, not {impedance}."),
        }}");

    protected override ScopeInputImpedance ReadInputImpedance(int channel)
    {
        var reported = Query($"CH{channel}:IMPedance?").Trim();

        return reported.StartsWith("FIF", StringComparison.OrdinalIgnoreCase)
            ? ScopeInputImpedance.FiftyOhm
            : reported.StartsWith("MEG", StringComparison.OrdinalIgnoreCase)
                ? ScopeInputImpedance.OneMegaohm
                : throw new InvalidOperationException(
                    $"The TDS3014B reported CH{channel} termination as '{reported}'.");
    }

    /// <summary>XY is a mode of <c>DISplay:FORMat</c> on this family.</summary>
    protected override void WriteTimebaseMode(TimebaseMode mode) =>
        Write($"DISplay:FORMat {mode switch
        {
            TimebaseMode.Normal => "YT",
            TimebaseMode.XY => "XY",
            TimebaseMode.Roll => throw new NotSupportedException(
                "The TDS3014B has no roll mode in DISplay:FORMat."),
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
        }}");

    /// <summary>
    /// Captures the screen as a PNG. <c>HARDCopy:FORMat</c> lists PNG, and <c>HARDCopy:PORT</c>
    /// selects where the image goes.
    ///
    /// <para><b>Unverified on hardware.</b> The commands are from the programmer manual, but which
    /// port returns the image over a LAN session is genuinely ambiguous — <c>ETHERnet</c> means
    /// "send to a network printer" rather than "return it to me", so <c>GPIb</c> is used here to
    /// make the instrument reply over the active interface. That reasoning needs confirming against
    /// the instrument before this is trusted; see the needs-bench criterion on #82.</para>
    /// </summary>
    public override byte[] CaptureScreenPng(int maxBytes = 2_000_000)
    {
        Write("HARDCopy:FORMat PNG");
        Write("HARDCopy:PORT GPIb");
        Write("HARDCopy STARt");

        return Link.ReadBytes(maxBytes);
    }
}
