using System.Globalization;
using HP8340B.Instruments.Visa;

namespace HP8340B.Instruments;

/// <summary>IF detector. 8560E Programming Guide p. 453.</summary>
public enum AnalyzerDetector
{
    /// <summary>Normal — alternates positive and negative peaks.</summary>
    Normal,
    /// <summary>Positive peak. What the squegging scans want: a spur must not be averaged away.</summary>
    Positive,
    /// <summary>Negative peak.</summary>
    Negative,
    /// <summary>Sample.</summary>
    Sample,
}

/// <summary>One trace, in dBm against frequency.</summary>
/// <param name="StartHz">Frequency of the first point.</param>
/// <param name="StopHz">Frequency of the last point.</param>
/// <param name="Amplitudes">Trace points in dBm, left to right.</param>
/// <param name="Settings">
/// The analyzer settings in force, recorded alongside the data because repo rule 3 requires every
/// measurement to carry them.
/// </param>
public sealed record AnalyzerTrace(
    double StartHz,
    double StopHz,
    IReadOnlyList<double> Amplitudes,
    string Settings)
{
    /// <summary>Frequency of point <paramref name="index"/>, interpolated across the span.</summary>
    public double FrequencyAt(int index)
    {
        if (Amplitudes.Count <= 1) return StartHz;
        return StartHz + (StopHz - StartHz) * index / (Amplitudes.Count - 1);
    }

    /// <summary>The lowest point and its frequency — the minimum-power point 5-14 hunts for.</summary>
    public (double Hz, double Dbm) Minimum()
    {
        var i = 0;
        for (var n = 1; n < Amplitudes.Count; n++) if (Amplitudes[n] < Amplitudes[i]) i = n;
        return (FrequencyAt(i), Amplitudes[i]);
    }

    /// <summary>The highest point and its frequency.</summary>
    public (double Hz, double Dbm) Maximum()
    {
        var i = 0;
        for (var n = 1; n < Amplitudes.Count; n++) if (Amplitudes[n] > Amplitudes[i]) i = n;
        return (FrequencyAt(i), Amplitudes[i]);
    }
}

/// <summary>
/// Driver for the HP 8563E spectrum analyzer, 9 kHz to 26.5 GHz.
///
/// <para>This instrument does more work in this project than any other: the squegging scans
/// (M1-04, M1-05), the spur scans (4-7/4-8), the zero-span relaxation-oscillation checks (M1-07),
/// the detector-independent swept envelope (M2-03) and the point tuning meter (M2-04) all run on
/// it. It replaces the manual's 8566B and, reaching 26.5 GHz directly, removes the need for the
/// K281C high-pass and the whole LO-plus-mixer squegging arrangement.</para>
///
/// <para>Commands are the 8560-series language, not SCPI. Every one is cited in
/// <see cref="Hp8563ECommands"/>.</para>
/// </summary>
public sealed class Hp8563E : IInstrument
{
    /// <summary>
    /// Trace length. The 8560 series returns 601 points.
    /// </summary>
    public const int TracePoints = 601;

    /// <summary>
    /// Maximum input this project will drive the analyzer to without a declared pad.
    /// Repo rule 5: warn before anything that could put more than +10 dBm in.
    /// </summary>
    public const double WarnAboveDbm = 10.0;

    private readonly IInstrumentLink _link;

    public string Role { get; }
    public string Model => "HP 8563E";
    public IInstrumentLink Link => _link;

    /// <summary>Pad in front of the analyzer input, dB, declared in config. Added back into readings.</summary>
    public double PadDb { get; set; }

    public Hp8563E(IInstrumentLink link, string role = "spectrum-analyzer")
    {
        _link = link ?? throw new ArgumentNullException(nameof(link));
        Role = role;
    }

    private static string Num(double v, string f = "0.######") =>
        v.ToString(f, CultureInfo.InvariantCulture);

    private void Send(string code, string? argument = null) =>
        _link.Write(argument is null
            ? Hp8563ECommands.Require(code)
            : $"{Hp8563ECommands.Require(code)} {argument}");

    // --- Basic state ------------------------------------------------------------------------

    public void Preset() => Send("IP");

    public string Identify() => _link.Query(Hp8563ECommands.Require("ID") + "?").Trim();

    public void SetCenterSpanHz(double centreHz, double spanHz)
    {
        Send("CF", $"{Num(centreHz)} HZ");
        Send("SP", $"{Num(spanHz)} HZ");
    }

    public void SetStartStopHz(double startHz, double stopHz)
    {
        if (stopHz <= startHz)
            throw new ArgumentException(
                $"Stop ({stopHz} Hz) must be above start ({startHz} Hz).", nameof(stopHz));

        Send("FA", $"{Num(startHz)} HZ");
        Send("FB", $"{Num(stopHz)} HZ");
    }

    public void SetReferenceLevelDbm(double dbm) => Send("RL", $"{Num(dbm)} DBM");

    public void SetResolutionBandwidthHz(double hz) => Send("RB", $"{Num(hz)} HZ");

