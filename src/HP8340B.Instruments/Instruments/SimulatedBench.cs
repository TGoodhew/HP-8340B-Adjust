using HP8340B.Instruments.Config;
using HP8340B.Instruments.Visa;

namespace HP8340B.Instruments;

/// <summary>
/// Builds the whole bench out of simulators so <c>--sim</c> runs every command path with no
/// hardware (repo rule 4). The 8340B model itself — band structure, a max-power curve with a
/// tunable SRD-bias parameter that produces power reversal and spurs when over-biased — is
/// M0-12; this is only enough to make <c>probe</c> and the command builders exercisable.
/// </summary>
public static class SimulatedBench
{
    /// <summary>
    /// A link that answers *IDN? plausibly for <paramref name="model"/> and otherwise stays quiet.
    /// </summary>
    public static SimulatedInstrumentLink LinkFor(string model, string resourceName)
    {
        var isDut = model.Contains("8340B", StringComparison.OrdinalIgnoreCase);

        var link = new SimulatedInstrumentLink(resourceName, command =>
        {
            var c = command.TrimStart();

            if (c.StartsWith("*IDN?", StringComparison.OrdinalIgnoreCase))
                return IdentityFor(model);

            // The 3458A answers ID? with a bare model number.
            if (model.Contains("3458A", StringComparison.OrdinalIgnoreCase)
                && c.StartsWith("ID?", StringComparison.OrdinalIgnoreCase))
                return "HP3458A";

            // The 5351A and the 3458A are talkers: a bare read returns the current measurement,
            // so the responder answers whatever was last written rather than only queries.
            if (model.Contains("5351A", StringComparison.OrdinalIgnoreCase))
                return "10000000000.0";

            if (model.Contains("3458A", StringComparison.OrdinalIgnoreCase))
                return "5.0000E-3";

            // The 8902A is a talker returning watts. 1 mW is 0 dBm — the calibrator reference
            // level, so CalibrateSensor's "is the sensor actually on the calibrator?" guard passes.
            if (model.Contains("8902A", StringComparison.OrdinalIgnoreCase))
                return "1.0E-3";

            // DS1104Z: a preamble and a synthetic detector trace, so the XY view is developable
            // offline. The trace falls away at both ends like a real swept envelope, so a broken
            // minimum-finder cannot pass against a flat line.
            if (model.Contains("DS1104Z", StringComparison.OrdinalIgnoreCase))
            {
                if (c.StartsWith(":WAVeform:PREamble?", StringComparison.OrdinalIgnoreCase))
                    return "2,0,600,1,1e-06,0,0,0.001,0,0";

                if (c.StartsWith(":WAVeform:DATA?", StringComparison.OrdinalIgnoreCase))
                    return string.Join(",", Enumerable.Range(0, 600).Select(i =>
                    {
                        var x = (i - 300) / 300.0;
                        // Negative-going: crystal detectors on this bench are negative polarity.
                        return (-0.5 + 0.4 * x * x).ToString("0.####",
                            System.Globalization.CultureInfo.InvariantCulture);
                    }));
            }

            // OI is the 8340B's identification query — it predates *IDN?. Table 3-2 says 19
            // ASCII characters, so the simulator returns exactly that width.
            if (isDut && c.StartsWith("OI", StringComparison.OrdinalIgnoreCase))
                return "HP8340B SIMULATED  "[..19];

            return string.Empty;
        });

        // Bit 3 = RF settled: a simulated instrument is always settled, so WaitSettled returns
        // immediately rather than burning its timeout in tests.
        link.StatusByte = (byte)StatusByte1.RfSettled;

        if (isDut)
        {
            // Both status bytes for OS (2b). Byte #2 bit 3 reports the external reference as
            // selected, so `probe --sim` exercises the same path a real bench does. RF unleveled
            // is clear: the simulated DUT starts inside its leveled range.
            link.NextBytes =
            [
                (byte)StatusByte1.RfSettled,
                (byte)StatusByte2.ExternalFreqRefSelected,
            ];
        }

        return link;
    }

    private static string IdentityFor(string model)
    {
        // Vendor prefix is cosmetic here; it only has to look like the real thing in probe output.
        var vendor = model.StartsWith("Rigol", StringComparison.OrdinalIgnoreCase) ? "RIGOL TECHNOLOGIES"
                   : model.StartsWith("Keysight", StringComparison.OrdinalIgnoreCase) ? "Keysight Technologies"
                   : model.StartsWith("Agilent", StringComparison.OrdinalIgnoreCase) ? "Agilent Technologies"
                   : "HEWLETT-PACKARD";

        var bare = model.Split(' ').Last();
        return $"{vendor},{bare},SIMULATED,0.0 (--sim)";
    }

