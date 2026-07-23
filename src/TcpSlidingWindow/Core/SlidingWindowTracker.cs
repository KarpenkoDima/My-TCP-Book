namespace TcpSlidingWindow.Core;

/// <summary>
/// Outcome of feeding one inbound segment to the receiver side.
/// </summary>
public enum SegmentAcceptance
{
    /// <summary>Segment starts exactly at RCV.NXT — accepted, window slides forward.</summary>
    InOrder,

    /// <summary>Segment is entirely below RCV.NXT — a duplicate/retransmit, already delivered.</summary>
    Duplicate,

    /// <summary>Segment starts ahead of RCV.NXT — out of order, must be buffered by the caller.</summary>
    OutOfOrder,

    /// <summary>Segment falls outside the advertised receive window entirely — dropped per RFC 9293 §3.4.</summary>
    OutsideWindow
}

/// <summary>
/// Immutable snapshot of one side's window state, cheap to copy and print.
/// A `readonly struct` here (unlike <see cref="TcpSegment"/>) is intentional:
/// this carries no Span, has connection lifetime rather than per-packet
/// lifetime, and is handed back to callers (e.g. for logging) who may want
/// to hold onto it — a ref struct would forbid that.
/// </summary>
public readonly record struct WindowSnapshot(
    uint SndUna,
    uint SndNxt,
    ushort SndWnd,
    uint RcvNxt,
    ushort RcvWnd)
{
    /// <summary>Bytes sent but not yet acknowledged.</summary>
    public uint BytesInFlight => SequenceMath.Distance(SndUna, SndNxt);

    /// <summary>Remaining room in the receiver's advertised window (RFC 9293 §3.3.1 "usable window").</summary>
    public uint UsableWindow => SndWnd - BytesInFlight > SndWnd ? 0 : SndWnd - (uint)BytesInFlight;
}

/// <summary>
/// Tracks one TCP connection's sliding window on both the send and receive
/// side. This is deliberately a plain mutable class, not a struct: the
/// whole point is a single long-lived, mutated-in-place state machine per
/// connection (mirroring a real TCB / Transmission Control Block per
/// RFC 9293 §3.3), which is exactly the shape a struct fights against
/// (copies, boxing when stored polymorphically, defensive-copy bugs).
/// </summary>
public sealed class SlidingWindowTracker
{
    /// <summary>Oldest unacknowledged byte we have sent (SND.UNA).</summary>
    public uint SndUna { get; private set; }

    /// <summary>Next sequence number we will send (SND.NXT).</summary>
    public uint SndNxt { get; private set; }

    /// <summary>Peer's last-advertised receive window (SND.WND).</summary>
    public ushort SndWnd { get; private set; }

    /// <summary>Next in-order sequence number we expect to receive (RCV.NXT).</summary>
    public uint RcvNxt { get; private set; }

    /// <summary>Window we advertise to the peer (RCV.WND).</summary>
    public ushort RcvWnd { get; private set; }

    public SlidingWindowTracker(uint initialSendSeq, uint initialRecvSeq, ushort peerWindow, ushort ourWindow)
    {
        SndUna = initialSendSeq;
        SndNxt = initialSendSeq;
        SndWnd = peerWindow;
        RcvNxt = initialRecvSeq;
        RcvWnd = ourWindow;
    }

    public WindowSnapshot Snapshot() => new(SndUna, SndNxt, SndWnd, RcvNxt, RcvWnd);

    /// <summary>
    /// Records that we transmitted <paramref name="length"/> payload bytes.
    /// Caller is responsible for having checked <see cref="WindowSnapshot.UsableWindow"/>
    /// first — this method only advances state, it does not enforce the limit,
    /// mirroring how a real sender's congestion/flow-control check happens
    /// *before* the segment is built, not inside the bookkeeping step.
    /// </summary>
    public void OnSegmentSent(int length)
    {
        if (length < 0) throw new ArgumentOutOfRangeException(nameof(length));
        SndNxt = unchecked(SndNxt + (uint)length);
    }

    /// <summary>
    /// Applies an incoming ACK. Returns false (and leaves state untouched)
    /// for an ACK that doesn't advance anything or acknowledges data we
    /// never sent — RFC 9293 §3.4 requires silently discarding those,
    /// not treating them as protocol errors.
    /// </summary>
    public bool OnAckReceived(uint ackNumber, ushort advertisedWindow)
    {
        // Old or duplicate ACK: SND.UNA <= ack <= SND.NXT must hold.
        if (SequenceMath.GreaterThan(ackNumber, SndNxt))
            return false; // ACKs data we never sent — discard.

        if (SequenceMath.GreaterThan(ackNumber, SndUna))
            SndUna = ackNumber; // genuine progress

        // Window can be updated even on a duplicate ACK (persists/probes rely on this).
        SndWnd = advertisedWindow;
        return true;
    }

    /// <summary>
    /// Classifies and, if in-order, applies an inbound data segment on the
    /// receive side, per RFC 9293 §3.4's segment acceptability test.
    /// </summary>
    public SegmentAcceptance OnSegmentReceived(TcpSegment segment)
    {
        uint segStart = segment.SequenceNumber;
        uint segLen = (uint)segment.Payload.Length;
        // A zero-length segment (pure ACK) is acceptable iff its sequence
        // number is inside the window; RFC 9293 §3.4 Table.
        uint effectiveLen = segLen == 0 ? 1 : segLen;
        uint segEndExclusive = unchecked(segStart + effectiveLen);

        bool windowIsOpen = RcvWnd > 0;
        bool acceptable = windowIsOpen
            ? SequenceMath.InWindow(segStart, RcvNxt, unchecked(RcvNxt + RcvWnd))
              || SequenceMath.InWindow(unchecked(segEndExclusive - 1), RcvNxt, unchecked(RcvNxt + RcvWnd))
            : segLen == 0 && segStart == RcvNxt;

        if (!acceptable)
            return SequenceMath.LessThan(segStart, RcvNxt) ? SegmentAcceptance.Duplicate : SegmentAcceptance.OutsideWindow;

        if (segStart == RcvNxt)
        {
            RcvNxt = unchecked(RcvNxt + segLen);
            return SegmentAcceptance.InOrder;
        }

        return SequenceMath.LessThan(segStart, RcvNxt) ? SegmentAcceptance.Duplicate : SegmentAcceptance.OutOfOrder;
    }

    /// <summary>Shrinks or grows the window we advertise to the peer (e.g. as our buffer drains).</summary>
    public void SetAdvertisedWindow(ushort newWindow) => RcvWnd = newWindow;
}
