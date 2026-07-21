namespace TcpReceivePipeline.Core;

/// <summary>
/// Решает, когда получатель обязан отправить ACK немедленно, а когда
/// можно отложить (чтобы не ACK-ать каждый отдельный сегмент — типичная
/// TCP-реализация экономит ACK'и, но не бесконечно долго).
///
/// Правила (RFC 1122 §4.2.3.2, RFC 5681 §3.2):
///   - сегмент пришёл не по порядку (gap) -> ACK немедленно. Это единственный
///     способ для sender'а обнаружить потерю через duplicate ACK, а не
///     только через RTO.
///   - иначе: ACK хотя бы на каждый второй full-size сегмент;
///   - и в любом случае не откладывать дольше фиксированного предела.
///
/// Этот класс не хранит время само по себе — он только считает "тик"
/// дедлайна планировщику события, который решает, действительно ли
/// нужно сработать (по Generation, тем же способом, что и в
/// RetransmissionController).
/// </summary>
public sealed class DelayedAckPolicy
{
    private const int SegmentsBeforeForcedAck = 2;
    private readonly double _maxDelay;

    private int _segmentsSinceAck;
    private double? _pendingDeadline;

    public DelayedAckPolicy(double maxDelay) => _maxDelay = maxDelay;

    public int Generation { get; private set; }

    /// <summary>
    /// Регистрирует приход сегмента. Возвращает true, если ACK нужно
    /// отправить прямо сейчас (немедленно), false — если можно отложить
    /// (дедлайн уже записан в <see cref="PendingDeadline"/>).
    /// </summary>
    public bool OnSegmentReceived(bool wasInOrderNoGap, double now)
    {
        if (!wasInOrderNoGap)
        {
            Reset();
            return true;
        }

        _segmentsSinceAck++;
        if (_segmentsSinceAck >= SegmentsBeforeForcedAck)
        {
            Reset();
            return true;
        }

        if (_pendingDeadline is null)
        {
            _pendingDeadline = now + _maxDelay;
            Generation++;
        }

        return false;
    }

    public double? PendingDeadline => _pendingDeadline;

    /// <summary>Вызывается планировщиком при срабатывании отложенного дедлайна.</summary>
    public bool TryConsumeDeadline(int generation)
    {
        if (_pendingDeadline is null || generation != Generation)
            return false;

        Reset();
        return true;
    }

    private void Reset()
    {
        _segmentsSinceAck = 0;
        _pendingDeadline = null;
        Generation++;
    }
}
