# Модуль 3: TCP State Machine и жизненный цикл соединения

*«TCP — это не просто протокол. Это конечный автомат с 11 состояниями, каждое из которых может убить ваш сервер».*

В предыдущем модуле мы разобрали, как TCP управляет скоростью отправки (congestion control). Теперь спустимся на уровень ниже и посмотрим на **жизненный цикл** самого TCP-соединения: как оно рождается, живёт и умирает. Именно здесь прячутся SYN flood атаки, TIME_WAIT exhaustion и загадочные RST-пакеты.

---

## Часть 3.1: TCP State Machine — 11 состояний

### Диаграмма состояний

```
                              ┌───────────┐
                    Passive   │  CLOSED   │   Active
                    Open      └─────┬─────┘   Open
                   ┌────────────────┼────────────────┐
                   │                │                │
                   ▼                │                ▼
            ┌──────────┐           │         ┌──────────┐
            │  LISTEN  │           │         │ SYN_SENT │
            └────┬─────┘           │         └────┬─────┘
     Recv SYN    │                 │              │  Recv SYN+ACK
     Send SYN+ACK│                │              │  Send ACK
                 ▼                 │              ▼
          ┌───────────┐           │       ┌─────────────┐
          │ SYN_RCVD  │───────────┘       │ ESTABLISHED │
          └─────┬─────┘  Recv ACK         └──────┬──────┘
                │                                │
                └────────────────┬───────────────┘
                                 │
                    Close        │        Recv FIN
                    Send FIN     │        Send ACK
                 ┌───────────────┼────────────────┐
                 ▼                                ▼
          ┌───────────┐                    ┌────────────┐
          │ FIN_WAIT_1│                    │ CLOSE_WAIT │
          └─────┬─────┘                    └─────┬──────┘
    Recv ACK    │    Recv FIN+ACK          Close │
                ▼    Send ACK                    ▼ Send FIN
          ┌───────────┐                    ┌────────────┐
          │ FIN_WAIT_2│                    │  LAST_ACK  │
          └─────┬─────┘                    └─────┬──────┘
    Recv FIN    │                          Recv ACK│
    Send ACK   │                                  │
                ▼                                  ▼
          ┌───────────┐                    ┌───────────┐
          │ TIME_WAIT │───── 2MSL ────────→│  CLOSED   │
          └───────────┘    timeout         └───────────┘
```

Каждое состояние — это конкретное поле в структуре `struct sock` в ядре:

```c
// include/net/inet_connection_sock.h
// Состояние хранится в sk->sk_state
enum {
    TCP_ESTABLISHED = 1,
    TCP_SYN_SENT,
    TCP_SYN_RECV,
    TCP_FIN_WAIT1,
    TCP_FIN_WAIT2,
    TCP_TIME_WAIT,
    TCP_CLOSE,
    TCP_CLOSE_WAIT,
    TCP_LAST_ACK,
    TCP_LISTEN,
    TCP_CLOSING,    // Обе стороны закрыли одновременно
    TCP_NEW_SYN_RECV, // Оптимизация для SYN cookies
};
```

---

## Часть 3.2: Three-Way Handshake — Рождение соединения

### Что происходит на самом деле

```
Клиент                                    Сервер
  │                                          │
  │  SYN (seq=ISN_c)                         │
  │─────────────────────────────────────────→│  SYN_SENT → SYN_RCVD
  │                                          │
  │  SYN+ACK (seq=ISN_s, ack=ISN_c+1)       │
  │←─────────────────────────────────────────│
  │                                          │
  │  ACK (ack=ISN_s+1)                       │
  │─────────────────────────────────────────→│  ESTABLISHED
  │          ESTABLISHED                     │
```

### ISN (Initial Sequence Number): Почему случайный

ISN не начинается с нуля. Ядро генерирует его через `secure_tcp_seq()`:

