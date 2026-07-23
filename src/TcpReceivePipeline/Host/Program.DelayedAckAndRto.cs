using System.Text;
using TcpReceivePipeline.Core;
using TcpReceivePipeline.Infrastructure;

namespace TcpReceivePipeline.Host;

/// <summary>
/// Тот же приёмный конвейер, что и в Program.WindowAndReassembly.cs, но
/// теперь с настоящим понятием времени: <see cref="PriorityQueue{TElement,TPriority}"/>
/// как очередь событий "arrival / ACK / RTO watchdog / delayed-ACK deadline",
/// упорядоченных по виртуальному тику.
///
/// Сценарий: 6 сегментов по 4 байта (24 байта), второй сегмент ("EFGH",
/// seq=1005) теряется в сети и никогда не долетает с первой попытки.
/// Пропускная способность приёмника намеренно большая (64 байта) — окно
/// в этом сценарии не в фокусе, в фокусе время.
///
/// Распространение — 1 тик в одну сторону; ACK считается "мгновенным"
/// обратным путём (упрощение ради читаемой трассировки — в реальности
/// return-trip добавил бы ещё один тик к каждому raw RTT-сэмплу).
/// </summary>
internal static class Program
{
    private const double PropagationDelay = 1.0;
    private const double InitialRtoGuess = 10.0; // грубая оценка до первого сэмпла; RFC 6298 рекомендует 1s
    private const double DelayedAckMaxDelay = 1.5; // тиков; должно оставаться меньше следующего RTO-дедлайна после fast retransmit

    private static void Main()
    {
        byte[] payload = Encoding.ASCII.GetBytes("ABCDEFGHIJKLMNOPQRSTUVWX"); // 24 байта -> 6 сегментов по 4
        const uint clientIsn = 1000;
        const int mss = 4;
        const int lostSegmentIndex = 1; // второй отправленный сегмент ("EFGH") теряется

        var sender = new TcpSendWindow(initialSendSeq: clientIsn + 1, peerWindow: 64);
        var retrans = new RetransmissionController(InitialRtoGuess);
        using var receiver = new TcpReceiveEndpoint(initialReceiveSequence: clientIsn + 1, capacity: 64);
        var ackPolicy = new DelayedAckPolicy(DelayedAckMaxDelay);

        using var applicationStream = new MemoryStream();
        double now = 0;

        var events = new PriorityQueue<Action, double>();
        void Schedule(double at, Action action) => events.Enqueue(action, at);

        TcpDataReadyHandler deliverToApplication = data =>
        {
            applicationStream.Write(data);
            Console.WriteLine($"  [t={now,4:0}]            -> application: \"{Encoding.ASCII.GetString(data)}\"");
        };

        void ScheduleWatchdog()
        {
            if (!retrans.HasOutstandingData) return;
            double deadline = retrans.OldestUnacked.SentAtTick + retrans.Rto;
            int generation = retrans.TimerGeneration;
            Schedule(deadline, () => OnWatchdogFired(generation));
        }

        void RetransmitSegment(InFlightSegment segment)
        {
            retrans.MarkRetransmitted(segment, now);
            Console.WriteLine($"  [t={now,4:0}] RETRANSMIT   seq={segment.SequenceNumber} len={segment.Payload.Length}");
            Schedule(now + PropagationDelay, () => DeliverToReceiver(segment.SequenceNumber, segment.Payload));
            ScheduleWatchdog();
        }

        void OnWatchdogFired(int generation)
        {
            if (generation != retrans.TimerGeneration || !retrans.HasOutstandingData)
                return; // устаревший watchdog — таймер уже был перезапущен или всё подтверждено

            retrans.OnRetransmitTimeout();
            Console.WriteLine($"  [t={now,4:0}] RTO TIMEOUT  (new RTO={retrans.Rto:0.0})");
            RetransmitSegment(retrans.OldestUnacked);
        }

        void SendAck()
        {
            uint ackNumber = receiver.RcvNxt;
            ushort window = receiver.AdvertisedWindow;
            Console.WriteLine($"  [t={now,4:0}] ACK          ack={ackNumber} win={window}");

            bool fastRetransmitNeeded = retrans.OnAckReceived(ackNumber, now);
            sender.OnAckReceived(ackNumber, window);
            ScheduleWatchdog();

            if (fastRetransmitNeeded)
            {
                double remaining = retrans.OldestUnacked.SentAtTick + retrans.Rto - now;
                Console.WriteLine($"  [t={now,4:0}] FAST RETRANSMIT (3-я подряд duplicate ACK, до RTO ещё {remaining:0.0} тиков)");
                RetransmitSegment(retrans.OldestUnacked);
            }
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

            Console.WriteLine($"  [t={now,4:0}] DELIVER      seq={seq} len={data.Length} -> {result}");

            if (ackPolicy.OnSegmentReceived(wasInOrderNoGap, now))
            {
                SendAck();
            }
            else
            {
                Console.WriteLine($"  [t={now,4:0}]              (ACK отложен до t={ackPolicy.PendingDeadline:0.0})");
                double deadline = ackPolicy.PendingDeadline!.Value;
                int generation = ackPolicy.Generation;
                Schedule(deadline, () => OnDelayedAckDeadline(generation));
            }
        }

        // --- t=0: отправляем все 6 сегментов одной пачкой (окно 64 не мешает).
        //     Крошечный сдвиг 0.01 на сегмент — не "хак ради детерминизма",
        //     а реалистичная модель: сериализация пакетов на линке занимает
        //     конечное время, поэтому они физически не могут уйти в один
        //     и тот же момент. Без этого сдвига все 5 прибывающих сегментов
        //     легли бы в PriorityQueue с одинаковым приоритетом (t=1), а её
        //     порядок среди равных приоритетов не гарантирован — сценарий
        //     перестал бы быть детерминированным.
        for (int i = 0; i < payload.Length / mss; i++)
        {
            now = i * 0.01;
            int offset = i * mss;
            uint seq = sender.SndNxt;
            sender.OnSegmentSent(mss);
            byte[] data = payload.AsSpan(offset, mss).ToArray();
            retrans.OnSegmentSent(seq, data, now);

            if (i == lostSegmentIndex)
            {
                Console.WriteLine($"  [t={now,4:0}] SEND         seq={seq} len={mss} \"{Encoding.ASCII.GetString(data)}\"  <-- будет потерян сетью");
                continue; // сегмент "отправлен", но никогда не будет доставлен с первой попытки
            }

            Console.WriteLine($"  [t={now,4:0}] SEND         seq={seq} len={mss} \"{Encoding.ASCII.GetString(data)}\"");
            Schedule(now + PropagationDelay, () => DeliverToReceiver(seq, data));
        }

        ScheduleWatchdog();

        while (events.TryDequeue(out Action? action, out double at))
        {
            now = at;
            action();
        }

        Console.WriteLine();
        Console.WriteLine($"Final stream: \"{Encoding.ASCII.GetString(applicationStream.ToArray())}\"");
        Console.WriteLine($"RCV.NXT={receiver.RcvNxt}  SND.UNA={sender.SndUna}  SND.NXT={sender.SndNxt}  final RTO={retrans.Rto:0.0}");
    }
}