    public void SetVideoBandwidthHz(double hz) => Send("VB", $"{Num(hz)} HZ");

    public void SetSweepTime(TimeSpan sweepTime) =>
        Send("ST", $"{Num(sweepTime.TotalSeconds)} S");

    public void SetDetector(AnalyzerDetector detector) => Send("DET", detector switch
    {
        AnalyzerDetector.Normal => "NRM",
        AnalyzerDetector.Positive => "POS",
        AnalyzerDetector.Negative => "NEG",
        AnalyzerDetector.Sample => "SMP",
        _ => throw new ArgumentOutOfRangeException(nameof(detector), detector, null),
    });

    /// <summary>
    /// Sets input attenuation.
    ///
    /// <para><b>Refuses 0 dB while a carrier at or above 0 dBm is expected.</b> Repo rule 5 and the
    /// S1 hook-up card both say the analyzer's attenuator must never be at 0 dB with a +10 dBm
    /// carrier present, and the squegging scans drive the DUT far higher than that.</para>
    /// </summary>
    /// <param name="attenuationDb">Attenuation in dB.</param>
    /// <param name="expectedCarrierDbm">
    /// Highest level expected at the analyzer input, after any pad. Pass null only when genuinely
    /// unknown — the guard cannot help then.
    /// </param>
    public void SetAttenuationDb(double attenuationDb, double? expectedCarrierDbm)
    {
        if (attenuationDb < 0)
            throw new ArgumentOutOfRangeException(
                nameof(attenuationDb), attenuationDb, "Attenuation cannot be negative.");

        if (attenuationDb == 0 && expectedCarrierDbm >= 0)
            throw new InvalidOperationException(
                $"Refusing 0 dB input attenuation with a carrier of {expectedCarrierDbm} dBm "
                + "expected. Repo rule 5: the 8563E's attenuator must not be at 0 dB with a "
                + "carrier this large. Raise the attenuation or declare a pad.");

        Send("AT", $"{Num(attenuationDb)} DB");
    }

    /// <summary>
    /// Checks a planned DUT output level against the analyzer's input, accounting for the declared
    /// pad. Returns a warning string, or null if the level is safe. Repo rule 5.
    /// </summary>
    public string? CheckInputLevel(double dutOutputDbm)
    {
        var atInput = dutOutputDbm - PadDb;
        if (atInput <= WarnAboveDbm) return null;

        return $"{atInput:+0.0;-0.0} dBm would reach the 8563E input "
               + $"(DUT at {dutOutputDbm:+0.0;-0.0} dBm, pad {PadDb:0.#} dB), above the "
               + $"{WarnAboveDbm:+0.0} dBm limit this project warns at. Add or declare a pad.";
    }

    // --- Sweeping ---------------------------------------------------------------------------

    public void SingleSweepMode() => Send("SNGLS");

    public void ContinuousSweepMode() => Send("CONTS");

    public void TakeSweep() => Send("TS");

    /// <summary>Max hold on trace A — the swept-envelope view (M2-03).</summary>
    public void MaxHold() => Send("MXMH", "TRA");

    /// <summary>Clear-write trace A, resetting a max-hold accumulation.</summary>
    public void ClearWrite() => Send("CLRW", "TRA");

    // --- Reading ----------------------------------------------------------------------------

    /// <summary>
    /// Reads trace A as dBm. Sends <c>TDF P</c> first, which returns real values in the
    /// fundamental units of the guide's Table 5-1 — dBm for amplitude — rather than measurement
    /// units that would need scaling.
    /// </summary>
    public AnalyzerTrace ReadTrace(double startHz, double stopHz, string settings)
    {
        Send("TDF", "P");
        var raw = _link.Query(Hp8563ECommands.Require("TRA") + "?");

        var points = raw
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
                ? v
                : throw new InvalidOperationException(
                    $"8563E trace contained '{p}', which is not a number. Raw response began: "
                    + $"{raw[..Math.Min(80, raw.Length)]}"))
            // Add the pad back, so the reading is the level at the DUT rather than at the analyzer.
            .Select(v => v + PadDb)
            .ToList();

        if (points.Count == 0)
            throw new InvalidOperationException("8563E returned an empty trace.");

