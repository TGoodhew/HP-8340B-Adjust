namespace HP8340B.Instruments.Model;

/// <summary>
/// The 8340B's five frequency bands. Bandswitch points are approximate — the exact
/// crossovers are themselves adjustable via CC77/CC78 (5-14 steps 59-67).
/// Manual: Section IV, "Frequency Ranges and Bandswitch Points".
/// </summary>
public enum BandId
{
    /// <summary>0.01-2.3 GHz, heterodyne (YO mixed with the A8 3.7 GHz oscillator).</summary>
    Band0 = 0,
    /// <summary>2.3-7.0 GHz, YIG oscillator fundamental.</summary>
    Band1 = 1,
    /// <summary>7.0-13.5 GHz, SYTM x2.</summary>
    Band2 = 2,
    /// <summary>13.5-20.0 GHz, SYTM x3.</summary>
    Band3 = 3,
    /// <summary>20.0-26.5 GHz, SYTM x4. HP 8340B only.</summary>
    Band4 = 4,
}

/// <summary>One band's frequency span and how the RF section produces it.</summary>
/// <param name="Id">Band number.</param>
/// <param name="StartGHz">Nominal lower edge, GHz.</param>
/// <param name="StopGHz">Nominal upper edge, GHz.</param>
/// <param name="Multiplier">SYTM multiplication factor; 1 for the YO fundamental, 0 for heterodyne band 0.</param>
/// <param name="Description">How the band is generated.</param>
public sealed record Band(
    BandId Id,
    double StartGHz,
    double StopGHz,
    int Multiplier,
    string Description)
{
    /// <summary>True if <paramref name="ghz"/> falls in this band (lower edge inclusive).</summary>
    public bool Contains(double ghz) => ghz >= StartGHz && ghz < StopGHz
                                        || (Id == BandId.Band4 && Math.Abs(ghz - StopGHz) < 1e-9);
}

/// <summary>The band table, and lookup from a frequency.</summary>
public static class Bands
{
    public static readonly IReadOnlyList<Band> All = new[]
    {
        new Band(BandId.Band0, 0.01,  2.3, 0, "Heterodyne (YO mixed with the A8 3.7 GHz oscillator)"),
        new Band(BandId.Band1, 2.3,   7.0, 1, "YIG oscillator (YO) fundamental"),
        new Band(BandId.Band2, 7.0,  13.5, 2, "SYTM x2"),
        new Band(BandId.Band3, 13.5, 20.0, 3, "SYTM x3"),
        new Band(BandId.Band4, 20.0, 26.5, 4, "SYTM x4 (8340B only)"),
    };

    public static Band Get(BandId id) => All.First(b => b.Id == id);

    /// <summary>The band containing <paramref name="ghz"/>, or null if out of range.</summary>
    public static Band? ForFrequency(double ghz) => All.FirstOrDefault(b => b.Contains(ghz));

    /// <summary>
    /// The A24 unleveled SRD-bias pot owning <paramref name="ghz"/>. Each multiplying band is
    /// split in half: band 2 at 10 GHz, band 3 at 15 GHz, band 4 at 23 GHz
    /// (manual 5-14 steps 70-80). Null in bands 0 and 1, which have no SRD bias pot.
    /// </summary>
    public static string? UnleveledPotFor(double ghz) => ForFrequency(ghz)?.Id switch
    {
        BandId.Band2 => ghz < 10.0 ? "X2A (A24R3)" : "X2B (A24R4)",
        BandId.Band3 => ghz < 15.0 ? "X3A (A24R6)" : "X3B (A24R7)",
        BandId.Band4 => ghz < 23.0 ? "X4A (A24R9)" : "X4B (A24R10)",
        _ => null,
    };

    /// <summary>
    /// The A24 leveled SRD-bias pot owning <paramref name="ghz"/> (manual 5-16 steps 30-50).
    /// One pot per multiplying band, no half-band split.
    /// </summary>
    public static string? LeveledPotFor(double ghz) => ForFrequency(ghz)?.Id switch
    {
        BandId.Band2 => "X2C (A24R5)",
        BandId.Band3 => "X3C (A24R8)",
        BandId.Band4 => "X4C (A24R11)",
        _ => null,
    };
}
