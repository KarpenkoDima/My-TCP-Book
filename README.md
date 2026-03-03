# TCP/IP & Network Engineering — Deep Dive Book

*От физики сетевой карты до QUIC. От ядра Linux до Windows Internals. Для тех, кому мало «просто работает».*

---

## О книге

12 000+ строк технического материала для Senior-инженеров, которые хотят понимать сети **на уровне ядра**, а не на уровне GUI. Каждый модуль содержит:

- **Kernel code** — реальные структуры и функции из исходников Linux/Windows
- **Диагностика** — eBPF, ETW, ss, pktmon, WinDbg
- **Production-сценарии** — что ломается и как чинить
- **Практические задания** — hands-on labs с конкретными командами
- **Chaos Engineering** — контролируемое внедрение сбоев

---

## Оглавление

### Часть I: Linux Network Stack — От провода до сокета

#### [Модуль 1: Физика и Ядро](modules/Module-01-Physical-Layer-and-Kernel.md)
*«Где реально умирают пакеты»* — 1465 строк

- 1.1 Жизнь пакета до Socket API (DMA, Ring Buffer, struct sk_buff)
- 1.2 Hard IRQ и NAPI (прерывания, polling, budget)
- 1.3 Масштабирование (RSS, RPS, RFS, XPS, aRFS)
- 1.4 sk_buff — главная структура ядра (linear data, page frags, cloning)
- 1.5 Offloading (TSO, GRO, GSO, checksum offload)
- 1.6 Kernel Bypass (DPDK, AF_XDP, io_uring zerocopy)
- Практика: ethtool, /proc/interrupts, bpftrace, XDP program

---

#### [Модуль 2: Bufferbloat и Congestion Control](modules/Module-02-Congestion-Control.md)
*«Война за очередь»* — 2125 строк

