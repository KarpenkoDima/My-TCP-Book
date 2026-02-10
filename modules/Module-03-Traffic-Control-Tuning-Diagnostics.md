# Модуль 3: Traffic Control, Тюнинг и Диагностика (Режим Бога)

*«Qdisc — это не просто очередь. Это политика, судья и палач в одном лице».*

В первом модуле мы разобрали, как пакет попадает в систему (RX path). Во втором — как TCP решает, сколько данных отправить. Теперь мы спускаемся на уровень ниже и смотрим на последний кусок головоломки: **как ядро планирует отправку пакетов на провод** (TX path).

Это критично важно, потому что здесь решается, кто первый попадёт в бутылочное горлышко. SSH-пакет или торрент? DNS-запрос или backup? И если все хотят отправить одновременно, а канал один — кто-то должен решить.

---

## Часть 3.1: Архитектура Qdisc — Как ядро управляет очередями

### Что такое Qdisc на самом деле

Qdisc (Queueing Discipline) — это абстракция в ядре Linux, которая определяет **политику планирования** для исходящих пакетов на сетевом интерфейсе. Когда приложение вызывает `send()` или когда ядро форвардит пакет, он не летит сразу на NIC. Он попадает в qdisc, который решает:

1. **Принять** пакет в очередь (enqueue)
2. **Когда** его отправить (dequeue)
3. **Дропнуть** ли его, если очередь переполнена

Каждый сетевой интерфейс в Linux имеет qdisc. По умолчанию это `pfifo_fast` или `fq_codel` (в новых ядрах), но вы можете заменить его на любой другой.

### Структура данных: struct Qdisc

В ядре qdisc представлен структурой `struct Qdisc` (`include/net/sch_generic.h`):

```c
struct Qdisc {
    int (*enqueue)(struct sk_buff *skb, struct Qdisc *sch,
                   struct sk_buff **to_free);
    struct sk_buff *(*dequeue)(struct Qdisc *sch);
    unsigned int    flags;

    struct Qdisc_ops    *ops;        // Таблица операций конкретного типа qdisc
    u32                 limit;       // Максимальный размер очереди (пакеты или байты)
    u32                 handle;      // Уникальный ID (например, 1:0)
    struct netdev_queue *dev_queue;  // Привязка к TX queue устройства

    struct gnet_stats_basic_packed bstats;  // Счётчики: bytes, packets
    struct gnet_stats_queue qstats;         // drops, overlimits, backlog

    struct sk_buff_head q;  // Для простых qdisc — встроенная очередь
    // ... ещё много полей
};
```

Ключевые поля:

- **enqueue**: функция добавления пакета. Возвращает `NET_XMIT_SUCCESS` (принят), `NET_XMIT_DROP` (дропнут) или `NET_XMIT_CN` (congestion notification).
- **dequeue**: функция извлечения следующего пакета для отправки. Возвращает `sk_buff*` или `NULL` (очередь пуста).
- **ops**: указатель на `struct Qdisc_ops` — таблицу операций, специфичных для типа qdisc.
- **bstats/qstats**: счётчики, которые вы видите в `tc -s qdisc show`.

### Операции Qdisc: struct Qdisc_ops

Каждый тип qdisc (pfifo, fq_codel, htb, cake) регистрирует свою таблицу операций:

```c
struct Qdisc_ops {
    struct Qdisc_ops *next;
    const char      *id;          // Имя: "fq_codel", "htb", "cake"
    int             priv_size;    // Размер приватных данных

    int (*enqueue)(struct sk_buff *, struct Qdisc *, struct sk_buff **);
    struct sk_buff *(*dequeue)(struct Qdisc *);
    struct sk_buff *(*peek)(struct Qdisc *);

    int (*init)(struct Qdisc *, struct nlattr *arg, struct netlink_ext_ack *);
    void (*reset)(struct Qdisc *);
    void (*destroy)(struct Qdisc *);
    int (*change)(struct Qdisc *, struct nlattr *arg, struct netlink_ext_ack *);

    int (*dump)(struct Qdisc *, struct sk_buff *);
    int (*dump_stats)(struct Qdisc *, struct gnet_dump *);

    struct module   *owner;
};
```