    /// <summary>Builds a simulated driver appropriate to <paramref name="config"/>'s model.</summary>
    public static IInstrument Create(InstrumentConfig config)
    {
        var resourceName = config.AddressUnknown ? $"SIM::{config.Role}::INSTR" : config.Address;
        var link = LinkFor(config.Model, resourceName);

        // Carry the configured timeout even in simulation, so a bad value shows up in tests
        // rather than only on the bench.
        if (config.TimeoutMs is { } ms) link.Timeout = TimeSpan.FromMilliseconds(ms);

        // The DUT and the analyzer get their real drivers over simulated links, so their command
        // builders are exercised identically to a hardware run. The DUT's link comes from the
        // physical model in SimulatedSweeper, so its status bytes -- UNLEVELED in particular --
        // mean what they mean on hardware.
        if (config.Role.Equals("dut", StringComparison.OrdinalIgnoreCase))
            return new Hp8340B(new SimulatedSweeper().CreateLink(resourceName));

        if (config.Model.Contains("8563E", StringComparison.OrdinalIgnoreCase))
            return new Hp8563E(new SimulatedAnalyzer().CreateLink(resourceName), config.Role);

        if (config.Model.Contains("5351A", StringComparison.OrdinalIgnoreCase))
            return new Hp5351A(link, config.Role);

        if (config.Model.Contains("3458A", StringComparison.OrdinalIgnoreCase))
            return new Hp3458A(link, config.Role);

        if (config.Model.Contains("DS1104Z", StringComparison.OrdinalIgnoreCase))
            return new RigolDs1104Z(link, config.Role);

        if (config.Model.Contains("8902A", StringComparison.OrdinalIgnoreCase))
            return new Hp8902A(link, config.Role);

        if (config.Model.Contains("11713A", StringComparison.OrdinalIgnoreCase))
            return new Hp11713A(link, config.Role);

        // The 8673B gets its own stateful simulator so read-backs reflect what was set.
        if (config.Model.Contains("8673B", StringComparison.OrdinalIgnoreCase))
            return new Hp8673B(new SimulatedSource().CreateLink(resourceName), config.Role);

        return IsLegacy(config.Model)
            ? new LegacyGpibInstrument(config.Role, config.Model, link, IsListenOnly(config.Model))
            : new ScpiInstrument(config.Role, config.Model, link);
    }

    /// <summary>Instruments that predate IEEE 488.2 and cannot answer *IDN?.</summary>
    public static bool IsLegacy(string model) =>
        new[] { "8902A", "8673B", "11713A", "8116A", "432A", "8340B" }
            .Any(m => model.Contains(m, StringComparison.OrdinalIgnoreCase));

    /// <summary>Instruments that cannot talk on the bus at all.</summary>
    public static bool IsListenOnly(string model) =>
        model.Contains("11713A", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Builds drivers from config, choosing simulated or live links.</summary>
public static class InstrumentFactory
{
    /// <summary>
    /// Creates a driver for <paramref name="config"/>. With <paramref name="simulate"/> true every
    /// instrument is a simulator; otherwise a live VISA session is opened.
    /// </summary>
    public static IInstrument Create(InstrumentConfig config, bool simulate)
    {
        if (simulate) return SimulatedBench.Create(config);

        if (config.AddressUnknown)
            throw new InvalidOperationException(
                $"Instrument '{config.Role}' ({config.Model}) has no address configured. Set it in "
                + "bench.json or bench.local.json, or run with --sim.");

        var link = new VisaInstrumentLink(
            config.Address,
            config.TimeoutMs is { } ms ? TimeSpan.FromMilliseconds(ms) : null);

        if (config.Role.Equals("dut", StringComparison.OrdinalIgnoreCase))
            return new Hp8340B(link);

        if (config.Model.Contains("8563E", StringComparison.OrdinalIgnoreCase))
            return new Hp8563E(link, config.Role);

        if (config.Model.Contains("5351A", StringComparison.OrdinalIgnoreCase))
            return new Hp5351A(link, config.Role);

        if (config.Model.Contains("3458A", StringComparison.OrdinalIgnoreCase))
            return new Hp3458A(link, config.Role);

        if (config.Model.Contains("DS1104Z", StringComparison.OrdinalIgnoreCase))
            return new RigolDs1104Z(link, config.Role);

        if (config.Model.Contains("8902A", StringComparison.OrdinalIgnoreCase))
            return new Hp8902A(link, config.Role);

        if (config.Model.Contains("11713A", StringComparison.OrdinalIgnoreCase))
            return new Hp11713A(link, config.Role);

        if (config.Model.Contains("8673B", StringComparison.OrdinalIgnoreCase))
            return new Hp8673B(link, config.Role);

        return SimulatedBench.IsLegacy(config.Model)
            ? new LegacyGpibInstrument(config.Role, config.Model, link,
                                       SimulatedBench.IsListenOnly(config.Model))
            : new ScpiInstrument(config.Role, config.Model, link);
    }
}
