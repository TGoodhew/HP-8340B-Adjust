using System.Globalization;
using HP8340B.Instruments.Visa;

namespace HP8340B.Instruments;

/// <summary>
/// A plug-in module found in a 3499A slot.
/// </summary>
/// <param name="Slot">Slot number.</param>
/// <param name="ReportedType">
/// Exactly what the mainframe said, e.g. "GP RELAY 44471". <b>Not necessarily the module's own
/// model number</b> — see <see cref="AmbiguousWith"/>.
/// </param>
/// <param name="Serial">Serial number, as reported. Some modules report none.</param>
/// <param name="AmbiguousWith">
/// The other modules that report the same string. Empty when the report is unambiguous.
/// </param>
public sealed record SwitchModule(
    int Slot,
    string ReportedType,
    string Serial,
    IReadOnlyList<string> AmbiguousWith)
{
    /// <summary>True when the mainframe cannot say which module this actually is.</summary>
    public bool IsAmbiguous => AmbiguousWith.Count > 0;

    /// <summary>
    /// What the mainframe returns for each module, and what it cannot distinguish.
    ///
    /// <para>From the guide's SYSTem:CTYPE? table (p. 154-155) and its footnotes. <b>This matters
    /// more on this bench than it looks.</b> The 44476A and 44476B driver modules that operate the
    /// coaxial switches both report "GP RELAY 44471", as do the 44471A/D and 44477A — footnote b
    /// says in as many words: "you must physically check the modules to determine which one is
    /// present". So the slot map that decision D-11 wants cannot be read off the instrument
    /// alone.</para>
    /// </summary>
    private static readonly Dictionary<string, string[]> Ambiguities = new(StringComparer.OrdinalIgnoreCase)
    {
        ["GP RELAY 44471"] = ["44471A", "44471D", "44476A", "44476B", "44477A"],
        ["VHF SW 44472"] = ["44472A", "44478A", "44478B"],
        ["RELAY MUX 44470"] = ["44470A", "44470D"],
    };

    /// <summary>Builds a module from the mainframe's reported string.</summary>
    public static SwitchModule FromReport(int slot, string reportedType, string serial)
    {
        var ambiguous = Ambiguities
            .Where(kv => reportedType.Contains(kv.Key, StringComparison.OrdinalIgnoreCase))
            .SelectMany(kv => kv.Value)
            .ToList();

        return new SwitchModule(slot, reportedType, serial, ambiguous);
    }

    /// <summary>
    /// Highest frequency the channels on a leg are good for, and whether that figure has a source.
    ///
    /// <para><b>It cannot be inferred from the module, and the manual says why.</b> The 44476A/B
    /// are carrier modules — the specification lists a component area, grid hole spacing and lead
    /// length, because the switches are mounted on them. The guide makes the same point about the
    /// N2276B in as many words: its characteristics "are determined by the switches and
    /// attenuators installed in it". So the limit belongs to the switch, is declared per leg in
    /// the routing data, and this only turns a declared model into a number.</para>
    ///
    /// <para><b>The 33311 figures below have no source in the local manual library.</b> There is
    /// no 33311B or 33311C manual or data sheet here. They are the widely-quoted ratings and they
    /// are very probably right — but "widely quoted" is not a citation, and this number decides
    /// whether a band-4 result may be reported as Spec. Anything unsourced is returned with
    /// <c>Confirmed: false</c> so it can be shown as unconfirmed rather than printed as fact.</para>
    /// </summary>
    public static (double MaxHz, bool Confirmed, string Source) RatingForSwitch(string switchModel)
    {
        ArgumentNullException.ThrowIfNull(switchModel);

        return switchModel switch
        {
            var m when m.Contains("33311C", StringComparison.OrdinalIgnoreCase) =>
                (26.5e9, false, "No 33311C data sheet in the local manual library — widely quoted "
                                + "as DC-26.5 GHz, never checked against a source."),

            var m when m.Contains("33311B", StringComparison.OrdinalIgnoreCase) =>
                (18e9, false, "No 33311B data sheet in the local manual library — widely quoted as "
                              + "DC-18 GHz, never checked against a source."),

            var m when m.Contains("44476A", StringComparison.OrdinalIgnoreCase) =>
                (18e9, true, "3499A/B/C guide, 44476A Microwave Switch Module specifications: "
                             + "\"Frequency Range: DC to 18 GHz\"."),

            var m when m.Contains("44472", StringComparison.OrdinalIgnoreCase) =>
                (100e6, false, "No figure for the 44472A in the local library. The guide gives the "
                               + "44478A/B as 1.3 GHz MUX modules; the 44472A's own rating has not "
                               + "been checked. Only used for DC and audio legs here, where it is "
                               + "not close to mattering."),

            _ => (0, false, "Not a switch this project knows the rating of."),
        };
    }

    /// <summary>
    /// Highest frequency the channels on a leg are good for. See
    /// <see cref="RatingForSwitch"/> for where each figure comes from and which are unsourced.
    /// </summary>
    public static double MaxHzForSwitch(string switchModel) => RatingForSwitch(switchModel).MaxHz;
}