Пример регистрации для `fq_codel` (`net/sched/sch_fq_codel.c`):

```c
static struct Qdisc_ops fq_codel_qdisc_ops __read_mostly = {
    .id         = "fq_codel",
    .priv_size  = sizeof(struct fq_codel_sched_data),
    .enqueue    = fq_codel_enqueue,
    .dequeue    = fq_codel_dequeue,
    .peek       = qdisc_peek_dequeued,
    .init       = fq_codel_init,
    .reset      = fq_codel_reset,
    .destroy    = fq_codel_destroy,
    .change     = fq_codel_change,
    .dump       = fq_codel_dump,
    .dump_stats = fq_codel_dump_stats,
    .owner      = THIS_MODULE,
};
```

При загрузке модуля вызывается `register_qdisc(&fq_codel_qdisc_ops)`, и ядро добавляет его в глобальный список доступных типов qdisc.

### Путь пакета: от send() до провода

Когда приложение отправляет данные, пакет проходит длинный путь:

```
send() syscall
  ↓
tcp_sendmsg() / udp_sendmsg()
  ↓
ip_queue_xmit() / ip_send_skb()
  ↓
ip_finish_output() → ip_finish_output2()
  ↓
neigh_output() [ARP resolution]
  ↓
dev_queue_xmit()          ← ЗДЕСЬ НАЧИНАЕТСЯ QDISC
  ↓
__dev_xmit_skb()
  ↓
qdisc->enqueue(skb)       // Пакет встаёт в очередь
  ↓
__qdisc_run()
  ↓
qdisc_restart()            // Цикл извлечения
  ↓  (loop)
  skb = qdisc->dequeue()
  ↓
sch_direct_xmit()
  ↓
dev_hard_start_xmit()
  ↓
netdev_ops->ndo_start_xmit()  // Драйвер NIC
  ↓
DMA на карту → Провод
```

### Критичная функция: __dev_xmit_skb

Это сердце TX path. Упрощённая версия (`net/core/dev.c`):

```c
static inline int __dev_xmit_skb(struct sk_buff *skb, struct Qdisc *q,
                                  struct net_device *dev,
                                  struct netdev_queue *txq)
{
    spinlock_t *root_lock = qdisc_lock(q);
    bool contended = qdisc_is_running(q);
    int rc;

    // Быстрый путь: если очередь пуста, отправляем напрямую (bypass)
    if (q->flags & TCQ_F_CAN_BYPASS && !qdisc_qlen(q) &&
        qdisc_run_begin(q)) {
        // Минуем очередь — пакет летит сразу на NIC
        if (sch_direct_xmit(skb, q, dev, txq, root_lock, true)) {
            __qdisc_run(q);  // Проверяем, не появилось ли ещё пакетов
        }
        qdisc_run_end(q);
        return NET_XMIT_SUCCESS;
    }

    // Медленный путь: ставим в очередь
    spin_lock(root_lock);
    rc = q->enqueue(skb, q, &to_free);
    if (qdisc_run_begin(q)) {
        // Запускаем планировщик
        __qdisc_run(q);
        qdisc_run_end(q);
    }
    spin_unlock(root_lock);

    return rc;
}
```

Два ключевых момента:

1. **Быстрый путь (bypass)**: если очередь пуста и qdisc разрешает bypass (`TCQ_F_CAN_BYPASS`), пакет летит сразу на NIC. Это критично для латентности — пакет не ждёт ни секунды.
2. **Медленный путь**: пакет ставится в очередь через `enqueue()`, потом запускается `__qdisc_run()`.

### __qdisc_run: Планировщик отправки

Эта функция вызывается для обработки очереди:

```c
void __qdisc_run(struct Qdisc *q)
{
    int quota = dev_tx_weight;  // Обычно 64 пакета
    int packets;

    while (qdisc_restart(q, &packets)) {
        quota -= packets;

        // Исчерпали квоту — планируем отложенную обработку
        if (quota <= 0) {
            __netif_schedule(q);
            break;
        }

        // Другие процессы ждут CPU — отдаём управление
        if (need_resched()) {
            __netif_schedule(q);
            break;
        }
    }
}
```

