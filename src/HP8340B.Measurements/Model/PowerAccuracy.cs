namespace HP8340B.Measurements.Model;

/// <summary>
/// One row of Table 4-9's accuracy or flatness block: a level range and its limit in each of the
/// three frequency columns.
/// </summary>
/// <param name="UpperDbm">Top of the level range, inclusive, in dBm.</param>
/// <param name="LowerDbm">Bottom of the level range, inclusive, in dBm.</param>
/// <param name="Band0">Limit in dB for band 0 (0.01 to &lt;2.3 GHz), or null where the manual
/// prints a dash.</param>
/// <param name="Bands1To3">Limit in dB for bands 1-3 (2.3 to &lt;20 GHz).</param>
/// <param name="Band4">Limit in dB for band 4 (20 to 26.5 GHz). 8340B only.</param>
public sealed record PowerSpecRow(
    double UpperDbm,
    double LowerDbm,
    double? Band0,
    double Bands1To3,
    double Band4)
{
    /// <summary>True if <paramref name="dbm"/> falls in this row's level range.</summary>
    public bool Contains(double dbm) => dbm <= UpperDbm && dbm >= LowerDbm;

    /// <summary>The limit for <paramref name="band"/>, or null where the manual prints a dash.</summary>
    public double? For(BandId band) => band switch
    {
        BandId.Band0 => Band0,
        BandId.Band4 => Band4,
        _ => Bands1To3,
    };
}

/// <summary>
/// Output power accuracy and flatness, manual Table 4-9, pp. 4-25 and 4-26.
///
/// <para><b>Transcribed from the rendered page images</b> (<c>pdftoppm -r 200</c>), not from the
/// text layer. Both <c>pdftotext</c> modes mangle this table: the Band 0 column has no entry on
/// the "+18 to +10 dBm" row — the manual prints a dash — so every Band 0 value shifts up by one
/// row in extraction and silently mis-reads as the row above. That is how this project's own
/// changelog briefly carried a wrong figure for band 0. Read the page image.</para>
///
/// <para>Note 3 to the table: the "+18 to +10 dBm" rows sit above the specified maximum leveled
/// power. The ALC loop typically operates up to +20 dB to make that range usable at frequencies
/// where more power than the specification happens to be available — which is why band 0, whose
/// maximum leveled power is +10 dBm, has a dash there.</para>
///
/// <para>The level ranges differ per option: Standard breaks at -9.95/-19.95/-49.95/-79.95,
/// Option 004 at -11.95/-21.95/-51.95/-81.95, and Options 001 and 005 have only three rows.</para>
/// </summary>
public static class PowerAccuracy
{
    private static readonly Dictionary<InstrumentOption, PowerSpecRow[]> AccuracyTable = new()
    {
        [InstrumentOption.Standard] =
        [
            new(+18, +10, null, 1.8, 2.3),
            new(+10, -9.95, 0.9, 1.5, 2.0),
            new(-10, -19.95, 1.2, 2.0, 2.5),
            new(-20, -49.95, 1.5, 2.3, 2.8),
            new(-50, -79.95, 1.8, 2.6, 3.1),
            new(-80, -100, 2.1, 2.9, 3.4),
        ],
        [InstrumentOption.Opt004] =
        [
            new(+18, +10, null, 2.0, 2.5),
            new(+10, -11.95, 1.0, 1.7, 2.2),
            new(-12, -21.95, 1.3, 2.2, 2.7),
            new(-22, -51.95, 1.6, 2.5, 3.0),
            new(-52, -81.95, 1.9, 2.8, 3.3),
            new(-82, -100, 2.2, 3.1, 3.6),
        ],
        [InstrumentOption.Opt001] =
        [
            new(+18, +10, null, 1.6, 2.0),
            new(+10, -10, 0.9, 1.3, 1.7),
            new(-10, -20, 1.7, 2.1, 2.5),
        ],
        [InstrumentOption.Opt005] =
        [
            new(+18, +10, null, 1.8, 2.2),
            new(+10, -10, 1.0, 1.5, 1.9),
            new(-10, -20, 1.8, 2.3, 2.7),
        ],
    };

