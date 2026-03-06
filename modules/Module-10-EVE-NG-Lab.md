# Модуль 10: EVE-NG Lab — Боевой полигон для .NET и Chaos Engineering

*«Если ты не можешь сломать свой сервис в лабе — production сделает это за тебя».*

---

## Обзор

Этот модуль — полная инструкция по развёртыванию enterprise-grade лабораторного стенда в EVE-NG. Не абстрактная теория, а конкретные конфиги, которые можно скопировать и запустить.

**Целевая аудитория:** Senior DevOps Engineer / Network Architect, который хочет тестировать отказоустойчивость .NET-сервисов в условиях, приближённых к production.

**Железо:** Core i9, 32 GB RAM, EVE-NG Community или Professional.

---

## Часть 10.1: Архитектура стенда

### Топология

```
                        ┌─────────────────────────────────────────────────┐
                        │            Management Network (OOB)             │
                        │               10.99.0.0/24                      │
                        │  ┌──────┬──────┬──────┬──────┬──────┬──────┐   │
                        │  │.10   │.11   │.12   │.20   │.21   │.30   │   │
                        └──┼──────┼──────┼──────┼──────┼──────┼──────┘   │
                           │      │      │      │      │      │          │
                ┌──────────┴──┐ ┌─┴────┐ ┌┴─────┐ ┌───┴──┐ ┌─┴─────┐ ┌─┴────────┐
                │ansible-     │ │app-01│ │app-02│ │db-sql│ │cache- │ │  EVE-NG  │
                │master       │ │      │ │      │ │      │ │redis  │ │  Host    │
                │Zone A       │ │Zone B│ │Zone B│ │Zone C│ │Zone C │ │          │
                └──────┬──────┘ └──┬───┘ └──┬───┘ └──┬───┘ └──┬────┘ └──────────┘
                       │           │        │        │        │
                       │      ┌────┴────────┴───┐    │        │
                       │      │  172.16.1.0/24   │    │        │
                       │      │  App Network     │    │        │
                       │      └────────┬─────────┘    │        │
                       │               │              │        │
                       │         ┌─────┴──────┐       │        │
                       │         │wan-router-1 │       │        │
                       │         │  Zone D     │       │        │
                       │         └─────┬──────┘       │        │
                       │               │              │        │
                       │          10.0.0.0/30         │        │
                       │          WAN Transit Link    │        │
                       │               │              │        │
                       │         ┌─────┴──────┐       │        │
                       │         │wan-router-2 │       │        │
                       │         │  Zone D     │       │        │
                       │         └─────┬──────┘       │        │
                       │               │              │        │
                       │      ┌────────┴──────────┐   │        │
                       │      │   172.16.2.0/24    │   │        │
                       │      │   Data Network     ├───┴────────┘
                       │      └───────────────────┘
                       │
                  ┌────┴──────────┐
                  │ 172.16.0.0/24 │
                  │ Control Net   │
                  └───────────────┘
```

### Адресная схема

| Узел | Management (eth0) | Production Network | Zone |
|---|---|---|---|
| ansible-master | 10.99.0.10 | 172.16.0.10/24 (Control) | A |
| app-01 | 10.99.0.11 | 172.16.1.11/24 (App) | B |
| app-02 | 10.99.0.12 | 172.16.1.12/24 (App) | B |
| wan-router-1 | 10.99.0.41 | e0/0: 172.16.1.1/24, e0/1: 10.0.0.1/30 | D |
| wan-router-2 | 10.99.0.42 | e0/0: 172.16.2.1/24, e0/1: 10.0.0.2/30 | D |
| db-sql | 10.99.0.20 | 172.16.2.20/24 (Data) | C |
| cache-redis | 10.99.0.21 | 172.16.2.21/24 (Data) | C |

### Ресурсы VM

| Узел | vCPU | RAM | Диск | Образ |
|---|---|---|---|---|
| ansible-master | 2 | 2 GB | 20 GB | Ubuntu 22.04 Server |
| app-01 | 2 | 4 GB | 20 GB | Ubuntu 22.04 Server |
| app-02 | 2 | 4 GB | 20 GB | Ubuntu 22.04 Server |
| db-sql | 2 | 4 GB | 40 GB | Ubuntu 22.04 Server |
| cache-redis | 1 | 2 GB | 10 GB | Ubuntu 22.04 Server |
| wan-router-1 | 1 | 512 MB | — | VyOS 1.4 / Cisco IOL |
| wan-router-2 | 1 | 512 MB | — | VyOS 1.4 / Cisco IOL |
| **Итого** | **11** | **~17 GB** | **~110 GB** | |

Остаётся ~15 GB RAM для EVE-NG host и гипервизора — более чем достаточно для Core i9 + 32 GB.

---

## Часть 10.2: Создание топологии в EVE-NG

### Шаг 1: Подготовка образов

```bash
# На EVE-NG host: загружаем образы
# Ubuntu Server 22.04 (qcow2 для QEMU)
mkdir -p /opt/unetlab/addons/qemu/linux-ubuntu-22.04/
cp ubuntu-22.04-server-cloudimg-amd64.img \
   /opt/unetlab/addons/qemu/linux-ubuntu-22.04/virtioa.qcow2

# VyOS 1.4 (rolling или LTS)
mkdir -p /opt/unetlab/addons/qemu/vyos-1.4/
cp vyos-1.4-rolling-amd64.qcow2 \
   /opt/unetlab/addons/qemu/vyos-1.4/virtioa.qcow2

# Фиксим permissions (обязательно после каждой загрузки образа)
/opt/unetlab/wrappers/unl_wrapper -a fixpermissions
```

### Шаг 2: Создание сетей в EVE-NG

В EVE-NG GUI создаём 4 сети (Networks):

| Имя сети | Тип | VLAN / Cloud | Назначение |
|---|---|---|---|
| `mgmt-oob` | Management (Cloud0) | Bridge к хосту | SSH, Ansible |
| `net-app` | Internal | — | 172.16.1.0/24 |
| `net-data` | Internal | — | 172.16.2.0/24 |
| `wan-transit` | Internal | — | 10.0.0.0/30 |
| `net-control` | Internal | — | 172.16.0.0/24 |

### Шаг 3: Подключение интерфейсов

Каждый узел подключается к сетям согласно таблице:

```
ansible-master:  eth0 → mgmt-oob,  eth1 → net-control
app-01:          eth0 → mgmt-oob,  eth1 → net-app
app-02:          eth0 → mgmt-oob,  eth1 → net-app
wan-router-1:    eth0 → mgmt-oob,  eth1 → net-app,      eth2 → wan-transit
wan-router-2:    eth0 → mgmt-oob,  eth1 → net-data,     eth2 → wan-transit
db-sql:          eth0 → mgmt-oob,  eth1 → net-data
cache-redis:     eth0 → mgmt-oob,  eth1 → net-data
```