`qdisc_restart()` вызывает `dequeue()`, достаёт пакет и отправляет его через `sch_direct_xmit()`. Это повторяется в цикле, пока не исчерпается квота (`dev_tx_weight`, обычно 64 пакета) или не опустеет очередь.

**Зачем квота?** Чтобы TX path не монополизировал CPU. Если в очереди миллион пакетов, ядро отправит 64, потом вернёт управление, потом снова 64. Это soft-realtime поведение, гарантирующее отзывчивость системы.

### Интеграция с NAPI (TX Completion)

Когда NIC отправил пакет, он генерирует прерывание TX completion. Драйвер освобождает DMA-буферы и вызывает `netif_tx_wake_queue()`, что может запустить `__qdisc_run()` снова. Современные NIC используют interrupt coalescing для TX: группируют прерывания (например, раз в 50 микросекунд или после 32 пакетов).

---

## Часть 3.2: Classless vs Classful Qdisc

### Classless Qdisc: Одна политика для всех

Простые qdisc без иерархии классов. Все пакеты проходят одну логику.

**pfifo_fast** — дефолт в старых ядрах. Три приоритетные очереди (band 0, 1, 2) на основе TOS/DSCP. Band 0 обслуживается первым. Проблема: нет справедливости, жирный поток забивает всё.

**fq_codel** — дефолт в новых ядрах. Fair Queueing + CoDel. Автоматически разделяет потоки по хешу и контролирует задержку. Для большинства серверов этого достаточно.

**cake** — всё-в-одном: Fair Queueing + AQM + шейпинг + NAT awareness. Идеален для домашних роутеров.

**tbf** (Token Bucket Filter) — простое ограничение скорости через алгоритм токенного ведра. Пакеты отправляются только при наличии токенов.

### Classful Qdisc: Иерархия и политики

Позволяют создавать **дерево классов** и распределять трафик по правилам.

```
       [HTB root 1:0]
            |
    +-------+-------+-------+
    |               |               |
[Class 1:10]   [Class 1:20]   [Class 1:30]
 (SSH/DNS)     (HTTP/HTTPS)    (Bulk)
    |               |               |
[fq_codel]     [fq_codel]     [fq_codel]
```

**HTB (Hierarchy Token Bucket)** — самый популярный. Иерархическое ограничение с гарантиями (`rate`) и потолками (`ceil`).

**HFSC (Hierarchical Fair Service Curve)** — позволяет задать гарантии по латентности, а не только по пропускной способности.

**PRIO** — строгие приоритеты: высокоприоритетный класс всегда обслуживается первым.

**CBQ (Class-Based Queueing)** — устаревший, сложный в настройке. Не используйте.

### HTB: Структура данных в ядре

```c
struct htb_class {
    struct Qdisc_class_common common;
    struct htb_class   *parent;       // Родитель в иерархии
    struct list_head   children;      // Дочерние классы

    struct Qdisc       *leaf_q;       // Child qdisc (если лист дерева)

    u64     rate;      // Гарантированная скорость (bytes/sec)
    u64     ceil;      // Максимальная скорость (bytes/sec)
    s64     tokens;    // Текущие токены для rate
    s64     ctokens;   // Текущие токены для ceil

    int     prio;      // Приоритет при конкуренции за излишек
    int     quantum;   // Байт за один round-robin цикл

    struct gnet_stats_basic_packed bstats;
    struct gnet_stats_queue qstats;
};
```

HTB использует **token bucket** — алгоритм «токенного ведра». У каждого класса два ведра:

- **tokens**: наполняются со скоростью `rate`. Класс может отправлять, пока есть токены.
- **ctokens**: наполняются со скоростью `ceil`. Класс может превысить `rate`, забирая неиспользованную полосу других классов, но не выше `ceil`.

При `dequeue()` HTB выбирает класс с наивысшим приоритетом, у которого есть токены, и забирает пакет из его child qdisc.

### HFSC: Гарантии по латентности

HTB гарантирует пропускную способность, но не задержку. HFSC позволяет задать **service curve** — функцию, описывающую минимальный сервис во времени.