```c
// net/core/secure_seq.c
u32 secure_tcp_seq(__be32 saddr, __be32 daddr,
                   __be16 sport, __be16 dport)
{
    u32 hash;

    // SipHash от 4-tuple + секретный ключ + таймер
    net_secret_init();
    hash = siphash_3u32((__force u32)saddr, (__force u32)daddr,
                        (__force u32)(sport | ((__force u32)dport << 16)),
                        &net_secret);

    return hash + (ktime_get_real_ns() >> 6);
}
```

**Зачем случайный ISN:**
1. **Защита от спуфинга**: атакующий не может угадать sequence number и внедрить пакет в чужое соединение
2. **Защита от старых пакетов**: если предыдущее соединение с теми же (src, dst, sport, dport) не полностью завершилось, старые пакеты с предсказуемым ISN могли бы попасть в новое соединение

### Серверная сторона: SYN Queue и Accept Queue

Когда сервер получает SYN, он создаёт полу-открытое соединение. В ядре это два раздельных буфера:

```
                   ┌─────────────────┐
    SYN ──────────→│   SYN Queue     │  (полу-открытые, ждём ACK)
                   │  (request_sock) │
                   └────────┬────────┘
                            │  Recv ACK
                            ▼
                   ┌─────────────────┐
                   │  Accept Queue   │  (полностью открытые, ждём accept())
                   │   (struct sock) │
                   └────────┬────────┘
                            │  accept() syscall
                            ▼
                     Приложение
```

```c
// Размеры очередей контролируются:
// SYN Queue:
net.ipv4.tcp_max_syn_backlog = 4096   // Макс. полу-открытых

// Accept Queue:
// Задаётся аргументом listen(fd, backlog)
// Ограничен сверху:
net.core.somaxconn = 4096
```

### Диагностика

```bash
# Текущее состояние очередей
ss -ltn
# Recv-Q = текущий размер accept queue
# Send-Q = максимальный размер accept queue (backlog)

# Переполнения
nstat -az | grep -i 'listen'
# TcpExtListenOverflows  — accept queue переполнена
# TcpExtListenDrops      — дропы из-за переполнения
```

Если `ListenOverflows` растёт — приложение не успевает вызывать `accept()`. Это не проблема сети, это проблема **приложения**.

---

## Часть 3.3: SYN Flood и защита

### Атака

Атакующий отправляет тысячи SYN-пакетов с поддельными IP-адресами. Сервер создаёт `request_sock` для каждого, отвечает SYN+ACK. ACK никогда не придёт (IP фальшивый). SYN Queue переполняется → легитимные клиенты не могут подключиться.

### SYN Cookies: Защита без состояния

```bash
net.ipv4.tcp_syncookies = 1
```

Когда SYN Queue переполнена, ядро **не создаёт** `request_sock`. Вместо этого оно кодирует параметры соединения прямо в ISN ответного SYN+ACK:

```c
// net/ipv4/syncookies.c (упрощённо)
static __u32 cookie_hash(__be32 saddr, __be32 daddr,
                          __be16 sport, __be16 dport,
                          u32 count, int c)
{
    // ISN = hash(src, dst, sport, dport, time) + MSS_index
    // MSS кодируется в младших битах
    // Время кодируется через 64-секундные интервалы (count)
    return siphash_4u32(saddr, daddr, sport | dport << 16,
                        count, &syncookie_secret[c]);
}
```

Когда клиент отвечает ACK, ядро **восстанавливает** параметры соединения из sequence number в ACK. Если хеш совпадает — соединение легитимное.

**Ограничения SYN Cookies:**
- Не поддерживают TCP Options (Window Scaling, SACK, Timestamps) в классическом варианте. Ядро Linux обходит это, кодируя основные опции в cookie.
- Не защищают от volumetric DDoS (когда канал забит трафиком).

### TCP Fast Open (TFO)

Позволяет отправить данные **прямо в SYN**:

```
Первое соединение:
  Client → SYN + TFO Cookie Request
  Server → SYN+ACK + TFO Cookie
  Client → ACK

Последующие:
  Client → SYN + TFO Cookie + HTTP GET /    ← Данные в SYN!
  Server → SYN+ACK + HTTP Response           ← Ответ сразу!
  Client → ACK
```

Экономит 1 RTT. Включение:

