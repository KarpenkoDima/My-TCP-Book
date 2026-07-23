using System.Text;
using TcpReceivePipeline.Core;
using TcpReceivePipeline.Infrastructure;

namespace TcpReceivePipeline.Host;

/// <summary>
/// Полный приёмный конвейер одного направления TCP-соединения:
///
///   sender (TcpSendWindow) --wire bytes--> parser --> TcpReceiveEndpoint
///                          <--ACK (ack=RCV.NXT, win=AdvertisedWindow)--
///
/// Сценарий специально злой: MSS=4, ёмкость получателя = 12 байт (то есть
/// не более 3 сегментов в буфере одновременно, доставленных или нет), а
/// сеть намеренно перемешивает первые три сегмента. Это одновременно
/// показывает:
///   - реальное сужение AdvertisedWindow, пока OOO-сегмент лежит в
///     очереди и не может быть отдан приложению;
///   - duplicate ACK (ack не продвигается) на сегменте, который лёг в
///     буфер, но не закрыл gap;
///   - каскадную доставку нескольких диапазонов за один Push, когда
///     наконец приходит недостающий сегмент.
/// </summary>
internal static class Program
{
    private const int Mss = 4;
    private const int ReceiverCapacity = 12; // 3 сегмента одновременно, не больше
    private const ushort InitialPeerWindow = ReceiverCapacity;

    private static void Main()
    {
        byte[] payload = Encoding.ASCII.GetBytes("ABCDEFGHIJKLMNOPQRST"); // 20 байт -> 5 сегментов по 4

        const uint clientIsn = 1000;

        var sender = new TcpSendWindow(initialSendSeq: clientIsn + 1, peerWindow: InitialPeerWindow);
        using var receiver = new TcpReceiveEndpoint(initialReceiveSequence: clientIsn + 1, capacity: ReceiverCapacity);

        using var applicationStream = new MemoryStream();
        TcpDataReadyHandler deliverToApplication = data =>
        {
            applicationStream.Write(data);
            Console.WriteLine($"           -> application: \"{Encoding.ASCII.GetString(data)}\"");
        };

        var onWire = new List<(uint Seq, int Offset, int Len)>();
        int offset = 0;
        int step = 0;
        int deliveriesSoFar = 0;

        PrintState("start", sender, receiver);

        while (offset < payload.Length || onWire.Count > 0)
        {
            // --- Sender: шлём, пока хватает окна, которое нам последний раз объявил получатель ---
            while (offset < payload.Length)
            {
                int chunkLen = Math.Min(Mss, payload.Length - offset);
                if (chunkLen > sender.UsableWindow) break;

                uint seq = sender.SndNxt;
                sender.OnSegmentSent(chunkLen);
                onWire.Add((seq, offset, chunkLen));

                Console.WriteLine($"[step {++step}] SEND    seq={seq,-6} len={chunkLen} " +
                                   $"payload=\"{Encoding.ASCII.GetString(payload, offset, chunkLen)}\"");
                offset += chunkLen;
            }

            if (onWire.Count == 0) break;

            // --- Network: первый сегмент из начальной тройки доставляется вторым по счёту
            //     (seq2 приходит раньше seq1), дальше — обычный FIFO ---
            int deliverIndex = deliveriesSoFar == 0 && onWire.Count >= 3 ? 1 : 0;
            var (seqToDeliver, offsetToDeliver, lenToDeliver) = onWire[deliverIndex];
            onWire.RemoveAt(deliverIndex);
            deliveriesSoFar++;

            DeliverSegment(seqToDeliver, offsetToDeliver, lenToDeliver, payload, receiver, sender, deliverToApplication, ref step);
        }

        Console.WriteLine();
        Console.WriteLine($"Final stream: \"{Encoding.ASCII.GetString(applicationStream.ToArray())}\"");
        PrintState("transfer complete", sender, receiver);
    }

    private static void DeliverSegment(
        uint seq, int payloadOffset, int len, byte[] fullPayload,
        TcpReceiveEndpoint receiver, TcpSendWindow sender,
        TcpDataReadyHandler onDataReady, ref int step)
    {
        Span<byte> wire = stackalloc byte[TcpHeaderParser.MinHeaderLength + Mss];
        SyntheticSegmentWriter.Write(
            wire, sourcePort: 51000, destPort: 443,
            sequenceNumber: seq, ackNumber: 0, flags: TcpFlags.ACK,
            window: 0, payload: fullPayload.AsSpan(payloadOffset, len));

        TcpHeaderParser.TryParse(wire, out TcpSegment segment);

        SegmentInsertResult result = receiver.Receive(segment, onDataReady);
        Console.WriteLine($"[step {++step}] DELIVER seq={seq,-6} len={len}  -> {result}");

        // Получатель ACK-ит каждый сегмент, даже если это не закрыло gap
        // (в реальном TCP это и есть duplicate ACK, триггер fast retransmit).
        uint ackNumber = receiver.RcvNxt;
        ushort advertisedWindow = receiver.AdvertisedWindow;

        uint sndUnaBefore = sender.SndUna;
        sender.OnAckReceived(ackNumber, advertisedWindow);
        string ackKind = sender.SndUna != sndUnaBefore ? "ACK" : "duplicate ACK";

        Console.WriteLine(
            $"           {ackKind,-14} ack={ackNumber,-6} win={advertisedWindow,-3} " +
            $"(buffered={receiver.BufferedBytes}, ranges={receiver.BufferedRangeCount})");

        PrintState(null, sender, receiver);
    }

    private static void PrintState(string? label, TcpSendWindow sender, TcpReceiveEndpoint receiver)
    {
        string tag = label is null ? "" : $"[{label}] ";
        Console.WriteLine(
            $"           {tag}SND.UNA={sender.SndUna,-6} SND.NXT={sender.SndNxt,-6} SND.WND={sender.SndWnd,-3} " +
            $"inFlight={sender.BytesInFlight,-3} usable={sender.UsableWindow,-3} | RCV.NXT={receiver.RcvNxt}\n");
    }
}