```bash
tc qdisc add dev eth0 root handle 1: hfsc default 30

# В первые 10 мс класс получает 100 Mbit (burst), потом 50 Mbit sustained
tc class add dev eth0 parent 1: classid 1:10 hfsc \
    sc m1 100mbit d 10ms m2 50mbit
```

- `m1 100mbit d 10ms`: в первые 10 мс класс получает 100 Mbit (burst для латентности).
- `m2 50mbit`: после burst класс получает 50 Mbit sustained.

HFSC сложнее HTB, но даёт тонкий контроль над latency-sensitive трафиком (VoIP, игры).

---

## Часть 3.3: TC Filters и Классификация

Classful qdisc бесполезен без механизма классификации — как ядро решает, в какой класс направить пакет?

### u32: Universal 32-bit classifier

Самый мощный и гибкий. Позволяет матчить любые биты в IP/TCP/UDP заголовках.

```bash
# Матч по destination port (HTTPS)
tc filter add dev eth0 protocol ip parent 1:0 prio 1 u32 \
    match ip dport 443 0xffff \
    flowid 1:20
```

Как это работает: берём поле destination port, применяем маску `0xffff` (все 16 бит), сравниваем с `443`. Можно матчить диапазоны: `match ip dport 8000 0xff00` поймает порты 8000-8255.

Продвинутый пример — матч по IP-адресу и TOS:

```bash
tc filter add dev eth0 protocol ip parent 1:0 prio 1 u32 \
    match ip dst 192.168.1.0/24 \
    match ip tos 0x10 0xff \
    flowid 1:10
```

### fw: Firewall mark classifier

Использует метки, установленные `iptables -j MARK`. Самый быстрый способ классификации.

```bash
# В iptables: маркируем пакеты
iptables -t mangle -A POSTROUTING -p tcp --dport 22 -j MARK --set-mark 1
iptables -t mangle -A POSTROUTING -p tcp --dport 443 -j MARK --set-mark 2

# В tc: классифицируем по метке
tc filter add dev eth0 protocol ip parent 1:0 prio 1 handle 1 fw flowid 1:10
tc filter add dev eth0 protocol ip parent 1:0 prio 1 handle 2 fw flowid 1:20
```

**Преимущество:** вся сложная логика (conntrack, state matching, маркировка по имени процесса) делается в netfilter один раз. TC просто читает `skb->mark` — это O(1).

**Недостаток:** два прохода (netfilter + tc). Небольшой дополнительный overhead.

### flow: Hash-based автоматическая классификация

Автоматически распределяет потоки по классам через хеширование:

```bash
tc filter add dev eth0 protocol ip parent 1:0 prio 1 \
    handle 1 flow hash keys src,dst,proto,proto-src,proto-dst divisor 1024
```

Полезно для автоматической балансировки без явных правил.

### cgroup: Per-container traffic control

Классифицирует пакеты по cgroup процесса-отправителя:

```bash
tc filter add dev eth0 protocol ip parent 1:0 prio 1 cgroup
```

Процессы в cgroup `/sys/fs/cgroup/net_cls/<name>` автоматически получают classid. Мощный инструмент для контейнеризации — можно лимитировать трафик Docker-контейнеров.

### Приоритеты фильтров

Если несколько фильтров матчат пакет, используется тот, у которого меньше `prio`:

```bash
# Prio 1: специфичные правила (SSH) — проверяются первыми
tc filter add dev eth0 parent 1:0 prio 1 u32 match ip dport 22 0xffff flowid 1:10

# Prio 10: общие правила (весь TCP) — ловят остальное
tc filter add dev eth0 parent 1:0 prio 10 u32 match ip protocol 6 0xff flowid 1:30
```

---

## Часть 3.4: Production Example — Полная настройка для веб-сервера

**Сценарий:** сервер с гигабитным каналом. HTTPS, SSH для администрирования, ночные бэкапы. Нужно гарантировать:

1. SSH и DNS всегда отзывчивы (100 Mbit гарантировано, до 300 Mbit burst)
2. HTTPS получает основную долю (600 Mbit, до 900 Mbit)
3. Бэкапы используют остатки (100 Mbit, могут забрать всё свободное)