    private static readonly Dictionary<InstrumentOption, PowerSpecRow[]> FlatnessTable = new()
    {
        [InstrumentOption.Standard] =
        [
            new(+18, +10, null, 1.2, 1.7),
            new(+10, -9.95, 0.6, 1.1, 1.6),
            new(-10, -19.95, 0.9, 1.6, 2.1),
            new(-20, -49.95, 1.2, 1.9, 2.4),
            new(-50, -79.95, 1.4, 2.2, 2.7),
            new(-80, -100, 1.7, 2.5, 3.0),
        ],
        [InstrumentOption.Opt004] =
        [
            new(+18, +10, null, 1.4, 1.9),
            new(+10, -11.95, 0.7, 1.3, 1.8),
            new(-12, -21.95, 1.0, 1.8, 2.3),
            new(-22, -51.95, 1.3, 2.1, 2.6),
            new(-52, -81.95, 1.5, 2.4, 2.9),
            new(-82, -100, 1.8, 2.7, 3.2),
        ],
        [InstrumentOption.Opt001] =
        [
            new(+18, +10, null, 1.0, 1.4),
            new(+10, -10, 0.6, 0.9, 1.3),
            new(-10, -20, 0.8, 1.5, 1.9),
        ],
        [InstrumentOption.Opt005] =
        [
            new(+18, +10, null, 1.2, 1.6),
            new(+10, -10, 0.7, 1.1, 1.5),
            new(-10, -20, 0.9, 1.7, 2.1),
        ],
    };

    /// <summary>Every accuracy row for <paramref name="option"/>, highest level first.</summary>
    public static IReadOnlyList<PowerSpecRow> AccuracyRows(InstrumentOption option) =>
        AccuracyTable[option];

    /// <summary>Every flatness row for <paramref name="option"/>, highest level first.</summary>
    public static IReadOnlyList<PowerSpecRow> FlatnessRows(InstrumentOption option) =>
        FlatnessTable[option];

    /// <summary>
    /// Output power accuracy limit in dB at <paramref name="levelDbm"/> and
    /// <paramref name="ghz"/>. Null where the manual specifies none — band 0 above +10 dBm.
    /// Throws if the level is outside the table or the frequency outside the instrument.
    /// </summary>
    public static double? AccuracyDb(InstrumentOption option, double levelDbm, double ghz) =>
        Lookup(AccuracyTable[option], option, levelDbm, ghz, "accuracy");

    /// <summary>
    /// Flatness limit in dB at <paramref name="levelDbm"/> and <paramref name="ghz"/>.
    /// Flatness is primarily a function of the RF path, so it is essentially the same at all ALC
    /// levels — but it does change when the step attenuator changes range (manual p. 4-27).
    /// </summary>
    public static double? FlatnessDb(InstrumentOption option, double levelDbm, double ghz) =>
        Lookup(FlatnessTable[option], option, levelDbm, ghz, "flatness");

    private static double? Lookup(
        PowerSpecRow[] rows, InstrumentOption option, double levelDbm, double ghz, string what)
    {
        var band = Bands.ForFrequency(ghz)
                   ?? throw new ArgumentOutOfRangeException(
                       nameof(ghz), ghz, "Outside the 8340B's 0.01-26.5 GHz range.");

        // The manual's level ranges are neither cleanly contiguous nor cleanly disjoint, so two
        // edge cases need deciding rather than falling out of a naive lookup.
        //
        // 1. Adjacent rows SHARE their top boundary: "+18 to +10 dBm" and "+10 to -9.95 dBm" both
        //    include +10. Take the LAST match — the row extending downward — because that is the
        //    one carrying a real specification. Taking the first would return band 0's dash at
        //    exactly +10 dBm, its maximum leveled power, which certainly does have a spec.
        // 2. Rows are then GAPPED by 0.05 dB: "+10 to -9.95" is followed by "-10 to -19.95", so
        //    -9.97 dBm falls in neither. That 0.05 dB is how the manual writes "just above -10",
        //    not a real discontinuity, so a level in a gap takes the row below it — the stricter
        //    of the two, and the one the level is about to enter.
        // The gap fallback must not reach outside the table: +25 dBm is above every row and has
        // no specification at all, and must throw rather than silently taking the top row.
        var withinTable = levelDbm <= rows[0].UpperDbm && levelDbm >= rows[^1].LowerDbm;

        var row = rows.LastOrDefault(r => r.Contains(levelDbm))
                  ?? (withinTable ? rows.FirstOrDefault(r => r.LowerDbm <= levelDbm) : null)
                  ?? throw new ArgumentOutOfRangeException(
                      nameof(levelDbm), levelDbm,
                      $"No Table 4-9 {what} row covers {levelDbm} dBm for {option}. The table spans "
                      + $"{rows[0].UpperDbm} to {rows[^1].LowerDbm} dBm.");

        return row.For(band.Id);
    }
}
