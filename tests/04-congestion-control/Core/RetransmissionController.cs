namespace TcpReceivePipeline.Core;

/// <summary>
/// Отвечает за надёжность отправки: какие сегменты ещё не подтверждены,
/// когда брать RTT-выборку (и когда нельзя — Karn's algorithm, RFC 6298
/// §5.3), и когда нужен fast retransmit (RFC 5681 §3.2: три ACK подряд с
/// одним и тем же номером).
///
/// Обратите внимание на реализацию duplicate-ACK счётчика: самый первый
/// ACK с новым значением — не дубликат, это база для сравнения. Только
/// последующие ACK с тем же значением увеличивают счётчик. Это ровно та
/// же логика, что и different_data_wins/first_data_wins в reassembler'е —
/// первое наблюдение устанавливает baseline, дальнейшие с ним сравниваются.
/// </summary>
public sealed class RetransmissionController
{
    public const int FastRetransmitThreshold = 3;

    private readonly List<InFlightSegment> _inFlight = new();
    private readonly RetransmissionTimer _timer;

    private bool _haveAck;
    private uint _lastAckValue;
    private int _duplicateAckCount;

    public RetransmissionController(double initialRto) => _timer = new RetransmissionTimer(initialRto);

    public double Rto => _timer.Rto;
    public bool HasOutstandingData => _inFlight.Count > 0;
    public InFlightSegment OldestUnacked => _inFlight[0];

    /// <summary>
    /// Меняется всякий раз, когда "точка отсчёта" таймера сдвигается:
    /// новый сегмент в пустую очередь, продвижение ACK, ретрансмит.
    /// Внешний планировщик событий использует это число, чтобы понять,
    /// что запланированный ранее watchdog устарел — без удаления из
    /// очереди событий.
    /// </summary>
    public int TimerGeneration { get; private set; }

    public void OnSegmentSent(uint sequenceNumber, ReadOnlySpan<byte> payload, double now)
    {
        bool wasEmpty = _inFlight.Count == 0;
        _inFlight.Add(new InFlightSegment(sequenceNumber, payload.ToArray(), now));
        if (wasEmpty)
            TimerGeneration++; // таймер запускается впервые
    }

    /// <summary>
    /// Обрабатывает входящий ACK. Возвращает true, если это ровно
    /// FastRetransmitThreshold-й ACK подряд с тем же значением — сигнал
    /// ретрансмитить немедленно, не дожидаясь RTO.
    /// </summary>
    public bool OnAckReceived(uint ackNumber, double now)
    {
        if (_haveAck && ackNumber == _lastAckValue)
        {
            _duplicateAckCount++;
            return _duplicateAckCount == FastRetransmitThreshold;
        }

        _haveAck = true;
        _lastAckValue = ackNumber;
        _duplicateAckCount = 0;

        bool anyRetransmitted = false;
        double? lastRemovedSentAt = null;

        while (_inFlight.Count > 0 && TcpSequence.GreaterThanOrEqual(ackNumber, _inFlight[0].End))
        {
            InFlightSegment head = _inFlight[0];
            _inFlight.RemoveAt(0);
            anyRetransmitted |= head.WasRetransmitted;
            lastRemovedSentAt = head.SentAtTick;
        }

        if (lastRemovedSentAt is not null)
        {
            // Karn's algorithm: если среди подтверждённых сейчас сегментов
            // был хоть один ретранслированный — какому именно посланному
            // байту соответствует этот ACK, неоднозначно, и выборку RTT
            // пропускаем целиком, а не только для "плохого" сегмента.
            if (!anyRetransmitted)
                _timer.OnRttSample(now - lastRemovedSentAt.Value);

            TimerGeneration++; // старый watchdog (если был) больше не актуален
        }

        return false;
    }

    /// <summary>Вызывается планировщиком, когда реально истёк RTO (а не fast retransmit).</summary>
    public void OnRetransmitTimeout() => _timer.OnTimeout();

    /// <summary>Отмечает сегмент как повторно отправленный и обновляет точку отсчёта для следующего RTO.</summary>
    public void MarkRetransmitted(InFlightSegment segment, double now)
    {
        segment.WasRetransmitted = true;
        segment.SentAtTick = now;
        TimerGeneration++;
    }
}
