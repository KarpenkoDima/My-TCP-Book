# Модуль 4: Internet Protocol — Анатомия сетевого уровня

*«IP — это не просто адрес в заголовке. Это целая машина принятия решений, работающая на каждом роутере между вами и сервером».*

---

## Часть 4.1: IP Header — 20 байт, которые правят интернетом

Каждый слышал про IP-адреса. Но давайте разберём, что **на самом деле** происходит, когда ядро обрабатывает IP-пакет.

### Структура заголовка в ядре

```c
// include/uapi/linux/ip.h
struct iphdr {
#if defined(__LITTLE_ENDIAN_BITFIELD)
    __u8    ihl:4,        // Internet Header Length (в 32-bit словах)
            version:4;     // Версия протокола (4 для IPv4)
#elif defined (__BIG_ENDIAN_BITFIELD)
    __u8    version:4,
            ihl:4;
#endif
    __u8    tos;          // Type of Service (DSCP + ECN)
    __be16  tot_len;      // Общая длина пакета (header + data)
    __be16  id;           // Identification для фрагментации
    __be16  frag_off;     // Flags (3 бита) + Fragment Offset (13 бит)
    __u8    ttl;          // Time To Live
    __u8    protocol;     // Протокол верхнего уровня (6=TCP, 17=UDP, 1=ICMP)
    __sum16 check;        // Header checksum
    __be32  saddr;        // Source IP
    __be32  daddr;        // Destination IP
    // За этим могут следовать IP Options (до 40 байт)
};
```

Обратите внимание на `#if defined(__LITTLE_ENDIAN_BITFIELD)` — порядок битовых полей `ihl` и `version` зависит от endianness процессора. На x86 (little-endian) `ihl` идёт первым в памяти.

### ip_rcv(): Первая линия обороны

Когда пакет прибывает из сети и доходит до IP-уровня, вызывается `ip_rcv()`. Эта функция — параноидальный валидатор:

```c
// net/ipv4/ip_input.c
int ip_rcv(struct sk_buff *skb, struct net_device *dev,
           struct packet_type *pt, struct net_device *orig_dev)
{
    struct iphdr *iph;
    u32 len;

    // Проверяем, что пакет достаточно длинный для минимального IP заголовка
    if (!pskb_may_pull(skb, sizeof(struct iphdr)))
        goto inhdr_error;

    iph = ip_hdr(skb);

    // Версия должна быть 4
    if (iph->version != 4)
        goto inhdr_error;

    // IHL (длина заголовка) должна быть >= 5 (5 × 4 = 20 байт)
    if (iph->ihl < 5)
        goto inhdr_error;

    // Проверяем, что в пакете действительно есть столько данных,
    // сколько заявлено в IHL
    if (!pskb_may_pull(skb, iph->ihl * 4))
        goto inhdr_error;

    // ВАЖНО: пересчитываем указатель iph, т.к. pskb_may_pull
    // мог переместить данные в линейную часть sk_buff
    iph = ip_hdr(skb);

    // Проверяем tot_len: пакет не может быть короче заявленного
    len = ntohs(iph->tot_len);
    if (skb->len < len || len < (iph->ihl * 4))
        goto inhdr_error;

    // Проверяем checksum заголовка
    if (ip_fast_csum((u8 *)iph, iph->ihl))
        goto csum_error;

    // Обрезаем sk_buff до реальной длины IP-пакета
    // (Ethernet может добавить padding для минимального размера кадра)
    __skb_trim(skb, len);

    // Передаём в NF_INET_PRE_ROUTING (netfilter hook)
    return NF_HOOK(NFPROTO_IPV4, NF_INET_PRE_ROUTING,
                   net, NULL, skb, dev, NULL, ip_rcv_finish);

inhdr_error:
    __IP_INC_STATS(net, IPSTATS_MIB_INHDRERRORS);
drop:
    kfree_skb(skb);
    return NET_RX_DROP;
}
```

Каждая проверка — это защита от malformed пакетов. В интернете **любое** поле IP-заголовка может быть подделано. Ядро проверяет всё: версию, длину, контрольную сумму. Только после этого пакет допускается дальше.

