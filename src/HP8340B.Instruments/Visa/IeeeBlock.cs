using System.Globalization;
using System.Text;

namespace HP8340B.Instruments.Visa;

/// <summary>
/// IEEE 488.2 definite-length block parsing, shared by every instrument that returns binary
/// waveform data.
/// </summary>
public static class IeeeBlock
{
    /// <summary>
    /// Strips the definite-length header (<c>#9000001200</c> style) and returns just the payload.
    ///
    /// <para>The byte count in the header is authoritative. A read buffer is normally asked for
    /// generously, so what comes back is usually longer than the data - a trailing terminator, and
    /// whatever padding the transport adds - and trusting the buffer length instead would append
    /// phantom samples to the end of every trace.</para>
    ///
    /// <para>A header that will not parse, or that claims more than arrived, falls back to "the
    /// rest of the buffer" rather than throwing: a short read is worth reporting as a short trace,
    /// which the caller can then reject.</para>
    /// </summary>
    public static ArraySegment<byte> StripHeader(byte[] response)
    {
        ArgumentNullException.ThrowIfNull(response);

        if (response.Length < 2 || response[0] != (byte)'#' || !char.IsAsciiDigit((char)response[1]))
            return new ArraySegment<byte>(response);

        var headerDigits = response[1] - '0';

        // '#0' is the indefinite-length form: everything after the header is payload.
        if (headerDigits == 0)
            return new ArraySegment<byte>(response, 2, response.Length - 2);

        var start = 2 + headerDigits;

        if (start > response.Length)
            return new ArraySegment<byte>(response);

        var declared = Encoding.ASCII.GetString(response, 2, headerDigits);

        if (!int.TryParse(declared, NumberStyles.Integer, CultureInfo.InvariantCulture, out var length)
            || length < 0 || start + length > response.Length)
            length = response.Length - start;

        return new ArraySegment<byte>(response, start, length);
    }

    /// <summary>
    /// Room for the largest definite-length header (<c>#9</c> plus nine digits) and a terminator,
    /// so one read can ask for a whole block.
    /// </summary>
    public const int OverheadBytes = 16;
}
