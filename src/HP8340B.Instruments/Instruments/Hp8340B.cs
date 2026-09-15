using System.Globalization;
using HP8340B.Instruments.Visa;

namespace HP8340B.Instruments;

/// <summary>
/// Driver for the HP 8340B Synthesized Sweeper — the DUT.
///
/// SKELETON (M0-03). Only commands whose codes are Verified in <see cref="Hp8340BCommands"/>
/// are implemented; start/stop frequency, sweep time and sweep mode wait on the operating
/// manual's Tables 3-1/3-2. Every write goes through <see cref="Hp8340BCommands.Require"/>,
/// so an unverified code throws rather than reaching the instrument (repo rule 2).
///
/// No method here writes a calibration constant. Cal-constant access is M0-04 and is guarded
/// separately (repo rule 1).
/// </summary>
public sealed class Hp8340B : IInstrument
{
    private readonly IInstrumentLink _link;

    public string Role => "dut";
    public string Model => "HP 8340B";
    public IInstrumentLink Link => _link;

    public Hp8340B(IInstrumentLink link) => _link = link ?? throw new ArgumentNullException(nameof(link));

    /// <summary>Device clear, preset, RF off. Leaves the DUT in a known posture.</summary>
    public void Initialize()
    {
        _link.Clear();
        _link.Write(Hp8340BCommands.Require("IP"));
        _link.Write(Hp8340BCommands.Require("RF0"));
    }

    public void Preset() => _link.Write(Hp8340BCommands.Require("IP"));

    public void SetCwGHz(double ghz) => _link.Write(
        $"{Hp8340BCommands.Require("CW")} " +
        $"{ghz.ToString("0.#########", CultureInfo.InvariantCulture)} " +
        $"{Hp8340BCommands.Require("GZ")}");

    public void SetPowerDbm(double dbm) => _link.Write(
        $"{Hp8340BCommands.Require("PL")} " +
        $"{dbm.ToString("0.###", CultureInfo.InvariantCulture)} " +
        $"{Hp8340BCommands.Require("DB")}");

    public void RfOn() => _link.Write(Hp8340BCommands.Require("RF1"));

    public void RfOff() => _link.Write(Hp8340BCommands.Require("RF0"));

    public void SingleSweep() => _link.Write(Hp8340BCommands.Require("S2"));

    public void TakeSweep() => _link.Write(Hp8340BCommands.Require("TS"));

    /// <summary>Reads status byte #1 by serial poll (Table 4-31).</summary>
    public StatusByte1 ReadStatus() => (StatusByte1)_link.SerialPoll();

    /// <summary>
    /// Waits for <see cref="StatusByte1.RfSettled"/>. This is the 8340B's stand-in for *OPC?.
    ///
    /// SKELETON: polls the status byte. The real implementation (M0-03) must decide between
    /// polling and SRQ, and must clear the mask state the DUT keeps between polls — do not
    /// trust this for timing-critical work until that issue is closed.
    /// </summary>
    public bool WaitSettled(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var status = ReadStatus();

            if (status.HasFlag(StatusByte1.SyntaxError))
                throw new InvalidOperationException(
                    "The 8340B reported an HP-IB syntax error (status byte #1 bit 5). The last "
                    + $"command sent was: {_link.History.LastOrDefault() ?? "(none)"}");

            if (status.HasFlag(StatusByte1.RfSettled)) return true;
            Thread.Sleep(20);
        }
        return false;
    }

    public ProbeResult Probe()
    {
        try
        {
            // The 8340B has no *IDN?. A serial poll is the cheapest proof of life: it needs no
            // command and cannot upset the instrument's state.
            var status = _link.SerialPoll();
            return new ProbeResult(
                Role, Model, _link.ResourceName, Responded: true,
                Identity: $"status byte #1 = 0x{status:X2} ({(StatusByte1)status})",
                // Extended status byte #2 bit 3 carries the external-reference state, but reading
                // byte #2 needs a code from Table 3-2 that is not yet verified (M0-03/M0-11).
                ExternalReference: null,
                Detail: "No *IDN? — this instrument predates IEEE 488.2. External-reference state "
                        + "needs extended status byte #2 bit 3 (Table 4-31), pending M0-03.");
        }
        catch (Exception ex)
        {
            return new ProbeResult(Role, Model, _link.ResourceName, Responded: false, Detail: ex.Message);
        }
    }

    public void Dispose() => _link.Dispose();
}