### TOS / DSCP / ECN: Битовое поле приоритетов

Поле `tos` (8 бит) сегодня разделено на две части:

```
 0   1   2   3   4   5   6   7
+---+---+---+---+---+---+---+---+
|         DSCP (6 бит)    | ECN |
+---+---+---+---+---+---+---+---+
```

**DSCP (Differentiated Services Code Point)** — первые 6 бит. Используется для приоритезации трафика на роутерах (QoS). Например, VoIP-трафик маркируется DSCP=46 (EF — Expedited Forwarding).

**ECN (Explicit Congestion Notification)** — последние 2 бита. Революционная идея: вместо дропа пакета роутер **помечает** его. Два бита:
- `00` — Non ECN-Capable Transport
- `01` или `10` — ECN Capable Transport (ECT)
- `11` — Congestion Experienced (CE)

Когда BBR или DCTCP выставляют ECT-бит, роутер, видя нарастающую очередь, помечает пакет CE вместо дропа. Получатель возвращает эту информацию отправителю через TCP, и тот снижает скорость **без потери пакета**.

**Проблема:** многие middleboxes (файрволлы, NAT) сбрасывают пакеты с ECN или обнуляют эти биты, считая их «подозрительными». Именно поэтому ECN до сих пор не стал стандартом.

### TTL: Защита от бесконечных петель

TTL (Time To Live) — счётчик, декрементируемый на каждом хопе. При TTL=0 пакет дропается.

```c
// net/ipv4/ip_forward.c
int ip_forward(struct sk_buff *skb)
{
    struct iphdr *iph = ip_hdr(skb);

    // Декрементируем TTL
    if (ip_decrease_ttl(iph) <= 0) {
        // TTL истёк — генерируем ICMP Time Exceeded
        icmp_send(skb, ICMP_TIME_EXCEEDED, ICMP_EXC_TTL, 0);
        goto drop;
    }

    // ... routing decision, netfilter hooks ...
}
```

Функция `ip_decrease_ttl()` содержит элегантную оптимизацию:

```c
static inline int ip_decrease_ttl(struct iphdr *iph)
{
    u32 check = (__force u32)iph->check;

    // Уменьшаем TTL
    iph->ttl--;

    // Инкрементальное обновление checksum
    // Вместо пересчёта всего заголовка (десятки инструкций),
    // обновляем только дельту от изменения TTL (2-3 инструкции)
    check += (__force u32)htons(0x0100);
    iph->check = (__force __sum16)(check + (check >= 0xFFFF));

    return iph->ttl;
}
```

Инкрементальное обновление checksum критично на высокоскоростных роутерах. При 10 Mpps разница между полным пересчётом и инкрементальным — это проценты CPU.

---

## Часть 4.2: IP Checksum — Быстрая математика

### Алгоритм: одна сумма дополнений

IP checksum — это one's complement sum (сумма в дополнительном коде) всех 16-битных слов заголовка, с инверсией результата.

```c
// Упрощённая версия
static inline __sum16 ip_fast_csum(const void *iph, unsigned int ihl)
{
    const u16 *buf = iph;
    u32 sum = 0;
    unsigned int count = ihl * 2;  // ihl в 32-bit словах, нам нужны 16-bit

    while (count > 0) {
        sum += *buf++;
        count--;
    }

    // Сворачиваем carry
    while (sum >> 16)
        sum = (sum & 0xFFFF) + (sum >> 16);

    return ~sum;  // Инверсия
}
```

На практике ядро использует ассемблерные оптимизации для каждой архитектуры (x86, ARM). Функция `ip_fast_csum()` на x86 развёрнута в цикл из `adc` инструкций.

### Инкрементальное обновление

Когда меняется только одно поле (TTL при форвардинге), нет смысла пересчитывать весь заголовок. RFC 1624 описывает инкрементальное обновление:

```
new_checksum = old_checksum - old_value + new_value
```

Это работает, потому что one's complement sum — ассоциативная операция.

### Почему Wireshark показывает «неправильные» checksums

При захвате трафика на отправляющей машине вы часто видите `Header checksum: incorrect`. Это не ошибка — это **checksum offload**. Ядро не считает checksum, а передаёт эту работу NIC. Wireshark видит пакет до того, как NIC заполнит поле.