```bash
# Сервер
net.ipv4.tcp_fastopen = 3  # 1=клиент, 2=сервер, 3=оба

# В приложении
setsockopt(fd, IPPROTO_TCP, TCP_FASTOPEN, &qlen, sizeof(qlen));
```

**Проблема:** многие middleboxes дропают SYN с данными. Adoption низкий.

---

## Часть 3.4: Передача данных — Sliding Window

### Окно отправки

TCP использует **скользящее окно** для управления потоком данных:

```
       snd_una          snd_nxt            snd_una + snd_wnd
         │                │                      │
         ▼                ▼                      ▼
┌────────┬────────────────┬──────────────────────┬──────────┐
│  ACKed │   Sent, not    │  Can send (window)   │ Cannot   │
│        │   ACKed yet    │                      │ send yet │
└────────┴────────────────┴──────────────────────┴──────────┘
```

В ядре это поля `struct tcp_sock`:

```c
struct tcp_sock {
    u32 snd_una;     // Первый неподтверждённый байт
    u32 snd_nxt;     // Следующий байт для отправки
    u32 snd_wnd;     // Размер окна получателя (rwnd)
    u32 snd_cwnd;    // Congestion window (в пакетах)

    u32 rcv_nxt;     // Следующий ожидаемый байт от отправителя
    u32 rcv_wnd;     // Наше окно приёма (сколько готовы принять)

    // Реальное окно отправки = min(snd_wnd, snd_cwnd * mss)
};
```

**Эффективное окно** = `min(rwnd, cwnd)`. TCP отправляет не быстрее, чем позволяет **самое маленькое** из двух ограничений: получатель (rwnd) или сеть (cwnd).

### Window Scaling

Классическое поле Window в TCP-заголовке — 16 бит (максимум 65535 байт). Для 10 Gbps канала с RTT=100мс нужно окно ~125 MB. Решение — **Window Scale option** (RFC 7323):

```
Реальное окно = Window × 2^scale_factor
```

Scale factor согласовывается в SYN/SYN+ACK (0..14). При scale=7: `65535 × 128 = 8 MB`.

```bash
# Включено по умолчанию
net.ipv4.tcp_window_scaling = 1
```

### Delayed ACK

Получатель не обязан отвечать ACK на каждый пакет. Он может подождать:
- До 200 мс (таймер)
- До получения второго пакета (ACK every other segment)

```c
// net/ipv4/tcp_input.c
// Delayed ACK таймер
inet_csk(sk)->icsk_ack.timeout = TCP_DELACK_MIN;  // ~40ms по умолчанию
```

**Проблема с Nagle:** если приложение отправляет маленькие порции, Nagle буферизирует их, ожидая ACK. Delayed ACK задерживает ACK. Результат: 200мс задержки. Решение: `TCP_NODELAY` (подробнее в Модуле 5).

---

## Часть 3.5: Retransmission — Когда пакеты теряются

### RTO (Retransmission Timeout)

Если ACK не пришёл за RTO — пакет считается потерянным и отправляется повторно.

```c
// net/ipv4/tcp_input.c
// Обновление SRTT и RTO по алгоритму Jacobson/Karels
static void tcp_rtt_estimator(struct sock *sk, long mrtt_us)
{
    struct tcp_sock *tp = tcp_sk(sk);
    long m = mrtt_us;  // Измеренный RTT

    if (tp->srtt_us == 0) {
        // Первое измерение
        tp->srtt_us = m << 3;      // SRTT = RTT
        tp->mdev_us = m << 1;      // RTTVAR = RTT/2
    } else {
        // Экспоненциальное скользящее среднее
        // SRTT = (1 - 1/8) * SRTT + 1/8 * RTT
        long err = m - (tp->srtt_us >> 3);
        tp->srtt_us += err;

        // RTTVAR = (1 - 1/4) * RTTVAR + 1/4 * |RTT - SRTT|
        if (err < 0) err = -err;
        err -= (tp->mdev_us >> 2);
        tp->mdev_us += err;
    }

    // RTO = SRTT + 4 * RTTVAR
    // Минимум 200ms, максимум 120 секунд
    inet_csk(sk)->icsk_rto = max(TCP_RTO_MIN,
        (tp->srtt_us >> 3) + tp->mdev_us);
}
```

