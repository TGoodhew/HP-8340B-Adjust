using HP8340B.Instruments.Visa;

namespace HP8340B.Instruments;

/// <summary>Outcome of asking one instrument whether it is there and healthy.</summary>
/// <param name="Role">Config role, e.g. "dut", "spectrum-analyzer".</param>
/// <param name="Model">Model as configured, e.g. "HP 8563E".</param>
/// <param name="Address">VISA resource string actually used.</param>
/// <param name="Responded">True if the instrument answered.</param>
/// <param name="Identity">What it said when asked to identify itself, if anything.</param>
/// <param name="ExternalReference">
/// Locked to the external 10 MHz where the instrument can report it; null where it cannot.
/// </param>
/// <param name="Detail">Error text, or a note such as "listen-only, cannot confirm".</param>
/// <param name="ReferenceApplies">
/// True for instruments whose 10 MHz reference is actually in play — the DUT, the analyzer, the
/// counter, the LO sources. False for a scope, a DMM or a switch driver, which have no reference
/// worth reporting.
///
/// <para>Without this, a null <see cref="ExternalReference"/> means two different things — "I
/// cannot tell you" and "the question does not apply" — and `probe` ends up warning about the
/// reference state of a power meter, which buries the one instrument that genuinely cannot
/// answer and needs a front-panel check.</para>
/// </param>
public sealed record ProbeResult(
    string Role,
    string Model,
    string Address,
    bool Responded,
    string? Identity = null,
    bool? ExternalReference = null,
    string? Detail = null,
    bool ReferenceApplies = false)
{
    /// <summary>
    /// True when the reference matters for this instrument but it could not be read back. These
    /// are the ones worth a front-panel check.
    /// </summary>
    public bool ReferenceUnconfirmed => ReferenceApplies && ExternalReference is null;
}

/// <summary>
/// Every instrument driver in this project, real or simulated, answers to this. The
/// <c>probe</c> command (M0-11) walks the configured bench calling <see cref="Probe"/>.
/// </summary>
public interface IInstrument : IDisposable
{
    /// <summary>Config role this driver was built for.</summary>
    string Role { get; }

    /// <summary>Model designation, e.g. "HP 8563E".</summary>
    string Model { get; }

    /// <summary>Underlying transport — real VISA or the simulator.</summary>
    IInstrumentLink Link { get; }

    /// <summary>Asks the instrument whether it is present and, where it can, healthy.</summary>
    ProbeResult Probe();
}
