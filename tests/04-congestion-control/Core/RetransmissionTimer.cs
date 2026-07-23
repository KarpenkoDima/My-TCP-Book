namespace TcpReceivePipeline.Core;

/// <summary>
/// Оценка retransmission timeout по RFC 6298.
///
/// До первого RTT-сэмпла используется грубая начальная оценка (RFC 6298
/// рекомендует 1s; здесь — настраиваемое значение в единицах симуляции).
/// После первого сэмпла — классические формулы Джейкобсона:
///
///   SRTT   = (1-α)·SRTT + α·R
///   RTTVAR = (1-β)·RTTVAR + β·|SRTT - R|
///   RTO    = SRTT + max(G, 4·RTTVAR)
///
/// где R — измеренный RTT, G — гранулярность часов (здесь принята за 1
/// тик), α=1/8, β=1/4 — константы из RFC 6298 §2.
/// </summary>
public sealed class RetransmissionTimer
{
    private const double Alpha = 1.0 / 8.0;
    private const double Beta = 1.0 / 4.0;
    private const double ClockGranularity = 1.0;
    private const double MinRto = 1.0;
    private const double MaxRto = 60.0;

    private double? _srtt;
    private double? _rttVar;

    public RetransmissionTimer(double initialRto) => Rto = initialRto;

    public double Rto { get; private set; }

    /// <summary>
    /// Регистрирует одно валидное измерение RTT. Вызывающая сторона
    /// обязана сама убедиться, что сегмент, породивший эту выборку, не
    /// был ретранслирован — это правило Карна (RFC 6298 §5.3), и оно
    /// живёт в <see cref="RetransmissionController"/>, а не здесь: этот
    /// класс ничего не знает о сегментах, только про числа.
    /// </summary>
    public void OnRttSample(double measuredRtt)
    {
        if (_srtt is null)
        {
            _srtt = measuredRtt;
            _rttVar = measuredRtt / 2.0;
        }
        else
        {
            _rttVar = (1 - Beta) * _rttVar!.Value + Beta * Math.Abs(_srtt.Value - measuredRtt);
            _srtt = (1 - Alpha) * _srtt.Value + Alpha * measuredRtt;
        }

        Rto = Math.Clamp(_srtt.Value + Math.Max(ClockGranularity, 4 * _rttVar!.Value), MinRto, MaxRto);
    }

    /// <summary>Экспоненциальный backoff при реальном истечении таймера (RFC 6298 §5.5). Karn's algorithm.</summary>
    public void OnTimeout() => Rto = Math.Min(Rto * 2, MaxRto);
}