**RTO = SRTT + 4 × RTTVAR** — формула Jacobson/Karels (RFC 6298). SRTT (Smoothed RTT) — сглаженное среднее. RTTVAR — вариация. Множитель 4 обеспечивает устойчивость к джиттеру.

### Fast Retransmit

Ждать RTO (сотни мс) — слишком долго. Fast Retransmit реагирует на **3 дублирующих ACK**:

```c
// net/ipv4/tcp_input.c (упрощённо)
static void tcp_fastretrans_alert(struct sock *sk, ...)
{
    struct tcp_sock *tp = tcp_sk(sk);

    // 3 дублирующих ACK = потеря
    if (tp->sacked_out >= tp->reordering) {  // reordering обычно = 3
        // Входим в Fast Recovery
        tcp_enter_recovery(sk, false);

        // Повторная отправка потерянного сегмента
        tcp_retransmit_skb(sk, tcp_rtx_queue_head(sk), 1);
    }
}
```

### SACK (Selective Acknowledgment)

Без SACK получатель может сказать только «я получил всё до байта X». С SACK он сообщает **блоки** полученных данных:

```
Отправлено: [1-1000] [1001-2000] [2001-3000] [3001-4000]
Получено:   [1-1000]             [2001-3000] [3001-4000]

Без SACK:   ACK=1001  (потерян 1001-2000, но отправитель не знает про 2001+)
С SACK:     ACK=1001, SACK=2001-4000  (отправитель знает: повторить только 1001-2000)
```

```bash
# Включено по умолчанию
net.ipv4.tcp_sack = 1
```

### RACK (Recent ACKnowledgment)

Современная замена дупликатным ACK. Вместо подсчёта дупликатов RACK использует **время**: если пакет не подтверждён в течение `min_rtt + reordering_window`, он считается потерянным.

```c
// net/ipv4/tcp_recovery.c
// RACK определяет потерю по времени, а не по количеству дупликатов
static void tcp_rack_detect_loss(struct sock *sk, u32 *reo_timeout)
{
    struct tcp_sock *tp = tcp_sk(sk);

    // Порог: самый свежий ACK timestamp - reo_wnd
    // Если пакет отправлен раньше этого порога и не ACKed — потерян
    s32 remaining = tp->rack.rtt_us + tp->rack.reo_wnd_us -
                    tcp_stamp_us_delta(tp->tcp_mstamp, skb->skb_mstamp);

    if (remaining <= 0) {
        // Пакет потерян
        tcp_mark_skb_lost(sk, skb);
    }
}
```

RACK включён по умолчанию в современных ядрах и постепенно заменяет классический Fast Retransmit.

---

## Часть 3.6: Закрытие соединения — Four-Way Handshake

### Нормальное закрытие

```
Клиент (Active Close)              Сервер (Passive Close)
  │                                    │
  │  FIN (seq=X)                       │
  │───────────────────────────────────→│  FIN_WAIT_1 → CLOSE_WAIT
  │                                    │
  │  ACK (ack=X+1)                     │
  │←───────────────────────────────────│  FIN_WAIT_2
  │                                    │
  │       ... сервер может ещё         │
  │       отправлять данные ...        │
  │                                    │
  │  FIN (seq=Y)                       │
  │←───────────────────────────────────│  LAST_ACK
  │                                    │
  │  ACK (ack=Y+1)                     │
  │───────────────────────────────────→│  CLOSED
  │  TIME_WAIT (2MSL)                  │
  │       ...                          │
  │  CLOSED                            │
```

### CLOSE_WAIT: Ваш код сломан

CLOSE_WAIT означает: **другая сторона закрыла соединение (послала FIN), но ваше приложение не вызвало `close()`**. Это **всегда** баг в приложении — утечка сокетов.

```bash
# Найти процессы с утечкой
ss -tanp state close-wait
```

Если видите тысячи CLOSE_WAIT для одного процесса — ищите незакрытые соединения в коде.

