# Лабораторный стенд --- Полигон для экспериментов

Тестировать сетевые эффекты на `localhost` --- это самообман. Пакеты копируются в памяти через loopback, минуя весь сетевой стек: NIC, драйвер, DMA, Ring Buffer, qdisc, маршрутизацию. Вы не увидите ни задержек, ни потерь, ни влияния congestion control. Результаты `iperf3 -c 127.0.0.1` не имеют ничего общего с реальностью.

Для отработки всех лабораторных из этой книги --- `tc netem`, BBR vs CUBIC, eBPF tracing, HTB shaping --- нужен **изолированный полигон**.

Ниже три уровня сложности. Выбирайте по ресурсам и задачам.

---

## Уровень 1: Network Namespaces (5 минут, нулевые ресурсы)

### Описание

Виртуальная сеть внутри вашего ядра. Никаких виртуальных машин, никаких гипервизоров. Network namespace --- это изолированный экземпляр сетевого стека: свои интерфейсы, своя таблица маршрутизации, свои правила iptables. Два namespace соединяются парой `veth` --- виртуальным патч-кордом.

**Топология:**

```
  ┌──────────────┐         veth pair         ┌──────────────┐
  │   Namespace  │                           │   Namespace  │
  │   "client"   │                           │   "server"   │
  │              │                           │              │
  │  veth-client ├───────────────────────────┤ veth-server  │
  │  10.0.0.1/24 │                           │ 10.0.0.2/24  │
  └──────────────┘                           └──────────────┘
```

### Полный скрипт создания стенда

Скопируйте целиком и выполните:

```bash
#!/bin/bash
# lab-ns-simple.sh --- Простой стенд на двух namespace
# Запуск: sudo bash lab-ns-simple.sh

set -euo pipefail

echo "[1/5] Удаляем старые namespace (если есть)..."
ip netns del client 2>/dev/null || true
ip netns del server 2>/dev/null || true

echo "[2/5] Создаём namespace..."
ip netns add client
ip netns add server

echo "[3/5] Создаём veth pair (виртуальный патч-корд)..."
ip link add veth-client type veth peer name veth-server

echo "[4/5] Помещаем концы кабеля в namespace..."
ip link set veth-client netns client
ip link set veth-server netns server

echo "[5/5] Назначаем IP и поднимаем интерфейсы..."
# Client
ip netns exec client ip addr add 10.0.0.1/24 dev veth-client
ip netns exec client ip link set veth-client up
ip netns exec client ip link set lo up

# Server
ip netns exec server ip addr add 10.0.0.2/24 dev veth-server
ip netns exec server ip link set veth-server up
ip netns exec server ip link set lo up

# Включаем offloading на veth (приближает поведение к реальному NIC)
ip netns exec client ethtool -K veth-client tso on gso on gro on
ip netns exec server ethtool -K veth-server tso on gso on gro on

echo ""
echo "=== Стенд готов ==="
echo "Client: 10.0.0.1 (namespace 'client')"
echo "Server: 10.0.0.2 (namespace 'server')"
echo ""
echo "Проверка:"
ip netns exec client ping -c 2 10.0.0.2
```

**Верификация:**

```bash
# Должно показать два namespace
ip netns list

# Должно показать veth-client с IP 10.0.0.1
sudo ip netns exec client ip addr show veth-client

# Должно показать veth-server с IP 10.0.0.2
sudo ip netns exec server ip addr show veth-server

# Пинг должен проходить с RTT ~0.05ms
sudo ip netns exec client ping -c 3 10.0.0.2
```

### Как пользоваться

**Базовый тест пропускной способности:**

```bash
# Терминал 1: запуск iperf3-сервера
sudo ip netns exec server iperf3 -s

# Терминал 2: запуск iperf3-клиента
sudo ip netns exec client iperf3 -c 10.0.0.2
```

**Эмуляция задержки (tc netem):**

```bash
# Добавляем 50ms задержки на интерфейс клиента
# В обе стороны получится ~100ms RTT
sudo ip netns exec client tc qdisc add dev veth-client root netem delay 50ms

# Проверяем: RTT должен быть ~100ms
sudo ip netns exec client ping -c 5 10.0.0.2

# Запускаем iperf3 и наблюдаем влияние задержки на throughput
sudo ip netns exec client iperf3 -c 10.0.0.2 -t 10

# Убираем задержку
sudo ip netns exec client tc qdisc del dev veth-client root
```

**Эмуляция потерь:**

```bash
# 5% потерь пакетов
sudo ip netns exec client tc qdisc add dev veth-client root netem loss 5%

# Тест: наблюдаем ретрансмиты в выводе iperf3
sudo ip netns exec client iperf3 -c 10.0.0.2 -t 30

# Убираем
sudo ip netns exec client tc qdisc del dev veth-client root
```

**Комбинированный хаос:**

```bash
# 80ms задержки + 20ms jitter + 2% потерь + 0.1% дублирования
sudo ip netns exec client tc qdisc add dev veth-client root netem \
    delay 80ms 20ms distribution normal \
    loss 2% \
    duplicate 0.1%

# Запускаем iperf3 и снимаем дамп одновременно
sudo ip netns exec server tcpdump -i veth-server -w /tmp/chaos.pcap &
sudo ip netns exec client iperf3 -c 10.0.0.2 -t 20

# Анализируем дамп
tcpdump -r /tmp/chaos.pcap -n | head -50
```

**Захват трафика tcpdump:**

```bash
# Слушаем трафик в namespace сервера
sudo ip netns exec server tcpdump -i veth-server -nn -c 20
```

**Очистка стенда:**

```bash
sudo ip netns del client
sudo ip netns del server
```

### Плюсы и минусы

**Плюсы:**
- Мгновенное создание --- 5 секунд на весь стенд
- Нулевое потребление RAM и CPU --- нет виртуальных машин
- Идеально для быстрых экспериментов с `tc netem` и `tcpdump`
- Работает на любом Linux, включая WSL2

**Минусы:**
- **Общее ядро.** Все namespace делят один и тот же kernel. Если вы сделаете `sysctl -w net.ipv4.tcp_congestion_control=bbr`, это применится глобально ко всем namespace. Невозможно запустить BBR на клиенте и CUBIC на сервере одновременно.
- **Нет реального сетевого стека.** Пакеты не проходят через драйвер, DMA, Ring Buffer. Тестировать RSS, RPS, offloading по-настоящему нельзя.
- **veth --- не NIC.** Поведение veth-пары отличается от реального сетевого адаптера: нет очередей TX/RX, нет interrupt coalescing.

### Расширение: Добавляем роутер

Для лабораторных, где нужен "чёрный ящик" между клиентом и сервером (эмуляция WAN-канала), добавляем третий namespace в роли маршрутизатора.

**Топология:**

```
  ┌──────────┐    veth pair 1    ┌──────────┐    veth pair 2    ┌──────────┐
  │  client   │                  │  router   │                  │  server   │
  │           │                  │           │                  │           │
  │ veth-cr ──┼──────────────────┼── veth-rc │                  │           │
  │ 10.0.1.2  │                  │ 10.0.1.1  │                  │           │
  │           │                  │           │                  │           │
  │           │                  │ veth-rs ──┼──────────────────┼── veth-sr │
  │           │                  │ 10.0.2.1  │                  │ 10.0.2.2  │
  └──────────┘                   └──────────┘                  └──────────┘

  Client default gw: 10.0.1.1         ip_forward=1       Server default gw: 10.0.2.1
```

