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

| Задание | Namespace | VMware | Physical |
|---------|:---------:|:------:|:--------:|
| **Модуль 1: Физика и Ядро** | | | |
| Ring buffer stats (`ethtool -S`) | &#10003; | &#10003; | &#10003; |
| RSS / RPS тестирование | &#10007; | &#10003; | &#10003; |
| **Модуль 2: Congestion Control** | | | |
| BBR vs CUBIC (iperf3 + ss -ti) | &#10003; | &#10003; | &#10003; |
| CAKE qdisc setup | &#10007; | &#10003; | &#10003; |
| **Модуль 3: Traffic Control** | | | |
| tc netem --- chaos engineering | &#10003; | &#10003; | &#10003; |
| HTB --- production shaping | &#10003; | &#10003; | &#10003; |
| eBPF tracing (tcplife, tcpretrans) | &#10003; | &#10003; | &#10003; |
| **Модуль 4: IP** | | | |
| IP-фрагментация | &#10003; | &#10003; | &#10003; |
| Policy routing (ip rule) | &#10007; | &#10003; | &#10003; |
| **Модуль 5: Архитектура приложений** | | | |
| io_uring echo-сервер | &#10003; | &#10003; | &#10003; |
| splice / zero-copy proxy | &#10003; | &#10003; | &#10003; |
| **Модуль 6: QUIC / HTTP3** | | | |
| QUIC тестирование | &#10003; | &#10003; | &#10003; |
| **Продвинутые** | | | |
| Детекция микроберстов | &#10007; | &#10007; | &#10003; |
| DPDK / XDP | &#10007; | &#10007; | &#10003; |

**Обозначения:**
- &#10003; --- стенд подходит для выполнения задания
- &#10007; --- стенд не подходит (результаты будут неадекватными или функциональность недоступна)

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

| Критерий | Namespace | VMware | Physical |
|----------|-----------|--------|----------|
| **Время развёртывания** | 5 секунд | 30 минут | часы/дни |
| **Потребление RAM** | 0 | ~3 GB | N/A |
| **Изоляция ядер** | Нет (общее ядро) | Да (отдельные ядра) | Да |
| **Реальный NIC** | Нет (veth) | Нет (vmxnet3) | Да |
| **BBR vs CUBIC одновременно** | Нет | Да | Да |
| **tc netem** | Да | Да | Да |
| **eBPF** | Да | Да | Да |
| **DPDK / XDP** | Нет | Нет | Да |
| **Микроберсты** | Нет | Нет | Да |
| **Покрытие лабораторных** | ~70% | ~90% | 100% |

**Начинайте с Уровня 2 (VMware).** Он покрывает подавляющее большинство лабораторных, даёт полную изоляцию ядер и позволяет безопасно экспериментировать с `tc`, `sysctl` и eBPF, не рискуя повесить рабочую машину.

Уровень 1 (Namespace) держите под рукой для быстрых проверок --- когда нужно за 10 секунд проверить гипотезу.

Уровень 3 (физический) --- когда дойдёте до модулей с DPDK/XDP или когда результаты виртуалки начнут расходиться с продакшеном.