---

## Часть 10.3: Базовая настройка Linux-узлов

### Netplan для всех Ubuntu-узлов

**ansible-master** (`/etc/netplan/00-installer-config.yaml`):

```yaml
network:
  version: 2
  ethernets:
    eth0:  # Management OOB
      addresses: [10.99.0.10/24]
      routes:
        - to: 10.99.0.0/24
          via: 10.99.0.1
      nameservers:
        addresses: [8.8.8.8, 1.1.1.1]
    eth1:  # Control Network
      addresses: [172.16.0.10/24]
```

**app-01** (`/etc/netplan/00-installer-config.yaml`):

```yaml
network:
  version: 2
  ethernets:
    eth0:
      addresses: [10.99.0.11/24]
      nameservers:
        addresses: [8.8.8.8]
    eth1:
      addresses: [172.16.1.11/24]
      routes:
        # Весь трафик к Data Zone идёт через wan-router-1
        - to: 172.16.2.0/24
          via: 172.16.1.1
```

**app-02** — аналогично, `10.99.0.12` и `172.16.1.12/24`.

**db-sql** (`/etc/netplan/00-installer-config.yaml`):

```yaml
network:
  version: 2
  ethernets:
    eth0:
      addresses: [10.99.0.20/24]
      nameservers:
        addresses: [8.8.8.8]
    eth1:
      addresses: [172.16.2.20/24]
      routes:
        # Обратный маршрут к App Zone через wan-router-2
        - to: 172.16.1.0/24
          via: 172.16.2.1
```

**cache-redis** — аналогично, `10.99.0.21` и `172.16.2.21/24`.

```bash
# Применяем на каждом узле
sudo netplan apply
```

---

# Этап 1: Маршрутизация — OSPF между WAN-роутерами

## Часть 10.4: Конфигурация VyOS-роутеров

Используем VyOS 1.4 — полноценный сетевой ОС на базе Linux, бесплатный, идеально подходит для EVE-NG. Конфиги ниже — для VyOS. Альтернативные конфиги для Cisco IOL даны отдельно.

### wan-router-1 (VyOS)

```bash
# Входим в режим конфигурации
configure

# === Интерфейсы ===

# Management (OOB)
set interfaces ethernet eth0 address '10.99.0.41/24'
set interfaces ethernet eth0 description 'Management OOB'

# К App Zone (Zone B) — 172.16.1.0/24
set interfaces ethernet eth1 address '172.16.1.1/24'
set interfaces ethernet eth1 description 'App Network - Zone B'

# WAN Transit Link к wan-router-2
set interfaces ethernet eth2 address '10.0.0.1/30'
set interfaces ethernet eth2 description 'WAN Transit to Router-2'

# === OSPF ===
# Area 0 — backbone. Все интерфейсы в одной area для простоты.
# В production с десятками роутеров — разбивать на area, здесь это overkill.

set protocols ospf area 0 network '172.16.1.0/24'
set protocols ospf area 0 network '10.0.0.0/30'

# Router ID — явно задаём, чтобы не зависеть от порядка поднятия интерфейсов
set protocols ospf parameters router-id '10.0.0.1'

# Passive interface: НЕ отправлять OSPF Hello в сторону App-серверов.
# Зачем: app-01/app-02 не являются OSPF-соседями, им не нужны Hello-пакеты.
# Маршрут к 172.16.1.0/24 всё равно будет анонсирован — passive не влияет на анонсы.
set protocols ospf passive-interface 'eth1'

# === Статические маршруты (fallback) ===
# Management default route
set protocols static route 0.0.0.0/0 next-hop '10.99.0.1'

# === Системное ===
set system host-name 'wan-router-1'
set service ssh port '22'

commit
save
```

### wan-router-2 (VyOS)

```bash
configure

# === Интерфейсы ===
set interfaces ethernet eth0 address '10.99.0.42/24'
set interfaces ethernet eth0 description 'Management OOB'

# К Data Zone (Zone C) — 172.16.2.0/24
set interfaces ethernet eth1 address '172.16.2.1/24'
set interfaces ethernet eth1 description 'Data Network - Zone C'

# WAN Transit Link к wan-router-1
set interfaces ethernet eth2 address '10.0.0.2/30'
set interfaces ethernet eth2 description 'WAN Transit to Router-1'

# === OSPF ===
set protocols ospf area 0 network '172.16.2.0/24'
set protocols ospf area 0 network '10.0.0.0/30'
set protocols ospf parameters router-id '10.0.0.2'

# Passive в сторону DB/Redis — та же логика
set protocols ospf passive-interface 'eth1'

# === Статические маршруты ===
set protocols static route 0.0.0.0/0 next-hop '10.99.0.1'

# === Системное ===
set system host-name 'wan-router-2'
set service ssh port '22'

commit
save
```

### Проверка OSPF

```bash
# На wan-router-1:
show ip ospf neighbor
# Ожидаем:
# Neighbor ID   Pri  State    Dead Time  Address     Interface
# 10.0.0.2      1    Full/DR  00:00:38   10.0.0.2    eth2

show ip route ospf
# Ожидаем маршрут к 172.16.2.0/24 через 10.0.0.2 (OSPF learned)
# O    172.16.2.0/24 [110/20] via 10.0.0.2, eth2, 00:05:12

# Проверяем связность
ping 172.16.2.1 source-address 172.16.1.1
# PING 172.16.2.1: 56 data bytes
# 64 bytes from 172.16.2.1: icmp_seq=1 ttl=64 time=0.8 ms
```

### Альтернатива: Cisco IOL конфигурация

Если вместо VyOS используется Cisco IOL (L3):

**wan-router-1 (Cisco IOS):**

```
enable
configure terminal

hostname wan-router-1

! Management
interface Ethernet0/0
 ip address 10.99.0.41 255.255.255.0
 description Management OOB
 no shutdown

! App Network
interface Ethernet0/1
 ip address 172.16.1.1 255.255.255.0
 description App Network - Zone B
 no shutdown

! WAN Transit
interface Ethernet0/2
 ip address 10.0.0.1 255.255.255.252
 description WAN Transit to Router-2
 no shutdown

! OSPF
router ospf 1
 router-id 10.0.0.1
 network 172.16.1.0 0.0.0.255 area 0
 network 10.0.0.0 0.0.0.3 area 0
 passive-interface Ethernet0/1

ip route 0.0.0.0 0.0.0.0 10.99.0.1

end
write memory
```

**wan-router-2 (Cisco IOS):**

