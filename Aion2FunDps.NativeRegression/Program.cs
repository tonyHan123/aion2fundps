// Phase 7 regression tool — feeds the same captured GamePacket stream
// through BOTH the managed PacketDispatcher and the native engine adapter,
// then diffs the emitted IGameEvent sequences.
//
// Usage:
//   Aion2FunDps.NativeRegression [seconds]
//     seconds — how long to capture before stopping. Default 30.
//
// Output:
//   - Per-type event counts (managed vs native)
//   - First N divergences with managed/native event side-by-side
//   - Pass/fail summary
//
// Requires:
//   - Npcap installed
//   - Aion2FunDps.Engine.dll present next to the exe (vcxproj output;
//     csproj copies it via the engine-DLL <None> include).

using Aion2FunDps.Capture;
using Aion2FunDps.Core;
using Aion2FunDps.Core.Models;
using Aion2FunDps.Protocol;
using Aion2FunDps.Protocol.NativeEngine;

int durationSeconds = args.Length > 0 && int.TryParse(args[0], out var s) ? s : 30;

Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.WriteLine($"=== Aion2FunDps Native-Engine Regression ===");
Console.WriteLine($"Capture duration: {durationSeconds}s");
Console.WriteLine();

NpcapAdapter capture;
try { capture = new NpcapAdapter(new CaptureOptions()); }
catch (Exception ex) { Console.WriteLine($"Capture init failed: {ex.Message}"); return 1; }

Console.WriteLine($"Selected NIC: {capture.SelectedInterface}");

var reorderer = new SequenceReorderer();
var assembler = new FrameAssembler();

// Each dispatcher gets its own emit collector. The lists are indexed in
// emit order — for a deterministic input stream both should be identical.
var managedEvents = new List<IGameEvent>();
var nativeEvents  = new List<IGameEvent>();

var managed = new PacketDispatcher();
using var native = new NativeEngineDispatcher(ev => nativeEvents.Add(ev));

void OnGamePacket(GamePacket gp)
{
    // Feed to managed first, then native. Both only read; neither
    // mutates the underlying SharpPcap buffer. Dispose only after
    // both have finished parsing.
    managed.Dispatch(gp, ev => managedEvents.Add(ev));
    native.Dispatch(gp);
    gp.Dispose();
}

using (capture)
{
    capture.Start();
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(durationSeconds));
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

    Action<OrderedChunk> onOrderedChunk = chunk => assembler.Feed(chunk, OnGamePacket);

    Console.WriteLine($"Capturing... press Ctrl+C to stop early");
    try
    {
        await foreach (var rawPacket in capture.Reader.ReadAllAsync(cts.Token))
            reorderer.Feed(rawPacket, onOrderedChunk);
    }
    catch (OperationCanceledException) { }

    capture.Stop();
}

Console.WriteLine();
Console.WriteLine($"=== Capture done ===");
Console.WriteLine($"Managed events: {managedEvents.Count}");
Console.WriteLine($"Native events:  {nativeEvents.Count}");
Console.WriteLine();

// Per-type counts side-by-side
var managedByType = managedEvents.GroupBy(e => e.GetType().Name)
                                 .ToDictionary(g => g.Key, g => g.Count());
var nativeByType  = nativeEvents .GroupBy(e => e.GetType().Name)
                                 .ToDictionary(g => g.Key, g => g.Count());
var allTypes = managedByType.Keys.Union(nativeByType.Keys).OrderBy(t => t).ToList();

Console.WriteLine($"  {"Type",-26} {"managed",10} {"native",10} {"delta",8}");
Console.WriteLine($"  {new string('-', 26)} {new string('-', 10)} {new string('-', 10)} {new string('-', 8)}");
foreach (var t in allTypes)
{
    int m = managedByType.GetValueOrDefault(t);
    int n = nativeByType .GetValueOrDefault(t);
    int delta = n - m;
    string mark = delta == 0 ? " " : (delta > 0 ? "+" : "-");
    Console.WriteLine($"  {t,-26} {m,10} {n,10} {mark}{Math.Abs(delta),7}");
}
Console.WriteLine();

// First N divergences. Most records' default Equals walks all init
// properties giving us field-level checking for free, but
// PartyRosterUpdate.Members is an IReadOnlyList<NicknameInfo> whose
// Equals is reference-only — two equivalent rosters from different
// dispatchers always compare unequal. Use a deep-equality wrapper that
// does element-wise comparison for that one type.
static bool EventsEqual(IGameEvent a, IGameEvent b)
{
    if (a is PartyRosterUpdate ra && b is PartyRosterUpdate rb)
    {
        if (ra.GroupId != rb.GroupId) return false;
        if (ra.TimestampTicks != rb.TimestampTicks) return false;
        if (ra.SourceIpv4 != rb.SourceIpv4) return false;
        if (ra.Confidence != rb.Confidence) return false;
        if (ra.ContainsSelf != rb.ContainsSelf) return false;
        if (ra.Members.Count != rb.Members.Count) return false;
        for (int i = 0; i < ra.Members.Count; i++)
        {
            if (!ra.Members[i].Equals(rb.Members[i])) return false;
        }
        return true;
    }
    return a.Equals(b);
}

const int MaxDivergences = 20;
int divergences = 0;
int aligned = Math.Min(managedEvents.Count, nativeEvents.Count);
Console.WriteLine($"=== Divergences (first {MaxDivergences}) ===");
for (int i = 0; i < aligned && divergences < MaxDivergences; i++)
{
    if (!EventsEqual(managedEvents[i], nativeEvents[i]))
    {
        divergences++;
        Console.WriteLine($"[{i}]");
        Console.WriteLine($"  managed: {Describe(managedEvents[i])}");
        Console.WriteLine($"  native:  {Describe(nativeEvents[i])}");
    }
}

// Render PartyRosterUpdate with member nicknames inline so divergences
// surface the actual content difference, not just "...List..."
static string Describe(IGameEvent ev)
{
    if (ev is PartyRosterUpdate r)
    {
        var nicks = string.Join(",", r.Members.Select(m => $"{m.UserId}:{m.Nickname}"));
        return $"PartyRosterUpdate {{ GroupId={r.GroupId}, Confidence={r.Confidence}, ContainsSelf={r.ContainsSelf}, Members=[{nicks}], TimestampTicks={r.TimestampTicks}, SourceIpv4={r.SourceIpv4} }}";
    }
    return ev.ToString() ?? "<null>";
}
if (managedEvents.Count != nativeEvents.Count)
{
    var longer = managedEvents.Count > nativeEvents.Count ? "managed" : "native";
    int diff = Math.Abs(managedEvents.Count - nativeEvents.Count);
    Console.WriteLine($"...plus {diff} extra {longer}-only events past the aligned tail.");
}

Console.WriteLine();
bool passed = divergences == 0
           && managedEvents.Count == nativeEvents.Count;
Console.WriteLine(passed
    ? "RESULT: PASS — both engines produced equal event streams."
    : "RESULT: FAIL — divergences found (see above).");

return passed ? 0 : 2;