### TIME_WAIT: Зачем ждать 2MSL

TIME_WAIT длится 2 × MSL (Maximum Segment Lifetime, обычно 60 секунд в Linux). Зачем?

1. **Защита от запоздалых пакетов**: если старый пакет от предыдущего соединения ещё бродит по сети, TIME_WAIT гарантирует, что он не попадёт в новое соединение с теми же (src, dst, sport, dport).
2. **Надёжное закрытие**: если последний ACK потерялся, сервер перешлёт FIN, и клиент в TIME_WAIT сможет ответить повторным ACK.

```c
// net/ipv4/tcp_minisocks.c
#define TCP_TIMEWAIT_LEN (60 * HZ)  // 60 секунд

// TIME_WAIT использует облегчённую структуру (не полный struct sock)
struct inet_timewait_sock {
    struct sock_common  __tw_common;
    volatile unsigned char tw_substate;
    unsigned char       tw_rcv_wscale;
    __be16              tw_sport;
    // ... минимум полей для экономии памяти
    // ~160 байт вместо ~2000 байт полного sock
};
```

### TIME_WAIT Exhaustion

На нагруженном прокси/балансировщике (Nginx, HAProxy) тысячи исходящих соединений закрываются каждую секунду. Каждое висит в TIME_WAIT 60 секунд. При 10K conn/sec: 600K сокетов в TIME_WAIT. Это исчерпывает ephemeral ports (по умолчанию 28232 порта).

**Решения:**

```bash
# 1. Переиспользовать TIME_WAIT для исходящих (БЕЗОПАСНО)
net.ipv4.tcp_tw_reuse = 1
# Работает ТОЛЬКО с tcp_timestamps=1
# Проверяет, что timestamp нового соединения > старого

# 2. Расширить диапазон ephemeral портов
net.ipv4.ip_local_port_range = 1024 65535
# Даёт 64511 портов вместо 28232

# 3. НИКОГДА не используйте tcp_tw_recycle
# Удалён из ядра с версии 4.12 (ломает NAT)
```

### RST: Аварийное закрытие

RST (Reset) — немедленное уничтожение соединения без handshake:

```c
// Отправка RST в ядре
// net/ipv4/tcp_output.c
void tcp_send_active_reset(struct sock *sk, gfp_t priority)
{
    struct sk_buff *skb;

    skb = alloc_skb(MAX_TCP_HEADER, priority);
    tcp_init_nondata_skb(skb, tcp_acceptable_seq(sk),
                          TCPHDR_ACK | TCPHDR_RST);
    tcp_transmit_skb(sk, skb, 0, priority);
}
```

**Когда ядро отправляет RST:**
- Пакет пришёл на закрытый порт (нет listener)
- Пакет не принадлежит ни одному известному соединению
- Приложение вызвало `close()` с `SO_LINGER` linger=0 (abort)
- SYN пришёл на порт, где backlog переполнен (и нет SYN cookies)

**Диагностика:**

```bash
# Счётчики RST
nstat -az | grep -i rst
# TcpExtTCPAbortOnData    — RST при наличии непрочитанных данных
# TcpExtTCPAbortOnClose   — RST из-за SO_LINGER=0
# TcpExtTCPAbortOnMemory  — RST из-за нехватки памяти
# TcpExtTCPAbortOnTimeout — RST по таймауту
```

---

## Часть 3.7: Таймеры TCP

TCP использует несколько таймеров, каждый с своей целью:

### 1. Retransmission Timer (RTO)

Пересылка неподтверждённых данных. Значение: `SRTT + 4×RTTVAR` (см. раздел 3.5).

```bash
# Посмотреть текущий RTO для соединения
ss -ti dst 10.0.0.2
# rto:204  — RTO в миллисекундах
```

### 2. Persist Timer (Zero Window Probe)

Когда получатель объявляет `rwnd=0` (буфер полон), отправитель не может слать данные. Persist timer периодически шлёт **window probe** — 1-байтовый сегмент, чтобы узнать, не открылось ли окно.