```
enable
configure terminal

hostname wan-router-2

interface Ethernet0/0
 ip address 10.99.0.42 255.255.255.0
 description Management OOB
 no shutdown

interface Ethernet0/1
 ip address 172.16.2.1 255.255.255.0
 description Data Network - Zone C
 no shutdown

interface Ethernet0/2
 ip address 10.0.0.2 255.255.255.252
 description WAN Transit to Router-1
 no shutdown

router ospf 1
 router-id 10.0.0.2
 network 172.16.2.0 0.0.0.255 area 0
 network 10.0.0.0 0.0.0.3 area 0
 passive-interface Ethernet0/1

ip route 0.0.0.0 0.0.0.0 10.99.0.1

end
write memory
```

### Почему OSPF, а не BGP?

| Критерий | OSPF | BGP |
|---|---|---|
| Сложность настройки | Минимальная | Высокая (AS numbers, peering, policies) |
| Сходимость | Быстрая (~1-5 сек) | Медленная (~30-90 сек по умолчанию) |
| Масштаб | Внутри одной AS | Между AS / дата-центрами |
| Для нашего лаба | Идеально — 2 роутера, одна area | Overkill |

BGP имеет смысл, если вы моделируете inter-DC routing или хотите отработать BGP communities/policies. Для chaos engineering — OSPF достаточно.

### Проверка end-to-end маршрутизации

```bash
# С app-01: пинг до db-sql через WAN
ping -c 5 172.16.2.20
# PING 172.16.2.20: 56 data bytes
# 64 bytes from 172.16.2.20: icmp_seq=1 ttl=62 time=1.2 ms
#                                         ^^^ TTL=62 = 64 - 2 хопа (два роутера)

# Traceroute показывает путь
traceroute 172.16.2.20
# 1  172.16.1.1 (wan-router-1)  0.5 ms
# 2  10.0.0.2   (wan-router-2)  0.8 ms
# 3  172.16.2.20 (db-sql)       1.2 ms

# С db-sql: обратный путь
traceroute 172.16.1.11
# 1  172.16.2.1 (wan-router-2)  0.4 ms
# 2  10.0.0.1   (wan-router-1)  0.7 ms
# 3  172.16.1.11 (app-01)       1.1 ms
```

**Ключевой момент:** app-серверы видят Data Zone **только** через WAN-роутеры. Прямого L2-линка нет. Это означает, что всё, что мы делаем на WAN Transit Link (latency, loss, shaping), **гарантированно** затронет трафик app → db.

---

# Этап 2: DevOps Автоматизация — Ansible

## Часть 10.5: Inventory

### Структура проекта

```
lab-ansible/
├── ansible.cfg
├── inventory/
│   ├── hosts.yml          # Основной inventory
│   └── group_vars/
│       ├── all.yml        # Переменные для всех
│       ├── app_servers.yml
│       ├── db_servers.yml
│       └── routers.yml
├── playbooks/
│   ├── 01-base-setup.yml
│   ├── 02-install-dotnet.yml
│   ├── 03-install-redis.yml
│   ├── 04-install-postgres.yml
│   └── 05-chaos-engineering.yml
├── roles/
│   └── common/
│       ├── tasks/
│       │   └── main.yml
│       └── templates/
│           └── sshd_config.j2
└── files/
    └── deploy_key.pub
```

### ansible.cfg

```ini
[defaults]
inventory = inventory/hosts.yml
remote_user = root
# Первый запуск — по паролю, дальше по ключу
ask_pass = false
host_key_checking = false
# Ускоряем: 5 параллельных соединений (у нас 7 узлов)
forks = 7
# Используем Management Network для Ansible
# Production-сети не трогаем
timeout = 30

[privilege_escalation]
become = true
become_method = sudo
become_user = root
```

### inventory/hosts.yml

```yaml
---
all:
  children:
    # === Zone A: DevOps & Control ===
    control:
      hosts:
        ansible-master:
          ansible_host: 10.99.0.10
          # ansible-master — localhost, не нужен SSH к себе
          ansible_connection: local

    # === Zone B: Application Zone ===
    app_servers:
      hosts:
        app-01:
          ansible_host: 10.99.0.11
          app_ip: 172.16.1.11  # Production IP для конфигов приложений
        app-02:
          ansible_host: 10.99.0.12
          app_ip: 172.16.1.12

    # === Zone C: Data Zone ===
    db_servers:
      hosts:
        db-sql:
          ansible_host: 10.99.0.20
          db_ip: 172.16.2.20
          db_port: 5432
        cache-redis:
          ansible_host: 10.99.0.21
          redis_ip: 172.16.2.21
          redis_port: 6379

    # === Zone D: WAN Routers ===
    routers:
      hosts:
        wan-router-1:
          ansible_host: 10.99.0.41
          ansible_network_os: vyos
          ansible_connection: network_cli
          # VyOS использует свой модуль, не стандартный SSH
        wan-router-2:
          ansible_host: 10.99.0.42
          ansible_network_os: vyos
          ansible_connection: network_cli

    # === Группы по функции (для playbooks) ===
    linux_servers:
      children:
        app_servers:
        db_servers:

    all_infra:
      children:
        linux_servers:
        routers:
```

**Почему inventory в YAML, а не INI?**

INI-формат (`[group]\nhost ansible_host=...`) работает для простых случаев. Но как только нужны вложенные группы (`children`), per-host переменные, и group_vars — YAML на порядок читаемее. В enterprise всегда YAML.

### inventory/group_vars/all.yml

```yaml
---
# Переменные для ВСЕХ хостов

# Пользователь для деплоя
deploy_user: deploy
deploy_group: deploy

# SSH ключ (публичная часть)
deploy_ssh_key: "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIExampleKeyHere deploy@lab"

# DNS
dns_servers:
  - 8.8.8.8
  - 1.1.1.1

# NTP
ntp_server: pool.ntp.org

# Общие пакеты для всех Linux-серверов
common_packages:
  - curl
  - wget
  - htop
  - iotop
  - net-tools
  - tcpdump
  - iperf3
  - bpfcc-tools       # eBPF tools из книги
  - linux-tools-common # perf
  - jq
  - unzip
```

### inventory/group_vars/app_servers.yml

```yaml
---
# .NET Runtime версия
dotnet_version: "8.0"
dotnet_channel: "LTS"

# Sysctl tuning для .NET-сервисов (из Модуля 4 книги)
app_sysctl:
  # TCP буферы: min 4KB, default 128KB, max 16MB
  net.ipv4.tcp_rmem: "4096 131072 16777216"
  net.ipv4.tcp_wmem: "4096 131072 16777216"
  # Backlog для high-concurrency
  net.core.somaxconn: 65535
  net.ipv4.tcp_max_syn_backlog: 65535
  # TIME_WAIT reuse (Модуль 3)
  net.ipv4.tcp_tw_reuse: 1
  # BBR congestion control (Модуль 4)
  net.ipv4.tcp_congestion_control: bbr
  net.core.default_qdisc: fq
```

### inventory/group_vars/db_servers.yml

