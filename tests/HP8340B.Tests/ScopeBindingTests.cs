using HP8340B.Instruments;
using HP8340B.Instruments.Model;
using HP8340B.Instruments.Visa;
using HP8340B.Measurements.Detector;
using Xunit;

namespace HP8340B.Tests;

/// <summary>
/// M0-29: a detector calibration has to record which scope and which termination it was taken on.
///
/// <para>The calibration maps detector volts to dB, and that is what makes 5-14's "back off until
/// the power is reduced approximately 0.5 dB" a measurement rather than an estimate. The load the
/// detector drives sets that mapping, and the load is now a config choice — five scopes, three of
/// them with selectable termination. Applying a 1 MΩ fit at 50 Ω is wrong by tens of dB and still
/// produces a curve that looks like a detector curve.</para>
/// </summary>
public class ScopeBindingTests
{
    private static ScopeBinding Binding(
        string model = "Rigol DS1104Z",
        int channel = 1,
        ScopeInputImpedance termination = ScopeInputImpedance.OneMegaohm,
        double? external = null) =>
        new(model, channel, termination, external);

    private static DetectorCalibration Calibrate(ScopeBinding scope) =>
        DetectorCalibrator.Calibrate(
            Detectors.Hp8470B, scope, BandId.Band2, 8e9,
            DetectorCalibrator.DefaultLevelsDbm,
            dbm => 0.5 * Math.Pow(10, dbm / 10.0) / 1000.0);

    // --- What the detector actually drives ---------------------------------------------------------

    [Fact]
    public void AFeedthroughDominatesTheScopeInputCompletely()
    {
        // A 1 Mohm input with a 50 ohm feedthrough on it is a 50 ohm load, not a 1 Mohm one. So the
        // S4 note about adding the 10100C is not a small change to the detector's working
        // conditions — it is a different measurement.
        var bare = Binding(termination: ScopeInputImpedance.OneMegaohm);
        var terminated = Binding(termination: ScopeInputImpedance.OneMegaohm, external: 50);

        Assert.Equal(1e6, bare.VideoLoadOhms, 3);
        Assert.Equal(50.0, terminated.VideoLoadOhms, 2);
    }

    [Fact]
    public void AFiftyOhmScopeInputIsTheSameLoadAsAFeedthrough()
    {
        var native = Binding(model: "Tektronix DPO3034", termination: ScopeInputImpedance.FiftyOhm);
        var external = Binding(termination: ScopeInputImpedance.OneMegaohm, external: 50);

        // Not bit-identical: the megohm sitting in parallel shaves 2.5 milliohms off the
        // feedthrough, giving 49.9975 rather than 50. That is 0.005%, which is four orders of
        // magnitude below anything this measurement can resolve -- so the two are the same load in
        // every sense that matters, and AppliesTo's 1% tolerance treats them as interchangeable.
        Assert.Equal(native.VideoLoadOhms, external.VideoLoadOhms, 2);

        Assert.True(
            Math.Abs(native.VideoLoadOhms - external.VideoLoadOhms) / native.VideoLoadOhms < 1e-4,
            "A 50 ohm feedthrough and a native 50 ohm input should be indistinguishable here.");
    }

    [Fact]
    public void TheBindingIsReadOffTheInstrumentRatherThanAsserted()
    {
        // What gets recorded should be what the scope reports, not what the operator believes is
        // set.
        using var scope = new RigolDs1104Z(new SimulatedInstrumentLink("SIM::s::INSTR"));

        var binding = ScopeBinding.From(scope, 2);

        Assert.Equal("Rigol DS1104Z", binding.ScopeModel);
        Assert.Equal(2, binding.Channel);
        Assert.Equal(ScopeInputImpedance.OneMegaohm, binding.Termination);
    }

    [Fact]
    public void ADescriptionNamesTheScopeChannelAndLoad()
    {
        var line = Binding(channel: 3, external: 50).Describe();

        Assert.Contains("Rigol DS1104Z", line);
        Assert.Contains("CH3", line);
        Assert.Contains("external load", line);
    }

    // --- Refusing a calibration that does not apply ---------------------------------------------------

