namespace TcpReceivePipeline.Core;

/// <summary>
/// Congestion window по RFC 5681. Три режима, разделённые ssthresh:
///
///   cwnd &lt; ssthresh  -> slow start:          cwnd += MSS за каждый новый ACK
///   cwnd &gt;= ssthresh -> congestion avoidance: cwnd += MSS²/cwnd за каждый новый ACK
///
/// и два пути входа в "потеряли сегмент":
///
///   3 duplicate ACK (fast retransmit, §3.2) -> ssthresh=max(flight/2,2·MSS), cwnd=ssthresh+3·MSS
///   RTO истёк (§3.1, более тяжёлый случай)   -> ssthresh=max(flight/2,2·MSS), cwnd=MSS (заново slow start)
///
/// Разница между этими двумя ветками намеренная: RTO — это "мы не видели
/// от peer вообще ничего", а fast retransmit — "peer жив, просто у нас gap".
/// Отсюда RTO откатывает cwnd гораздо консервативнее.
/// </summary>
public sealed class CongestionController
{
    private readonly int _mss;

    public CongestionController(int mss, double initialSsthresh)
    {
        _mss = mss;
        Cwnd = InitialWindow(mss);
        Ssthresh = initialSsthresh;
    }

    public double Cwnd { get; private set; }
    public double Ssthresh { get; private set; }
    public bool InSlowStart => Cwnd < Ssthresh;

    /// <summary>RFC 5681 §3.1: IW = min(4·SMSS, max(2·SMSS, 4380 bytes)).</summary>
    private static double InitialWindow(int mss) => Math.Min(4 * mss, Math.Max(2 * mss, 4380));

    public void OnNewDataAcked()
    {
        if (Cwnd < Ssthresh)
            Cwnd += _mss; // slow start — экспоненциальный рост
        else
            Cwnd += Math.Max(1.0, (double)_mss * _mss / Cwnd); // congestion avoidance — линейный рост
    }

    /// <summary>RFC 5681 §3.2 шаг 3: "inflate" — ещё один dup ACK во время recovery означает,
    /// что ещё один сегмент покинул сеть, значит можно впустить ещё один новый.</summary>
    public void OnDuplicateAckDuringRecovery() => Cwnd += _mss;

    public void OnFastRetransmitEntered(int flightSizeAtLoss)
    {
        Ssthresh = Math.Max(flightSizeAtLoss / 2.0, 2.0 * _mss);
        Cwnd = Ssthresh + 3 * _mss; // §3.2 шаг 2
    }

    /// <summary>Вызывается на ACK, который наконец подтвердил все данные, бывшие в полёте на момент входа в recovery.</summary>
    public void OnFastRecoveryExit() => Cwnd = Ssthresh; // §3.2 шаг 4 — "deflate"

    public void OnRtoTimeout(int flightSizeAtLoss)
    {
        Ssthresh = Math.Max(flightSizeAtLoss / 2.0, 2.0 * _mss);
        Cwnd = _mss; // §3.1 loss window — начинаем slow start заново, с нуля
    }
}