/// <summary>
/// Driver for the Agilent 3499A Switch/Control mainframe.
///
/// <para>Source: <b>Agilent 3499A/B/C User's Guide and Programming Reference</b>
/// (`3499A User &amp; Programming.pdf` in the local manual library). SCPI, with the command
/// summary on pp. 118 onward. Channel numbers are <c>snn</c> — slot then two digits — and channel
/// lists are written <c>(@101)</c> or <c>(@101:112)</c>.</para>
///
/// <para><b>Never hot-switch.</b> Coaxial relays switched with RF flowing arc, and the damage is
/// cumulative and invisible until the insertion loss has drifted. Repo rule 5 requires the caller
/// to turn the RF off, change state, confirm the readback, and only then turn it back on. Setting
/// <see cref="RfIsOff"/> makes that structural rather than advisory: with it wired, every open and
/// close refuses while the RF is on.</para>
/// </summary>
public sealed class Agilent3499A : IInstrument
{
    private readonly IInstrumentLink _link;

    public string Role { get; }
    public string Model => "Agilent 3499A";
    public IInstrumentLink Link => _link;

    /// <summary>
    /// Asked before every switch operation. Returning false refuses the operation.
    ///
    /// <para>Wire this to the DUT's RF state — <c>() =&gt; !dutRfIsOn</c> — and hot-switching
    /// becomes impossible rather than merely discouraged. Left null, the driver cannot tell and
    /// switches anyway, which is why <see cref="Probe"/> says so.</para>
    /// </summary>
    public Func<bool>? RfIsOff { get; set; }

    public Agilent3499A(IInstrumentLink link, string role = "switch-matrix")
    {
        _link = link ?? throw new ArgumentNullException(nameof(link));
        Role = role;
    }

    /// <summary>Formats a channel list the way the guide writes it: <c>(@101,203)</c>.</summary>
    public static string ChannelList(params int[] channels)
    {
        ArgumentNullException.ThrowIfNull(channels);

        if (channels.Length == 0)
            throw new ArgumentException("At least one channel.", nameof(channels));

        foreach (var channel in channels) ValidateChannel(channel);

        return "(@" + string.Join(",", channels.Select(c => c.ToString(CultureInfo.InvariantCulture))) + ")";
    }

    /// <summary>Slots on a 3499A mainframe. The guide: "3499A slots 0 through 5".</summary>
    public const int MinSlot = 0;
    public const int MaxSlot = 5;

    /// <summary>
    /// Checks a channel number is the guide's <c>snn</c> shape — a slot then a two-digit channel.
    ///
    /// <para>Slot 0 is valid on a 3499A, so channel 012 is a real channel and the range starts at
    /// 0, not 100. The B and C mainframes have different slot counts (0-2 and 0-9); this driver
    /// is for the A.</para>
    /// </summary>
    public static void ValidateChannel(int channel)
    {
        var slot = channel / 100;

        if (channel < 0 || slot > MaxSlot)
            throw new ArgumentOutOfRangeException(
                nameof(channel), channel,
                $"3499A channel numbers are snn — slot {MinSlot}-{MaxSlot} then a two-digit "
                + "channel, e.g. 101. See the guide: \"The channel number is in the form of snn, "
                + "where s is the slot number and nn is the channel number\", and \"3499A slots 0 "
                + "through 5\".");
    }

    private void GuardHotSwitch(string what)
    {
        if (RfIsOff is null) return;

        if (!RfIsOff())
            throw new InvalidOperationException(
                $"Refusing to {what} while the RF is on. Repo rule 5: coaxial relays switched hot "
                + "arc, and the damage accumulates invisibly until the insertion loss has drifted. "
                + "Turn the DUT's RF output off, switch, confirm the readback, then turn it back on.");
    }

    // --- Module identification ------------------------------------------------------------------

    /// <summary>The guide's reported string for an empty slot: "NO CARD 00000".</summary>
    public const string EmptySlotReport = "NO CARD";