### Шаг 1: HTB root

```bash
tc qdisc add dev eth0 root handle 1: htb default 30
```

- `handle 1:` — ID root qdisc (major:minor, здесь 1:0)
- `default 30` — пакеты без классификации идут в класс 1:30

### Шаг 2: Классы

```bash
# Интерактивный трафик (SSH, DNS) — высший приоритет
tc class add dev eth0 parent 1: classid 1:10 htb \
    rate 100mbit ceil 300mbit prio 0 quantum 1500

# HTTPS — основная нагрузка
tc class add dev eth0 parent 1: classid 1:20 htb \
    rate 600mbit ceil 900mbit prio 1 quantum 1500

# Bulk (бэкапы, обновления) — низший приоритет
tc class add dev eth0 parent 1: classid 1:30 htb \
    rate 100mbit ceil 1000mbit prio 2 quantum 1500
```

Параметры:
- `rate`: гарантированная полоса. Класс **всегда** получит эту скорость, если есть трафик.
- `ceil`: потолок. Класс может превысить `rate`, заняв неиспользуемое другими, но не выше `ceil`.
- `prio`: приоритет при конкуренции за излишек. 0 — высший.
- `quantum`: байт за один round-robin цикл. Обычно = MTU (1500).

**Логика:** если все три класса активны, каждый получит свой `rate`: 100 + 600 + 100 = 800 Mbit. Если SSH молчит, его 100 Mbit перераспределяются — HTTPS (prio 1) получит первым.

### Шаг 3: Child qdisc под каждый класс

```bash
tc qdisc add dev eth0 parent 1:10 handle 10: fq_codel
tc qdisc add dev eth0 parent 1:20 handle 20: fq_codel
tc qdisc add dev eth0 parent 1:30 handle 30: fq_codel
```

HTB сам не имеет очереди для пакетов — он только распределяет полосу. Реальная очередь — в child qdisc. Мы используем `fq_codel` для справедливости и контроля задержки внутри каждого класса.

### Шаг 4: Фильтры (iptables + fw)

```bash
# Маркируем пакеты в iptables
iptables -t mangle -A POSTROUTING -o eth0 -p tcp --dport 22 -j MARK --set-mark 0x10
iptables -t mangle -A POSTROUTING -o eth0 -p udp --dport 53 -j MARK --set-mark 0x10
iptables -t mangle -A POSTROUTING -o eth0 -p tcp --dport 443 -j MARK --set-mark 0x20
iptables -t mangle -A POSTROUTING -o eth0 -p tcp --sport 873 -j MARK --set-mark 0x30

# Классифицируем в tc по меткам
tc filter add dev eth0 protocol ip parent 1:0 prio 1 handle 0x10 fw flowid 1:10
tc filter add dev eth0 protocol ip parent 1:0 prio 1 handle 0x20 fw flowid 1:20
tc filter add dev eth0 protocol ip parent 1:0 prio 1 handle 0x30 fw flowid 1:30
```

### Шаг 5: Мониторинг

```bash
# Статистика qdisc
tc -s qdisc show dev eth0

# Статистика классов
tc -s class show dev eth0

# Статистика фильтров
tc -s filter show dev eth0

# Живой мониторинг
watch -n 1 'tc -s class show dev eth0'
```

Пример вывода `tc -s class show`:

```
class htb 1:10 parent 1: prio 0 rate 100Mbit ceil 300Mbit burst 1600b cburst 1600b
 Sent 524288000 bytes 350000 pkt (dropped 12, overlimits 450)
 rate 95Mbit 63000pps backlog 0b 0p requeues 0
```

- `Sent`: сколько прошло через класс
- `dropped`: дропы (переполнение child qdisc)
- `overlimits`: сколько раз класс пытался превысить `ceil`
- `rate`: текущая скорость (измеренная)
- `backlog`: сколько сейчас в очереди

---

## Часть 3.5: Chaos Engineering с tc netem

### Эмуляция плохих сетей

`netem` позволяет искусственно ухудшить сеть для тестирования отказоустойчивости.