```yaml
---
postgres_version: "16"
postgres_listen_address: "0.0.0.0"
postgres_port: 5432
postgres_max_connections: 200

redis_version: "7"
redis_bind_address: "0.0.0.0"
redis_maxmemory: "1gb"
redis_maxmemory_policy: "allkeys-lru"
```

---

## Часть 10.6: Playbooks

### Playbook 1: Базовая настройка Linux (01-base-setup.yml)

```yaml
---
# 01-base-setup.yml
# Базовая настройка ВСЕХ Linux-серверов в лабе.
# Запуск: ansible-playbook playbooks/01-base-setup.yml

- name: Base setup for all Linux servers
  hosts: linux_servers
  become: true

  tasks:
    # ── Пользователь deploy ──────────────────────────────────
    - name: Create deploy group
      group:
        name: "{{ deploy_group }}"
        state: present

    - name: Create deploy user
      user:
        name: "{{ deploy_user }}"
        group: "{{ deploy_group }}"
        shell: /bin/bash
        create_home: true
        state: present

    # Даём sudo без пароля — это лаб, не production.
    # В production — ограниченный sudoers с конкретными командами.
    - name: Grant passwordless sudo to deploy
      copy:
        content: "{{ deploy_user }} ALL=(ALL) NOPASSWD: ALL\n"
        dest: "/etc/sudoers.d/{{ deploy_user }}"
        mode: "0440"
        validate: "visudo -cf %s"  # Проверяем синтаксис перед записью!

    # ── SSH ключи ────────────────────────────────────────────
    - name: Add SSH authorized key for deploy user
      authorized_key:
        user: "{{ deploy_user }}"
        key: "{{ deploy_ssh_key }}"
        state: present
        exclusive: false  # Не удалять другие ключи

    # ── Пакеты ───────────────────────────────────────────────
    - name: Update apt cache
      apt:
        update_cache: true
        cache_valid_time: 3600  # Не обновлять чаще раза в час

    - name: Install common packages
      apt:
        name: "{{ common_packages }}"
        state: present

    # ── Sysctl ───────────────────────────────────────────────
    # Применяем TCP-тюнинг из Модуля 4 книги
    - name: Apply sysctl settings
      sysctl:
        name: "{{ item.key }}"
        value: "{{ item.value }}"
        state: present
        sysctl_file: /etc/sysctl.d/99-lab-tuning.conf
        reload: true
      loop: "{{ app_sysctl | dict2items }}"
      when: app_sysctl is defined

    # ── NTP ──────────────────────────────────────────────────
    - name: Install and configure chrony (NTP)
      apt:
        name: chrony
        state: present

    - name: Set NTP server
      lineinfile:
        path: /etc/chrony/chrony.conf
        regexp: '^server '
        line: "server {{ ntp_server }} iburst"
      notify: restart chrony

    # ── Hostname ─────────────────────────────────────────────
    - name: Set hostname
      hostname:
        name: "{{ inventory_hostname }}"

  handlers:
    - name: restart chrony
      service:
        name: chrony
        state: restarted
```

### Playbook 2: Установка .NET Runtime (02-install-dotnet.yml)

```yaml
---
# 02-install-dotnet.yml
# Устанавливаем .NET Runtime на app-серверы.
# НЕ SDK — на production/staging нужен только Runtime.
# Запуск: ansible-playbook playbooks/02-install-dotnet.yml

- name: Install .NET Runtime on app servers
  hosts: app_servers
  become: true

  tasks:
    # Microsoft подписывает пакеты — нужен их GPG-ключ и репозиторий
    - name: Download Microsoft package signing key
      get_url:
        url: https://packages.microsoft.com/config/ubuntu/22.04/packages-microsoft-prod.deb
        dest: /tmp/packages-microsoft-prod.deb
        mode: "0644"

    - name: Install Microsoft repository
      apt:
        deb: /tmp/packages-microsoft-prod.deb
        state: present

    - name: Update apt cache after adding Microsoft repo
      apt:
        update_cache: true

    # aspnetcore-runtime включает в себя .NET Runtime + ASP.NET Core Runtime.
    # Если ваши сервисы — чистые console apps без web, можно ставить dotnet-runtime.
    - name: Install ASP.NET Core Runtime
      apt:
        name: "aspnetcore-runtime-{{ dotnet_version }}"
        state: present

    - name: Verify .NET installation
      command: dotnet --list-runtimes
      register: dotnet_check
      changed_when: false

    - name: Show installed runtimes
      debug:
        msg: "{{ dotnet_check.stdout_lines }}"

    # ── Директория для приложений ────────────────────────────
    - name: Create application directory
      file:
        path: /opt/apps
        state: directory
        owner: "{{ deploy_user }}"
        group: "{{ deploy_group }}"
        mode: "0755"

    # ── Systemd template для .NET-сервисов ───────────────────
    # Каждый сервис будет запускаться как systemd unit
    - name: Create systemd service template
      copy:
        content: |
          # /etc/systemd/system/dotnet-app@.service
          # Использование: systemctl start dotnet-app@myservice
          # Файл приложения: /opt/apps/myservice/myservice.dll
          [Unit]
          Description=.NET Service - %i
          After=network.target

          [Service]
          Type=exec
          User={{ deploy_user }}
          Group={{ deploy_group }}
          WorkingDirectory=/opt/apps/%i
          ExecStart=/usr/bin/dotnet /opt/apps/%i/%i.dll
          Restart=always
          RestartSec=5
          # Limits
          LimitNOFILE=65535
          # Environment
          Environment=DOTNET_ENVIRONMENT=Staging
          Environment=ASPNETCORE_URLS=http://0.0.0.0:5000
          # Logging
          StandardOutput=journal
          StandardError=journal
          SyslogIdentifier=dotnet-%i

          [Install]
          WantedBy=multi-user.target
        dest: /etc/systemd/system/dotnet-app@.service
        mode: "0644"
      notify: reload systemd

  handlers:
    - name: reload systemd
      systemd:
        daemon_reload: true
```

### Playbook 3: Установка Redis (03-install-redis.yml)