- 2.1 Анатомия Bufferbloat (Little's Law, queueing theory)
- 2.2 CUBIC — рабочая лошадка интернета (cubic_cong_avoid(), Wmax, β=0.7)
- 2.3 BBR — Bottleneck Bandwidth and RTT (bbr_update_bw(), bbr_update_min_rtt(), фазы: Startup/Drain/ProbeBW/ProbeRTT)
- 2.4 DCTCP и ECN — Congestion Control для дата-центров
- 2.5 AQM — Active Queue Management (CoDel, FQ_CoDel, CAKE с kernel code)
- 2.6 Искусственное ограничение скорости (Shaping)
- Практика: CUBIC vs BBR бенчмарк, Amsterdam→NY кейс, sysctl tuning

---

#### [Модуль 3: TCP State Machine](modules/Module-03-TCP-State-Machine.md)
*«Жизненный цикл соединения»* — 753 строки

- 3.1 TCP State Machine — 11 состояний (ASCII-диаграмма полного автомата)
- 3.2 Three-Way Handshake (secure_tcp_seq(), ISN generation)
- 3.3 SYN Flood и защита (SYN/Accept queues, SYN Cookies, TCP Fast Open)
- 3.4 Передача данных — Sliding Window (tcp_sock fields, Window Scaling, Delayed ACK)
- 3.5 Retransmission (RTO Jacobson/Karels, Fast Retransmit, SACK scoreboard, RACK)
- 3.6 Закрытие соединения — Four-Way Handshake (TIME_WAIT, CLOSE_WAIT, RST)
- 3.7 Таймеры TCP (RTO, Persist, Keepalive, TIME_WAIT, FIN_WAIT_2)
- 3.8 Диагностика состояний (ss -ti, nstat, /proc/net/tcp, eBPF)
- 3.9 Типичные production-проблемы (CLOSE_WAIT leak, TIME_WAIT exhaustion)

---

#### [Модуль 4: Traffic Control, Тюнинг и Диагностика](modules/Module-04-Traffic-Control-Tuning-Diagnostics.md)
*«Режим Бога»* — 792 строки

- 4.1 Архитектура Qdisc (struct Qdisc, __dev_xmit_skb(), TX path)
- 4.2 Classless vs Classful Qdisc (pfifo_fast, fq_codel, HTB, HFSC)
- 4.3 TC Filters и Классификация (u32, flower, BPF classifier)
- 4.4 Production Example — полная настройка HTB для веб-сервера
- 4.5 Chaos Engineering с tc netem (delay, loss, corruption, reorder)
- 4.6 Микроберсты — невидимый убийца
- 4.7 eBPF и BCC — рентген ядра (tcpretrans, tcplife, tcpconnlat, bpftrace)
- 4.8 Sysctl Tuning — опасная зона (tcp_rmem/wmem, somaxconn, tw_reuse)
- 4.9 Performance Impact и оптимизация
- 4.10 Продвинутые техники

---

#### [Модуль 5: Internet Protocol](modules/Module-05-IP-Protocol.md)
*«Анатомия сетевого уровня»* — 933 строки

- 5.1 IP Header — 20 байт (struct iphdr, ip_rcv() с kernel code)
- 5.2 IP Checksum — one's complement, инкрементальное обновление
- 5.3 Фрагментация (ip_fragment(), PMTUD, DF bit, MTU 1500, Jumbo Frames)
- 5.4 Routing — FIB trie (Patricia trie, longest prefix match, Policy Routing)
- 5.5 IP Forwarding Path (ip_forward(), Netfilter hooks, conntrack overhead)
- 5.6 IP Options — редкие, но опасные (Source Routing, slow path)
- 5.7 Multicast (IGMP, MFC, финансовые фиды, IPTV)
- 5.8 IPsec Integration (XFRM framework, ESP, AES-GCM, hardware offload)
- 5.9 **IPv6** — struct ipv6hdr, отсутствие checksum, Extension Headers, обязательный PMTUD, Flow Label, NDP, dual-stack, production-проблемы
- Практика: scapy craft, bpftrace ip_rcv, фрагментация, Policy Routing

---

### Часть II: Прикладной уровень и будущее

#### [Модуль 6: Архитектура приложений](modules/Module-06-Application-Architecture.md)
*«Пишем код, который летает»* — 444 строки

- 6.1 Zero-Copy (sendfile, splice, MSG_ZEROCOPY, vmsplice)
- 6.2 IO Models — эволюция (select → poll → epoll → io_uring)
- 6.3 Прикладной уровень .NET/C# (System.IO.Pipelines, SAEA, Span\<T\>, Kestrel internals)
- 6.4 Практические паттерны (TCP_NODELAY, SO_REUSEPORT, connection pooling)

---

#### [Модуль 7: QUIC и HTTP/3](modules/Module-07-QUIC-HTTP3.md)
*«Будущее транспортного уровня»* — 452 строки

- 7.1 Фундаментальные проблемы TCP для Web (HoL blocking, handshake latency)
- 7.2 Архитектура QUIC (UDP + TLS 1.3 + streams)
- 7.3 Handshake — 0-RTT и 1-RTT
- 7.4 Stream Multiplexing без HoL Blocking
- 7.5 Connection Migration (CID, переключение Wi-Fi → LTE)
- 7.6 User-space Congestion Control
- 7.7 Структура QUIC-пакета (Header, Frames)
- 7.8 HTTP/3 (QPACK, server push, priorities)
- 7.9 QUIC в Linux и .NET (System.Net.Quic, msquic)
- 7.10 Проблемы и критика QUIC (middlebox ossification, CPU cost)
- 7.11 Когда QUIC, а когда TCP

---

### Часть III: Лаборатории и Chaos Engineering

#### [Модуль 8: EVE-NG Lab](modules/Module-08-EVE-NG-Lab.md)
*«Боевой полигон для .NET и Chaos Engineering»* — 1581 строка

- **Этап 1 — Инфраструктура:** 7 узлов, 4 зоны, OSPF (VyOS + Cisco IOL)
  - 8.1 Архитектура стенда (топология, адресная схема, ресурсы VM)
  - 8.2 Создание топологии в EVE-NG
  - 8.3 Базовая настройка Linux-узлов (Netplan)
  - 8.4 Конфигурация VyOS/Cisco роутеров (OSPF Area 0)
- **Этап 2 — Ansible:** полная автоматизация
  - 8.5 Inventory (YAML, group_vars, sysctl из Модуля 4)
  - 8.6 Playbooks: base-setup, .NET Runtime, Redis, PostgreSQL
- **Этап 3 — Chaos Engineering:** внедрение сбоев
  - 8.7 Теория (где внедрять: роутер vs endpoint)
  - 8.8 High Latency (150ms+jitter → Npgsql pool exhaustion), Packet Loss (5% → Polly retry + Circuit Breaker), Bandwidth Shaping (10Mbit HTB → backpressure, Channel\<T\>)
  - 8.9 VyOS-native Traffic Policy
  - 8.10 Мониторинг (Prometheus + Grafana + .NET metrics)
  - 8.11 Скрипт полного развёртывания

---

#### [Лабораторный стенд — Базовый](modules/Lab-Setup.md)
*«Полигон для экспериментов»* — 1062 строки

- Уровень 1: Network Namespaces (5 минут, zero overhead)
- Уровень 2: VMware 3-VM топология
- Уровень 3: Физические серверы
- Матрица совместимости задач × уровней

---

### Часть IV: Windows Network Internals

#### [Модуль 9: Windows Server Network Stack](modules/Module-09-Windows-Network-Stack.md)
*«NDIS, RSS и траблшутинг на уровне ядра»* — 1089 строк

- 9.1 Ментальная модель (аэропорт = NDIS pipeline)
- 9.2 Архитектура NDIS 6.x (NET_BUFFER_LIST, filter chain, miniport)
- 9.3 RSS — Receive Side Scaling (Toeplitz hash, NUMA trap, UDP hash)
- 9.4 VMQ и dVMQ — RSS для Hyper-V
- 9.5 RSC — Receive Segment Coalescing (latency trade-off)
- 9.6 SR-IOV — обход гипервизора (VF, Live Migration failover)
- 9.7 WFP — Windows Filtering Platform (filter forensics, pktmon vs Wireshark)
- 9.8 tcpip.sys (CUBIC/DCTCP/LEDBAT, AutoTuning, AFD.sys)
- 9.9 Хирургическая диагностика (ETW, xperf, WPA, DPC/ISR analysis)
- 9.10 Hardcore Lab — Chaos Engineering на Windows
- 9.11 Чеклист для Production (Invoke-NetworkStackAudit)

---

#### [Модуль 10: Windows Client Networking](modules/Module-10-Windows-Client-Networking.md)
*«Wi-Fi, DNS Client, VPN и "Нет интернета"»* — 1398 строк

- 10.1 Анатомия клиентского стека (nwifi.sys, WLAN AutoConfig)
- 10.2 Wi-Fi Stack — 802.11 изнутри
  - Roaming analysis (Event ID 12011/12012, 802.11r FT)
  - Power Management (PnPCapabilities registry)
  - **Modern Standby (S0ix)** — powercfg GUID, Network Connected Standby
  - ETW-трассировка `scenario=wlan`
- 10.3 DNS Client
  - Полный путь резолвинга (Cache → HOSTS → NRPT → Interface DNS)
  - Negative cache trap (MaxNegativeCacheTtl = 900!)
  - NRPT — перехват DNS VPN-клиентами, catch-all namespace "."
  - DoH (DNS over HTTPS) — конфликт с корпоративным DNS
  - **SMHNR** — Smart Multi-Homed Name Resolution (DNS leak мимо NRPT)
  - Interface Metric и приоритет DNS
- 10.4 NCSI — «Нет интернета» (DNS + HTTP + HTTPS probes)
  - Почему NCSI врёт (proxy, captive portal, SSL inspection)
  - **IPv6 NCSI** — параллельные пробы к ipv6.msftncsi.com
  - Кастомный NCSI endpoint для air-gapped сетей
- 10.5 NLA — Network Location Awareness (гонка Domain/Public при VPN)
- 10.6 VPN Stack (Always On VPN, IKEv2, split tunneling, MTU black hole)
- 10.7 Сетевые сбросы (winsock reset, ip reset, adapter reset)
- 10.8 ETW-диагностика клиентских проблем
- 10.9 Типичные проблемы — Cookbook
- 10.10 Invoke-ClientNetworkDiag — полная диагностика одной командой

---

## Статистика

| Модуль | Строк | Тема |
|---|---|---|
| Модуль 1 | 1 465 | Physical Layer & Kernel |
| Модуль 2 | 2 125 | Congestion Control |
| Модуль 3 | 753 | TCP State Machine |
| Модуль 4 | 792 | Traffic Control & Tuning |
| Модуль 5 | 933 | IP Protocol + IPv6 |
| Модуль 6 | 444 | Application Architecture |
| Модуль 7 | 452 | QUIC / HTTP/3 |
| Модуль 8 | 1 581 | EVE-NG Lab & Chaos Engineering |
| Модуль 9 | 1 089 | Windows Server Stack |
| Модуль 10 | 1 398 | Windows Client Networking |
| Lab Setup | 1 062 | Базовый полигон |
| **Итого** | **12 094** | |

---

## Как читать

**Путь сетевого инженера (Linux):**
`Модуль 1 → 2 → 3 → 4 → 5 → Lab Setup`

**Путь .NET-разработчика:**
`Модуль 3 → 2 → 6 → 7 → 8 (Chaos Engineering)`

**Путь Windows-админа:**
`Модуль 9 → 10 → 3 (TCP State Machine для понимания ss/netstat)`

**Путь DevOps-инженера:**
`Модуль 8 (EVE-NG Lab) → 4 (TC/Tuning) → 1 (eBPF) → 2 (BBR)`

---

## Требования

- **Linux labs:** Ubuntu 22.04+, root-доступ, ядро 5.15+
- **Windows labs:** Windows Server 2019+ / Windows 10/11, PowerShell 5.1+
- **EVE-NG lab:** Core i9, 32 GB RAM, EVE-NG Community/Professional
- **Знания:** Уверенное владение Linux CLI / PowerShell, понимание TCP/IP на уровне CCNA+
