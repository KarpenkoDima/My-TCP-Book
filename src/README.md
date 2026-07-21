# TCP Receive/Send Path на C# — Исходный код

Три проекта, каждый — самостоятельная, компилируемая реализация
фрагмента TCP-стека на .NET 9.

## Порядок чтения

| Проект | Модуль книги | Что реализует |
|--------|-------------|---------------|
| `TcpSlidingWindow/` | [Модуль 11](../modules/Module-11-Sliding-Window-Implementation.md) | SND.UNA/NXT/WND, RCV.NXT/WND, арифметика seq по модулю 2³², zero-alloc парсинг заголовка |
| `TcpOutOfOrderReassembly/` | [Модуль 12](../modules/Module-12-Out-of-Order-Reassembly.md) | Reassembly buffer: overlap handling, 64-bit unwrapping, ArrayPool, DoS-защита |
| `TcpReceivePipeline/` | [Модуль 13](../modules/Module-13-TCP-Receive-Pipeline.md) | Полный pipeline: Delayed ACK (RFC 1122), RTO (RFC 6298), Fast Retransmit, Congestion Control (RFC 5681) |

## Запуск

```bash
# Sliding Window — передача 20 байт при узком окне
dotnet run --project TcpSlidingWindow

# Out-of-Order Reassembly — три сценария (reorder, overlap, wrap-around)
dotnet run --project TcpOutOfOrderReassembly

# TCP Receive Pipeline — потеря сегмента, fast retransmit, congestion control
dotnet run --project TcpReceivePipeline
```

Требования: .NET 9 SDK.

## Эволюция кода между проектами

```
TcpSlidingWindow          TcpOutOfOrderReassembly       TcpReceivePipeline
─────────────────         ───────────────────────       ──────────────────
SequenceMath.cs     →     TcpSequence.cs           →    TcpSequence.cs
SlidingWindowTracker.cs                            →    TcpSendWindow.cs (SND)
                                                   +    TcpReceiveEndpoint.cs (RCV + reassembler)
                          TcpStreamReassembler.cs  →    TcpStreamReassembler.cs
                          BufferedSegment.cs       →    BufferedSegment.cs
TcpSegment.cs                                     →    TcpSegment.cs
TcpHeaderParser.cs                                →    TcpHeaderParser.cs
SyntheticSegmentWriter.cs                         →    SyntheticSegmentWriter.cs
                                                   +    InFlightSegment.cs
                                                   +    RetransmissionTimer.cs
                                                   +    RetransmissionController.cs
                                                   +    DelayedAckPolicy.cs
                                                   +    CongestionController.cs
```

## Карта RFC

| Механизм | RFC | Проект |
|----------|-----|--------|
| TCP header, sequence space | RFC 9293 §3.1, §3.4 | TcpSlidingWindow |
| Sliding window (SND/RCV) | RFC 9293 §3.3.1 | TcpSlidingWindow |
| Reassembly, overlap | RFC 9293 §3.4 | TcpOutOfOrderReassembly |
| Delayed ACK | RFC 1122 §4.2.3.2 | TcpReceivePipeline |
| RTO (Jacobson/Karn) | RFC 6298 | TcpReceivePipeline |
| Fast retransmit | RFC 5681 §3.2 | TcpReceivePipeline |
| Slow start / congestion avoidance | RFC 5681 §3.1 | TcpReceivePipeline |
