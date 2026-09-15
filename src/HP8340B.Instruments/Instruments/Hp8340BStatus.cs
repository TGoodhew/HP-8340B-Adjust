namespace HP8340B.Instruments;

/// <summary>
/// Status byte #1. Manual Table 4-31, p. 4-85. The 8340B predates IEEE 488.2 (no *OPC?),
/// so every settle-wait in this project is built on these bits read by serial poll.
/// </summary>
[Flags]
public enum StatusByte1 : byte
{
    None = 0,
    /// <summary>Bit 0 (1): any front panel key pressed.</summary>
    FrontPanelKeyPressed = 1,
    /// <summary>Bit 1 (2): numeric entry completed (HP-IB or front panel).</summary>
    NumericEntryComplete = 2,
    /// <summary>Bit 2 (4): change in the extended status byte.</summary>
    ExtendedStatusChanged = 4,
    /// <summary>Bit 3 (8): RF settled. The settle-wait after any frequency/level change.</summary>
    RfSettled = 8,
    /// <summary>Bit 4 (16): end of sweep. The wait after TS (take sweep).</summary>
    EndOfSweep = 16,
    /// <summary>Bit 5 (32): HP-IB syntax error — a malformed command was sent.</summary>
    SyntaxError = 32,
    /// <summary>Bit 6 (64): request service (RQS).</summary>
    RequestService = 64,
    /// <summary>Bit 7 (128): new frequencies or sweep time in effect.</summary>
    NewFrequenciesInEffect = 128,
}

/// <summary>
/// Extended status byte #2. Manual Table 4-31, p. 4-85. "(L)" in the manual marks the bits
/// latched until read.
/// </summary>
[Flags]
public enum StatusByte2 : byte
{
    None = 0,
    /// <summary>Bit 0 (1, latched): self test failed.</summary>
    SelfTestFailed = 1,
    /// <summary>Bit 1 (2): over modulation.</summary>
    OverModulation = 2,
    /// <summary>Bit 2 (4): oven cold.</summary>
    OvenCold = 4,
    /// <summary>
    /// Bit 3 (8): external frequency reference selected. This is how <c>probe</c> confirms the
    /// DUT is on the Z3805A 10 MHz rather than its internal standard (M0-11).
    /// </summary>
    ExternalFreqRefSelected = 8,
    /// <summary>Bit 4 (16, latched): RF unlocked.</summary>
    RfUnlocked = 16,
    /// <summary>
    /// Bit 5 (32, latched): power failure.
    /// </summary>
    PowerFailure = 32,
    /// <summary>
    /// Bit 6 (64, latched): RF unleveled — the front-panel UNLEVELED lamp. This is how the
    /// leveled squegging scan finds "max leveled power, just below UNLEVELED" over the bus
    /// (M1-05) instead of by eye.
    /// </summary>
    RfUnleveled = 64,
    /// <summary>Bit 7 (128): fault indicator on.</summary>
    FaultIndicatorOn = 128,
}