    [Fact]
    public void ACalibrationThatDoesNotKnowItsScopeIsRefusedRatherThanAssumedToApply()
    {
        // Silence is not evidence of a match. A calibration predating M0-29 cannot answer the
        // question, so it must not be allowed to answer it optimistically.
        var old = DetectorCalibrator.Calibrate(
            Detectors.Hp8470B, 1e6, BandId.Band2, 8e9,
            DetectorCalibrator.DefaultLevelsDbm,
            dbm => 0.5 * Math.Pow(10, dbm / 10.0) / 1000.0);

        Assert.Null(old.Scope);
        Assert.False(old.AppliesTo(Detectors.Hp8470B, Binding(), BandId.Band2));
        Assert.Contains("does not record which scope", old.ExplainMismatch(
            Detectors.Hp8470B, Binding(), BandId.Band2));
    }

    [Fact]
    public void TheSameScopeChannelAndTerminationApplies()
    {
        var calibration = Calibrate(Binding());

        Assert.True(calibration.AppliesTo(Detectors.Hp8470B, Binding(), BandId.Band2));
        Assert.Equal("It does apply.",
            calibration.ExplainMismatch(Detectors.Hp8470B, Binding(), BandId.Band2));
    }

    [Fact]
    public void ChangingTheTerminationInvalidatesTheCalibration()
    {
        // The case that motivated the issue: flip the scope input and the old fit is wrong by tens
        // of dB everywhere, while still looking like a detector curve.
        var calibration = Calibrate(Binding());

        var moved = Binding(termination: ScopeInputImpedance.FiftyOhm);

        Assert.False(calibration.AppliesTo(Detectors.Hp8470B, moved, BandId.Band2));

        var why = calibration.ExplainMismatch(Detectors.Hp8470B, moved, BandId.Band2);
        Assert.Contains("now into 50", why);
    }

    [Fact]
    public void FittingAFeedthroughInvalidatesItToo()
    {
        var calibration = Calibrate(Binding());

        Assert.False(calibration.AppliesTo(
            Detectors.Hp8470B, Binding(external: 50), BandId.Band2));
    }

    [Fact]
    public void UsingADifferentScopeInvalidatesIt()
    {
        var calibration = Calibrate(Binding());

        var other = Binding(model: "Tektronix DPO3034");

        Assert.False(calibration.AppliesTo(Detectors.Hp8470B, other, BandId.Band2));
        Assert.Contains("now on the Tektronix DPO3034",
            calibration.ExplainMismatch(Detectors.Hp8470B, other, BandId.Band2));
    }

    [Fact]
    public void MovingToAnotherChannelInvalidatesIt()
    {
        var calibration = Calibrate(Binding(channel: 1));

        Assert.False(calibration.AppliesTo(Detectors.Hp8470B, Binding(channel: 2), BandId.Band2));
    }

    [Fact]
    public void EverythingWrongIsNamedAtOnce()
    {
        var calibration = Calibrate(Binding());

        var why = calibration.ExplainMismatch(
            Detectors.Hp8473C,
            Binding(model: "Tektronix DPO3034", channel: 4, termination: ScopeInputImpedance.FiftyOhm),
            BandId.Band4);

        Assert.Contains("DPO3034", why);
        Assert.Contains("CH4", why);
        Assert.Contains("50", why);
        Assert.Contains("band 4", why);
    }

    // --- The load and the scope cannot drift apart -------------------------------------------------

    [Fact]
    public void TheCalibrationTakesItsLoadFromTheBinding()
    {
        // Passing the scope and the load separately would let them disagree, and a calibration
        // recording 1 Mohm while the detector drove 50 ohm is worse than one recording nothing: it
        // carries the authority of a stated figure.
        var calibration = Calibrate(Binding(termination: ScopeInputImpedance.FiftyOhm));

        Assert.Equal(50.0, calibration.VideoLoadOhms, 2);
        Assert.Equal(ScopeInputImpedance.FiftyOhm, calibration.Scope!.Termination);
    }

    [Fact]
    public void AFiftyOhmCalibrationCarriesTheSquareLawWarning()
    {
        // The existing setup check already knows a 50 ohm load costs about 29 dB against the
        // detector's 1.3 kohm output impedance and voids the square-law spec. Binding the scope in
        // makes that fire from a termination setting rather than from a number somebody typed.
        var calibration = Calibrate(Binding(termination: ScopeInputImpedance.FiftyOhm));

        Assert.Contains(calibration.Warnings, w => w.Contains("square-law"));
        Assert.Contains(calibration.Warnings, w => w.Contains("sensitivity is specified into"));
    }

    [Fact]
    public void AMegohmCalibrationIsCleanOfLoadWarnings()
    {
        var calibration = Calibrate(Binding());

        Assert.DoesNotContain(calibration.Warnings, w => w.Contains("square-law"));
    }
}
