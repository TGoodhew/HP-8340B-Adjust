namespace HP8340B.Bench.Model;

/// <summary>Somebody confirming they have made the connections on a hook-up card.</summary>
/// <param name="SetupId">The setup acknowledged.</param>
/// <param name="At">When, UTC.</param>
/// <param name="By">Who, for the session record.</param>
/// <param name="UnverifiedConnectors">
/// True if the card still carried connector names that have never been checked against the
/// instruments. Recorded because it changes how much the acknowledgement is worth.
/// </param>
public sealed record CardAcknowledgement(
    string SetupId,
    DateTime At,
    string By,
    bool UnverifiedConnectors);

/// <summary>
/// The gate a measurement block has to pass before it starts (M0-19 and M0-20).
///
/// <para>Two things have to be true: somebody has said they made the connections, and the
/// instruments agree that they did. Either alone is weak — an acknowledgement is a promise and a
/// self-check without one means nobody looked at the card — so both are required and both are
/// logged.</para>
/// </summary>
public sealed class BlockGate
{
    private readonly List<CardAcknowledgement> _acknowledgements = [];
    private readonly List<SelfCheckOutcome> _outcomes = [];

    /// <summary>
    /// How long an acknowledgement stays good. Cables get moved; an acknowledgement from three
    /// hours ago is not evidence about the bench as it is now.
    /// </summary>
    public TimeSpan AcknowledgementValidFor { get; set; } = TimeSpan.FromHours(2);

    /// <summary>Every acknowledgement, for the session record.</summary>
    public IReadOnlyList<CardAcknowledgement> Acknowledgements => _acknowledgements;

    /// <summary>Every self-check result, for the session record.</summary>
    public IReadOnlyList<SelfCheckOutcome> SelfCheckResults => _outcomes;

    /// <summary>
    /// Records that the card for <paramref name="setup"/> has been read and the connections made.
    /// </summary>
    public CardAcknowledgement Acknowledge(BenchSetup setup, string by)
    {
        ArgumentNullException.ThrowIfNull(setup);

        if (string.IsNullOrWhiteSpace(by))
            throw new ArgumentException(
                "An acknowledgement needs a name. It goes into the session record as evidence "
                + "about who checked the bench, and an anonymous one is not evidence.", nameof(by));

        var acknowledgement = new CardAcknowledgement(
            setup.Id, DateTime.UtcNow, by, setup.HasUnverifiedConnectors);

        _acknowledgements.Add(acknowledgement);
        return acknowledgement;
    }

    /// <summary>Runs and records the self-check for <paramref name="setup"/>.</summary>
    public SelfCheckOutcome RunSelfCheck(BenchSetup setup, SelfCheckContext context)
    {
        var outcome = SelfChecks.Run(setup, context);
        _outcomes.Add(outcome);

        return outcome;
    }

    /// <summary>The most recent acknowledgement for <paramref name="setupId"/> that is still valid.</summary>
    public CardAcknowledgement? CurrentAcknowledgement(string setupId) =>
        _acknowledgements
            .Where(a => a.SetupId.Equals(setupId, StringComparison.OrdinalIgnoreCase))
            .Where(a => DateTime.UtcNow - a.At < AcknowledgementValidFor)
            .OrderByDescending(a => a.At)
            .FirstOrDefault();

    /// <summary>The most recent self-check result for <paramref name="setupId"/>.</summary>
    public SelfCheckOutcome? LastSelfCheck(string setupId) =>
        _outcomes
            .Where(o => o.SetupId.Equals(setupId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(o => o.At)
            .FirstOrDefault();

    /// <summary>
    /// Throws unless the block may start: the card is acknowledged and current, and the
    /// self-check passed.
    ///
    /// <para>A <see cref="SelfCheckStatus.NotRun"/> does not open the gate. That is the point of
    /// having the third state at all — a check that could not run has proven nothing, and
    /// treating it as a pass would hand back exactly the false reassurance the checks exist to
    /// remove.</para>
    /// </summary>
    public void RequireReady(string setupId)
    {
        if (CurrentAcknowledgement(setupId) is null)
        {
            var stale = _acknowledgements.Any(a =>
                a.SetupId.Equals(setupId, StringComparison.OrdinalIgnoreCase));

            throw new InvalidOperationException(
                stale
                    ? $"The hook-up card for {setupId} was acknowledged, but more than "
                      + $"{AcknowledgementValidFor.TotalHours:0.#} hours ago. Cables get moved — "
                      + "read the card again and re-acknowledge."
                    : $"The hook-up card for {setupId} has not been acknowledged. Run "
                      + $"`hp8340b setup show {setupId}`, make the connections, and acknowledge it "
                      + "before starting a block.");
        }

        var check = LastSelfCheck(setupId);

        if (check is null)
            throw new InvalidOperationException(
                $"The wiring self-check for {setupId} has not been run. The card says what to "
                + "connect; the check is what knows whether it was done.");

        if (check.Status != SelfCheckStatus.Passed)
            throw new InvalidOperationException(
                $"The wiring self-check for {setupId} {(check.Status == SelfCheckStatus.Failed ? "FAILED" : "could not run")}. "
                + $"Expected: {check.Expected} Observed: {check.Observed} "
                + "A mis-cabled bench produces plausible-looking wrong numbers, so the block does "
                + "not start.");
    }

    /// <summary>True if the block may start. The non-throwing form of <see cref="RequireReady"/>.</summary>
    public bool IsReady(string setupId)
    {
        try
        {
            RequireReady(setupId);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