```yaml
---
# 03-install-redis.yml
# Запуск: ansible-playbook playbooks/03-install-redis.yml

- name: Install and configure Redis
  hosts: cache-redis
  become: true

  tasks:
    - name: Install Redis
      apt:
        name: redis-server
        state: present

    # ── Конфигурация Redis ───────────────────────────────────
    # bind: слушаем на production-интерфейсе (не только localhost)
    - name: Configure Redis - bind address
      lineinfile:
        path: /etc/redis/redis.conf
        regexp: '^bind '
        line: "bind 127.0.0.1 {{ redis_ip }}"
      notify: restart redis

    # maxmemory: ограничиваем, чтобы Redis не съел всю RAM
    - name: Configure Redis - maxmemory
      lineinfile:
        path: /etc/redis/redis.conf
        regexp: '^# maxmemory '
        line: "maxmemory {{ redis_maxmemory }}"
      notify: restart redis

    # eviction policy: когда память кончится — удаляем наименее используемые ключи
    - name: Configure Redis - eviction policy
      lineinfile:
        path: /etc/redis/redis.conf
        regexp: '^# maxmemory-policy'
        line: "maxmemory-policy {{ redis_maxmemory_policy }}"
      notify: restart redis

    # Отключаем protected-mode для лаба (в production — используйте ACL)
    - name: Configure Redis - disable protected mode (LAB ONLY!)
      lineinfile:
        path: /etc/redis/redis.conf
        regexp: '^protected-mode'
        line: "protected-mode no"
      notify: restart redis

    - name: Enable and start Redis
      service:
        name: redis-server
        state: started
        enabled: true

    # ── Проверка ─────────────────────────────────────────────
    - name: Test Redis connectivity
      command: redis-cli -h {{ redis_ip }} ping
      register: redis_ping
      changed_when: false

    - name: Verify Redis responds with PONG
      assert:
        that:
          - "'PONG' in redis_ping.stdout"
        fail_msg: "Redis не отвечает на PING!"
        success_msg: "Redis работает: {{ redis_ping.stdout }}"

  handlers:
    - name: restart redis
      service:
        name: redis-server
        state: restarted
```

### Playbook 4: Установка PostgreSQL (04-install-postgres.yml)

```yaml
---
# 04-install-postgres.yml
# Запуск: ansible-playbook playbooks/04-install-postgres.yml

- name: Install and configure PostgreSQL
  hosts: db-sql
  become: true

  tasks:
    - name: Install PostgreSQL
      apt:
        name:
          - "postgresql-{{ postgres_version }}"
          - postgresql-client
          - python3-psycopg2  # Для ansible модулей postgresql_*
        state: present

    # ── Слушаем на всех интерфейсах (не только localhost) ────
    - name: Configure PostgreSQL - listen_addresses
      lineinfile:
        path: "/etc/postgresql/{{ postgres_version }}/main/postgresql.conf"
        regexp: "^#?listen_addresses"
        line: "listen_addresses = '{{ postgres_listen_address }}'"
      notify: restart postgresql

    - name: Configure PostgreSQL - max_connections
      lineinfile:
        path: "/etc/postgresql/{{ postgres_version }}/main/postgresql.conf"
        regexp: "^#?max_connections"
        line: "max_connections = {{ postgres_max_connections }}"
      notify: restart postgresql

    # ── pg_hba.conf: разрешаем подключения из App Zone ──────
    # md5 = пароль, scram-sha-256 = лучше, но md5 проще для лаба
    - name: Allow connections from App Zone
      lineinfile:
        path: "/etc/postgresql/{{ postgres_version }}/main/pg_hba.conf"
        line: "host    all    all    172.16.1.0/24    scram-sha-256"
      notify: restart postgresql

    - name: Enable and start PostgreSQL
      service:
        name: postgresql
        state: started
        enabled: true

    # ── Создаём базу и пользователя для приложения ───────────
    - name: Create application database
      become_user: postgres
      postgresql_db:
        name: appdb
        state: present

    - name: Create application user
      become_user: postgres
      postgresql_user:
        name: appuser
        password: "LabPass123!"  # В production — Ansible Vault!
        db: appdb
        priv: "ALL"
        state: present

    # ── Проверка ─────────────────────────────────────────────
    - name: Test PostgreSQL connectivity
      become_user: postgres
      postgresql_query:
        db: appdb
        query: "SELECT version();"
      register: pg_version

    - name: Show PostgreSQL version
      debug:
        msg: "{{ pg_version.query_result[0].version }}"

  handlers:
    - name: restart postgresql
      service:
        name: postgresql
        state: restarted
```

### Запуск всех playbooks

```bash
# С ansible-master:

# 1. Базовая настройка всех Linux-серверов
ansible-playbook playbooks/01-base-setup.yml

# 2. .NET Runtime на app-серверы
ansible-playbook playbooks/02-install-dotnet.yml

# 3. Redis
ansible-playbook playbooks/03-install-redis.yml

# 4. PostgreSQL
ansible-playbook playbooks/04-install-postgres.yml

# Проверяем, что всё работает:
ansible linux_servers -m ping
# app-01 | SUCCESS
# app-02 | SUCCESS
# db-sql | SUCCESS
# cache-redis | SUCCESS
```

---

# Этап 3: Chaos Engineering — Внедрение сбоев

## Часть 10.7: Теория — Где внедрять сбои

Внедрять latency/loss можно в двух местах:

**1. На WAN-роутерах (VyOS traffic-policy / Cisco QoS):**
- Плюс: реалистично — сбои на сетевом оборудовании
- Минус: конфигурация зависит от платформы

**2. На Linux-узлах (tc netem):**
- Плюс: универсально, мощно, точный контроль
- Минус: сбой на endpoint, а не на сети

Мы используем **оба подхода** — VyOS для реалистичного моделирования WAN, и `tc netem` для гранулярного контроля.

### Ansible Playbook для Chaos Engineering (05-chaos-engineering.yml)