```bash
# Задержка 100мс ± 20мс (нормальное распределение)
tc qdisc add dev eth0 root netem delay 100ms 20ms distribution normal

# Потеря 1% пакетов
tc qdisc change dev eth0 root netem delay 100ms 20ms loss 1%

# Дубликаты и переупорядочивание (ад для TCP)
tc qdisc change dev eth0 root netem delay 100ms reorder 25% 50%

# Ограничение полосы (1 Мбит)
tc qdisc add dev eth0 root handle 1: tbf rate 1mbit burst 32kbit latency 400ms
tc qdisc add dev eth0 parent 1:1 netem delay 100ms loss 1%

# Сброс всего
tc qdisc del dev eth0 root
```

**Зачем:** если ваш сервис падает при потере 1% пакетов — его нельзя в продакшен. Тестируйте таймауты и ретраи **до** релиза.

**Практический сценарий:** перед деплоем мобильного приложения:

```bash
# Эмулируем 4G с высокой вариабельностью
tc qdisc add dev eth0 root netem delay 80ms 40ms distribution pareto loss 2% duplicate 0.5%
```

Запускаем автотесты. Если приложение зависает или выдаёт ошибки — нужно чинить.

---

## Часть 3.6: Микроберсты (The Invisible Assassin)

### Почему графики мониторинга врут

Вы смотрите в Grafana: загрузка канала 500 Mbps при линке 1 Gbps. Но пакеты дропаются. Почему?

**Ответ:** вы смотрите на среднее за 1 секунду. Реальный трафик — это вспышки.

Представьте: в течение 100 мс прилетает пачка данных на 10 Gbps. Буфер переполняется мгновенно. Следующие 900 мс — тишина. Среднее за секунду: 1 Gbps. Всё выглядит нормально. Реальность: дропы и тормоза.

### Как обнаружить

```bash
# Смотрим дропы очередей
tc -s qdisc show dev eth0
```

Ищите поле `dropped`. Если счётчик растёт при «нормальной» средней загрузке — это микроберсты.

```bash
# Смотрим статистику драйвера
ethtool -S eth0 | grep -E 'tx_.*drop|tx_.*err'
```

### Решения

1. **BBR Pacing** — BBR отправляет пакеты равномерно, не пачками. Это сглаживает берсты на отправке.
2. **Traffic shaping** — шейпим трафик чуть ниже лимита канала.
3. **Увеличить txqueuelen** — увеличивает буфер, но ценой латентности (плохой компромисс).
4. **fq qdisc** — Fair Queueing естественным образом растягивает берсты.

---

## Часть 3.7: eBPF и BCC — Рентген ядра

Когда `tcpdump` не показывает причину (задержка внутри самого ядра), на сцену выходит **eBPF** — способ безопасно запускать код прямо в ядре без перекомпиляции.

### BCC Tools — готовый набор инструментов

Установка: `apt install bpfcc-tools` (Ubuntu) или `dnf install bcc-tools` (Fedora).

**tcpretrans** — кто теряет пакеты? Показывает каждый ретрансмит в реальном времени:

```bash
$ tcpretrans
TIME     PID    LADDR:LPORT  -> RADDR:RPORT  STATE
10:00:01 1234   10.0.0.1:22  -> 192.168.1.5  ESTABLISHED
10:00:02 5678   10.0.0.1:443 -> 172.16.0.3   ESTABLISHED
```

**tcplife** — статистика жизни соединений. Показывает, сколько байт передано, длительность и RTT. Идеально для поиска медленных запросов к БД.

```bash
$ tcplife
PID   COMM       LADDR:LPORT  RADDR:RPORT    TX_KB  RX_KB  MS
1234  nginx      10.0.0.1:443 172.16.0.3     1520   45     3200
5678  postgres   10.0.0.1:5432 10.0.0.2      0      128    15
```

**tcpconnlat** — задержка рукопожатия (SYN → ACK). Если тут большие числа — проблема в перегрузке CPU на приёмной стороне или в backlog queue overflow.

```bash
$ tcpconnlat
PID    COMM       IP SADDR:SPORT  DADDR:DPORT  LAT(ms)
1234   curl       4  10.0.0.1:42  93.184.216.34:443  45.2
```

### Custom bpftrace скрипты

Трейсим каждый dequeue из HTB:

```bash
bpftrace -e '
kprobe:htb_dequeue {
    printf("%llu: HTB dequeue on CPU %d\n", nsecs, cpu);
}
'
```

Трейсим TCP congestion events:

```bash
bpftrace -e '
tracepoint:tcp:tcp_probe {
    printf("cwnd=%d ssthresh=%d snd_wnd=%d srtt=%d\n",
           args->snd_cwnd, args->ssthresh,
           args->snd_wnd, args->srtt_us);
}
'
```

---

## Часть 3.8: Sysctl Tuning — Опасная зона

Интернет полон вредных советов («скопируй эти 20 строк в sysctl.conf, и сеть полетит»). Разберём главные параметры и **почему** они такие.

### Буферы памяти: tcp_rmem, tcp_wmem

Формат: `min default max`.

```bash
# Дефолт в Linux
net.ipv4.tcp_rmem = 4096  131072  6291456
net.ipv4.tcp_wmem = 4096  16384   4194304
```

**Ошибка новичка:** ставить огромные `min` и `default`. Это заставит ядро выделять мегабайты на **каждое** idle-соединение. При 10K соединений сервер упадёт в OOM.

**Правило:** трогайте только `max`. Позвольте ядру (autotuning) самому выбирать размер.

**Расчёт max:** `Max_Buffer = Bandwidth (Bps) × RTT (sec)`. Для 10 Gbps и 100 мс RTT:

```
Max_Buffer = 10,000,000,000 / 8 × 0.1 = 125,000,000 байт ≈ 125 МБ
```

```bash
# Для 10Gbps сервера
net.ipv4.tcp_rmem = 4096 131072 134217728
net.ipv4.tcp_wmem = 4096 16384  134217728
net.core.rmem_max = 134217728
net.core.wmem_max = 134217728
```

### TIME_WAIT

**tcp_tw_reuse = 1** — **ВКЛЮЧИТЬ**. Позволяет переиспользовать сокеты в TIME_WAIT для исходящих соединений, если TCP timestamps говорят, что это безопасно. Спасает при высокой нагрузке на прокси (Nginx, HAProxy).

**tcp_tw_recycle** — **ЗАБЫТЬ И НЕ ТРОГАТЬ**. Ломает соединения от клиентов за NAT (офисы, мобильные сети). Удалён из новых ядер, но в старых гайдах всплывает постоянно.

### SYN Cookies

```bash
net.ipv4.tcp_syncookies = 1
```

Включить. Защита от SYN Flood атак. Когда backlog переполнен, ядро не запоминает соединение, а шифрует параметры в Sequence Number. Клиент возвращает их в ACK, и ядро восстанавливает состояние.

### tcp_timestamps за NAT

TCP timestamps используются для PAWS (Protection Against Wrapped Sequence numbers) и RTT measurement. Но за NAT-ом несколько клиентов имеют один IP. Если их timestamps «перепрыгивают» (один клиент послал ts=1000, другой ts=500), сервер может дропнуть пакеты.

**Рекомендация:** timestamps оставить включёнными (`tcp_timestamps = 1`), но не использовать `tcp_tw_recycle` (который полагается на per-IP timestamp tracking).

---

## Часть 3.9: Performance Impact и Оптимизация

### Overhead от классификации

Каждый фильтр добавляет латентность:

| Классификатор | Overhead на пакет |
|---|---|
| fw (firewall mark) | ~50–100 нс |
| u32 (простое правило) | ~200–500 нс |
| u32 (сложное, много матчей) | ~1–2 мкс |
| iptables + fw | ~500 нс – 1 мкс |

**Рекомендация:** для high-throughput используйте `fw` с iptables mangle. Для простых случаев `u32` достаточен. Избегайте каскадов u32-правил.

### Lockless Qdisc для multi-queue NICs

Современные 10G/40G/100G карты имеют множество TX queues. Каждая может обслуживаться отдельным CPU без lock contention. Но классический qdisc имеет глобальный spinlock.

**Решение:** `mq` (multi-queue) как root + независимые qdisc для каждой очереди:

