using System.Text;
using TcpOutOfOrderReassembly;

RunReorderScenario();
Console.WriteLine();
RunOverlapScenario();
Console.WriteLine();
RunWrapAroundScenario();

return;

// ---------------------------------------------------------------------
// Сценарий 1: сегменты приходят в произвольном порядке, один дубликат,
// один поздний ретрансмит уже доставленного диапазона.
//
// Ожидаемый поток: "Hello TCP out of order!"
// ---------------------------------------------------------------------
static void RunReorderScenario()
{
    Console.WriteLine("=== Scenario 1: out-of-order + duplicates ===");

    const uint initialSequence = 1000;
    using var output = new MemoryStream();
    using var reassembler = new TcpStreamReassembler(
        initialReceiveSequence: initialSequence,
        receiveWindow: 64 * 1024);

    TcpDataReadyHandler consume = data =>
    {
        output.Write(data);
        Console.WriteLine($"  -> application: \"{Encoding.ASCII.GetString(data)}\"");
    };

    // offset 0   "Hello "
    // offset 6   "TCP "
    // offset 10  "out "
    // offset 14  "of "
    // offset 17  "order!"
    Receive(reassembler, consume, initialSequence, offset: 17, text: "order!");  // пришёл первым
    Receive(reassembler, consume, initialSequence, offset: 10, text: "out ");    // gap ещё есть
    Receive(reassembler, consume, initialSequence, offset: 0, text: "Hello ");   // доставится только "Hello "
    Receive(reassembler, consume, initialSequence, offset: 14, text: "of ");     // gap всё ещё 6..9
    Receive(reassembler, consume, initialSequence, offset: 6, text: "TCP ");     // закрывает gap -> весь остаток
    Receive(reassembler, consume, initialSequence, offset: 6, text: "TCP ");     // старый duplicate
    Receive(reassembler, consume, initialSequence, offset: 17, text: "order!"); // старый duplicate

    Console.WriteLine();
    Console.WriteLine($"Final stream: \"{Encoding.ASCII.GetString(output.ToArray())}\"");
    Console.WriteLine($"RCV.NXT: {reassembler.RcvNxt}");
    Console.WriteLine($"Buffered bytes: {reassembler.BufferedBytes}");
    Console.WriteLine($"Buffered ranges: {reassembler.BufferedRangeCount}");
}

static void Receive(
    TcpStreamReassembler reassembler,
    TcpDataReadyHandler consume,
    uint initialSequence,
    int offset,
    string text)
{
    uint sequenceNumber = TcpSequence.Add(initialSequence, offset);
    byte[] payload = Encoding.ASCII.GetBytes(text);

    Console.WriteLine($"RX SEQ={sequenceNumber}, offset={offset}, len={payload.Length}, data=\"{text}\"");

    SegmentInsertResult result = reassembler.Push(sequenceNumber, payload, consume);

    Console.WriteLine(
        $"  result={result}, RCV.NXT={reassembler.RcvNxt}, buffered={reassembler.BufferedBytes}");
}

// ---------------------------------------------------------------------
// Сценарий 2: частичное перекрытие двух буферизованных диапазонов.
//
// [5005,5010) "FGHIJ"
// [5008,5013) "IJKLM"  <- только "KLM" новые, "IJ" уже сохранены
// [5000,5005) "ABCDE"  <- закрывает начальный gap, всё сливается
//
// Ожидаемый поток: "ABCDEFGHIJKLM"
// ---------------------------------------------------------------------
static void RunOverlapScenario()
{
    Console.WriteLine("=== Scenario 2: partial overlap ===");

    using var output = new MemoryStream();
    using var reassembler = new TcpStreamReassembler(
        initialReceiveSequence: 5000,
        receiveWindow: 65_535);

    void Consume(ReadOnlySpan<byte> data)
    {
        output.Write(data);
        Console.WriteLine($"  -> application: \"{Encoding.ASCII.GetString(data)}\"");
    }

    void Push(uint sequence, string text)
    {
        byte[] payload = Encoding.ASCII.GetBytes(text);
        Console.WriteLine($"RX SEQ={sequence}, len={payload.Length}, data=\"{text}\"");
        SegmentInsertResult result = reassembler.Push(sequence, payload, Consume);
        Console.WriteLine($"  result={result}, buffered={reassembler.BufferedBytes}, ranges={reassembler.BufferedRangeCount}");
    }

    Push(5005, "FGHIJ");
    Push(5008, "IJKLM");
    Push(5000, "ABCDE");

    Console.WriteLine();
    Console.WriteLine($"Final stream: \"{Encoding.ASCII.GetString(output.ToArray())}\"");
}

// ---------------------------------------------------------------------
// Сценарий 3: sequence number проходит через 2^32 - 1 -> 0 без каких-либо
// специальных ветвлений в основном алгоритме — вся развёртка происходит
// один раз, при вычислении relativeStart через TcpSequence.Distance.
// ---------------------------------------------------------------------
static void RunWrapAroundScenario()
{
    Console.WriteLine("=== Scenario 3: sequence number wraparound ===");

    uint initialSequence = uint.MaxValue - 4;
    using var output = new MemoryStream();
    using var reassembler = new TcpStreamReassembler(initialSequence, receiveWindow: 1024);

    void Consume(ReadOnlySpan<byte> data) => output.Write(data);

    void Push(int offset, string text)
    {
        uint sequence = TcpSequence.Add(initialSequence, offset);
        Console.WriteLine($"offset={offset}, wire SEQ={sequence}");
        reassembler.Push(sequence, Encoding.ASCII.GetBytes(text), Consume);
    }

    Console.WriteLine($"initialSequence = {initialSequence}");
    Push(offset: 5, text: "WORLD");
    Push(offset: 0, text: "HELLO");

    Console.WriteLine($"Final stream: \"{Encoding.ASCII.GetString(output.ToArray())}\"");
    Console.WriteLine($"RCV.NXT={reassembler.RcvNxt}");
}