```yaml
---
# 05-chaos-engineering.yml
# Chaos Engineering playbook.
# Запуск с тегами:
#   ansible-playbook playbooks/05-chaos-engineering.yml --tags latency
#   ansible-playbook playbooks/05-chaos-engineering.yml --tags loss
#   ansible-playbook playbooks/05-chaos-engineering.yml --tags shaping
#   ansible-playbook playbooks/05-chaos-engineering.yml --tags reset

- name: "Chaos Engineering - WAN Impairments"
  hosts: wan-router-1
  become: true

  vars:
    # Интерфейс WAN Transit Link
    wan_interface: eth2

  tasks:
    # ════════════════════════════════════════════════════════
    # СЦЕНАРИЙ 1: High Latency
    # ════════════════════════════════════════════════════════
    - name: "[CHAOS] Apply 150ms latency + 50ms jitter on WAN link"
      # tc netem на VyOS работает так же, как на любом Linux —
      # VyOS под капотом это Debian.
      command: >
        tc qdisc replace dev {{ wan_interface }} root netem
        delay 150ms 50ms distribution normal
      tags: [latency, chaos]

    # ════════════════════════════════════════════════════════
    # СЦЕНАРИЙ 2: Packet Loss
    # ════════════════════════════════════════════════════════
    - name: "[CHAOS] Apply 5% packet loss on WAN link"
      command: >
        tc qdisc replace dev {{ wan_interface }} root netem
        loss 5% 25%
      # loss 5% 25% = 5% потерь с 25% корреляцией.
      # Корреляция важна! Без неё потери равномерные (unrealistic).
      # С корреляцией потери идут «пачками» — как в реальном WAN.
      tags: [loss, chaos]

    # ════════════════════════════════════════════════════════
    # СЦЕНАРИЙ 3: Microbursts + Bandwidth Shaping
    # ════════════════════════════════════════════════════════
    - name: "[CHAOS] Shape bandwidth to 10 Mbit/s with burst queue"
      # HTB (Hierarchical Token Bucket) + netem = точная симуляция
      # медленного канала с задержками.
      #
      # Архитектура:
      #   HTB (rate control) → netem (delay/loss)
      #
      # HTB ограничивает скорость, netem добавляет характеристики канала.
      shell: |
        # Удаляем предыдущие правила
        tc qdisc del dev {{ wan_interface }} root 2>/dev/null || true

        # Корневой HTB qdisc
        tc qdisc add dev {{ wan_interface }} root handle 1: htb default 10

        # Класс: 10 Mbit/s, burst 15k
        # burst = размер токен-бакета. При burst=15k допускаются
        # кратковременные всплески до ~15KB перед throttling.
        # Маленький burst = более агрессивное shaping, но возможны
        # микрозадержки. 15k — разумный компромисс.
        tc class add dev {{ wan_interface }} parent 1: classid 1:10 \
           htb rate 10mbit burst 15k

        # Leaf qdisc: добавляем netem с задержкой для реалистичности
        tc qdisc add dev {{ wan_interface }} parent 1:10 handle 10: \
           netem delay 10ms 5ms
      tags: [shaping, chaos]

    # ════════════════════════════════════════════════════════
    # КОМБО: Latency + Loss + Shaping (ад)
    # ════════════════════════════════════════════════════════
    - name: "[CHAOS] COMBO - Realistic bad WAN: shaped + latency + loss"
      shell: |
        tc qdisc del dev {{ wan_interface }} root 2>/dev/null || true

        # HTB: ограничиваем полосу до 10 Mbit/s
        tc qdisc add dev {{ wan_interface }} root handle 1: htb default 10
        tc class add dev {{ wan_interface }} parent 1: classid 1:10 \
           htb rate 10mbit burst 15k

        # netem: 150ms delay + 50ms jitter + 5% loss + 0.1% reorder
        tc qdisc add dev {{ wan_interface }} parent 1:10 handle 10: \
           netem delay 150ms 50ms distribution normal \
           loss 5% 25% \
           reorder 0.1% 50%
        # reorder 0.1% = 0.1% пакетов придёт не в порядке.
        # TCP SACK (Модуль 3) должен справляться, но при 5% loss + reorder
        # SACK scoreboard может переполниться.
      tags: [combo, chaos]

    # ════════════════════════════════════════════════════════
    # RESET: Убрать все сбои
    # ════════════════════════════════════════════════════════
    - name: "[RESET] Remove all traffic impairments"
      command: tc qdisc del dev {{ wan_interface }} root
      ignore_errors: true  # Ок если нечего удалять
      tags: [reset, clean]
```

---

## Часть 10.8: Детальный разбор Chaos-сценариев

### Сценарий 1: High Latency (150ms + 50ms jitter)

**Что тестируем:** Как Dapper / Entity Framework / Npgsql в .NET справляются с медленным SQL.

```bash
# Применяем на wan-router-1
ansible-playbook playbooks/05-chaos-engineering.yml --tags latency
```

**Ожидаемый эффект на .NET-приложение:**

```
До:  SQL query: 2ms (LAN)
После: SQL query: 300-400ms (150ms × 2 стороны + jitter + обработка)
```

**Что ломается:**
- Connection pool exhaustion — дефолтный `Max Pool Size=100` в Npgsql исчерпывается за секунды при latency 300ms+ и 100 RPS
- Command Timeout — дефолт 30 секунд, но при jitter единичные запросы могут улететь в 500ms+
- Health checks — ASP.NET health check к БД начинает failить, Kubernetes может убить pod

**Что проверять в .NET:**

```csharp
// Строка подключения с таймаутами (обязательно настраивать!)
var connectionString = new NpgsqlConnectionStringBuilder
{
    Host = "172.16.2.20",
    Database = "appdb",
    Username = "appuser",
    Password = "LabPass123!",

    // Таймаут на установку соединения — 5 сек вместо дефолтных 15
    Timeout = 5,

    // Таймаут на выполнение команды — 10 сек
    CommandTimeout = 10,

    // Размер пула — увеличиваем при высокой latency
    MaxPoolSize = 200,
    MinPoolSize = 10,

    // Keepalive — раннее обнаружение мёртвых соединений
    KeepAlive = 30
};
```

**Мониторинг из Linux:**

```bash
# На app-01: наблюдаем за задержками в реальном времени
ping -c 100 172.16.2.20 | tail -1
# rtt min/avg/max/mdev = 100.2/150.5/250.8/50.1 ms

# Отслеживаем TCP retransmissions (если jitter вызывает RTO)
watch -n 1 'ss -ti dst 172.16.2.20 | grep -E "rto:|rtt:"'
# rtt:152.5/48.3 rto:400   ← RTO адаптировался к latency

# eBPF: задержки TCP-соединений
tcpconnlat 100  # Показывать соединения дольше 100ms
# PID    COMM         IP SADDR         DADDR         DPORT LAT(ms)
# 1234   dotnet       4  172.16.1.11   172.16.2.20   5432  312.5
```

---

### Сценарий 2: Packet Loss (5% к Redis)

**Что тестируем:** Polly retry policies, Circuit Breaker, переполнение in-memory Channel<T> когда кэш недоступен.

```bash
# Применяем
ansible-playbook playbooks/05-chaos-engineering.yml --tags loss
```

**Или гранулярно — loss только к Redis (через iptables mark + tc):**

```bash
# На wan-router-1: loss только для трафика к Redis (172.16.2.21:6379)
# Шаг 1: Маркируем пакеты к Redis
iptables -t mangle -A FORWARD -d 172.16.2.21 -p tcp --dport 6379 \
    -j MARK --set-mark 10

# Шаг 2: HTB + netem с фильтром по mark
tc qdisc add dev eth2 root handle 1: htb default 20

# Класс для Redis-трафика — с потерями
tc class add dev eth2 parent 1: classid 1:10 htb rate 1gbit
tc qdisc add dev eth2 parent 1:10 handle 10: netem loss 5% 25%

# Класс для всего остального — без потерь
tc class add dev eth2 parent 1: classid 1:20 htb rate 1gbit

# Фильтр: пакеты с mark 10 → в класс с потерями
tc filter add dev eth2 parent 1: protocol ip prio 1 \
    handle 10 fw flowid 1:10
```