Проверить: `ethtool -k eth0 | grep tx-checksum`.

---

## Часть 4.3: Фрагментация — Почему MTU 1500 не случайность

### Почему фрагментация — катастрофа

IP может фрагментировать пакеты, если они больше MTU канала. Но это **убивает** производительность:

- Reassembly требует буферизации всех фрагментов (memory pressure)
- Потеря **одного** фрагмента = потеря **всего** пакета = повторная передача **всего**
- Промежуточные роутеры тратят ресурсы на дополнительную работу
- Фрагменты легко использовать для атак (fragment overlap attacks)

### Path MTU Discovery (PMTUD)

TCP решает проблему элегантно: не фрагментировать вообще. При отправке TCP выставляет **Don't Fragment (DF)** бит:

```c
// net/ipv4/tcp_output.c (упрощённо)
static int tcp_transmit_skb(struct sock *sk, struct sk_buff *skb, ...)
{
    struct inet_sock *inet = inet_sk(sk);
    struct iphdr *iph;

    // Выставляем DF для Path MTU Discovery
    if (inet->pmtudisc == IP_PMTUDISC_DO)
        iph->frag_off = htons(IP_DF);

    // ...
}
```

Когда пакет с DF встречает канал с меньшим MTU, роутер **дропает** его и отправляет ICMP Fragmentation Needed (тип 3, код 4) обратно. TCP получает это, снижает MSS, и следующие пакеты будут меньше.

**Проблема:** многие файрволлы блокируют ICMP. Если ICMP Fragmentation Needed не доходит, TCP не знает о проблеме, и соединение «зависает» — это называется **PMTU black hole**.

### Фрагментация в ядре

Для UDP (без PMTUD) фрагментация неизбежна:

```c
// net/ipv4/ip_output.c (упрощённо)
int ip_fragment(struct net *net, struct sock *sk, struct sk_buff *skb,
                unsigned int mtu,
                int (*output)(struct net *, struct sock *, struct sk_buff *))
{
    struct iphdr *iph = ip_hdr(skb);
    unsigned int hlen = iph->ihl * 4;     // Длина IP-заголовка
    unsigned int left = skb->len - hlen;   // Данные для фрагментации
    unsigned int ptr = 0;                  // Смещение в данных
    // Offset должен быть кратен 8 байтам (legacy ограничение)
    unsigned int fraglen = ((mtu - hlen) & ~7);

    while (left > 0) {
        unsigned int len = left > fraglen ? fraglen : left;
        struct sk_buff *skb2;

        // Создаём новый sk_buff для фрагмента
        skb2 = alloc_skb(len + hlen + ...);

        // Копируем IP-заголовок
        skb_copy_from_linear_data(skb, skb_network_header(skb2), hlen);

        // Заполняем поля фрагмента
        iph = ip_hdr(skb2);
        iph->frag_off = htons(ptr >> 3);  // Смещение в 8-байтных блоках

        if (left > fraglen)
            iph->frag_off |= htons(IP_MF);  // More Fragments

        iph->tot_len = htons(len + hlen);

        // Пересчитываем checksum
        ip_send_check(iph);

        // Отправляем фрагмент
        output(net, sk, skb2);

        ptr += len;
        left -= len;
    }
    return 0;
}
```

`frag_off` содержит смещение в **8-байтных блоках** (не в байтах!). Это значит, что все фрагменты (кроме последнего) должны быть кратны 8 байтам. Legacy ограничение из времён, когда экономили каждый бит в заголовке.

### Reassembly: ip_defrag()

Получатель собирает фрагменты обратно. Ядро использует хеш-таблицу, индексированную по (src_ip, dst_ip, id, protocol):

```c
// net/ipv4/ip_fragment.c
struct ipq {
    struct inet_frag_queue q;
    u8              ecn;       // ECN bits
    u16             max_df_size; // Largest DF fragment
    int             iif;       // Input interface
    // ... фрагменты хранятся в связном списке
};
```

Таймаут на сборку: `ipfrag_time` (по умолчанию 30 секунд). Если не все фрагменты пришли за это время — всё дропается и ICMP Time Exceeded отправляется обратно.