    /// <summary>
    /// Identifies the module in <paramref name="slot"/> — <c>SYSTem:CTYPE? &lt;slot&gt;</c>,
    /// which "returns a string containing the module identification in the specified slot".
    ///
    /// <para>Returns null for an empty slot, which the guide reports as "NO CARD 00000".</para>
    ///
    /// <para><b>The answer may not identify the module.</b> See
    /// <see cref="SwitchModule.IsAmbiguous"/> — the driver modules on this bench all report the
    /// same string.</para>
    /// </summary>
    public SwitchModule? IdentifySlot(int slot)
    {
        if (slot is < MinSlot or > MaxSlot)
            throw new ArgumentOutOfRangeException(
                nameof(slot), slot, $"A 3499A has slots {MinSlot} through {MaxSlot}.");

        var response = _link.Query($"SYSTem:CTYPE? {slot}").Trim();

        if (string.IsNullOrWhiteSpace(response)) return null;

        if (response.StartsWith(EmptySlotReport, StringComparison.OrdinalIgnoreCase)) return null;

        // The guide's table gives the form "<description> <model>, <serial>"; a few modules
        // report no serial at all.
        var fields = response.Split(',', StringSplitOptions.TrimEntries);
        var type = fields[0];
        var serial = fields.Length > 1 ? fields[1] : string.Empty;

        return SwitchModule.FromReport(slot, type, serial);
    }

    /// <summary>
    /// Identifies every populated slot.
    ///
    /// <para>Feeds decision D-11, but does not settle it: the modules this bench uses to drive
    /// the coaxial switches cannot be told apart over the bus. See
    /// <see cref="SlotMapNeedsPhysicalCheck"/>.</para>
    /// </summary>
    public IReadOnlyList<SwitchModule> IdentifyAllSlots() =>
        Enumerable.Range(MinSlot, MaxSlot - MinSlot + 1)
            .Select(IdentifySlot)
            .OfType<SwitchModule>()
            .ToList();

    /// <summary>
    /// The slots whose module the mainframe cannot identify, and what each could be.
    ///
    /// <para>These have to be read off the modules themselves. The guide is explicit about it —
    /// "you must physically check the modules to determine which one is present" — so decision
    /// D-11 cannot be closed from the bus alone however thoroughly it is queried.</para>
    /// </summary>
    public IReadOnlyList<SwitchModule> SlotMapNeedsPhysicalCheck() =>
        IdentifyAllSlots().Where(m => m.IsAmbiguous).ToList();

    // --- Switching ------------------------------------------------------------------------------

    /// <summary>Closes channels — <c>ROUTe:CLOSe</c>. Refuses while the RF is on.</summary>
    public void Close(params int[] channels)
    {
        GuardHotSwitch("close a relay");
        _link.Write($"ROUTe:CLOSe {ChannelList(channels)}");
    }

    /// <summary>Opens channels — <c>ROUTe:OPEN</c>. Refuses while the RF is on.</summary>
    public void Open(params int[] channels)
    {
        GuardHotSwitch("open a relay");
        _link.Write($"ROUTe:OPEN {ChannelList(channels)}");
    }

    /// <summary>Opens everything — <c>ROUTe:OPEN ALL</c>.</summary>
    public void OpenAll()
    {
        GuardHotSwitch("open all relays");
        _link.Write("ROUTe:OPEN ALL");
    }

    /// <summary>
    /// True for each channel the mainframe reports closed — <c>ROUTe:CLOSe?</c>, which
    /// "Queries relay closed state". The guide returns comma-separated values in the same order
    /// as the query.
    /// </summary>
    public IReadOnlyList<bool> IsClosed(params int[] channels)
    {
        var response = _link.Query($"ROUTe:CLOSe? {ChannelList(channels)}").Trim();

        var states = response
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(v => v == "1")
            .ToList();

        if (states.Count != channels.Length)
            throw new InvalidOperationException(
                $"Asked the 3499A about {channels.Length} channel(s) and got {states.Count} "
                + $"answer(s): '{response}'.");

        return states;
    }

    /// <summary>
    /// Closes <paramref name="channels"/> and confirms the mainframe agrees, throwing if it does
    /// not.
    ///
    /// <para>The issue's requirement, and worth being strict about: a relay that did not actually
    /// move produces a measurement through the wrong path, which looks like a perfectly plausible
    /// wrong number rather than a failure.</para>
    /// </summary>
    public void CloseVerified(params int[] channels)
    {
        Close(channels);

        var states = IsClosed(channels);
        var failed = channels.Where((_, i) => !states[i]).ToList();

        if (failed.Count > 0)
            throw new InvalidOperationException(
                $"Closed {string.Join(", ", channels)} but the 3499A reports "
                + $"{string.Join(", ", failed)} still open. A relay that did not move sends the "
                + "measurement down the wrong path, which looks like a plausible wrong number "
                + "rather than a failure. Check the module and the channel numbering.");
    }

