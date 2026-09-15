namespace HP8340B.Instruments.Model;

/// <summary>
/// The 8340B output option, which sets the maximum-leveled-power limits.
/// Manual Table 4-9 (p. 4-24).
/// </summary>
public enum InstrumentOption
{
    /// <summary>Front panel output WITH the 90 dB step attenuator.</summary>
    Standard,
    /// <summary>Front panel output, no attenuator.</summary>
    Opt001,
    /// <summary>Rear panel output with attenuator.</summary>
    Opt004,
    /// <summary>Rear panel output, no attenuator.</summary>
    Opt005,
}

/// <summary>
/// Maximum leveled output power per option and band, 0 C to +35 C.
/// Manual Table 4-9 (1 of 2), p. 4-24. Verified against the service manual text layer
/// on 15 Sep 2026. Band 4 is split at 23 GHz; every other band has a single figure.
/// </summary>
public static class MaxLeveledPower
{
    // Columns: Band 0, Band 1, Band 2, Band 3, Band 4 (20-23 GHz), Band 4 (23-26.5 GHz).
    private static readonly Dictionary<InstrumentOption, double[]> Table = new()
    {
        [InstrumentOption.Standard] = [+10.0, +12.0, +10.0, +9.0, +3.0, +1.0],
        [InstrumentOption.Opt001]   = [+10.0, +13.0, +12.0, +11.0, +6.0, +4.0],
        [InstrumentOption.Opt004]   = [+10.0, +11.0, +9.0, +7.0, +1.0, -1.0],
        [InstrumentOption.Opt005]   = [+10.0, +12.0, +11.0, +9.0, +4.0, +2.0],
    };

    /// <summary>
    /// Maximum leveled power in dBm at <paramref name="ghz"/> for <paramref name="option"/>.
    /// Throws if the frequency is outside 0.01-26.5 GHz.
    /// </summary>
    public static double ForFrequency(InstrumentOption option, double ghz)
    {
        var band = Bands.ForFrequency(ghz)
                   ?? throw new ArgumentOutOfRangeException(
                       nameof(ghz), ghz, "Outside the 8340B's 0.01-26.5 GHz range.");

        var row = Table[option];
        return band.Id switch
        {
            BandId.Band4 => ghz < 23.0 ? row[4] : row[5],
            _ => row[(int)band.Id],
        };
    }

    /// <summary>
    /// The 5-14 minimum-power criterion: the lowest power in a band must sit more than
    /// 1 dB above the max-leveled-power spec (steps 15, 22 and 30).
    /// </summary>
    public static double MinPowerTargetDbm(InstrumentOption option, double ghz) =>
        ForFrequency(option, ghz) + 1.0;
}

/// <summary>
/// How far a measurement can be trusted against the manual's own requirement. Every
/// measurement records one of these (repo rule 3).
/// </summary>
public enum TraceabilityClass
{
    /// <summary>Meets the manual's stated requirement with the equipment used.</summary>
    Spec,
    /// <summary>Correct relative to itself (deltas valid) but not an absolute reference.</summary>
    Relative,
    /// <summary>Indicative only — e.g. spectrum-analyzer amplitude standing in for a power meter.</summary>
    Typical,
    /// <summary>Cannot be performed with the equipment on hand.</summary>
    NotPossible,
}
