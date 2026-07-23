namespace TcpReceivePipeline.Core;

/// <summary>
/// Отвечает только за SND-сторону соединения (RFC 9293 §3.3.1):
/// SND.UNA, SND.NXT, SND.WND. Больше не хранит ничего о приёме —
/// приём (RCV.NXT/RCV.WND) целиком переехал в <see cref="TcpReceiveEndpoint"/>,
/// потому что там теперь есть реальное состояние (reassembly buffer),
/// а не просто три числа.
/// </summary>
public sealed class TcpSendWindow
{
    public uint SndUna { get; private set; }
    public uint SndNxt { get; private set; }
    public ushort SndWnd { get; private set; }

    public TcpSendWindow(uint initialSendSeq, ushort peerWindow)
    {
        SndUna = initialSendSeq;
        SndNxt = initialSendSeq;
        SndWnd = peerWindow;
    }

    /// <summary>Отправлено, но ещё не подтверждено. Беззнаковое вычитание корректно и через wraparound.</summary>
    public uint BytesInFlight => unchecked(SndNxt - SndUna);

    /// <summary>Сколько ещё можно отправить прямо сейчас, не выходя за окно, объявленное пиром.</summary>
    public uint UsableWindow => SndWnd - BytesInFlight > SndWnd ? 0 : SndWnd - BytesInFlight;

    public void OnSegmentSent(int length)
    {
        if (length < 0) throw new ArgumentOutOfRangeException(nameof(length));
        SndNxt = unchecked(SndNxt + (uint)length);
    }

    /// <summary>
    /// Применяет входящий ACK. Возвращает false для ACK на данные,
    /// которые мы не отправляли — RFC 9293 §3.4 требует такие молча
    /// отбрасывать, а не считать ошибкой протокола.
    /// </summary>
    public bool OnAckReceived(uint ackNumber, ushort advertisedWindow)
    {
        if (TcpSequence.GreaterThan(ackNumber, SndNxt))
            return false;

        if (TcpSequence.GreaterThan(ackNumber, SndUna))
            SndUna = ackNumber;

        SndWnd = advertisedWindow; // обновляем даже на дублирующем ACK — так ведут себя persist/probe
        return true;
    }
}