    // --- Relay wear ------------------------------------------------------------------------------

    /// <summary>
    /// Relay operation counts — <c>DIAGnostic:RELay:CYCLes?</c>. Logged with every routed
    /// measurement because coaxial relays wear out, and a leg whose count is climbing fast is
    /// worth knowing about before its loss drifts.
    /// </summary>
    public IReadOnlyList<long> RelayCycles(params int[] channels)
    {
        var response = _link.Query($"DIAGnostic:RELay:CYCLes? {ChannelList(channels)}").Trim();

        return response
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(v => long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
                ? n
                : throw new InvalidOperationException($"3499A returned '{v}' for a cycle count."))
            .ToList();
    }

    /// <summary>Highest cycle count on a module — <c>DIAGnostic:RELay:CYCLes:MAX? &lt;slot&gt;</c>.</summary>
    public long MaxRelayCycles(int slot)
    {
        var response = _link.Query($"DIAGnostic:RELay:CYCLes:MAX? {slot}").Trim();

        return long.TryParse(response, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
            ? n
            : throw new InvalidOperationException($"3499A returned '{response}' for a cycle count.");
    }

    // --- Safe state --------------------------------------------------------------------------------

    /// <summary>
    /// The state the bench should sit in when nothing is being measured: every relay open, so no
    /// leg is connected to anything and the DVM multiplexer is not across a test point.
    ///
    /// <para><c>SYSTem:CPON ALL</c> returns every module to its power-on state, which the guide
    /// defines per module; <c>ROUTe:OPEN ALL</c> then makes the intent explicit rather than
    /// depending on what each module's power-on state happens to be.</para>
    /// </summary>
    public void ApplySafeState()
    {
        GuardHotSwitch("apply the safe state");

        _link.Write("*RST");
        _link.Write("SYSTem:CPON ALL");
        _link.Write("ROUTe:OPEN ALL");
    }

    /// <summary>Reads the mainframe's error queue — <c>SYSTem:ERRor?</c>.</summary>
    public string ReadError() => _link.Query("SYSTem:ERRor?").Trim();

    public ProbeResult Probe()
    {
        try
        {
            var identity = _link.Query("*IDN?").Trim();
            var modules = IdentifyAllSlots();

            var detail = modules.Count == 0
                ? "No modules identified. The slot map is decision D-11 and has to come from the "
                  + "instrument, not from assumption."
                : "Slots: " + string.Join("; ", modules.Select(m => $"{m.Slot}={m.ReportedType}")) + ".";

            var ambiguous = modules.Where(m => m.IsAmbiguous).ToList();

            if (ambiguous.Count > 0)
                detail += " NOTE: slot(s) "
                          + string.Join(", ", ambiguous.Select(m => m.Slot))
                          + " cannot be identified over the bus — the guide's own footnote says "
                          + "\"you must physically check the modules to determine which one is "
                          + "present\". D-11 cannot be closed from the bus alone.";

            if (RfIsOff is null)
                detail += " Hot-switch guard is NOT wired: this driver cannot tell whether the RF "
                          + "is on, so it will switch regardless. Set RfIsOff before a routed run.";

            return new ProbeResult(
                Role, Model, _link.ResourceName,
                Responded: !string.IsNullOrWhiteSpace(identity),
                Identity: identity,
                ExternalReference: null,
                Detail: detail);
        }
        catch (Exception ex)
        {
            return new ProbeResult(Role, Model, _link.ResourceName, Responded: false, Detail: ex.Message);
        }
    }

    public void Dispose() => _link.Dispose();
}

/// <summary>
/// A simulated 3499A with a declarable slot map, so the routing model and the hook-up cards are
/// developable before D-11 is answered.
/// </summary>
public sealed class SimulatedSwitchMatrix
{
    private readonly Dictionary<int, bool> _closed = [];
    private readonly Dictionary<int, long> _cycles = [];

    /// <summary>
    /// Modules by slot. Defaults to a plausible build of what this bench is said to have: two
    /// 33311C on 44476 driver modules, a third 44476 for the 33311Bs, and a 44472A VHF
    /// multiplexer. <b>Not the real map</b> — that is D-11 and has to be read off the instrument.
    /// </summary>
    public Dictionary<int, (string Report, string Serial)> Slots { get; } = new()
    {
        // These are the strings the guide says the mainframe returns, not the module numbers --
        // which is the whole point: a 44476A and a 44476B are indistinguishable here.
        [1] = ("GP RELAY 44471", "SIM00001"),
        [2] = ("GP RELAY 44471", "SIM00002"),
        [3] = ("VHF SW 44472", "SIM00003"),
    };

