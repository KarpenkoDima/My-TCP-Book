using System.Text;
using TcpSlidingWindow.Core;
using TcpSlidingWindow.Infrastructure;

namespace TcpSlidingWindow.Host;

/// <summary>
/// Demonstrates the sliding window (RFC 9293 §3.4) end to end:
///   1. A synthetic segment is built into a wire buffer (SyntheticSegmentWriter).
///   2. It's parsed back with zero allocations (TcpHeaderParser), exactly as
///      a captured packet would be.
///   3. The receiver's SlidingWindowTracker classifies it (in-order /
///      duplicate / out-of-order / outside window) and, if accepted,
///      slides RCV.NXT forward.
///   4. A cumulative ACK is written back and fed to the sender's tracker,
///      which slides SND.UNA forward and re-opens usable window.
///
/// The transfer deliberately reorders one segment on the wire to show the
/// out-of-order path, and the peer advertises a small window (12 bytes)
/// against a 4-byte MSS, so the sender is forced to stall until an ACK
/// arrives — the actual "sliding" behaviour, not just a straight-line send.
/// </summary>
internal static class Program
{
    private const int Mss = 4;
    private const ushort PeerWindow = 12; // deliberately small: forces stalls, i.e. real sliding
    private const ushort OurWindow = 64;

    private static void Main()
    {
        byte[] payload = Encoding.ASCII.GetBytes("ABCDEFGHIJKLMNOPQRST"); // 20 bytes -> 5 segments of 4

        const uint clientIsn = 1000;
        const uint serverIsn = 5000;

        // Three-way handshake is assumed already complete: both sides start
        // counting from ISN + 1, per RFC 9293 §3.4.
        var sender = new SlidingWindowTracker(
            initialSendSeq: clientIsn + 1, initialRecvSeq: serverIsn + 1,
            peerWindow: PeerWindow, ourWindow: OurWindow);

        var receiver = new SlidingWindowTracker(
            initialSendSeq: serverIsn + 1, initialRecvSeq: clientIsn + 1,
            peerWindow: OurWindow, ourWindow: PeerWindow);

        PrintHeader();
        PrintState("handshake", sender, receiver);

        int offset = 0;
        // Segment 3 and 4 are swapped in flight to demonstrate the
        // out-of-order path through SlidingWindowTracker.OnSegmentReceived.
        var pendingOnWire = new Queue<(uint Seq, int Offset, int Len)>();
        int step = 0;

        while (offset < payload.Length || pendingOnWire.Count > 0)
        {
            var senderState = sender.Snapshot();

            // --- Sender: emit as many segments as the usable window allows ---
            while (offset < payload.Length)
            {
                int chunkLen = Math.Min(Mss, payload.Length - offset);
                if (chunkLen > senderState.UsableWindow) break; // window closed: must wait for an ACK

                uint seq = sender.SndNxt;
                Span<byte> wire = stackalloc byte[TcpHeaderParser.MinHeaderLength + Mss];
                int written = SyntheticSegmentWriter.Write(
                    wire, sourcePort: 51000, destPort: 443,
                    sequenceNumber: seq, ackNumber: 0, flags: TcpFlags.ACK,
                    window: sender.RcvWnd, payload: payload.AsSpan(offset, chunkLen));

                sender.OnSegmentSent(chunkLen);
                senderState = sender.Snapshot();
                pendingOnWire.Enqueue((seq, offset, chunkLen));
                offset += chunkLen;

                Console.WriteLine($"[step {++step}] SEND   seq={seq,-6} len={chunkLen} " +
                                   $"payload=\"{Encoding.ASCII.GetString(wire.Slice(TcpHeaderParser.MinHeaderLength, chunkLen))}\"" +
                                   $"  (wrote {written}B wire buffer)");
            }

            if (pendingOnWire.Count == 0) break;

            // --- Network: deliver the oldest in-flight segment, except swap
            //     segments 3 and 4 once to show reordering being handled ---
            var (deliverSeq, deliverOffset, deliverLen) = pendingOnWire.Count >= 2 && step == 5
                ? SwapAndTakeSecond(pendingOnWire)
                : pendingOnWire.Dequeue();

            ReconstructAndDeliver(deliverSeq, deliverOffset, deliverLen, payload, receiver, sender, ref step);
        }

        PrintState("transfer complete", sender, receiver);
    }

    private static (uint, int, int) SwapAndTakeSecond(Queue<(uint Seq, int Offset, int Len)> queue)
    {
        var first = queue.Dequeue();
        var second = queue.Dequeue();
        queue.Enqueue(first); // requeue the one we're deliberately delaying
        return second;
    }

    private static void ReconstructAndDeliver(
        uint seq, int payloadOffset, int len, byte[] fullPayload,
        SlidingWindowTracker receiver, SlidingWindowTracker sender, ref int step)
    {
        Span<byte> wire = stackalloc byte[TcpHeaderParser.MinHeaderLength + Mss];
        SyntheticSegmentWriter.Write(
            wire, sourcePort: 51000, destPort: 443,
            sequenceNumber: seq, ackNumber: 0, flags: TcpFlags.ACK,
            window: PeerWindow, payload: fullPayload.AsSpan(payloadOffset, len));

        if (!TcpHeaderParser.TryParse(wire, out TcpSegment segment))
        {
            Console.WriteLine($"[step {++step}] DELIVER seq={seq}: malformed segment, dropped");
            return;
        }

        SegmentAcceptance result = receiver.OnSegmentReceived(segment);
        Console.WriteLine($"[step {++step}] DELIVER seq={seq,-6} len={len}  -> {result}");

        if (result != SegmentAcceptance.InOrder)
            return; // real stack would buffer OOO segments; out of scope for this demo

        // Receiver ACKs cumulatively with RCV.NXT, per RFC 9293 §3.4.
        Span<byte> ackWire = stackalloc byte[TcpHeaderParser.MinHeaderLength];
        SyntheticSegmentWriter.Write(
            ackWire, sourcePort: 443, destPort: 51000,
            sequenceNumber: 0, ackNumber: receiver.RcvNxt, flags: TcpFlags.ACK,
            window: receiver.RcvWnd, payload: ReadOnlySpan<byte>.Empty);

        TcpHeaderParser.TryParse(ackWire, out TcpSegment ackSegment);
        bool applied = sender.OnAckReceived(ackSegment.AcknowledgmentNumber, ackSegment.WindowSize);

        Console.WriteLine($"           ACK    ack={ackSegment.AcknowledgmentNumber,-6} win={ackSegment.WindowSize}" +
                           $"  applied={applied}");

        PrintState(null, sender, receiver);
    }

    private static void PrintHeader() =>
        Console.WriteLine("SND.UNA / SND.NXT / SND.WND (sender)   RCV.NXT / RCV.WND (receiver)\n");

    private static void PrintState(string? label, SlidingWindowTracker sender, SlidingWindowTracker receiver)
    {
        var s = sender.Snapshot();
        var r = receiver.Snapshot();
        string tag = label is null ? "         " : $"[{label}]";
        Console.WriteLine(
            $"           {tag,-20} SND.UNA={s.SndUna,-6} SND.NXT={s.SndNxt,-6} SND.WND={s.SndWnd,-4} " +
            $"inFlight={s.BytesInFlight,-3} usable={s.UsableWindow,-3} | RCV.NXT={r.RcvNxt}\n");
    }
}