**Полный скрипт:**

```bash
#!/bin/bash
# lab-ns-router.sh --- Стенд с тремя namespace (client / router / server)
# Запуск: sudo bash lab-ns-router.sh

set -euo pipefail

# --- Очистка ---
for ns in client router server; do
    ip netns del $ns 2>/dev/null || true
done

echo "[1/6] Создаём namespace..."
ip netns add client
ip netns add router
ip netns add server

echo "[2/6] Создаём veth pair 1: client <-> router..."
ip link add veth-cr type veth peer name veth-rc
ip link set veth-cr netns client
ip link set veth-rc netns router

echo "[3/6] Создаём veth pair 2: router <-> server..."
ip link add veth-rs type veth peer name veth-sr
ip link set veth-rs netns router
ip link set veth-sr netns server

echo "[4/6] Настраиваем IP-адреса..."
# Client: 10.0.1.2/24
ip netns exec client ip addr add 10.0.1.2/24 dev veth-cr
ip netns exec client ip link set veth-cr up
ip netns exec client ip link set lo up

# Router: 10.0.1.1/24 (сторона клиента) + 10.0.2.1/24 (сторона сервера)
ip netns exec router ip addr add 10.0.1.1/24 dev veth-rc
ip netns exec router ip link set veth-rc up
ip netns exec router ip addr add 10.0.2.1/24 dev veth-rs
ip netns exec router ip link set veth-rs up
ip netns exec router ip link set lo up

# Server: 10.0.2.2/24
ip netns exec server ip addr add 10.0.2.2/24 dev veth-sr
ip netns exec server ip link set veth-sr up
ip netns exec server ip link set lo up

echo "[5/6] Настраиваем маршрутизацию..."
# Включаем forwarding в router namespace
ip netns exec router sysctl -w net.ipv4.ip_forward=1

# Default gateway для client -> через router
ip netns exec client ip route add default via 10.0.1.1

# Default gateway для server -> через router
ip netns exec server ip route add default via 10.0.2.1

echo "[6/6] Включаем offloading..."
ip netns exec client ethtool -K veth-cr tso on gso on gro on 2>/dev/null || true
ip netns exec server ethtool -K veth-sr tso on gso on gro on 2>/dev/null || true

echo ""
echo "=== Стенд с роутером готов ==="
echo "Client:  10.0.1.2  (namespace 'client')"
echo "Router:  10.0.1.1 / 10.0.2.1  (namespace 'router', ip_forward=1)"
echo "Server:  10.0.2.2  (namespace 'server')"
echo ""
echo "Проверка сквозного пинга (client -> server через router):"
ip netns exec client ping -c 3 10.0.2.2
echo ""
echo "Traceroute:"
ip netns exec client traceroute -n 10.0.2.2 2>/dev/null || \
    ip netns exec client ip route get 10.0.2.2
```

**Верификация:**

```bash
# Пинг от клиента к серверу должен проходить через роутер
sudo ip netns exec client ping -c 3 10.0.2.2

# Проверяем, что forwarding включён
sudo ip netns exec router sysctl net.ipv4.ip_forward
# net.ipv4.ip_forward = 1

# Проверяем маршруты клиента
sudo ip netns exec client ip route
# default via 10.0.1.1 dev veth-cr
# 10.0.1.0/24 dev veth-cr proto kernel scope link src 10.0.1.2
```

**Применяем tc netem на роутере (эмуляция WAN):**

```bash
# Задержка 50ms в обе стороны на роутере (total RTT ~200ms)
sudo ip netns exec router tc qdisc add dev veth-rc root netem delay 50ms
sudo ip netns exec router tc qdisc add dev veth-rs root netem delay 50ms

# Проверяем RTT
sudo ip netns exec client ping -c 5 10.0.2.2
# rtt min/avg/max = ~200ms

# Тест пропускной способности
sudo ip netns exec server iperf3 -s &
sudo ip netns exec client iperf3 -c 10.0.2.2 -t 10

# Очистка tc на роутере
sudo ip netns exec router tc qdisc del dev veth-rc root
sudo ip netns exec router tc qdisc del dev veth-rs root
```

**Очистка всего стенда:**

```bash
for ns in client router server; do
    sudo ip netns del $ns 2>/dev/null || true
done
```

---

## Уровень 2: VMware (Рекомендуемый)

### Архитектура

Полноценная виртуальная лаборатория с тремя виртуальными машинами и изолированными ядрами. Каждая VM --- это отдельный Linux со своим ядром, своими sysctl, своим congestion control.

```
                        VMware Workstation / Player
  ┌─────────────────────────────────────────────────────────────────────┐
  │                                                                     │
  │  ┌──────────┐     LAN Segment 1      ┌──────────┐     LAN Segment 2      ┌──────────┐
  │  │  Client   │   Link_Client_Router   │  Router   │   Link_Router_Server   │  Server   │
  │  │          │                         │          │                         │          │
  │  │   ens33 ─┼─────────────────────────┼─ ens34   │                         │          │
  │  │ .60.10   │                         │ .60.1    │                         │          │
  │  │          │                         │          │                         │          │
  │  │          │                         │ ens35 ───┼─────────────────────────┼─ ens33   │
  │  │          │                         │ .50.1    │                         │ .50.10   │
  │  │          │                         │          │                         │          │
  │  │          │                         │ ens33 ───┼─── NAT (Internet)       │          │
  │  │          │                         │ (DHCP)   │                         │          │
  │  └──────────┘                         └──────────┘                         └──────────┘
  │                                                                     │
  │  192.168.60.0/24                                    192.168.50.0/24 │
  └─────────────────────────────────────────────────────────────────────┘
```

**Почему роутер посередине:**
- `tc netem` применяется на Router --- он становится "чёрным ящиком" между Client и Server, как WAN-канал в реальной жизни
- Client и Server не знают о задержках/потерях --- они видят только "плохую сеть", как в продакшене
- Изолированные ядра: на Client можно включить BBR, на Server оставить CUBIC
- Router раздаёт интернет через NAT --- на Client и Server можно ставить пакеты через `apt`

### Шаг 1: Подготовка виртуальных сетей в VMware

#### VMware Workstation Pro

1. Откройте VMware Workstation
2. Зайдите в настройки любой VM (или создайте новую) -> **Hardware** -> **Network Adapter** -> **LAN Segments...**
3. Нажмите **Add** и создайте два сегмента:
   - `Link_Client_Router`
   - `Link_Router_Server`
4. Нажмите **OK**

LAN Segment --- это полностью изолированная виртуальная сеть. Никакого DHCP, никакого NAT. Только те VM, которые вы к ней подключите.

#### VMware Player (бесплатная версия)

В Player нет LAN Segments. Используйте кастомные VMnet:

1. Откройте **Virtual Network Editor** (может потребовать прав администратора)
2. Нажмите **Add Network**:
   - **VMnet2**: Host-only, **снимите галку "Connect a host virtual adapter"**, **снимите галку "Use local DHCP"**
   - **VMnet3**: Host-only, **снимите галку "Connect a host virtual adapter"**, **снимите галку "Use local DHCP"**
3. Нажмите **Apply**

Далее в инструкции вместо "LAN Segment `Link_Client_Router`" используйте "Custom (VMnet2)", а вместо "LAN Segment `Link_Router_Server`" --- "Custom (VMnet3)".

