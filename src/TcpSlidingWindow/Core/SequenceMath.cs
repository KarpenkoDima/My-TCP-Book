namespace TcpSlidingWindow.Core;

/// <summary>
/// TCP sequence numbers live in a 32-bit modular space (RFC 9293 §3.4):
/// they wrap from 0xFFFFFFFF back to 0, and "less than" only means
/// anything relative to a reference point. The classic RFC 793 trick
/// (also carried into RFC 9293) is to compare via *signed* subtraction:
/// if (int)(a - b) is negative, a is "before" b in sequence-space terms,
/// as long as the two points are within 2^31 of each other. This is
/// branchless, allocation-free, and correct across wraparound — unlike a
/// naive `a &lt; b` which breaks the moment the space wraps.
/// </summary>
public static class SequenceMath
{
    /// <summary>True if <paramref name="a"/> precedes <paramref name="b"/> in sequence-space.</summary>
    public static bool LessThan(uint a, uint b) => unchecked((int)(a - b)) < 0;

    public static bool LessThanOrEqual(uint a, uint b) => a == b || LessThan(a, b);

    public static bool GreaterThan(uint a, uint b) => LessThan(b, a);

    public static bool GreaterThanOrEqual(uint a, uint b) => a == b || GreaterThan(a, b);

    /// <summary>True if seq lies in [windowStart, windowEnd) in sequence-space.</summary>
    public static bool InWindow(uint seq, uint windowStart, uint windowEnd) =>
        LessThanOrEqual(windowStart, seq) && LessThan(seq, windowEnd);

    /// <summary>Unsigned distance from a to b, i.e. how many bytes ahead b is of a.</summary>
    public static uint Distance(uint from, uint to) => unchecked(to - from);
}