        return new AnalyzerTrace(startHz, stopHz, points, settings);
    }

    /// <summary>Peak-searches and returns the marker's frequency in Hz and amplitude in dBm.</summary>
    public (double Hz, double Dbm) PeakSearch()
    {
        Send("MKPK", "HI");

        var hz = Query(Hp8563ECommands.Require("MKF") + "?");
        var dbm = Query(Hp8563ECommands.Require("MKA") + "?");

        return (hz, dbm + PadDb);

        double Query(string q)
        {
            var text = _link.Query(q).Trim();
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
                ? v
                : throw new InvalidOperationException($"8563E returned '{text}' for {q}.");
        }
    }

    /// <summary>Sets centre frequency to the marker — <c>MKCF</c>.</summary>
    public void MarkerToCentre() => Send("MKCF");

    // --- Presets ----------------------------------------------------------------------------

    /// <summary>
    /// The squegging / spur-scan preset from 5-14 steps 70-80: carrier +/- 300 MHz, RBW 300 kHz,
    /// VBW 100 kHz, positive peak detection.
    ///
    /// <para>The manual mixes the DUT against a second 8340B and views a 590-800 MHz IF on an
    /// 8566B. Looking at the carrier directly on the 8563E is equivalent and better: it removes
    /// the mixing products the manual has to warn about distinguishing from real squegging.</para>
    /// </summary>
    public void SpurScanPreset(double carrierHz, double? expectedCarrierDbm = null)
    {
        SetCenterSpanHz(carrierHz, 600e6);
        SetResolutionBandwidthHz(300e3);
        SetVideoBandwidthHz(100e3);
        SetDetector(AnalyzerDetector.Positive);

        if (expectedCarrierDbm is { } dbm) SetReferenceLevelDbm(Math.Ceiling(dbm / 10) * 10);
    }

    /// <summary>
    /// Swept-envelope preset (M2-03): max hold across a band with positive peak detection, so a
    /// power hole cannot be averaged away.
    /// </summary>
    public void EnvelopePreset(double startHz, double stopHz, double referenceDbm)
    {
        SetStartStopHz(startHz, stopHz);
        SetReferenceLevelDbm(referenceDbm);
        SetDetector(AnalyzerDetector.Positive);
        ClearWrite();
        MaxHold();
    }

    /// <summary>
    /// Zero-span preset (M1-07): the time-domain view where squegging shows as a periodic
    /// amplitude collapse. Wide RBW and VBW so the envelope is not filtered away.
    /// </summary>
    public void ZeroSpanPreset(double carrierHz, TimeSpan sweepTime)
    {
        SetCenterSpanHz(carrierHz, 0);
        SetResolutionBandwidthHz(3e6);
        SetVideoBandwidthHz(3e6);
        SetSweepTime(sweepTime);
        SetDetector(AnalyzerDetector.Positive);
    }

    /// <summary>
    /// Captures the screen as HP-GL, the same path GpibMcp uses for 8563E capture and 7090A
    /// forwarding. The analyzer plots itself to the bus.
    ///
    /// <para>The sweep is stopped first and restarted afterwards: plotting a running trace
    /// captures a half-drawn sweep, which is worse than useless as evidence in a session record.
    /// The caller gets back the raw HP-GL, which the reports project renders or forwards.</para>
    /// </summary>
    public string CaptureScreenHpgl(bool restoreContinuous = true)
    {
        // Pre-roll: freeze on a complete sweep so the plot shows a whole trace.
        SingleSweepMode();
        TakeSweep();

        var hpgl = _link.Query(
            $"{Hp8563ECommands.Require("PLOT")} 550,279,9750,7479;");

        if (restoreContinuous) ContinuousSweepMode();

        return hpgl;
    }

    /// <summary>
    /// Selects the frequency reference — <c>FREF INT</c> or <c>FREF EXT</c>. The analyzer presets
    /// to INT, so this has to be set deliberately after any <c>IP</c>.
    /// </summary>
    public void SetFrequencyReference(bool external) =>
        _link.Write($"{Hp8563ECommands.Require("FREF")} {(external ? "EXT" : "INT")};");

    /// <summary>
    /// True if the analyzer reports the external 10 MHz reference selected — <c>FREF?</c>.
    ///
    /// <para>Unlike the 5351A, the 8563E does answer this, which matters because the analyzer's
    /// own reference sets the frequency axis every spur and envelope measurement is judged
    /// against. Null if the response is neither INT nor EXT rather than guessing.</para>
    /// </summary>
    public bool? IsExternalReferenceSelected()
    {
        var response = _link.Query($"{Hp8563ECommands.Require("FREF")}?").Trim();

        if (response.StartsWith("EXT", StringComparison.OrdinalIgnoreCase)) return true;
        if (response.StartsWith("INT", StringComparison.OrdinalIgnoreCase)) return false;

        return null;
    }

    public ProbeResult Probe()
    {
        try
        {
            var identity = Identify();
            var external = IsExternalReferenceSelected();

            var detail = PadDb != 0 ? $"Pad {PadDb:0.#} dB declared. " : string.Empty;

            detail += external switch
            {
                true => "Frequency reference: EXT.",
                false => "Frequency reference: INT — the analyzer is on its own crystal, not the "
                         + "Z3805A. Note IP presets FREF back to INT.",
                null => "FREF? gave no recognisable answer, so the reference is unconfirmed.",
            };

            return new ProbeResult(
                Role, Model, _link.ResourceName,
                Responded: !string.IsNullOrWhiteSpace(identity),
                Identity: identity,
                ExternalReference: external,
                Detail: detail,
                ReferenceApplies: true);
        }
        catch (Exception ex)
        {
            return new ProbeResult(Role, Model, _link.ResourceName, Responded: false, Detail: ex.Message);
        }
    }

    public void Dispose() => _link.Dispose();
}
