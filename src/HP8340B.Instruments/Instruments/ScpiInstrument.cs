using HP8340B.Instruments.Visa;

namespace HP8340B.Instruments;

/// <summary>
/// SKELETON stand-in for every IEEE 488.2 / SCPI instrument on this bench that has not had its
/// own driver written yet (8563E, 5351A, 3458A, DS1104Z, 8902A, 8673B, 8116A, DG1032Z, E4438C,
/// 8903B, 3499A, U8485A). It can do exactly one useful thing — *IDN? — which is all
/// <c>probe</c> needs. Each of these is replaced by a real driver in its own M0 issue.
/// </summary>
public sealed class ScpiInstrument : IInstrument
{
    private readonly IInstrumentLink _link;

    public string Role { get; }
    public string Model { get; }
    public IInstrumentLink Link => _link;

    public ScpiInstrument(string role, string model, IInstrumentLink link)
    {
        Role = role;
        Model = model;
        _link = link ?? throw new ArgumentNullException(nameof(link));
    }

    public ProbeResult Probe()
    {
        try
        {
            var identity = _link.Query("*IDN?");
            return new ProbeResult(
                Role, Model, _link.ResourceName,
                Responded: !string.IsNullOrWhiteSpace(identity),
                Identity: identity,
                Detail: "Skeleton SCPI driver — *IDN? only. A real driver is pending its M0 issue.");
        }
        catch (Exception ex)
        {
            return new ProbeResult(Role, Model, _link.ResourceName, Responded: false, Detail: ex.Message);
        }
    }

    public void Dispose() => _link.Dispose();
}

/// <summary>
/// SKELETON stand-in for the pre-488.2 HP-IB instruments that cannot answer *IDN? (the 8902A,
/// 8673B, 11713A, 8116A). A serial poll is the only non-intrusive proof of life.
/// </summary>
public sealed class LegacyGpibInstrument : IInstrument
{
    private readonly IInstrumentLink _link;
    private readonly bool _listenOnly;

    public string Role { get; }
    public string Model { get; }
    public IInstrumentLink Link => _link;

    /// <param name="listenOnly">
    /// True for devices that cannot talk at all (the 11713A). These can never be confirmed over
    /// the bus, so probe reports them as unconfirmed rather than failing the bench.
    /// </param>
    public LegacyGpibInstrument(string role, string model, IInstrumentLink link, bool listenOnly = false)
    {
        Role = role;
        Model = model;
        _link = link ?? throw new ArgumentNullException(nameof(link));
        _listenOnly = listenOnly;
    }

    public ProbeResult Probe()
    {
        try
        {
            var status = _link.SerialPoll();
            return new ProbeResult(
                Role, Model, _link.ResourceName,
                Responded: true,
                Identity: $"status byte = 0x{status:X2}",
                Detail: _listenOnly
                    ? "Listen-only device: it accepts commands but cannot talk, so presence "
                      + "cannot be confirmed over the bus. Confirm at the front panel."
                    : "Skeleton legacy driver — serial poll only. Pre-488.2, so no *IDN?.");
        }
        catch (Exception ex)
        {
            return new ProbeResult(Role, Model, _link.ResourceName, Responded: false, Detail: ex.Message);
        }
    }

    public void Dispose() => _link.Dispose();
}
