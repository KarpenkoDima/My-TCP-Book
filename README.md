## Для кого эта книга

- **Senior System/Network Engineers** — кто хочет понимать, почему пинг растёт при скачивании, где теряются пакеты и как работает BBR
- **High-Load Backend Developers** — кто пишет на .NET/C# и хочет выжать максимум из Kestrel, io_uring и System.IO.Pipelines
- **Windows Enterprise Engineers** — кто устал гадать, почему svchost грузит CPU, почему Windows говорит «Нет интернета» и как EDR-агенты убивают latency
- **DevOps/SRE** — кто строит chaos engineering стенды и тюнит sysctl на production-серверах

## Что вы узнаете

- Почему пинг растёт при скачивании торрента (Bufferbloat, AQM)
- Где именно теряются пакеты в ядре Linux (NAPI budget, sk_buff, ring buffer overflow)
- Как BBR обходит CUBIC на трансатлантических линках и почему ему нужен fq
- Почему `TIME_WAIT` — не баг, а фича, и когда `tw_reuse` безопасен
- Как CrowdStrike Falcon может удвоить p99 latency на SQL Server
- Почему Windows говорит «Нет интернета» при работающем VPN (NCSI + NLA)
- Как написать zero-copy proxy на io_uring и почему sendfile() бесполезен для HTTPS (без kTLS)
