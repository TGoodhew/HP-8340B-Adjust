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
    /// Polls <paramref name="link"/> until <paramref name="isReady"/> accepts the status byte, or
    /// <paramref name="timeout"/> elapses. Returns the accepted status byte, or null on timeout.
    /// </summary>
    /// <param name="abort">
    /// Optional predicate checked on every poll. If it accepts, the wait throws rather than
    /// continuing — used for error bits such as the 8340B's HP-IB syntax error, where waiting out
    /// the full timeout would hide the real fault.
    /// </param>
    /// <param name="abortMessage">Builds the exception message when <paramref name="abort"/> fires.</param>
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
        var deadline = DateTime.UtcNow + timeout;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var status = link.SerialPoll();

            if (abort?.Invoke(status) == true)
                throw new InvalidOperationException(
                    abortMessage?.Invoke(status)
                    ?? $"{link.ResourceName} reported an error condition: status 0x{status:X2}.");

            if (isReady(status)) return status;

            // Check the deadline AFTER polling, so a zero timeout still performs one poll. A
            // simulator is always ready, and a caller asking "are you ready now?" should get an
            // answer rather than an immediate timeout.
            if (DateTime.UtcNow >= deadline) return null;

            Thread.Sleep(gap);
        }
    }
}