```c
// net/ipv4/tcp_timer.c
static void tcp_probe_timer(struct sock *sk)
{
    struct tcp_sock *tp = tcp_sk(sk);

    // Если окно всё ещё закрыто — отправляем probe
    if (tp->snd_wnd == 0) {
        tcp_send_probe0(sk);
        // Экспоненциальный backoff: 500ms, 1s, 2s, 4s, ...
        inet_csk_reset_xmit_timer(sk, ICSK_TIME_PROBE0,
                                   min(icsk->icsk_rto << icsk->icsk_backoff,
                                       TCP_RTO_MAX), TCP_RTO_MAX);
    }
}
```

### 3. Keepalive Timer

Проверяет, жив ли партнёр, при idle-соединении:

```bash
net.ipv4.tcp_keepalive_time = 7200   # 2 часа до первого probe
net.ipv4.tcp_keepalive_intvl = 75    # 75 сек между probe
net.ipv4.tcp_keepalive_probes = 9    # 9 проб до RST
```

Итого: соединение признаётся мёртвым через `7200 + 75 × 9 = 7875 секунд` (~2.2 часа). Для production это слишком долго — используйте application-level heartbeat.

```c
// Включение keepalive в коде
int val = 1;
setsockopt(fd, SOL_SOCKET, SO_KEEPALIVE, &val, sizeof(val));

// Настройка per-socket (переопределяет sysctl)
int idle = 60;    // Начать probes через 60 сек idle
int interval = 5; // Интервал 5 сек
int count = 3;    // 3 пробы

setsockopt(fd, IPPROTO_TCP, TCP_KEEPIDLE, &idle, sizeof(idle));
setsockopt(fd, IPPROTO_TCP, TCP_KEEPINTVL, &interval, sizeof(interval));
setsockopt(fd, IPPROTO_TCP, TCP_KEEPCNT, &count, sizeof(count));
```

### 4. TIME_WAIT Timer (2MSL)

60 секунд в Linux (не настраивается через sysctl, захардкожено в ядре).

### 5. FIN_WAIT_2 Timer

Если после отправки FIN и получения ACK вторая сторона не закрывается:

```bash
net.ipv4.tcp_fin_timeout = 60   # Таймаут FIN_WAIT_2 (секунды)
```

---

## Часть 3.8: Диагностика состояний TCP

### ss: Главный инструмент

```bash
# Все TCP-соединения с состояниями
ss -tan

# Только определённое состояние
ss -tan state time-wait
ss -tan state close-wait
ss -tan state established

# Подсчёт соединений по состояниям
ss -tan | awk 'NR>1 {print $1}' | sort | uniq -c | sort -rn

# Детальная информация о конкретном соединении
ss -ti dst 10.0.0.2:443
# Вывод: cubic wscale:7,7 rto:204 rtt:1.2/0.5 mss:1448
#         cwnd:10 ssthresh:7 bytes_acked:15230 segs_out:120
```

### nstat: Счётчики ядра

```bash
nstat -az | grep -i tcp

# Ключевые метрики:
# TcpActiveOpens        — connect() вызовы (клиент)
# TcpPassiveOpens       — accept() вызовы (сервер)
# TcpInSegs / TcpOutSegs — входящие/исходящие сегменты
# TcpRetransSegs        — ретрансмиты (должно быть < 1% от OutSegs)
# TcpExtTCPTimeouts     — RTO таймауты
# TcpExtTCPLossProbes   — TLP probes (tail loss)
# TcpExtTCPOFOQueue     — out-of-order пакеты
```

### /proc/net/tcp: Прямой доступ к таблице

```bash
# Каждая строка — TCP-соединение
cat /proc/net/tcp
#  sl  local_address rem_address   st tx_queue rx_queue ...
#   0: 0100007F:1F90 0100007F:C5D4 01 00000000:00000000 ...
#                                   ^^
#                                   st = state (01 = ESTABLISHED)
```

Состояние `st` в hex: 01=ESTABLISHED, 02=SYN_SENT, 06=TIME_WAIT, 08=CLOSE_WAIT, 0A=LISTEN.

### eBPF трассировка