**Что ломается:**
- Redis Timeout — StackExchange.Redis дефолтный `syncTimeout=5000ms`, при 5% loss TCP retransmission добавляет 200ms+ RTO
- Polly Circuit Breaker — при серии таймаутов CB открывается, приложение переключается на fallback
- Channel<T> overflow — если приложение использует in-memory channel как буфер между producer и Redis writer, при недоступности Redis channel переполняется

**Что проверять в .NET:**

```csharp
// Polly: retry + circuit breaker для Redis
services.AddStackExchangeRedisCache(options =>
{
    options.Configuration = "172.16.2.21:6379,syncTimeout=2000,asyncTimeout=2000";
});

// Polly retry policy с exponential backoff
var retryPolicy = Policy
    .Handle<RedisConnectionException>()
    .Or<RedisTimeoutException>()
    .WaitAndRetryAsync(
        retryCount: 3,
        sleepDurationProvider: attempt =>
            TimeSpan.FromMilliseconds(100 * Math.Pow(2, attempt)),
        // 100ms → 200ms → 400ms
        onRetry: (exception, delay, attempt, ctx) =>
        {
            logger.LogWarning(
                "Redis retry {Attempt} after {Delay}ms: {Error}",
                attempt, delay.TotalMilliseconds, exception.Message);
        });

// Circuit Breaker: после 5 ошибок — открываем на 30 секунд
var circuitBreaker = Policy
    .Handle<RedisConnectionException>()
    .CircuitBreakerAsync(
        exceptionsAllowedBeforeBreaking: 5,
        durationOfBreak: TimeSpan.FromSeconds(30),
        onBreak: (ex, duration) =>
            logger.LogError("Circuit OPEN for {Duration}s", duration.TotalSeconds),
        onReset: () =>
            logger.LogInformation("Circuit CLOSED - Redis recovered"));
```

**Мониторинг:**

```bash
# TCP retransmissions к Redis
watch -n 1 'nstat -az | grep -E "TcpRetrans|TcpTimeouts"'

# Или через eBPF
tcpretrans
# TIME     PID    IP LADDR:LPORT   RADDR:RPORT   STATE
# 14:22:01 1234   4  172.16.1.11:  172.16.2.21:  ESTABLISHED
#                    48810          6379           (5 retransmits)
```

---

### Сценарий 3: Bandwidth Shaping (10 Mbit/s) — Backpressure

**Что тестируем:** Рост потребления памяти в .NET при медленном downstream. Backpressure через Kestrel, System.IO.Pipelines, Channel<T>.

```bash
ansible-playbook playbooks/05-chaos-engineering.yml --tags shaping
```

**Физика проблемы:**

```
Нормальный режим:
  app-01 → db-sql: 1 Gbit/s
  SQL result set 10 MB = передаётся за ~80ms

Shaping 10 Mbit/s:
  app-01 → db-sql: 10 Mbit/s
  SQL result set 10 MB = передаётся за ~8 СЕКУНД
  Всё это время данные буферизируются в памяти!
```

**Что ломается:**
- **Kestrel output buffer** — HTTP response буферизируется. 100 параллельных запросов × 10 MB = 1 GB в памяти
- **Npgsql buffer** — результаты SQL-запросов буферизируются в managed heap
- **GC pressure** — Gen2 collections, паузы, ещё больше задержек → каскадный отказ

**Что проверять:**

```bash
# Мониторим пропускную способность
iperf3 -c 172.16.2.20 -t 10
# [ ID] Interval      Transfer    Bandwidth
# [  5] 0.00-10.00 s  11.9 MBytes  9.98 Mbits/sec  ← shaped!

# TCP window size — адаптируется к bandwidth
ss -ti dst 172.16.2.20 | grep -oP 'cwnd:\d+'
# cwnd:14  ← маленькое окно из-за shaped bandwidth

# Мониторим send buffer на app-01
watch -n 0.5 'ss -tnm dst 172.16.2.20 | grep -A1 ESTAB'
# ESTAB  0  435600  172.16.1.11:48810  172.16.2.20:5432
#        skmem:(r0,rb131072,t435600,tb2626560,f1792,w2304,...)
#                              ^^^^^^ 435KB в send buffer — растёт!
```

**Что проверять в .NET:**

```csharp
// Правильный подход: streaming вместо буферизации
// ПЛОХО — загружает весь результат в память:
var allRows = await connection.QueryAsync<Order>("SELECT * FROM orders");

// ХОРОШО — streaming через IAsyncEnumerable:
await foreach (var row in connection.QueryUnbufferedAsync<Order>(
    "SELECT * FROM orders"))
{
    await channel.Writer.WriteAsync(row);
    // Channel с BoundedCapacity создаёт backpressure:
    // если consumer не успевает — WriteAsync блокируется
}

// Channel с bounded capacity — КРИТИЧНО для backpressure
var channel = Channel.CreateBounded<Order>(new BoundedChannelOptions(1000)
{
    FullMode = BoundedChannelFullMode.Wait,
    // Wait = блокировать producer когда буфер полный
    // DropOldest = терять старые данные (для real-time метрик)
    SingleReader = true,
    SingleWriter = true
});
```

---

## Часть 10.9: VyOS-native Traffic Policy (альтернатива tc)

Если хотите управлять chaos через VyOS CLI вместо прямых tc-команд:

```bash
configure

# === Traffic Policy: rate limiter ===
set traffic-policy shaper WAN-SHAPE bandwidth '10mbit'
set traffic-policy shaper WAN-SHAPE default bandwidth '10mbit'
set traffic-policy shaper WAN-SHAPE default burst '15k'
set traffic-policy shaper WAN-SHAPE default queue-type 'fq-codel'
# fq-codel здесь — чтобы при shaping работала fair queuing
# и не было голодания TCP-потоков (см. Модуль 5 книги)

# Применяем к WAN-интерфейсу
set interfaces ethernet eth2 traffic-policy out 'WAN-SHAPE'

commit
save

# === Network Emulator (netem через VyOS) ===
set traffic-policy network-emulator WAN-CHAOS bandwidth '100mbit'
set traffic-policy network-emulator WAN-CHAOS delay '150'
set traffic-policy network-emulator WAN-CHAOS corruption '0.1'
set traffic-policy network-emulator WAN-CHAOS loss '5'

set interfaces ethernet eth2 traffic-policy out 'WAN-CHAOS'

commit

# === Убрать политику ===
delete interfaces ethernet eth2 traffic-policy
commit
```

---

## Часть 10.10: Мониторинг и Observability

### Prometheus + Grafana на ansible-master (опционально)

