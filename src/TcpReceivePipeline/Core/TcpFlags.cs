namespace TcpReceivePipeline.Core;

[Flags]
public enum TcpFlags : byte
{
    None = 0,
    FIN = 1 << 0,
    SYN = 1 << 1,
    RST = 1 << 2,
    PSH = 1 << 3,
    ACK = 1 << 4,
    URG = 1 << 5,
    ECE = 1 << 6,
    CWR = 1 << 7
}