```bash
# Автоматически создаём mq для multi-queue NIC
tc qdisc add dev eth0 root mq

# Под каждую TX queue ставим fq_codel
for i in $(seq 0 15); do  # Если 16 TX queues
    tc qdisc add dev eth0 parent :$(printf '%x' $((i+1))) fq_codel
done
```

Каждое CPU core работает со своей TX queue и своим qdisc без конкуренции за lock. Критично для >40 Gbps.

### noqueue: Когда qdisc не нужен

Для виртуальных интерфейсов (veth, bridge, tun) часто не нужен qdisc:

```bash
tc qdisc add dev veth0 root noqueue
```

Пакеты отправляются немедленно без буферизации. Снижает латентность для inter-container communication.

---

## Часть 3.10: Продвинутые техники

### IFB: Ingress shaping

`tc` по умолчанию работает только на egress (TX path). Для контроля входящего трафика используем IFB — виртуальный интерфейс, перенаправляющий ingress на egress:

```bash
# Загружаем модуль
modprobe ifb numifbs=1
ip link set dev ifb0 up

# Перенаправляем ingress eth0 → egress ifb0
tc qdisc add dev eth0 handle ffff: ingress
tc filter add dev eth0 parent ffff: protocol ip u32 match u32 0 0 \
    action mirred egress redirect dev ifb0

# Настраиваем шейпинг на ifb0 как обычный egress
tc qdisc add dev ifb0 root handle 1: htb default 30
tc class add dev ifb0 parent 1: classid 1:10 htb rate 50mbit
```

### Динамическая приоритизация

Скрипт, который мониторит латентность и меняет приоритеты:

```bash
#!/bin/bash
while true; do
    latency=$(curl -o /dev/null -s -w '%{time_total}' http://localhost/health)

    if (( $(echo "$latency > 0.1" | bc -l) )); then
        # Латентность высокая — повышаем приоритет HTTPS
        tc class change dev eth0 classid 1:20 htb prio 0
    else
        tc class change dev eth0 classid 1:20 htb prio 1
    fi

    sleep 5
done
```

---

## Практическое задание (Hardcore Edition)

### Задача 1: Построить полную иерархию для production

На сервере настройте HTB с тремя классами: Interactive (SSH, DNS), Web (HTTP/HTTPS), Bulk (всё остальное). Соотношение rate: 20:60:20, ceil: 40:90:100 (процент от канала). Под каждый класс — `fq_codel`. Используйте `iptables + fw`. Напишите скрипт мониторинга (каждую секунду логирует статистику классов в файл).

Проверка: три параллельных iperf3 на разных портах. Убедитесь, что распределение соответствует заданному.

### Задача 2: Измерить overhead классификации

Benchmark:
1. 1 миллион пакетов через `noqueue` (baseline)
2. 1 миллион через HTB + fw classifier
3. 1 миллион через HTB + u32 (сложное правило)

Используйте `pktgen` и `perf` для профилирования. Цель: числа overhead в наносекундах на пакет.

### Задача 3: Debug qdisc с eBPF

Напишите bpftrace скрипт, трейсящий `htb_dequeue`: timestamp, classid, backlog. Запустите трафик и проанализируйте: видите ли переключения между классами? Как меняется backlog?

### Задача 4: Реализовать простой qdisc

Создайте kernel module с простейшим `myfifo` — FIFO очередь с ограничением по размеру. Зарегистрируйте через `register_qdisc()`. Реализуйте `enqueue`, `dequeue`, `init`, `destroy`. Добавьте счётчик dropped.

### Задача 5: Troubleshooting

Сценарий: на сервере HTB с тремя классами. SSH периодически лагает (500ms+), хотя класс SSH имеет `rate 100mbit` и общая нагрузка всего 50 Mbit.

Вопросы:
1. Какие команды запустите для диагностики?
2. Какие метрики посмотрите в `tc -s class show`?
3. Как проверите, не виноват ли child qdisc (fq_codel)?
4. Может ли быть виноват `quantum` или `burst` параметр HTB?

Дайте пошаговый план debugging с конкретными командами.

---

**Следующий модуль:** Internet Protocol — спускаемся на сетевой уровень и разбираем, как ядро принимает решения о маршрутизации каждого пакета.
