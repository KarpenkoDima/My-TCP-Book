using System.Text;
using TcpReceivePipeline.Core;
using TcpReceivePipeline.Infrastructure;

namespace TcpReceivePipeline.Host;

/// <summary>
/// То же самое, что и Program.DelayedAckAndRto.cs, плюс RFC 5681 congestion
/// control. Ключевое отличие в поведении: раньше все 6 сегментов уходили
/// одним залпом (окно 64 ничего не ограничивало). Теперь usable window —
/// min(cwnd, rwnd) - bytesInFlight, и initial window (16 байт при MSS=4)
/// пропускает только 4 сегмента сразу; оставшиеся два уходят позже, когда
/// придёт ACK и освободит cwnd/bytesInFlight — это меняет тайминг всего
/// сценария по сравнению с предыдущей версией, и трассировка ниже это учитывает.
/// </summary>
internal static class Program
{
    private const double PropagationDelay = 1.0;
    private const double InitialRtoGuess = 10.0;
    private const double DelayedAckMaxDelay = 1.5;
    private const int Mss = 4;
    private const double InitialSsthresh = 1000.0; // намеренно большой — весь старт идёт в slow start

    private static void Main()
    {
        byte[] payload = Encoding.ASCII.GetBytes("ABCDEFGHIJKLMNOPQRSTUVWX"); // 24 байта -> 6 сегментов по 4
        const uint clientIsn = 1000;
        const int lostSegmentIndex = 1; // второй отправленный сегмент ("EFGH") теряется

        var sender = new TcpSendWindow(initialSendSeq: clientIsn + 1, peerWindow: 64);
        var retrans = new RetransmissionController(InitialRtoGuess);
        var congestion = new CongestionController(Mss, InitialSsthresh);
        using var receiver = new TcpReceiveEndpoint(initialReceiveSequence: clientIsn + 1, capacity: 64);
        var ackPolicy = new DelayedAckPolicy(DelayedAckMaxDelay);

        using var applicationStream = new MemoryStream();
        double now = 0;
        int offset = 0;
        int sentCount = 0;
        bool inFastRecovery = false;
        uint recoveryExitAck = 0;

        var events = new PriorityQueue<Action, double>();
        void Schedule(double at, Action action) => events.Enqueue(action, at);

        TcpDataReadyHandler deliverToApplication = data =>
        {
            applicationStream.Write(data);
            Console.WriteLine($"  [t={now,5:0.000}]            -> application: \"{Encoding.ASCII.GetString(data)}\"");
        };

        void ScheduleWatchdog()
        {
            if (!retrans.HasOutstandingData) return;
            double deadline = retrans.OldestUnacked.SentAtTick + retrans.Rto;
            int generation = retrans.TimerGeneration;
            Schedule(deadline, () => OnWatchdogFired(generation));
        }

        // Крошечный сдвиг на каждую фактическую передачу — как и в прошлой
        // версии, это не "хак ради детерминизма PriorityQueue", а модель
        // конечного времени сериализации пакета на линке.
        void TrySendMore()
        {
            while (offset < payload.Length)
            {
                double usable = Math.Min(congestion.Cwnd, sender.SndWnd) - sender.BytesInFlight;
                int chunkLen = Math.Min(Mss, payload.Length - offset);
                if (chunkLen > usable) break;

                uint seq = sender.SndNxt;
                sender.OnSegmentSent(chunkLen);
                byte[] data = payload.AsSpan(offset, chunkLen).ToArray();
                retrans.OnSegmentSent(seq, data, now);
                ScheduleWatchdog();

                bool isLost = sentCount == lostSegmentIndex;
                Console.WriteLine(
                    $"  [t={now,5:0.000}] SEND         seq={seq} len={chunkLen} \"{Encoding.ASCII.GetString(data)}\"" +
                    $"  cwnd={congestion.Cwnd:0.0} ssthresh={congestion.Ssthresh:0.0}" +
                    (isLost ? "  <-- будет потерян сетью" : ""));

                if (!isLost)
                    Schedule(now + PropagationDelay, () => DeliverToReceiver(seq, data));

                offset += chunkLen;
                sentCount++;
                now += 0.001; // стагер: несколько сегментов могут уйти в рамках одного TrySendMore
            }
        }

        void RetransmitSegment(InFlightSegment segment)
        {
            retrans.MarkRetransmitted(segment, now);
            Console.WriteLine($"  [t={now,5:0.000}] RETRANSMIT   seq={segment.SequenceNumber} len={segment.Payload.Length}");
            Schedule(now + PropagationDelay, () => DeliverToReceiver(segment.SequenceNumber, segment.Payload));
            ScheduleWatchdog();
        }

        void OnWatchdogFired(int generation)
        {
            if (generation != retrans.TimerGeneration || !retrans.HasOutstandingData)
                return;

            retrans.OnRetransmitTimeout();
            int flightSizeAtLoss = (int)sender.BytesInFlight;
            congestion.OnRtoTimeout(flightSizeAtLoss);
            inFastRecovery = false; // RTO отменяет любой незавершённый fast recovery
            Console.WriteLine($"  [t={now,5:0.000}] RTO TIMEOUT  new RTO={retrans.Rto:0.0}  cwnd -> {congestion.Cwnd:0.0}  ssthresh -> {congestion.Ssthresh:0.0}");
            RetransmitSegment(retrans.OldestUnacked);
        }

        void SendAck()
        {
            uint ackNumber = receiver.RcvNxt;
            ushort window = receiver.AdvertisedWindow;
            Console.WriteLine($"  [t={now,5:0.000}] ACK          ack={ackNumber} win={window}");

            uint sndUnaBefore = sender.SndUna;
            bool fastRetransmitNeeded = retrans.OnAckReceived(ackNumber, now);
            sender.OnAckReceived(ackNumber, window);
            bool newDataAcked = sender.SndUna != sndUnaBefore;

            if (inFastRecovery)
            {
                if (newDataAcked && TcpSequence.GreaterThanOrEqual(sender.SndUna, recoveryExitAck))
                {
                    congestion.OnFastRecoveryExit();
                    inFastRecovery = false;
                    Console.WriteLine($"  [t={now,5:0.000}]              fast recovery exit -> cwnd={congestion.Cwnd:0.0}");
                }
                else if (!newDataAcked)
                {
                    congestion.OnDuplicateAckDuringRecovery();
                    Console.WriteLine($"  [t={now,5:0.000}]              dup ACK during recovery -> cwnd inflate to {congestion.Cwnd:0.0}");
                }
            }
            else if (newDataAcked)
            {
                congestion.OnNewDataAcked();
                Console.WriteLine($"  [t={now,5:0.000}]              new data ACKed -> cwnd={congestion.Cwnd:0.0} ({(congestion.InSlowStart ? "slow start" : "congestion avoidance")})");
            }

            ScheduleWatchdog();

            if (fastRetransmitNeeded)
            {
                int flightSizeAtLoss = (int)sender.BytesInFlight;
                congestion.OnFastRetransmitEntered(flightSizeAtLoss);
                inFastRecovery = true;
                recoveryExitAck = sender.SndNxt;
                Console.WriteLine(
                    $"  [t={now,5:0.000}] FAST RETRANSMIT  flightSizeAtLoss={flightSizeAtLoss}" +
                    $"  ssthresh -> {congestion.Ssthresh:0.0}  cwnd -> {congestion.Cwnd:0.0}");
                RetransmitSegment(retrans.OldestUnacked);
            }

            TrySendMore(); // ACK мог освободить bytesInFlight и/или вырастить cwnd
        }

        void OnDelayedAckDeadline(int generation)
        {
            if (ackPolicy.TryConsumeDeadline(generation))
                SendAck();
        }

        void DeliverToReceiver(uint seq, byte[] data)
        {
            Span<byte> wire = stackalloc byte[TcpHeaderParser.MinHeaderLength + data.Length];
            SyntheticSegmentWriter.Write(wire, sourcePort: 51000, destPort: 443,
                sequenceNumber: seq, ackNumber: 0, flags: TcpFlags.ACK, window: 0, payload: data);
            TcpHeaderParser.TryParse(wire, out TcpSegment segment);

            uint rcvNxtBefore = receiver.RcvNxt;
            SegmentInsertResult result = receiver.Receive(segment, deliverToApplication);
            bool wasInOrderNoGap = seq == rcvNxtBefore;

            Console.WriteLine($"  [t={now,5:0.000}] DELIVER      seq={seq} len={data.Length} -> {result}");

            if (ackPolicy.OnSegmentReceived(wasInOrderNoGap, now))
            {
                SendAck();
            }
            else
            {
                double deadline = ackPolicy.PendingDeadline!.Value;
                Console.WriteLine($"  [t={now,5:0.000}]              (ACK отложен до t={deadline:0.000})");
                int generation = ackPolicy.Generation;
                Schedule(deadline, () => OnDelayedAckDeadline(generation));
            }
        }

        Console.WriteLine($"initial cwnd (RFC 5681 IW) = {congestion.Cwnd:0.0}, ssthresh = {congestion.Ssthresh:0.0}");
        TrySendMore();

        while (events.TryDequeue(out Action? action, out double at))
        {
            now = at;
            action();
        }

        Console.WriteLine();
        Console.WriteLine($"Final stream: \"{Encoding.ASCII.GetString(applicationStream.ToArray())}\"");
        Console.WriteLine(
            $"RCV.NXT={receiver.RcvNxt}  SND.UNA={sender.SndUna}  SND.NXT={sender.SndNxt}  " +
            $"final RTO={retrans.Rto:0.0}  final cwnd={congestion.Cwnd:0.0}  final ssthresh={congestion.Ssthresh:0.0}");
    }
}