**Верификация:** В Virtual Network Editor должны быть видны VMnet2 и VMnet3 с типом Host-only и отключённым DHCP.

### Шаг 2: Создание виртуальных машин

Скачайте ISO-образ **Ubuntu Server 22.04 LTS** (или 24.04 LTS). Используйте минимальную установку --- нам не нужен GUI.

**Рекомендуемые ресурсы для каждой VM:**

| Параметр | Router | Client | Server |
|----------|--------|--------|--------|
| CPU | 1 vCPU | 1 vCPU | 1 vCPU |
| RAM | 1 GB | 1 GB | 1 GB |
| Disk | 10 GB | 10 GB | 10 GB |
| **Итого на хосте** | **3 vCPU, 3 GB RAM, 30 GB disk** |||

#### VM "Router" (центральный узел)

Создайте VM, установите Ubuntu Server. **Три сетевых адаптера:**

| Adapter | Тип | Назначение |
|---------|-----|------------|
| Network Adapter 1 | **NAT** | Доступ в интернет (для `apt install`) |
| Network Adapter 2 | **LAN Segment** -> `Link_Client_Router` | Связь с Client |
| Network Adapter 3 | **LAN Segment** -> `Link_Router_Server` | Связь с Server |

Чтобы добавить дополнительные адаптеры: **VM Settings** -> **Add...** -> **Network Adapter**.

При установке Ubuntu задайте hostname: `router`.

#### VM "Client" (генератор трафика)

Создайте VM, установите Ubuntu Server. **Один сетевой адаптер:**

| Adapter | Тип | Назначение |
|---------|-----|------------|
| Network Adapter 1 | **LAN Segment** -> `Link_Client_Router` | Связь с Router |

Не добавляйте NAT-адаптер. Client получит интернет через Router (как в реальной сети).

При установке Ubuntu задайте hostname: `client`.

#### VM "Server" (приёмник трафика)

Создайте VM, установите Ubuntu Server. **Один сетевой адаптер:**

| Adapter | Тип | Назначение |
|---------|-----|------------|
| Network Adapter 1 | **LAN Segment** -> `Link_Router_Server` | Связь с Router |

Не добавляйте NAT-адаптер. Server получит интернет через Router.

При установке Ubuntu задайте hostname: `server`.

### Шаг 3: Настройка Router

Залогиньтесь в консоль Router. Определите имена интерфейсов:

```bash
ip link show
```

Обычно: `ens33` (NAT), `ens34` (LAN Segment 1), `ens35` (LAN Segment 2). Если имена отличаются, подставьте свои.

Чтобы точно определить, какой интерфейс к какому сегменту подключён, посмотрите MAC-адреса:

```bash
ip link show | grep -E "^[0-9]|link/ether"
```

Сопоставьте MAC-адреса с теми, что показаны в настройках VM в VMware (Advanced для каждого адаптера).

**Настройка:**

```bash
# --- Шаг 3.1: Назначаем IP на внутренние интерфейсы ---

# ens34 -> связь с Client (подсеть 192.168.60.0/24)
sudo ip addr add 192.168.60.1/24 dev ens34
sudo ip link set ens34 up

# ens35 -> связь с Server (подсеть 192.168.50.0/24)
sudo ip addr add 192.168.50.1/24 dev ens35
sudo ip link set ens35 up

# --- Шаг 3.2: Включаем маршрутизацию (IP Forwarding) ---
sudo sysctl -w net.ipv4.ip_forward=1

# --- Шаг 3.3: Настраиваем NAT (Masquerade) ---
# Чтобы Client и Server могли выходить в интернет через Router
# ens33 --- интерфейс с NAT (смотрит в интернет через VMware)
sudo iptables -t nat -A POSTROUTING -o ens33 -j MASQUERADE
sudo iptables -A FORWARD -i ens34 -o ens33 -j ACCEPT
sudo iptables -A FORWARD -i ens35 -o ens33 -j ACCEPT
sudo iptables -A FORWARD -i ens34 -o ens35 -j ACCEPT
sudo iptables -A FORWARD -i ens35 -o ens34 -j ACCEPT

# --- Шаг 3.4: Установка пакетов ---
sudo apt update && sudo apt install -y iperf3 iproute2 tcpdump
```

**Верификация Router:**

```bash
# IP-адреса назначены
ip -4 addr show ens34
# inet 192.168.60.1/24 ...

ip -4 addr show ens35
# inet 192.168.50.1/24 ...

# Forwarding включён
sysctl net.ipv4.ip_forward
# net.ipv4.ip_forward = 1

# NAT-интерфейс имеет IP от VMware DHCP
ip -4 addr show ens33
# inet 192.168.x.x/24 ... (назначен VMware NAT DHCP)

# Интернет работает
ping -c 2 8.8.8.8
```

### Шаг 4: Настройка Server

Залогиньтесь в консоль Server. У него единственный интерфейс (обычно `ens33`), подключённый к `Link_Router_Server`.

```bash
# --- Шаг 4.1: Назначаем IP ---
sudo ip addr add 192.168.50.10/24 dev ens33
sudo ip link set ens33 up

# --- Шаг 4.2: Шлюз по умолчанию --- наш Router ---
sudo ip route add default via 192.168.50.1

# --- Шаг 4.3: DNS (иначе apt не сможет резолвить имена) ---
echo "nameserver 8.8.8.8" | sudo tee /etc/resolv.conf

# --- Шаг 4.4: Установка пакетов ---
sudo apt update && sudo apt install -y iperf3 iproute2 tcpdump
```

**Верификация Server:**

```bash
# IP назначен
ip -4 addr show ens33
# inet 192.168.50.10/24 ...

# Шлюз настроен
ip route show default
# default via 192.168.50.1 dev ens33

# Пинг до Router
ping -c 2 192.168.50.1
# Должен отвечать

# Пинг до интернета (через NAT на Router)
ping -c 2 8.8.8.8
# Должен отвечать --- значит NAT на Router работает

# DNS работает
ping -c 2 google.com
```

### Шаг 5: Настройка Client

Залогиньтесь в консоль Client. Единственный интерфейс (обычно `ens33`) подключён к `Link_Client_Router`.

```bash
# --- Шаг 5.1: Назначаем IP ---
sudo ip addr add 192.168.60.10/24 dev ens33
sudo ip link set ens33 up

# --- Шаг 5.2: Шлюз по умолчанию --- наш Router ---
sudo ip route add default via 192.168.60.1

# --- Шаг 5.3: DNS ---
echo "nameserver 8.8.8.8" | sudo tee /etc/resolv.conf

# --- Шаг 5.4: Установка пакетов ---
# Client получает расширенный набор --- bpfcc-tools для eBPF-лабораторных
sudo apt update && sudo apt install -y iperf3 iproute2 tcpdump bpfcc-tools
```

**Верификация Client:**

```bash
# IP назначен
ip -4 addr show ens33
# inet 192.168.60.10/24 ...

# Шлюз настроен
ip route show default
# default via 192.168.60.1 dev ens33

# Пинг до Router (ближний интерфейс)
ping -c 2 192.168.60.1

# Пинг до Server (через Router)
ping -c 2 192.168.50.10
# Должен отвечать --- трафик идёт: Client -> Router -> Server

# Пинг до интернета
ping -c 2 8.8.8.8

# Traceroute: видим, что трафик идёт через Router
traceroute -n 192.168.50.10
# 1  192.168.60.1   ...ms  (Router)
# 2  192.168.50.10  ...ms  (Server)
```

