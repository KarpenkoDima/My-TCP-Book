namespace TcpReceivePipeline.Core;

/// <summary>
/// Один отправленный, но ещё не подтверждённый сегмент. В отличие от
/// <see cref="TcpSegment"/> (ref struct, живёт один parse) — это обычный
/// класс: он должен пережить время в полёте, потенциально быть
/// ретранслирован, и его нельзя держать как Span, потому что исходный
/// wire-буфер отправителя давно переиспользован под следующий сегмент.
/// Поэтому payload здесь — реальная копия (byte[]), а не срез.
/// </summary>
public sealed class InFlightSegment
{
    public InFlightSegment(uint sequenceNumber, byte[] payload, double sentAtTick)
    {
        SequenceNumber = sequenceNumber;
        Payload = payload;
        SentAtTick = sentAtTick;
    }

    public uint SequenceNumber { get; }
    public byte[] Payload { get; }

    /// <summary>Время последней (пере)передачи — используется для расчёта следующего RTO deadline.</summary>
    public double SentAtTick { get; set; }

    /// <summary>Karn's algorithm: если true, ACK на этот сегмент не даёт валидную RTT-выборку.</summary>
    public bool WasRetransmitted { get; set; }

    public uint End => unchecked(SequenceNumber + (uint)Payload.Length);
}