    /// <summary>Which channels the model reports closed.</summary>
    public IReadOnlyDictionary<int, bool> Closed => _closed;

    /// <summary>
    /// Channels that refuse to close, so the readback check can be tested against a relay that
    /// did not move.
    /// </summary>
    public HashSet<int> StuckOpen { get; } = [];

    public SimulatedInstrumentLink CreateLink(string resourceName = "SIM::switch-matrix::INSTR")
    {
        SimulatedInstrumentLink? link = null;

        link = new SimulatedInstrumentLink(resourceName, command => Respond(command, link!));
        return link;
    }

    private string Respond(string command, SimulatedInstrumentLink link)
    {
        var c = command.Trim();

        if (c.StartsWith("*IDN?", StringComparison.OrdinalIgnoreCase))
            return "Agilent Technologies,3499A,SIMULATED,1.0";

        if (c.StartsWith("SYSTem:ERRor?", StringComparison.OrdinalIgnoreCase))
            return "+0,\"No error\"";

        if (c.StartsWith("SYSTem:CTYPe?", StringComparison.OrdinalIgnoreCase))
        {
            var slot = int.Parse(c.Split(' ')[^1], CultureInfo.InvariantCulture);

            return Slots.TryGetValue(slot, out var module)
                ? $"{module.Report}, {module.Serial}"
                : "NO CARD 00000";
        }

        // Apply whatever the last write asked for, then answer queries from the model's state.
        ApplyWrites(link);

        if (c.StartsWith("ROUTe:CLOSe?", StringComparison.OrdinalIgnoreCase))
            return string.Join(",", ChannelsIn(c).Select(ch => _closed.GetValueOrDefault(ch) ? "1" : "0"));

        if (c.StartsWith("ROUTe:OPEN?", StringComparison.OrdinalIgnoreCase))
            return string.Join(",", ChannelsIn(c).Select(ch => _closed.GetValueOrDefault(ch) ? "0" : "1"));

        if (c.StartsWith("DIAGnostic:RELay:CYCLes:MAX?", StringComparison.OrdinalIgnoreCase))
        {
            var slot = int.Parse(c.Split(' ')[^1], CultureInfo.InvariantCulture);
            var onSlot = _cycles.Where(kv => kv.Key / 100 == slot).Select(kv => kv.Value).ToList();

            return (onSlot.Count > 0 ? onSlot.Max() : 0).ToString(CultureInfo.InvariantCulture);
        }

        if (c.StartsWith("DIAGnostic:RELay:CYCLes?", StringComparison.OrdinalIgnoreCase))
            return string.Join(",", ChannelsIn(c).Select(ch => _cycles.GetValueOrDefault(ch)));

        return string.Empty;
    }

    private int _applied;

    /// <summary>
    /// Replays writes the link has recorded since last time, so the model tracks state that
    /// arrives by Write rather than Query.
    /// </summary>
    private void ApplyWrites(SimulatedInstrumentLink link)
    {
        for (; _applied < link.History.Count; _applied++)
        {
            var c = link.History[_applied].Trim();

            if (c.Equals("ROUTe:OPEN ALL", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var key in _closed.Keys.ToList()) _closed[key] = false;
                continue;
            }

            if (c.StartsWith("ROUTe:CLOSe ", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var channel in ChannelsIn(c))
                {
                    if (StuckOpen.Contains(channel)) continue;

                    _closed[channel] = true;
                    _cycles[channel] = _cycles.GetValueOrDefault(channel) + 1;
                }

                continue;
            }

            if (c.StartsWith("ROUTe:OPEN ", StringComparison.OrdinalIgnoreCase))
                foreach (var channel in ChannelsIn(c))
                {
                    if (_closed.GetValueOrDefault(channel)) _cycles[channel] = _cycles.GetValueOrDefault(channel) + 1;
                    _closed[channel] = false;
                }
        }
    }

    private static IEnumerable<int> ChannelsIn(string command)
    {
        var start = command.IndexOf("(@", StringComparison.Ordinal);
        if (start < 0) return [];

        var end = command.IndexOf(')', start);
        if (end < 0) return [];

        return command[(start + 2)..end]
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(v => int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : -1)
            .Where(n => n > 0);
    }
}