### Шаг 6: Проверка стенда

Все три машины настроены. Теперь полная проверка --- трафик от Client к Server **обязан** пройти через Router.

**Тест 1: iperf3 через Router:**

```bash
# На Server:
iperf3 -s

# На Client:
iperf3 -c 192.168.50.10
```

Вы должны увидеть пропускную способность в несколько Гбит/с (VMware оптимизирует трафик внутри памяти хоста).

**Тест 2: Пинг от Client к Server:**

```bash
# На Client:
ping -c 10 192.168.50.10
```

RTT должен быть менее 1ms (задержка виртуального свитча VMware).

**Тест 3: Traceroute --- проверяем маршрут:**

```bash
# На Client:
traceroute -n 192.168.50.10
```

Ожидаемый вывод:

```
 1  192.168.60.1  0.345 ms  0.276 ms  0.218 ms    <- Router
 2  192.168.50.10  0.512 ms  0.439 ms  0.388 ms   <- Server
```

Если traceroute показывает два хопа --- стенд собран правильно. Весь трафик проходит через Router.

**Тест 4: tcpdump на Router --- видим транзитный трафик:**

```bash
# На Router:
sudo tcpdump -i ens34 -nn icmp
# и одновременно на Client:
ping -c 5 192.168.50.10
```

На Router вы должны видеть ICMP echo request и echo reply --- трафик проходит транзитом.

### Шаг 7: Первая лабораторная --- The Lag

Стенд готов. Проведём первый эксперимент: посмотрим, как задержка убивает пропускную способность TCP.

**На Router --- добавляем 100ms задержки:**

```bash
# Добавляем задержку на интерфейс, смотрящий в сторону Client
# Задержка 100ms в каждую сторону = RTT 200ms
sudo tc qdisc add dev ens34 root netem delay 100ms
```

**На Client --- запускаем тест:**

```bash
# Проверяем RTT: должен быть ~200ms
ping -c 5 192.168.50.10

# Запускаем iperf3
iperf3 -c 192.168.50.10 -t 10
```

Наблюдаем: пропускная способность упала с нескольких Гбит/с до десятков Мбит/с. Канал тот же, но TCP не может утилизировать его из-за высокого RTT. Это прямое следствие формулы BDP (Bandwidth-Delay Product).

**Дополнительно --- включаем BBR и сравниваем:**

```bash
# На Client:
sudo sysctl -w net.ipv4.tcp_congestion_control=bbr

# Снова запускаем iperf3
iperf3 -c 192.168.50.10 -t 10

# Сравните throughput с CUBIC. BBR должен показать значительно больше
# на каналах с высоким RTT.
```

**Очистка:**

```bash
# На Router --- убираем задержку
sudo tc qdisc del dev ens34 root

# На Client --- возвращаем CUBIC (если меняли)
sudo sysctl -w net.ipv4.tcp_congestion_control=cubic
```

### Сделать настройки постоянными

После перезагрузки VM все настройки `ip addr`, `ip route`, `sysctl`, `iptables` сбросятся. Чтобы не вводить команды каждый раз, используем Netplan и systemd.

#### Router: `/etc/netplan/01-lab.yaml`

```yaml
network:
  version: 2
  renderer: networkd
  ethernets:
    ens33:
      # NAT-интерфейс --- получает IP от VMware DHCP
      dhcp4: true
    ens34:
      # Связь с Client
      addresses:
        - 192.168.60.1/24
      dhcp4: false
    ens35:
      # Связь с Server
      addresses:
        - 192.168.50.1/24
      dhcp4: false
```

Применение:

```bash
sudo netplan apply

# Проверка
ip -4 addr show ens34
ip -4 addr show ens35
```

#### Router: systemd-сервис для forwarding и NAT

Создайте файл `/etc/systemd/system/lab-router.service`:

```ini
[Unit]
Description=Lab Router - IP forwarding and NAT
After=network-online.target
Wants=network-online.target

[Service]
Type=oneshot
RemainAfterExit=yes

# Включаем forwarding
ExecStart=/sbin/sysctl -w net.ipv4.ip_forward=1

# NAT masquerade
ExecStart=/sbin/iptables -t nat -A POSTROUTING -o ens33 -j MASQUERADE
ExecStart=/sbin/iptables -A FORWARD -i ens34 -o ens33 -j ACCEPT
ExecStart=/sbin/iptables -A FORWARD -i ens35 -o ens33 -j ACCEPT
ExecStart=/sbin/iptables -A FORWARD -i ens34 -o ens35 -j ACCEPT
ExecStart=/sbin/iptables -A FORWARD -i ens35 -o ens34 -j ACCEPT

[Install]
WantedBy=multi-user.target
```

Активация:

```bash
sudo systemctl daemon-reload
sudo systemctl enable lab-router.service
sudo systemctl start lab-router.service

# Проверка
sudo systemctl status lab-router.service
sysctl net.ipv4.ip_forward
sudo iptables -t nat -L POSTROUTING -n
```

#### Server: `/etc/netplan/01-lab.yaml`

```yaml
network:
  version: 2
  renderer: networkd
  ethernets:
    ens33:
      addresses:
        - 192.168.50.10/24
      routes:
        - to: default
          via: 192.168.50.1
      nameservers:
        addresses:
          - 8.8.8.8
          - 8.8.4.4
      dhcp4: false
```

```bash
sudo netplan apply
ping -c 2 8.8.8.8   # Проверка
```

#### Client: `/etc/netplan/01-lab.yaml`

```yaml
network:
  version: 2
  renderer: networkd
  ethernets:
    ens33:
      addresses:
        - 192.168.60.10/24
      routes:
        - to: default
          via: 192.168.60.1
      nameservers:
        addresses:
          - 8.8.8.8
          - 8.8.4.4
      dhcp4: false
```

```bash
sudo netplan apply
ping -c 2 192.168.50.10   # Сквозной пинг через Router
```

**После настройки Netplan и systemd-сервиса:** перезагрузите все три VM (`sudo reboot`) и убедитесь, что стенд поднимается автоматически:

```bash
# На Client после ребута:
ping -c 3 192.168.50.10   # Должен работать без ручной настройки
traceroute -n 192.168.50.10   # Два хопа через Router
```

---

## Уровень 2.5: Физический стенд на домашнем оборудовании

### Зачем

VMware-стенд (Уровень 2) — отличная песочница, но трафик в ней никогда не покидает RAM гипервизора. Вы не видите реальных очередей на порту, реального ARP, реального влияния коллизий в полудуплексе, реального поведения TCP через настоящий роутер с аппаратным QoS.

Физический стенд на домашнем оборудовании даёт:
- **Реальные задержки.** Пакеты проходят через PHY, MAC, буферы коммутатора, очереди роутера. RTT 0.3–0.8ms вместо 0.02ms в VMware.
- **Реальные очереди MikroTik.** Queue Tree / Simple Queues / HTB на RouterOS — это настоящий traffic shaping, который используют ISP. Не эмуляция через `tc netem`.
- **Реальные ограничения.** 100 Mbps порты RB951 — это не баг, а фича: congestion становится видимым на обычном `iperf3`, без искусственных ограничений.
- **VLAN, firewall, NAT на железе.** CRS326 с аппаратной коммутацией VLAN + MikroTik firewall = среда, максимально приближённая к продакшену.

### Имеющееся оборудование