### MTU 1500: Почему именно это число

1500 байт — это наследие Ethernet из 1980-х. При разработке выбирали между эффективностью (больше данных = меньше overhead заголовков) и задержкой (большой пакет долго передаётся на медленном канале). 1500 — компромисс для 10 Mbps Ethernet.

**Jumbo Frames (9000 байт)** — используются в дата-центрах. Снижают overhead заголовков с 2.6% (1500) до 0.4% (9000). Но требуют поддержки на **всём** пути — один свитч с MTU 1500 заставит фрагментировать.

---

## Часть 4.4: Routing — Как ядро решает, куда слать пакет

### FIB: Forwarding Information Base

Linux использует **FIB trie** — compressed trie (Patricia trie) для longest prefix match (LPM).

```c
// include/net/ip_fib.h
struct fib_table {
    struct hlist_node   tb_hlist;
    u32                 tb_id;         // ID таблицы (main=254, local=255)
    int                 tb_num_default;
    struct rcu_head     rcu;
    unsigned long       *tb_data;      // Указатель на trie
    unsigned long       __data[];
};
```

### Почему trie, а не hash table

Hash table даёт O(1) для exact match. Но routing — это **prefix matching**: для адреса `192.168.1.100` нужно найти наиболее длинный совпадающий префикс среди `192.168.1.0/24`, `192.168.0.0/16`, `0.0.0.0/0`. Hash table для этого бесполезен — пришлось бы проверять все 32 возможных длины префикса.

Trie даёт O(W) в худшем случае, где W = 32 (количество бит в IPv4-адресе). На практике compressed trie (Patricia) значительно быстрее — пропускает общие префиксы.

### Lookup путь

```c
// Упрощённый путь поиска маршрута
struct fib_result res;
struct flowi4 fl4 = {
    .daddr = iph->daddr,    // Куда
    .saddr = iph->saddr,    // Откуда
    .flowi4_tos = iph->tos, // TOS для policy routing
    .flowi4_oif = dev->ifindex, // Интерфейс
};

// Ищем маршрут в FIB
err = fib_lookup(net, &fl4, &res, 0);
if (err)
    goto no_route;  // ICMP Destination Unreachable

// res содержит: next hop, output device, metric, и т.д.
```

### Policy Routing: Несколько таблиц

Linux поддерживает до 256 таблиц маршрутизации. Выбор таблицы — через `ip rule`:

```bash
# Трафик от 10.0.0.0/8 идёт через таблицу 100
ip rule add from 10.0.0.0/8 table 100

# В таблице 100 — маршрут через другой шлюз
ip route add default via 172.16.0.1 table 100

# Основная таблица main — для всего остального
ip route add default via 192.168.1.1
```

Это позволяет реализовать multi-homing (два провайдера), VPN routing, и сложные сетевые топологии.

---

## Часть 4.5: IP Forwarding Path — Полный маршрут пакета

### ASCII диаграмма

```
Пакет прибывает на eth0
  ↓
ip_rcv()                        // Валидация заголовка
  ↓
NF_INET_PRE_ROUTING             // Netfilter: DNAT, conntrack
  ↓
ip_rcv_finish()
  ↓
Routing Decision:
  Для нас? → ip_local_deliver() → TCP/UDP
  Не для нас? ↓
  ↓
ip_forward()                    // TTL--, netfilter FORWARD
  ↓
NF_INET_FORWARD                 // Netfilter: фильтрация
  ↓
ip_forward_finish()
  ↓
dst_output() → ip_output()
  ↓
NF_INET_POST_ROUTING            // Netfilter: SNAT, MASQUERADE
  ↓
ip_finish_output()
  ↓
ip_finish_output2()
  ↓
neigh_output()                  // ARP resolution
  ↓
dev_queue_xmit()                // → Qdisc → NIC → Провод
```

### ip_forward(): Ключевая функция

