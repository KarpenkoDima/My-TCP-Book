namespace TcpOutOfOrderReassembly;

/// <summary>
/// Операции над 32-битным TCP sequence space.
///
/// Сравнение корректно, когда расстояние между значениями
/// меньше 2^31, что обеспечивается ограниченным receive window.
/// </summary>
public static class TcpSequence
{
    public static bool LessThan(uint left, uint right)
        => unchecked((int)(left - right)) < 0;

    public static bool LessThanOrEqual(uint left, uint right)
        => left == right || LessThan(left, right);

    public static bool GreaterThan(uint left, uint right)
        => LessThan(right, left);

    public static bool GreaterThanOrEqual(uint left, uint right)
        => left == right || GreaterThan(left, right);

    /// <summary>
    /// Знаковое расстояние from -> to в модульном пространстве.
    ///
    /// Положительный результат означает, что to находится впереди from.
    /// Отрицательный — что to находится позади from.
    /// </summary>
    public static int Distance(uint from, uint to)
        => unchecked((int)(to - from));

    public static uint Add(uint sequenceNumber, int count)
        => unchecked(sequenceNumber + (uint)count);

    public static uint Add(uint sequenceNumber, uint count)
        => unchecked(sequenceNumber + count);
}
