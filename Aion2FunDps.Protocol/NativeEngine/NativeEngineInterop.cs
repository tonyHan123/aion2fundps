using System.Runtime.InteropServices;

namespace Aion2FunDps.Protocol.NativeEngine;

/// <summary>
/// P/Invoke surface for Aion2FunDps.Engine.dll. Mirrors the C ABI declared
/// in Aion2FunDps.Engine/include/engine_api.h — keep both files in sync
/// when adding new exports.
///
/// Why a dedicated class:
///   - Keeps all unsafe-by-convention DllImport / function-pointer
///     marshalling in one auditable place.
///   - Lets the higher-level <see cref="NativeEngineDispatcher"/>
///     consume a clean managed API without leaking raw IntPtr handles.
///
/// All POD structs use [StructLayout(Sequential)] with explicit field
/// ordering matching the C struct in engine_api.h. Fields are sized
/// concretely (Int32, Int64, byte) — no `int`/`long` aliases — so the
/// layout doesn't drift on hypothetical future ARM64 builds.
/// </summary>
internal static class NativeEngineInterop
{
    public const string DllName = "Aion2FunDps.Engine";

    // =========================================================================
    // Lifecycle / log
    // =========================================================================

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void Aion2FunOnLog(IntPtr ctx, int level, IntPtr message);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern uint Aion2Fun_GetVersion();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int Aion2Fun_Init();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void Aion2Fun_Shutdown();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void Aion2Fun_SetLogCallback(Aion2FunOnLog cb, IntPtr ctx);

    // =========================================================================
    // Event POD layouts — sequential, must match engine_api.h byte-for-byte.
    // =========================================================================

    [StructLayout(LayoutKind.Sequential)]
    public struct DamageEvent
    {
        public int  ActorId;
        public int  TargetId;
        public int  Damage;
        public uint SkillCode;
        public int  Type;
        public byte Specials;
        public int  Loop;
        public byte IsDot;
        public long TimestampTicks;
        public uint SourceIpv4;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MobHpUpdate
    {
        public int  MobId;
        public long CurrentHp;
        public long TimestampTicks;
        public uint SourceIpv4;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct EncounterAnnouncement
    {
        public int  MobCode;
        public long TimestampTicks;
        public uint SourceIpv4;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct CombatBoundary
    {
        public int  MobId;
        public long TimestampTicks;
        public uint SourceIpv4;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NicknameInfo
    {
        public int    UserId;
        public IntPtr Nickname;        // raw UTF-8, NOT null-terminated
        public int    NicknameLen;
        public byte   IsSelf;
        public int    Server;
        public int    Job;
        public int    CombatPower;
        public long   TimestampTicks;
        public uint   SourceIpv4;
        public byte   IsPartyMember;
        public byte   IsRosterStart;
        public int    RoomId;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SummonSpawnInfo
    {
        public int    SummonId;
        public int    OwnerId;
        public int    MobCode;          // -1 sentinel if absent
        public long   TimestampTicks;
        public uint   SourceIpv4;
        public IntPtr OwnerName;        // null when not extractable
        public int    OwnerNameLen;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct CombatPowerUpdate
    {
        public IntPtr Nickname;
        public int    NicknameLen;
        public int    ServerId;
        public int    CombatPower;
        public long   TimestampTicks;
        public uint   SourceIpv4;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PartyLeft
    {
        public long TimestampTicks;
        public uint SourceIpv4;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DungeonAnnouncement
    {
        public int  DungeonId;
        public long TimestampTicks;
        public uint SourceIpv4;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PartyRosterUpdate
    {
        public int    GroupId;
        public IntPtr Members;          // *NicknameInfo[MembersCount] — valid only during callback
        public int    MembersCount;
        public long   TimestampTicks;
        public uint   SourceIpv4;
        public byte   Confidence;       // 0=Strong, 1=Weak
        public byte   ContainsSelf;
    }

    // =========================================================================
    // Event callback delegates — Cdecl, ctx as first arg matching engine_api.h
    // =========================================================================

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void OnDamage(IntPtr ctx, ref DamageEvent evt);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void OnMobHp(IntPtr ctx, ref MobHpUpdate evt);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void OnEncounter(IntPtr ctx, ref EncounterAnnouncement evt);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void OnCombatBoundary(IntPtr ctx, ref CombatBoundary evt);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void OnNickname(IntPtr ctx, ref NicknameInfo evt);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void OnSummonSpawn(IntPtr ctx, ref SummonSpawnInfo evt);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void OnCombatPower(IntPtr ctx, ref CombatPowerUpdate evt);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void OnPartyLeft(IntPtr ctx, ref PartyLeft evt);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void OnDungeon(IntPtr ctx, ref DungeonAnnouncement evt);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void OnPartyRoster(IntPtr ctx, ref PartyRosterUpdate evt);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void OnGamePacket(
        IntPtr ctx,
        ulong  flowKey,
        uint   sourceIpv4,
        long   timestampTicks,
        IntPtr body,
        int    bodyLength);

    // =========================================================================
    // PacketDispatcher
    // =========================================================================

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr Aion2Fun_Dispatcher_Create();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void Aion2Fun_Dispatcher_Destroy(IntPtr h);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void Aion2Fun_Dispatcher_SetContext(IntPtr h, IntPtr ctx);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void Aion2Fun_Dispatcher_SetOnLog(IntPtr h, Aion2FunOnLog cb);
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void Aion2Fun_Dispatcher_SetOnDamage(IntPtr h, OnDamage cb);
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void Aion2Fun_Dispatcher_SetOnMobHp(IntPtr h, OnMobHp cb);
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void Aion2Fun_Dispatcher_SetOnEncounter(IntPtr h, OnEncounter cb);
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void Aion2Fun_Dispatcher_SetOnCombatBoundary(IntPtr h, OnCombatBoundary cb);
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void Aion2Fun_Dispatcher_SetOnNickname(IntPtr h, OnNickname cb);
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void Aion2Fun_Dispatcher_SetOnSummonSpawn(IntPtr h, OnSummonSpawn cb);
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void Aion2Fun_Dispatcher_SetOnCombatPower(IntPtr h, OnCombatPower cb);
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void Aion2Fun_Dispatcher_SetOnPartyLeft(IntPtr h, OnPartyLeft cb);
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void Aion2Fun_Dispatcher_SetOnDungeon(IntPtr h, OnDungeon cb);
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void Aion2Fun_Dispatcher_SetOnPartyRoster(IntPtr h, OnPartyRoster cb);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void Aion2Fun_Dispatcher_Dispatch(
        IntPtr h, IntPtr body, int bodyLength,
        uint sourceIpv4, long timestampTicks);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern long Aion2Fun_Dispatcher_MalformedCount(IntPtr h);
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern long Aion2Fun_Dispatcher_Lz4SuccessCount(IntPtr h);
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern long Aion2Fun_Dispatcher_Lz4FailureCount(IntPtr h);

    // =========================================================================
    // FrameAssembler
    // =========================================================================

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr Aion2Fun_FrameAssembler_Create();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void Aion2Fun_FrameAssembler_Destroy(IntPtr h);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void Aion2Fun_FrameAssembler_Feed(
        IntPtr h,
        ulong flowKey,
        uint sourceIpv4,
        long timestampTicks,
        IntPtr chunkData, UIntPtr chunkLength,
        OnGamePacket cb,
        IntPtr ctx);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void Aion2Fun_FrameAssembler_Reset(IntPtr h, ulong flowKey);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern long Aion2Fun_FrameAssembler_MalformedFrames(IntPtr h);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int Aion2Fun_FrameAssembler_FlowCount(IntPtr h);
}