| Устройство | Роль в стенде | Ключевые характеристики |
|---|---|---|
| **MikroTik CRS326-24G-2S+RM** | Core Switch | 24× GbE, 2× SFP+ (10G), SwOS/RouterOS, VLAN, mirror port |
| **MikroTik hAP ac2** | Router / WAN emulator | 5× GbE, RouterOS 7, Wi-Fi ac, IPsec HW, 128MB RAM |
| **MikroTik RB951Ui-2HnD** | Router / Bandwidth limiter | 5× 100Mbps FE, RouterOS, PoE out, 128MB RAM |
| **Ноутбук Win10** (i7, 8GB, SSD) | Client / Wireshark station | Основная рабочая станция, генератор трафика |
| **Ноутбук Ubuntu** (Intel, 4GB, SSD) | Server / DUT | Приёмник трафика, iperf3/nginx сервер |
| **Ноутбук Win/Linux** (i3, 4GB, SSD) | Monitor / 2nd client | Захват трафика, второй источник нагрузки |

### Топология стенда

```
                         ┌─────────────────────────────────────────┐
                         │     MikroTik CRS326-24G-2S+RM          │
                         │         (Core Switch)                   │
                         │                                         │
                         │  Port 1    Port 2    Port 3    Port 4   │
                         │  VLAN 10   VLAN 10   VLAN 20   VLAN 20  │
                         └────┬─────────┬─────────┬─────────┬──────┘
                              │         │         │         │
                              │         │         │         │
                         ┌────┴────┐    │    ┌────┴────┐    │
                         │ Laptop  │    │    │ Laptop  │    │
                         │ Win10   │    │    │ Ubuntu  │    │
                         │ (Client)│    │    │(Server) │    │
                         │ .10.10  │    │    │ .20.10  │    │
                         └─────────┘    │    └─────────┘    │
                                        │                   │
                              ┌─────────┴───────────────────┴──────┐
                              │       MikroTik hAP ac2             │
                              │     (Router / WAN Emulator)        │
                              │                                     │
                              │  ether2          ether3             │
                              │  192.168.10.1    192.168.20.1       │
                              │                                     │
                              │  ether1 ──── ISP / Internet         │
                              └─────────────────────────────────────┘

  Подсеть Client: 192.168.10.0/24  (VLAN 10)
  Подсеть Server: 192.168.20.0/24  (VLAN 20)

  Опционально:
                         ┌─────────────────────────────────────────┐
                         │  MikroTik RB951Ui-2HnD                  │
                         │  (WAN bottleneck — 100 Mbps ports)      │
                         │                                          │
                         │  Включается между hAP ac2 и CRS326     │
                         │  для эмуляции медленного WAN-канала      │
                         └─────────────────────────────────────────┘

  3-й ноутбук:
  - Подключается к CRS326 mirror port для пассивного захвата трафика
  - Или как второй клиент для тестирования fairness (два потока конкурируют)
```

### Шаг 1: Настройка CRS326 (Core Switch)

CRS326 работает в режиме SwOS (аппаратная коммутация) или RouterOS (программная). Для нашего стенда используем **RouterOS** — он гибче и позволяет настроить port mirroring.

```
# Подключаемся к CRS326 (по умолчанию 192.168.88.1, admin без пароля)
# Через WinBox или SSH

# --- Создаём VLAN-ы ---

# Bridge для коммутации
/interface bridge
add name=bridge1 vlan-filtering=no

# Добавляем порты в bridge
/interface bridge port
add bridge=bridge1 interface=ether1 pvid=10
add bridge=bridge1 interface=ether2 pvid=10
add bridge=bridge1 interface=ether3 pvid=20
add bridge=bridge1 interface=ether4 pvid=20

# Настраиваем VLAN на bridge
/interface bridge vlan
add bridge=bridge1 tagged=bridge1 untagged=ether1,ether2 vlan-ids=10
add bridge=bridge1 tagged=bridge1 untagged=ether3,ether4 vlan-ids=20

# Включаем VLAN filtering (делаем последним — иначе потеряете доступ!)
/interface bridge
set bridge1 vlan-filtering=yes

# --- Port Mirroring (для 3-го ноутбука с Wireshark) ---
# Зеркалируем трафик с ether1 (Client) на ether5 (Monitor)
/interface ethernet switch
set ingress-mirror-src=ether1 egress-mirror-src=ether1 mirror-target=ether5
```

**Важно:** Перед включением `vlan-filtering=yes` убедитесь, что у вас есть доступ к CRS326 через порт, который не будет заблокирован. Лучше всего подключаться через ether24 или через MAC-адрес в WinBox.

**Верификация:**

```
# Проверяем VLAN
/interface bridge vlan print

# Проверяем, что порты в правильных VLAN
/interface bridge port print

# Проверяем mirror
/interface ethernet switch print
```

### Шаг 2: Настройка hAP ac2 (Router)

hAP ac2 — основной маршрутизатор. Два интерфейса смотрят в разные VLAN через CRS326, один — в интернет.

```
# Сбрасываем к заводским настройкам (если нужно)
/system reset-configuration no-defaults=yes

# --- Интерфейсы ---
# ether1 → ISP (WAN, DHCP client)
# ether2 → CRS326 (VLAN 10, подсеть клиента)
# ether3 → CRS326 (VLAN 20, подсеть сервера)

# --- IP-адреса ---
/ip address
add address=192.168.10.1/24 interface=ether2
add address=192.168.20.1/24 interface=ether3

# WAN — DHCP от ISP
/ip dhcp-client
add interface=ether1 disabled=no

# --- Маршрутизация (уже работает, hAP ac2 — роутер из коробки) ---

# --- NAT для доступа в интернет ---
/ip firewall nat
add chain=srcnat out-interface=ether1 action=masquerade

# --- Firewall — разрешаем forwarding между подсетями ---
/ip firewall filter
add chain=forward action=accept src-address=192.168.10.0/24 dst-address=192.168.20.0/24
add chain=forward action=accept src-address=192.168.20.0/24 dst-address=192.168.10.0/24
add chain=forward action=accept connection-state=established,related

# --- DNS ---
/ip dns
set allow-remote-requests=yes servers=8.8.8.8,8.8.4.4
```

**Верификация:**

```
# Проверяем интерфейсы
/ip address print

# Проверяем маршрут в интернет
/ping 8.8.8.8 count=3

# Проверяем маршруты
/ip route print
```

### Шаг 3: Настройка ноутбуков

**Client (Windows 10, i7, 8GB):**

```powershell
# Настройка через GUI: Network Settings → Ethernet → IP Settings → Manual
# IP:      192.168.10.10
# Mask:    255.255.255.0
# Gateway: 192.168.10.1
# DNS:     192.168.10.1 (или 8.8.8.8)

# Или через PowerShell (от администратора):
New-NetIPAddress -InterfaceAlias "Ethernet" -IPAddress 192.168.10.10 -PrefixLength 24 -DefaultGateway 192.168.10.1
Set-DnsClientServerAddress -InterfaceAlias "Ethernet" -ServerAddresses 8.8.8.8,8.8.4.4

# Проверка
ping 192.168.10.1      # Gateway
ping 192.168.20.10     # Server (через роутер)
ping 8.8.8.8           # Internet
tracert 192.168.20.10  # Должен показать хоп через 192.168.10.1
```

