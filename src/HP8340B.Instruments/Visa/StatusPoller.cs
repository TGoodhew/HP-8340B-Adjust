namespace HP8340B.Instruments.Visa;

/// <summary>
/// Waits for a condition on an instrument's status byte.
///
/// <para><b>Polling, not SRQ — and why.</b> Every instrument on this bench that needs a
/// settle-wait exposes the condition in its serial-poll status byte, and each one uses different
/// bits with different meanings. SRQ would need per-instrument mask configuration (the 8340B's
/// `RM` and `RE` codes), a controller-level service-request handler shared across the bus, and
/// careful teardown so a stale handler cannot fire into a later measurement. That is a lot of
/// shared mutable state on a bus this project also hands over to PN.EXE (M0-16).</para>
///
/// <para>Polling is slower but local, stateless and trivially testable against a simulator. At a
/// 20 ms interval it costs at most 20 ms of latency on a measurement that is already taking
/// hundreds of milliseconds, which is not the bottleneck. If a future measurement genuinely needs
/// SRQ latency, it can be added per driver without disturbing this.</para>
///
/// <para>The bits themselves are per instrument, so the predicate is the caller's: this class
/// owns only the waiting.</para>
/// </summary>
public static class StatusPoller
{
    /// <summary>Default gap between polls. See the class remarks on why this is fine.</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMilliseconds(20);

    /// <summary>
    /// How long the most recent wait actually took. Worth reading after a timeout: see the
    /// remarks on <see cref="WaitFor"/> about the one case where it can exceed the budget.
    /// </summary>
    public static TimeSpan LastElapsed { get; private set; }

    /// <summary>
    /// Polls <paramref name="link"/> until <paramref name="isReady"/> accepts the status byte, or
    /// <paramref name="timeout"/> elapses. Returns the accepted status byte, or null on timeout.
    /// </summary>
    /// <param name="abort">
    /// Optional predicate checked on every poll. If it accepts, the wait throws rather than
    /// continuing — used for error bits such as the 8340B's HP-IB syntax error, where waiting out
    /// the full timeout would hide the real fault.
    /// </param>
    /// <param name="abortMessage">Builds the exception message when <paramref name="abort"/> fires.</param>
    /// <remarks>
    /// <para><b>The deadline bounds the whole wait, not just the gaps between polls.</b> That
    /// distinction matters because <see cref="IInstrumentLink.SerialPoll"/> blocks: on a wedged
    /// bus it does not return until the instrument's own VISA timeout expires, and this project
    /// configures 20 s for the DUT and the 8902A. A loop that only checked the clock between
    /// polls would let a 5 s settle-wait run to 25 s — and, worse, report nothing unusual, so the
    /// overrun would show up as a mysteriously slow measurement rather than as a fault.
    /// </para>
    /// <para>So before starting each poll after the first, this checks whether there is time for
    /// one, using how long the last poll actually took. The first poll is always made, because a
    /// caller asking "are you ready now?" deserves an answer; if <i>that</i> one blocks for longer
    /// than the budget the wait does overrun, and nothing short of a cancellable transport can
    /// prevent it. <see cref="LastElapsed"/> records what really happened either way.</para>
    /// <para>Found by the HP-Attenuator session, which hit the same shape of bug: a 30 s budget
    /// that overran to 134 s because the deadline was only enforced between blocking reads.</para>
    /// </remarks>
    public static byte? WaitFor(
        IInstrumentLink link,
        Func<byte, bool> isReady,
        TimeSpan timeout,
        Func<byte, bool>? abort = null,
        Func<byte, string>? abortMessage = null,
        TimeSpan? interval = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(link);
        ArgumentNullException.ThrowIfNull(isReady);

        var gap = interval ?? DefaultInterval;
        var started = DateTime.UtcNow;
        var deadline = started + timeout;
        var longestPoll = TimeSpan.Zero;

        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var pollStarted = DateTime.UtcNow;
                var status = link.SerialPoll();
                var pollTook = DateTime.UtcNow - pollStarted;

                if (pollTook > longestPoll) longestPoll = pollTook;

                if (abort?.Invoke(status) == true)
                    throw new InvalidOperationException(
                        abortMessage?.Invoke(status)
                        ?? $"{link.ResourceName} reported an error condition: status 0x{status:X2}.");

                if (isReady(status)) return status;

                // Check the deadline AFTER polling, so a zero timeout still performs one poll. A
                // simulator is always ready, and a caller asking "are you ready now?" should get
                // an answer rather than an immediate timeout.
                var now = DateTime.UtcNow;
                if (now >= deadline) return null;

                // Do not START a poll that cannot finish inside the budget. Without this the
                // deadline only bounds the gaps between polls, and a blocking SerialPoll on a
                // wedged bus overruns it by a whole instrument timeout.
                if (now + gap + longestPoll > deadline) return null;

                Thread.Sleep(gap);
            }
        }
        finally
        {
            LastElapsed = DateTime.UtcNow - started;
        }
    }
}