```bash
# Docker на ansible-master для мониторинга
# (Ansible playbook для установки Docker — стандартный, опускаем)

# docker-compose.yml
cat << 'EOF' > /opt/monitoring/docker-compose.yml
version: "3.8"

services:
  prometheus:
    image: prom/prometheus:latest
    ports:
      - "9090:9090"
    volumes:
      - ./prometheus.yml:/etc/prometheus/prometheus.yml
    restart: unless-stopped

  grafana:
    image: grafana/grafana:latest
    ports:
      - "3000:3000"
    environment:
      - GF_SECURITY_ADMIN_PASSWORD=admin
    restart: unless-stopped

  node-exporter:
    image: prom/node-exporter:latest
    ports:
      - "9100:9100"
    restart: unless-stopped
EOF

# prometheus.yml — скрейпим метрики со всех узлов
cat << 'EOF' > /opt/monitoring/prometheus.yml
global:
  scrape_interval: 5s

scrape_configs:
  - job_name: 'node-exporter'
    static_configs:
      - targets:
          - '10.99.0.10:9100'  # ansible-master
          - '10.99.0.11:9100'  # app-01
          - '10.99.0.12:9100'  # app-02
          - '10.99.0.20:9100'  # db-sql
          - '10.99.0.21:9100'  # cache-redis

  - job_name: 'dotnet-apps'
    static_configs:
      - targets:
          - '10.99.0.11:5000'  # app-01 /metrics
          - '10.99.0.12:5000'  # app-02 /metrics
EOF

cd /opt/monitoring && docker compose up -d
```

### Ключевые метрики для chaos-тестирования

| Метрика | Источник | Что показывает при chaos |
|---|---|---|
| `node_network_transmit_bytes_total` | node-exporter | Пропускная способность — видим shaping |
| `node_network_receive_drop_total` | node-exporter | Дропы пакетов |
| `dotnet_gc_heap_size_bytes` | .NET /metrics | Рост памяти при backpressure |
| `dotnet_gc_collection_count_total` | .NET /metrics | GC pressure при буферизации |
| `http_server_request_duration_seconds` | .NET /metrics | Рост latency запросов |
| `http_server_active_requests` | .NET /metrics | Накопление запросов (pool exhaustion) |
| `db_connection_pool_active` | Custom metric | Исчерпание пула БД |
| `polly_circuit_breaker_state` | Custom metric | 0=closed, 1=open, 0.5=half-open |

---

## Часть 10.11: Скрипт полного развёртывания

Финальный скрипт для запуска всего стенда с нуля:

```bash
#!/bin/bash
# deploy-lab.sh — Развёртывание полного лабораторного стенда
# Запуск с ansible-master: bash deploy-lab.sh

set -euo pipefail

echo "=== Шаг 1: Проверяем connectivity ко всем узлам ==="
ansible all -m ping --limit linux_servers
echo "OK: Все Linux-серверы доступны"

echo ""
echo "=== Шаг 2: Базовая настройка (пользователи, пакеты, sysctl) ==="
ansible-playbook playbooks/01-base-setup.yml

echo ""
echo "=== Шаг 3: Установка .NET Runtime ==="
ansible-playbook playbooks/02-install-dotnet.yml

echo ""
echo "=== Шаг 4: Установка Redis ==="
ansible-playbook playbooks/03-install-redis.yml

echo ""
echo "=== Шаг 5: Установка PostgreSQL ==="
ansible-playbook playbooks/04-install-postgres.yml

echo ""
echo "=== Шаг 6: Проверяем end-to-end связность ==="
echo "--- App → DB (через WAN) ---"
ansible app_servers -m command -a "ping -c 3 172.16.2.20"

echo "--- App → Redis (через WAN) ---"
ansible app_servers -m command -a "ping -c 3 172.16.2.21"

echo "--- App → Redis CLI ---"
ansible app-01 -m command -a "redis-cli -h 172.16.2.21 ping"

echo ""
echo "============================================"
echo " Лаб готов!"
echo " "
echo " Chaos Engineering:"
echo "   ansible-playbook playbooks/05-chaos-engineering.yml --tags latency"
echo "   ansible-playbook playbooks/05-chaos-engineering.yml --tags loss"
echo "   ansible-playbook playbooks/05-chaos-engineering.yml --tags shaping"
echo "   ansible-playbook playbooks/05-chaos-engineering.yml --tags combo"
echo "   ansible-playbook playbooks/05-chaos-engineering.yml --tags reset"
echo "============================================"
```

---

## Практические задания

### Задание 1: Развёртывание с нуля

Разверните полный стенд по инструкции. Убедитесь, что:
1. `app-01` пингует `db-sql` через WAN (TTL уменьшается на 2)
2. `traceroute` показывает путь через оба роутера
3. OSPF-соседство установлено (`show ip ospf neighbor` — State: Full)
4. Redis отвечает PONG с app-серверов
5. PostgreSQL принимает подключения из App Zone

### Задание 2: Chaos Latency — найти порог отказа

1. Включите latency 50ms → 100ms → 200ms → 500ms → 1000ms (пошагово)
2. На каждом уровне запустите нагрузочный тест (.NET-приложения или `pgbench`)
3. Найдите порог, при котором connection pool исчерпывается
4. Рассчитайте: при latency X ms и Max Pool Size Y, какой максимальный RPS выдержит приложение?
   - Формула: `MaxRPS = PoolSize / (AvgQueryTime + RTT)`

### Задание 3: Chaos Loss — тестирование Polly

1. Включите 1% → 5% → 15% → 30% потерь к Redis
2. Наблюдайте за Circuit Breaker: при каком % loss он открывается?
3. Проверьте, что при открытом CB приложение отдаёт данные из fallback (прямой запрос в БД)
4. Отслеживайте `TcpRetransSegs` через `nstat` — как коррелирует с % loss?

### Задание 4: Backpressure — найти OOM

1. Включите shaping 10 Mbit/s
2. Запустите .NET-сервис, который стримит большие result sets из PostgreSQL
3. Отправьте 50 параллельных запросов
4. Наблюдайте за `dotnet_gc_heap_size_bytes` и RSS процесса
5. Найдите момент, когда GC не справляется и OOMKiller убивает процесс
6. Реализуйте backpressure через `Channel.CreateBounded<T>` и повторите тест

### Задание 5: COMBO — Финальный стресс-тест

1. Включите combo-режим (shaping + latency + loss)
2. Запустите полный стек: API → Redis → PostgreSQL
3. Подайте реалистичную нагрузку через `wrk` или `k6`
4. Снимите полный профиль:
   - `ss -ti` — состояние TCP-соединений (cwnd, rtt, retrans)
   - `nstat` — TCP-статистика
   - `tcpretrans` (eBPF) — ретрансмиссии
   - Grafana — метрики приложения и node-exporter
5. Напишите отчёт: что сломалось первым, почему, и как защититься

---

**Предыдущий модуль:** [Модуль 9: Windows Client Networking](Module-09-Windows-Client-Networking.md) — Wi-Fi, DNS Client, VPN и «Нет интернета».

**Дополнительный ресурс:** [Lab-Setup.md](Lab-Setup.md) — базовый лаб на Network Namespaces и VMware (без EVE-NG).