```c
int ip_forward(struct sk_buff *skb)
{
    struct iphdr *iph = ip_hdr(skb);
    struct rtable *rt = skb_rtable(skb);

    // 1. Проверяем TTL
    if (iph->ttl <= 1) {
        icmp_send(skb, ICMP_TIME_EXCEEDED, ICMP_EXC_TTL, 0);
        goto drop;
    }

    // 2. Проверяем, не слишком ли большой пакет для next hop
    if (skb->len > dst_mtu(&rt->dst) && (iph->frag_off & htons(IP_DF))) {
        // Пакет слишком большой и DF стоит → ICMP Frag Needed
        icmp_send(skb, ICMP_DEST_UNREACH, ICMP_FRAG_NEEDED,
                  htonl(dst_mtu(&rt->dst)));
        goto drop;
    }

    // 3. Декрементируем TTL
    ip_decrease_ttl(iph);

    // 4. Netfilter FORWARD hook
    return NF_HOOK(NFPROTO_IPV4, NF_INET_FORWARD,
                   net, NULL, skb, skb->dev, rt->dst.dev,
                   ip_forward_finish);
drop:
    kfree_skb(skb);
    return NET_RX_DROP;
}
```

### Conntrack: Цена отслеживания соединений

Netfilter connection tracking (`nf_conntrack`) добавляет значительный overhead:

- Хеш-таблица всех соединений (память)
- Lookup на каждый пакет (CPU)
- Lock contention при высоком PPS

Для чистого роутера (без NAT и stateful firewall) можно отключить:

```bash
# Отключаем conntrack для forwarded трафика
iptables -t raw -A PREROUTING -j NOTRACK
iptables -t raw -A OUTPUT -j NOTRACK
```

Или в nftables:

```bash
nft add rule ip raw prerouting notrack
```

Это может дать 20-30% прирост PPS на чисто forwarding нагрузке.

---

## Часть 4.6: IP Options — Редкие, но опасные

IP Options занимают байты между базовым 20-байтным заголовком и данными (до 40 байт).

### Типы

- **Record Route** — каждый роутер записывает свой IP. Максимум 9 хопов (40 байт / 4 байта на IP).
- **Timestamp** — каждый роутер добавляет время прохождения.
- **Loose Source Routing** — отправитель задаёт список роутеров, через которые пакет должен пройти.
- **Strict Source Routing** — отправитель задаёт **точный** путь.

### Почему они убивают производительность

1. **Переменная длина заголовка** — NIC не может делать hardware offload (TSO, checksum) для пакетов с options.
2. **Slow path** — ядро переключается с fast path на slow path для обработки options.
3. **Безопасность** — Source Routing позволяет обходить файрволлы. Почти все production-роутеры дропают такие пакеты.

```bash
# Дропаем пакеты с source routing
sysctl -w net.ipv4.conf.all.accept_source_route=0
```

---

## Часть 4.7: Multicast

### Основы

IP multicast использует адреса 224.0.0.0/4 (224.0.0.0 — 239.255.255.255). Один отправитель — много получателей.

**IGMP (Internet Group Management Protocol)** — протокол, которым хосты сообщают роутеру, какие multicast-группы им интересны.

```bash
# Посмотреть, в каких группах состоит хост
cat /proc/net/igmp

# Или через ip
ip maddr show
```

### Kernel multicast routing

```c
// net/ipv4/ipmr.c
static int ip_mr_input(struct sk_buff *skb)
{
    // Ищем multicast route в MFC (Multicast Forwarding Cache)
    cache = ipmr_cache_find(mrt, iph->saddr, iph->daddr);
    if (cache) {
        // Пересылаем на все выходные интерфейсы
        return ip_mr_forward(net, mrt, skb, cache, local);
    }
    // Нет маршрута — отправляем в userspace (mrouted/pimd)
    return ipmr_cache_unresolved(mrt, vif, skb, dev);
}
```

### Когда multicast важен

- **Финансовые данные** — биржевые фиды (market data) раздаются multicast на тысячи подписчиков
- **IPTV** — видеопотоки для телевидения
- **Кластерные протоколы** — Pacemaker, Corosync используют multicast для обнаружения узлов

---

## Часть 4.8: IPsec Integration

### XFRM Framework

Linux реализует IPsec через **XFRM** (transform) framework, который встраивается в IP path:

```
Исходящий:
ip_output() → xfrm_output() → ESP/AH шифрование → ip_output() (уже зашифрованный)

Входящий:
ip_rcv() → xfrm_input() → ESP/AH расшифровка → ip_rcv_finish() (расшифрованный)
```

### ESP (Encapsulating Security Payload)

```
+----------+----------+----------+----------+
| IP Header | ESP Hdr  | Encrypted Payload  | ESP Trailer |
+----------+----------+----------+----------+
                       |← шифрование →|
```

ESP шифрует payload и опционально аутентифицирует весь пакет. Алгоритмы: AES-GCM (рекомендуемый), AES-CBC + HMAC-SHA256.

### Performance

IPsec в ядре — дорогая операция:
- AES-GCM: ~3-5 Gbps на одном ядре (software)
- С аппаратным offload (Intel QAT, NIC inline crypto): 25-100 Gbps

```bash
# Проверить, поддерживает ли NIC IPsec offload
ethtool -k eth0 | grep esp
```

Современные NIC (Intel E810, Mellanox ConnectX-6) поддерживают inline IPsec — шифрование/расшифровка происходит прямо на карте, без участия CPU.

---

## Практическое задание

### Задача 1: Craft IP-пакетов с scapy

Установите scapy (`pip install scapy`). Создайте и отправьте пакеты:

```python
from scapy.all import *

# Обычный ICMP ping
pkt = IP(dst="10.0.0.2", ttl=64) / ICMP()
send(pkt)

# Пакет с маленьким TTL (должен умереть на 3-м хопе)
pkt = IP(dst="8.8.8.8", ttl=3) / ICMP()
sr1(pkt)  # Получим ICMP Time Exceeded

# Пакет с IP Options (Record Route)
pkt = IP(dst="10.0.0.2", options=[IPOption_RR()]) / ICMP()
ans = sr1(pkt)
print(ans[IP].options)  # Увидим список роутеров
```

### Задача 2: Trace ip_rcv() с bpftrace

```bash
bpftrace -e '
kprobe:ip_rcv {
    $skb = (struct sk_buff *)arg0;
    $iph = (struct iphdr *)($skb->head + $skb->network_header);
    printf("TTL=%d proto=%d src=%s\n",
           $iph->ttl, $iph->protocol,
           ntop(AF_INET, &$iph->saddr));
}
'
```

Запустите и наблюдайте за входящими пакетами. Какой TTL приходит? Какие протоколы? Видите ли вы паттерны?

### Задача 3: Эксперимент с фрагментацией

```bash
# Отправляем UDP-пакет размером 10000 байт (будет фрагментирован)
ping -s 10000 -c 1 10.0.0.2

# На стороне получателя захватываем
tcpdump -i eth0 -v 'ip[6:2] & 0x3fff != 0'  # Только фрагменты

# Считаем фрагменты: 10000 / (1500 - 20) ≈ 7 фрагментов
```

### Задача 4: Policy Routing с двумя ISP

Настройте сервер с двумя провайдерами:

```bash
# Таблицы
echo "100 isp1" >> /etc/iproute2/rt_tables
echo "200 isp2" >> /etc/iproute2/rt_tables

# Маршруты
ip route add default via 10.0.1.1 table isp1
ip route add default via 10.0.2.1 table isp2

# Правила
ip rule add from 10.0.1.0/24 table isp1
ip rule add from 10.0.2.0/24 table isp2

# Балансировка
ip route add default \
    nexthop via 10.0.1.1 weight 1 \
    nexthop via 10.0.2.1 weight 1
```

### Задача 5: Производительность routing lookup

Создайте таблицу с 10K, 100K и 500K маршрутов. Измерьте:
1. Время lookup с помощью `ip route get <addr>`
2. Потребление памяти: `cat /proc/net/fib_triestat`
3. PPS с разным числом маршрутов (iperf3 через forwarding)

```bash
# Генерируем маршруты
for i in $(seq 1 100000); do
    ip route add $((i/256)).$((i%256)).0.0/16 via 10.0.0.1 2>/dev/null
done

# Статистика trie
cat /proc/net/fib_triestat
```

---

**Следующий модуль:** Архитектура приложений — поднимаемся из ядра в user space и учимся писать код, который не убивает производительность.