Установите инструменты:
- [Wireshark](https://www.wireshark.org/) — захват и анализ трафика
- [iperf3 для Windows](https://iperf.fr/iperf-download.php) — тестирование пропускной способности
- [Nmap для Windows](https://nmap.org/download.html) — сканирование сети
- [PuTTY](https://www.putty.org/) или Windows Terminal + SSH — доступ к MikroTik и Ubuntu

**Server (Ubuntu, 4GB):**

```bash
# /etc/netplan/01-lab.yaml
sudo tee /etc/netplan/01-lab.yaml << 'NETPLAN'
network:
  version: 2
  renderer: networkd
  ethernets:
    # Имя интерфейса узнайте через: ip link show
    enp2s0:
      addresses:
        - 192.168.20.10/24
      routes:
        - to: default
          via: 192.168.20.1
      nameservers:
        addresses:
          - 8.8.8.8
          - 8.8.4.4
      dhcp4: false
NETPLAN

sudo netplan apply

# Установка инструментов
sudo apt update && sudo apt install -y \
    iperf3 iproute2 tcpdump ethtool \
    bpfcc-tools nmap hping3 tshark python3-scapy \
    nginx  # веб-сервер для HTTP-тестов

# Проверка
ping -c 3 192.168.20.1     # Gateway
ping -c 3 192.168.10.10    # Client
ping -c 3 8.8.8.8          # Internet
traceroute -n 192.168.10.10  # Хоп через 192.168.20.1
```

**Monitor / 2-й клиент (3-й ноутбук):**

Подключается к CRS326 ether5 (mirror port). Этот порт получает копию всего трафика с ether1 — пассивный захват без влияния на сеть.

```bash
# Если ставите Linux:
sudo ip addr add 192.168.10.20/24 dev enp2s0   # Или отдельная подсеть
sudo ip link set enp2s0 up

# Захват зеркалированного трафика:
sudo tcpdump -i enp2s0 -w /tmp/mirror-capture.pcap

# Или просто Wireshark на этом интерфейсе — видите ВСЁ,
# что идёт от/к Client, не влияя на трафик.
```

### Шаг 4: Эмуляция WAN через MikroTik Queue

Главное преимущество MikroTik — **Queue Tree** и **Simple Queues**. Это реальный traffic shaping на аппаратном роутере, а не `tc netem` на Linux.

**Ограничение полосы (Simple Queue):**

```
# На hAP ac2:
# Ограничиваем Client до 10 Mbps download / 5 Mbps upload
/queue simple
add name=client-limit target=192.168.10.10/32 \
    max-limit=5M/10M

# Проверяем:
# На Server: iperf3 -s
# На Client: iperf3 -c 192.168.20.10
# Должно показать ~10 Mbps вместо ~940 Mbps
```

**Эмуляция задержки и потерь (Queue + Mangle):**

RouterOS не имеет встроенного `netem`, но можно эмулировать задержки через queue + burst, а для полноценной эмуляции WAN подключаем RB951 в разрыв.

### Шаг 5: RB951 как WAN Bottleneck

RB951Ui-2HnD имеет 100 Mbps порты — это естественное ограничение пропускной способности. Включаем его между hAP ac2 и CRS326 для эмуляции медленного WAN-канала.

**Топология с RB951:**

```
  Client ── CRS326 (VLAN 10) ── hAP ac2 ── RB951 ── CRS326 (VLAN 20) ── Server
                                             │
                                        100 Mbps
                                       bottleneck
```

```
# На RB951:
# ether1 → от hAP ac2 ether3
# ether2 → к CRS326 (VLAN 20)

/ip address
add address=10.99.0.2/30 interface=ether1
add address=10.99.0.5/30 interface=ether2

/ip route
add dst-address=192.168.10.0/24 gateway=10.99.0.1
add dst-address=192.168.20.0/24 gateway=10.99.0.6

# Эмуляция плохого канала — Queue + Queue Type
/queue type
add name=wan-shape kind=pcq pcq-rate=20M pcq-limit=50KiB

/queue simple
add name=wan-bottleneck target=ether2 \
    max-limit=20M/20M \
    queue=wan-shape/wan-shape

# Теперь весь трафик Client→Server ограничен 20 Mbps
# через реальный 100 Mbps линк с очередями PCQ
```

### Шаг 6: Лабораторные на физическом стенде

**Лаб 1: Реальный Bandwidth-Delay Product**

```bash
# На Server:
iperf3 -s

# На Client (PowerShell или WSL):
iperf3.exe -c 192.168.20.10 -t 30

# Замеряем RTT:
ping 192.168.20.10
# Ожидаем: 0.5–1.5ms (реальный RTT через коммутатор + роутер)

# Сравните с VMware (RTT ~0.02ms) — разница в 25–75 раз!
```

**Лаб 2: Queue Tree — приоритизация трафика**

```
# На hAP ac2: SSH-трафик приоритетнее, чем bulk download

# Маркируем пакеты
/ip firewall mangle
add chain=forward protocol=tcp dst-port=22 action=mark-packet \
    new-packet-mark=ssh-traffic passthrough=no
add chain=forward action=mark-packet \
    new-packet-mark=bulk-traffic passthrough=no

# Queue Tree с приоритетами
/queue tree
add name=total parent=ether3 max-limit=50M
add name=ssh parent=total packet-mark=ssh-traffic \
    priority=1 max-limit=50M
add name=bulk parent=total packet-mark=bulk-traffic \
    priority=8 max-limit=45M
```

**Лаб 3: Fairness — два клиента конкурируют**

```bash
# Client 1 (Win10): iperf3.exe -c 192.168.20.10 -t 60
# Client 2 (3-й ноутбук): iperf3 -c 192.168.20.10 -t 60 -p 5202

# На Server: два экземпляра iperf3
iperf3 -s -p 5201 &
iperf3 -s -p 5202 &

# На Monitor (mirror port): захватываем и анализируем в Wireshark
# Statistics → I/O Graphs → фильтры по IP
# Видим, как два потока делят полосу в реальном времени
```

**Лаб 4: Port mirroring + Wireshark анализ**

```
# Mirror уже настроен на CRS326 (ether1 → ether5)
# На 3-м ноутбуке (ether5) запускаем Wireshark:

# Фильтр: tcp.analysis.retransmission
# Фильтр: tcp.analysis.duplicate_ack
# Фильтр: tcp.window_size_value < 1000

# Это пассивный захват — вы видите реальные ретрансмиты,
# дупликаты ACK, window shrink без влияния на трафик.
```

### Плюсы и минусы

**Плюсы:**
- **Реальная физика.** Пакеты проходят через PHY, MAC, кабели, буферы коммутатора. RTT, jitter, потери — настоящие.
- **MikroTik Queue ≈ продакшен ISP.** PCQ, HTB, Queue Tree — те же технологии, что используют провайдеры. Опыт переносится напрямую.
- **Port mirroring.** Пассивный захват трафика на отдельном ноутбуке — без влияния на сеть, как в реальном NOC.
- **VLAN на железе.** CRS326 делает аппаратную коммутацию VLAN — zero CPU overhead.
- **Бюджет ≈ $0.** Всё оборудование уже есть.
- **100 Mbps RB951 = встроенный bottleneck.** Идеально для наблюдения congestion без искусственных ограничений.

**Минусы:**
- **Нет `tc netem` на MikroTik.** RouterOS не умеет эмулировать произвольные потери, jitter, reorder. Для этих экспериментов используйте Linux namespace (Уровень 1) на Ubuntu-ноутбуке.
- **4 GB RAM на двух ноутбуках.** Недостаточно для тяжёлых eBPF-инструментов или сбора больших pcap. Wireshark на 4 GB будет тормозить на захватах > 500 MB.
- **Нет 10G.** Максимум 1 Gbps через CRS326. Для DPDK/XDP-лабораторных нужны SFP+ NIC и DAC-кабели (Уровень 3).
- **Физическое пространство.** Три ноутбука + три MikroTik + кабели = нужен стол.

### Матрица: что можно делать на этом стенде

| Лабораторная | Поддержка | Примечание |
|---|---|---|
| iperf3 throughput тесты | Да | До 940 Mbps (GbE) |
| BBR vs CUBIC сравнение | Да | На Ubuntu-ноутбуке (Server) |
| tc netem (delay, loss, jitter) | Частично | Только на Linux-ноутбуках, не на MikroTik |
| Traffic shaping (HTB, PCQ) | Да | MikroTik Queue Tree — продакшен-уровень |
| VLAN, inter-VLAN routing | Да | CRS326 + hAP ac2 |
| Port mirroring + Wireshark | Да | CRS326 mirror → 3-й ноутбук |
| Firewall / NAT | Да | MikroTik firewall |
| eBPF tracing | Частично | Только на Ubuntu-ноутбуке (4GB — ограничение) |
| DPDK / XDP | Нет | Нет 10G NIC |
| Wi-Fi тестирование | Да | hAP ac2 имеет 802.11ac |

---

## Уровень 3: Физические серверы (для 10G+ тестирования)

### Когда нужен

Виртуализация врёт. Вот конкретные причины:

- **Батчинг гипервизора.** VMware/KVM не передают пакеты по одному --- они накапливают batch и отправляют разом. Реальные микроберсты на 10G/25G/100G вы в виртуалке не увидите и не сможете отладить.
- **Нет реальных очередей NIC.** Виртуальный адаптер (vmxnet3, virtio-net) не имеет аппаратных TX/RX очередей, interrupt coalescing, RSS. Тестировать Ring Buffer tuning или driver offloading бессмысленно.
- **Нет реального DMA.** В виртуалке "DMA" --- это просто копирование памяти внутри гипервизора. Тестировать DPDK, XDP, AF_XDP без реального NIC невозможно.
- **Тайминги нестабильны.** Гипервизор может вставить паузу (vCPU scheduling) посреди вашего теста. Jitter в микросекундном диапазоне непредсказуем.

**Физический стенд обязателен для:**
- Разработка и отладка NIC-драйверов
- DPDK / XDP / AF_XDP
- Тестирование реальных NIC offload: TSO, GRO, RSS, Flow Director
- Обнаружение и анализ микроберстов
- Тестирование при скоростях 10 Gbps и выше
- Профилирование interrupt affinity и NUMA

### Минимальный стенд

```
  ┌──────────────┐         DAC / SFP+        ┌──────────────┐
  │  Machine A    │         10G Direct        │  Machine B    │
  │  (Generator)  ├───────────────────────────┤  (DUT)        │
  │               │      No switch needed     │               │
  │  pktgen-dpdk  │                           │  Your app /   │
  │  or TRex      │                           │  XDP program  │
  └──────────────┘                           └──────────────┘

  Опционально:

  ┌──────────────┐         ┌──────────────┐         ┌──────────────┐
  │  Machine A    ├─────────┤  Machine C    ├─────────┤  Machine B    │
  │  (Generator)  │  10G    │  (Middlebox)  │  10G    │  (DUT)        │
  └──────────────┘         └──────────────┘         └──────────────┘
```

**Два ПК/сервера, соединённых напрямую** (без свитча):
- **Machine A** --- генератор трафика: `pktgen-dpdk`, `TRex` (Cisco) или `MoonGen`
- **Machine B** --- DUT (Device Under Test): ваше приложение, XDP-программа, сервер с тюнингом
- **Machine C** (опционально) --- middlebox: роутер с `tc`, firewall, DPI

Прямое соединение (без свитча) исключает буферизацию коммутатора и позволяет точно измерять поведение на уровне NIC.

### Рекомендации по железу

**10G NIC (бюджетный вариант):**

| Модель | Чип | Поддержка ядром | Примечание |
|--------|-----|-----------------|------------|
| Intel X520-DA2 | 82599ES | `ixgbe` (in-tree) | Дуальный SFP+. Лучший выбор. На eBay за $20-40. |
| Intel X540-T2 | X540 | `ixgbe` (in-tree) | Дуальный 10GBASE-T (RJ45). Не нужны SFP+. |
| Mellanox ConnectX-3 | MT27500 | `mlx4_en` (in-tree) | Дуальный SFP+. Отличная поддержка DPDK. |

**Кабели:**
- **DAC (Direct Attach Copper)** --- лучший выбор для стенда: дешёвые ($5-15), надёжные, длина до 5 метров. Ищите "SFP+ DAC 10G 1m/3m".
- **SFP+ модули + оптика** --- нужны только если расстояние больше 5 метров. Для стенда избыточны.
- **10GBASE-T (RJ45, для X540)** --- используйте Cat6a кабель. Проще, но NIC потребляет больше энергии и имеет выше задержку.

**NUMA-соображения для DPDK/XDP:**

```bash
# Определите, к какому NUMA-узлу подключена NIC
cat /sys/class/net/ens1f0/device/numa_node

# Привяжите приложение к тому же NUMA-узлу
numactl --cpunodebind=0 --membind=0 ./your_dpdk_app

# Или для XDP: убедитесь, что IRQ NIC обрабатываются на CPU того же NUMA-узла
# Посмотрите текущее распределение IRQ:
cat /proc/interrupts | grep ens1f0

# Установите affinity вручную:
echo 1 > /proc/irq/XX/smp_affinity   # где XX --- номер IRQ
```

**Быстрая проверка линка:**

```bash
# На обеих машинах: убедитесь, что линк поднялся на 10G
ethtool ens1f0 | grep -E "Speed|Link detected"
# Speed: 10000Mb/s
# Link detected: yes

# Назначьте IP и проверьте
# Machine A:
sudo ip addr add 10.10.10.1/24 dev ens1f0
sudo ip link set ens1f0 up

# Machine B:
sudo ip addr add 10.10.10.2/24 dev ens1f0
sudo ip link set ens1f0 up

# Пинг
ping -c 5 10.10.10.2

# iperf3 --- должен показать ~9.4 Gbps (line rate 10G минус overhead)
# Machine B:
iperf3 -s
# Machine A:
iperf3 -c 10.10.10.2 -t 30 -P 4   # 4 потока для максимальной утилизации
```

---

## Матрица задач по модулям

Какой уровень стенда нужен для каждой лабораторной:

| Задание | Namespace | VMware | MikroTik | Physical 10G |
|---------|:---------:|:------:|:--------:|:------------:|
| **Модуль 1: Физика и Ядро** | | | | |
| Ring buffer stats (`ethtool -S`) | &#10003; | &#10003; | &#10003; | &#10003; |
| RSS / RPS тестирование | &#10007; | &#10003; | &#10003; | &#10003; |
| **Модуль 2: IP Protocol** | | | | |
| IP-фрагментация | &#10003; | &#10003; | &#10003; | &#10003; |
| Policy routing (ip rule) | &#10007; | &#10003; | &#10003; | &#10003; |
| **Модуль 4: Congestion Control** | | | | |
| BBR vs CUBIC (iperf3 + ss -ti) | &#10003; | &#10003; | &#10003; | &#10003; |
| CAKE qdisc setup | &#10007; | &#10003; | &#10007;* | &#10003; |
| **Модуль 5: Traffic Control** | | | | |
| tc netem --- chaos engineering | &#10003; | &#10003; | &#10003;** | &#10003; |
| HTB --- production shaping | &#10003; | &#10003; | &#10003; | &#10003; |
| MikroTik Queue Tree / PCQ | &#10007; | &#10007; | &#10003; | &#10007; |
| eBPF tracing (tcplife, tcpretrans) | &#10003; | &#10003; | &#10003;*** | &#10003; |
| **Модуль 6: Архитектура приложений** | | | | |
| io_uring echo-сервер | &#10003; | &#10003; | &#10003; | &#10003; |
| splice / zero-copy proxy | &#10003; | &#10003; | &#10003; | &#10003; |
| **Модуль 7: QUIC / HTTP3** | | | | |
| QUIC тестирование | &#10003; | &#10003; | &#10003; | &#10003; |
| **Продвинутые** | | | | |
| Port mirroring + Wireshark | &#10007; | &#10007; | &#10003; | &#10003; |
| VLAN / inter-VLAN routing | &#10007; | &#10007; | &#10003; | &#10003; |
| Детекция микроберстов | &#10007; | &#10007; | &#10007; | &#10003; |
| DPDK / XDP | &#10007; | &#10007; | &#10007; | &#10003; |

**Обозначения:**
- &#10003; --- стенд подходит для выполнения задания
- &#10007; --- стенд не подходит (результаты будут неадекватными или функциональность недоступна)
- \* CAKE --- только на Linux-ноутбуках, RouterOS не поддерживает CAKE
- \*\* tc netem --- на Ubuntu-ноутбуке (через namespace Уровня 1), не на MikroTik
- \*\*\* eBPF --- ограничено 4 GB RAM на Ubuntu-ноутбуке

**Рекомендация:** Для прохождения 90% лабораторных достаточно **Уровня 2 (VMware)**. Namespace (Уровень 1) покрывает ~70% заданий и идеален для быстрых экспериментов. Физический стенд нужен только для модулей, связанных с реальным железом.

---

## Полезные утилиты (установить на все машины)

| Утилита | Пакет | Назначение |
|---------|-------|------------|
| `iperf3` | `iperf3` | Тестирование пропускной способности TCP/UDP |
| `ip`, `tc`, `ss` | `iproute2` | Управление сетью: адреса, маршруты, qdisc, состояние сокетов |
| `tcpdump` | `tcpdump` | Захват и анализ пакетов |
| `bpftrace`, `tcplife`, `tcpretrans` | `bpfcc-tools` | eBPF-инструменты для трассировки ядра |
| `scapy` | `python3-scapy` | Крафтинг и отправка произвольных пакетов (Python) |
| `nmap` | `nmap` | Сканирование сети и портов |
| `hping3` | `hping3` | Продвинутый ping: TCP/UDP/ICMP, flood, traceroute |
| `tshark` | `tshark` | CLI-версия Wireshark для анализа pcap |
| `curl` | `curl` | HTTP-клиент для тестирования QUIC/HTTP3 |
| `ethtool` | `ethtool` | Статистика и настройка NIC |
| `traceroute` | `traceroute` | Трассировка маршрута пакетов |
| `mtr` | `mtr-tiny` | Комбинация traceroute + ping в реальном времени |

### Скрипт установки для Ubuntu 22.04 / 24.04

Скопируйте и выполните на каждой машине стенда:

```bash
#!/bin/bash
# install-lab-tools.sh --- Установка инструментов для лабораторного стенда
# Запуск: sudo bash install-lab-tools.sh

set -euo pipefail

echo "=== Обновление списка пакетов ==="
apt update

echo "=== Установка основных инструментов ==="
apt install -y \
    iperf3 \
    iproute2 \
    tcpdump \
    ethtool \
    traceroute \
    mtr-tiny \
    curl \
    net-tools

echo "=== Установка расширенных инструментов ==="
apt install -y \
    bpfcc-tools \
    nmap \
    hping3 \
    tshark \
    python3-scapy

echo "=== Верификация установки ==="
echo ""
echo "--- Версии инструментов ---"
iperf3 --version 2>&1 | head -1
ip -V 2>&1 | head -1
tcpdump --version 2>&1 | head -1
ethtool --version 2>&1 | head -1
nmap --version 2>&1 | head -1
hping3 --version 2>&1 | head -1 || true
tshark --version 2>&1 | head -1
scapy --version 2>&1 || python3 -c "import scapy; print('scapy', scapy.__version__)" 2>/dev/null || true
echo ""
echo "=== Все инструменты установлены ==="
```

**Верификация после установки:**

```bash
# Быстрая проверка --- все команды доступны
which iperf3 ip tc ss tcpdump ethtool nmap hping3 tshark scapy traceroute mtr
```

---

## Краткая справка: Какой стенд выбрать

| Критерий | Namespace | VMware | MikroTik + ноутбуки | Physical 10G |
|----------|-----------|--------|---------------------|--------------|
| **Время развёртывания** | 5 секунд | 30 минут | 1–2 часа | часы/дни |
| **Потребление RAM** | 0 | ~3 GB | N/A (отдельные машины) | N/A |
| **Изоляция ядер** | Нет (общее ядро) | Да (отдельные ядра) | Да (отдельные машины) | Да |
| **Реальный NIC** | Нет (veth) | Нет (vmxnet3) | Да (GbE) | Да (10G+) |
| **Реальные задержки** | Нет (~0.02ms) | Нет (~0.02ms) | Да (0.5–1.5ms) | Да |
| **BBR vs CUBIC одновременно** | Нет | Да | Да | Да |
| **tc netem** | Да | Да | Частично (Linux-ноутбуки) | Да |
| **HW traffic shaping** | Нет | Нет | Да (MikroTik Queue) | Да |
| **VLAN на железе** | Нет | Нет | Да (CRS326) | Да |
| **Port mirroring** | Нет | Нет | Да (CRS326) | Да |
| **eBPF** | Да | Да | Частично (4GB RAM) | Да |
| **DPDK / XDP** | Нет | Нет | Нет | Да |
| **Микроберсты** | Нет | Нет | Частично (GbE) | Да |
| **Покрытие лабораторных** | ~70% | ~90% | ~85% | 100% |

**Рекомендация по выбору:**

- **Уровень 1 (Namespace)** — держите под рукой для быстрых проверок. За 10 секунд разворачиваете стенд, проверяете гипотезу, удаляете.

- **Уровень 2 (VMware)** — покрывает подавляющее большинство лабораторных, даёт полную изоляцию ядер и позволяет безопасно экспериментировать с `tc`, `sysctl` и eBPF, не рискуя повесить рабочую машину.

- **Уровень 2.5 (MikroTik + ноутбуки)** — если оборудование уже есть, используйте его **параллельно с VMware**. MikroTik даёт то, чего нет в виртуалке: реальные очереди, реальные задержки, port mirroring, VLAN на железе, traffic shaping продакшен-уровня. Идеально для понимания, как TCP ведёт себя в настоящей сети. Для `tc netem`-экспериментов запускайте namespace (Уровень 1) на Ubuntu-ноутбуке.

- **Уровень 3 (Physical 10G)** — когда дойдёте до модулей с DPDK/XDP или когда результаты виртуалки начнут расходиться с продакшеном.
