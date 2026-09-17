namespace HP8340B.Instruments;

/// <summary>
/// What the app needs from an oscilloscope, so a different one can be substituted by writing a
/// driver rather than by changing the application.
///
/// <para><b>Why this exists.</b> Somebody else rebuilding this bench will not own this bench. The
/// multimeter role already works this way - <c>IVoltmeter</c>, with the 3458A, 34401A and DM3058
/// behind it - and the scope is the next role with more than one plausible instrument, because
/// there are five on this bench alone. See M0-32 (#89) for the audit this comes out of.</para>
///
/// <para><b>The contract, stated rather than implied.</b> A delegate or a concrete driver carries
/// no promise about units or failure behaviour, and somebody substituting an instrument has to
/// reverse-engineer the expectation from a call site. So, for every implementation:</para>
///
/// <list type="bullet">
/// <item>Sample values are <b>volts at the BNC</b>, with probe ratio, vertical scale and offset
/// already applied. Never raw codes, never divisions.</item>
/// <item>Time is <b>seconds relative to the trigger</b>, which may be negative for pre-trigger
/// samples.</item>
/// <item>Setting methods block until the instrument has accepted the setting. Captures block until
/// a frame is available or the link times out.</item>
/// <item>A failed read throws. It never returns an empty or partial trace, because a short trace
/// reads downstream as a real measurement of a shorter sweep.</item>
/// <item><see cref="Capability"/> is a fact about the model, is safe to read without touching the
/// instrument, and must be sourced from that instrument's manual.</item>
/// </list>
/// </summary>
public interface IOscilloscope : IInstrument
{
    /// <summary>What this model can do. Cheap, and does not touch the bus.</summary>
    ScopeCapability Capability { get; }

    /// <summary>Turns a channel's display on or off.</summary>
    void SetChannelEnabled(int channel, bool on);

    /// <summary>Vertical scale, volts per division.</summary>
    void SetChannelScale(int channel, double voltsPerDivision);

    /// <summary>Vertical offset, volts.</summary>
    void SetChannelOffset(int channel, double volts);

    /// <summary>Input coupling.</summary>
    void SetChannelCoupling(int channel, ScopeCoupling coupling);

    /// <summary>
    /// Input termination.
    ///
    /// <para>Explicit in the interface rather than left to the driver's defaults, because it
    /// changes what the 8473C detector does: loading it into 50 Ω instead of 1 MΩ changes its
    /// sensitivity and its time constant, which invalidates the detector calibration the 0.5 dB
    /// stop rule depends on. See M0-29 (#85).</para>
    /// </summary>
    /// <exception cref="NotSupportedException">
    /// If the model cannot do it - a 1 MΩ-only scope asked for 50 Ω must refuse rather than
    /// silently stay where it was, which would leave the calibration describing a termination that
    /// is not in use.
    /// </exception>
    void SetInputImpedance(int channel, ScopeInputImpedance impedance);

    /// <summary>The termination currently in use, as the instrument reports it.</summary>
    ScopeInputImpedance GetInputImpedance(int channel);

    /// <summary>Probe attenuation ratio. Getting it wrong scales every reading.</summary>
    void SetProbeRatio(int channel, double ratio);

    /// <summary>Horizontal scale, seconds per division.</summary>
    void SetTimebaseScale(double secondsPerDivision);

    /// <summary>Horizontal mode, including the XY display 5-14 is built around.</summary>
    void SetTimebaseMode(TimebaseMode mode);

    /// <summary>External trigger, driven by the 8340B's rear sweep trigger in S4, S5 and S7.</summary>
    void SetExternalTrigger(double levelVolts, bool rising = true);

    /// <summary>
    /// Reads one channel as volts, refreshing whatever scaling information the instrument needs.
    /// The one-shot path.
    /// </summary>
    ScopeWaveform ReadWaveform(int channel);

    /// <summary>
    /// Reads one channel as volts reusing cached scaling - the live-view path, and the one that
    /// decides whether somebody can tune against this scope.
    ///
    /// <para>Implementations must call <see cref="InvalidateWaveformScaling"/> internally from any
    /// setting that changes what a raw sample means. A stale scale factor produces a trace wrong by
    /// a constant, which looks entirely plausible.</para>
    /// </summary>
    ScopeWaveform ReadWaveformStreaming(int channel);

    /// <summary>Discards cached scaling, forcing the next capture to re-read it.</summary>
    void InvalidateWaveformScaling();

    /// <summary>Captures the screen as a PNG, for the session record.</summary>
    byte[] CaptureScreenPng(int maxBytes = 2_000_000);
}