```bash
# Отслеживание смены состояний TCP в реальном времени
bpftrace -e '
tracepoint:tcp:tcp_set_state {
    printf("%-6d %-16s %-6d -> %-6d %s:%d -> %s:%d\n",
           args->oldstate, comm, args->oldstate, args->newstate,
           ntop(AF_INET, &args->saddr), args->sport,
           ntop(AF_INET, &args->daddr), args->dport);
}
'

# Распределение RTT по соединениям
bpftrace -e '
kretprobe:tcp_rcv_established {
    @rtt = hist(((struct tcp_sock *)arg0)->srtt_us >> 3);
}
'
```

---

## Часть 3.9: Типичные production-проблемы

### Проблема 1: Тысячи TIME_WAIT

**Симптом:** `ss -s` показывает 50K+ timewait. Новые исходящие соединения отвергаются с `EADDRNOTAVAIL`.

**Решение:**
```bash
net.ipv4.tcp_tw_reuse = 1
net.ipv4.ip_local_port_range = 1024 65535
```

Также: используйте connection pooling (HTTP keep-alive, gRPC persistent connections).

### Проблема 2: SYN_RECV растёт

**Симптом:** `ss -tan state syn-recv | wc -l` показывает тысячи. Легитимные клиенты не могут подключиться.

**Диагностика:** SYN flood или слишком маленький backlog.

**Решение:**
```bash
net.ipv4.tcp_syncookies = 1
net.ipv4.tcp_max_syn_backlog = 65535
net.core.somaxconn = 65535
```

### Проблема 3: CLOSE_WAIT накапливаются

**Симптом:** тысячи CLOSE_WAIT для одного процесса.

**Причина:** приложение не вызывает `close()` на сокете после получения EOF.

**Решение:** искать баг в коде. Проверить утечки file descriptors:
```bash
ls -la /proc/<pid>/fd | wc -l
```

### Проблема 4: Высокий % ретрансмитов

**Симптом:** `nstat` показывает `TcpRetransSegs / TcpOutSegs > 1%`.

**Диагностика:**
```bash
# Какие соединения теряют пакеты
tcpretrans  # из bcc-tools

# Или
ss -ti | grep retrans
```

**Причины:** congestion, битый кабель, перегруженный свитч, firewall дропает.

---

## Практическое задание

### Задача 1: Наблюдение за TCP state machine

Запустите bpftrace трассировку `tcp_set_state`. Откройте в браузере сайт. Наблюдайте переходы: CLOSED → SYN_SENT → ESTABLISHED → FIN_WAIT_1 → FIN_WAIT_2 → TIME_WAIT → CLOSED.

### Задача 2: SYN Flood и защита

На стенде запустите `hping3 --syn --flood` на сервер. Наблюдайте рост SYN_RECV через `ss -tan state syn-recv | wc -l`. Включите SYN cookies и повторите — accept queue должна оставаться стабильной.

### Задача 3: TIME_WAIT exhaustion

Напишите скрипт, открывающий и закрывающий тысячи TCP-соединений в секунду. Наблюдайте накопление TIME_WAIT. Включите `tcp_tw_reuse` и проверьте разницу.

### Задача 4: CLOSE_WAIT детектив

Напишите сервер, который намеренно не вызывает `close()` после получения FIN от клиента. Подключитесь 100 раз и закройте клиент. Убедитесь, что сервер накопил 100 CLOSE_WAIT. Найдите их через `ss -tanp state close-wait`.

### Задача 5: RTO и Fast Retransmit

На стенде добавьте 5% потерь через `tc netem loss 5%`. Запустите iperf3 и одновременно трейсите ретрансмиты через `tcpretrans`. Подсчитайте: какая доля ретрансмитов была по RTO таймауту, а какая — по Fast Retransmit (3 dup ACK)?

```bash
# На роутере
tc qdisc add dev ens34 root netem loss 5%

# На клиенте
tcpretrans &
iperf3 -c 192.168.50.10 -t 30
```

---

**Следующий модуль:** Traffic Control, Тюнинг и Диагностика — как ядро планирует отправку пакетов и как настроить приоритезацию трафика в production.
