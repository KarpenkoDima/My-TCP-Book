# Глава 4. Congestion control

Глава 3 отвечала на "потерян ли сегмент и когда". Эта глава — на
совсем другой вопрос: **с какой скоростью вообще можно слать данные**,
даже если ничего не потеряно и получатель готов принять больше. Это
RFC 5681, отдельный от RTO (RFC 6298) и delayed ACK (RFC 1122) документ —
не потому что бюрократия, а потому что у congestion window (`cwnd`,
`ssthresh`) свой жизненный цикл состояния, переживающий много ACK-ов.

## Три режима

```
cwnd < ssthresh   -> slow start:          cwnd += MSS за каждый новый ACK
cwnd >= ssthresh  -> congestion avoidance: cwnd += MSS²/cwnd за каждый новый ACK
```

и два разных пути в "потеряли сегмент":

```
3 duplicate ACK (fast retransmit, §3.2) -> ssthresh = max(flight/2, 2·MSS)
                                            cwnd = ssthresh + 3·MSS
RTO истёк (§3.1, тяжелее)                -> ssthresh = max(flight/2, 2·MSS)
                                            cwnd = MSS  (заново slow start)
```

Разница между веткам намеренная: RTO значит "мы вообще не видели ответа
от пира", fast retransmit значит "пир жив, у нас просто gap". Поэтому RTO
откатывает `cwnd` гораздо консервативнее.

## Что меняется в остальном коде

`Core/CongestionController.cs` — единственный новый файл в этой главе.
Но он меняет поведение отправителя во всех остальных: usable window
теперь

```
usable = min(cwnd, rwnd) - bytesInFlight
```

вместо просто `rwnd`, как было в главах 1 и 3. Initial window по RFC 5681
§3.1:

```csharp
private static double InitialWindow(int mss)
    => Math.Min(4 * mss, Math.Max(2 * mss, 4380));
```

При MSS=4 это 16 байт — то есть **initial cwnd реально ограничивает
первый залп 4 сегментами**, даже если окно получателя (`rwnd`) намного
больше.

## Пример прогона — тот же сценарий, что и в главе 3, но с cwnd

```
initial cwnd = 16.0, ssthresh = 1000.0

[t=0.000] SEND seq=1001 "ABCD"  cwnd=16.0
[t=0.001] SEND seq=1005 "EFGH"  cwnd=16.0  <-- потерян
[t=0.002] SEND seq=1009 "IJKL"  cwnd=16.0
[t=0.003] SEND seq=1013 "MNOP"  cwnd=16.0
                          -- cwnd исчерпан, QRST/UVWX ждут --

[t=1.002] ACK ack=1005 win=60
           new data ACKed -> cwnd=20.0 (slow start)
[t=1.002] SEND seq=1017 "QRST"   <- окно приоткрылось, хвост уходит только теперь
[t=1.003] SEND seq=1021 "UVWX"

           ... (duplicate ACK #1, #2, #3 — как в главе 3) ...

[t=2.003] FAST RETRANSMIT  flightSizeAtLoss=20  ssthresh -> 10.0  cwnd -> 22.0
[t=2.003] RETRANSMIT seq=1005

[t=3.003] DELIVER seq=1005 -> каскад из 5 диапазонов

[t=4.503] ACK ack=1025 win=64
           fast recovery exit -> cwnd=10.0   <- deflate до ssthresh

Final stream: "ABCDEFGHIJKLMNOPQRSTUVWX"
final RTO=3.0  final cwnd=10.0  final ssthresh=10.0
```

Заметь: тайминг сдвинулся по сравнению с главой 3 — там пятый и шестой
сегменты уходили сразу же (`t=0`), здесь они физически не могут уйти
раньше `t≈1.002`, потому что до этого момента `cwnd` был исчерпан. Это не
артефакт трассировки — это ровно то, ради чего congestion control
существует: он **навязывает темп отправки**, а не просто наблюдает.

## Что осталось нереализованным

- `OnDuplicateAckDuringRecovery` (RFC 5681 §3.2 шаг 3, "inflate") ни разу
  не сработал в этом прогоне — recovery длился ровно от 3-го dup ACK до
  следующего же ACK, который сразу оказался финальным.
- NewReno partial ACK (RFC 6582) не реализован — здесь весь backlog
  доставился одним каскадом, разница partial/full ACK не проявилась.
- Pacing — `TrySendMore` выпускает весь доступный кусок окна одним
  циклом, без равномерного распределения во времени.
