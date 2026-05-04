namespace Aion2FunDps.Protocol;

/// <summary>
/// Counter surface that <see cref="DiagnosticLogger"/> and the UI status bar
/// read from a packet dispatcher. Both managed <see cref="PacketDispatcher"/>
/// and the native-engine adapter implement this so the consumers don't care
/// which parser is wired up.
///
/// The native engine doesn't currently track unknown-opcode or nickname
/// parse-rate counters — its implementations return 0 / empty. Those
/// counters are debug-only diagnostic surfaces (UI text + boss-kill log
/// entry); zeros in native mode are deliberately accepted for now since
/// the equivalent observability moves into log-line emission once the
/// native parser stabilises.
/// </summary>
public interface IDispatcherTelemetry
{
    long MalformedCount { get; }
    long UnknownCount { get; }
    IReadOnlyDictionary<ushort, long> UnknownByOpcode { get; }
    long SelfNickSeen { get; }
    long SelfNickParsed { get; }
    long OtherNickSeen { get; }
    long OtherNickParsed { get; }
}
