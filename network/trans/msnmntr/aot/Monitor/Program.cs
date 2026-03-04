/*
 * C# port of monitor.cpp for the Monitor Sample (msnmntr).
 * Targets .NET 8 with Native AOT.
 * All Win32 / WFP interop uses LibraryImport (source-generated P/Invoke).
 */

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace MonitorSample;

// -------------------------------------------------------------------------
//  Constants (from ioctl.h)
// -------------------------------------------------------------------------
internal static class Constants
{
    public const string MonitorDosName = @"\\.\MonitorSample";

    // CTL_CODE(FILE_DEVICE_NETWORK=0x12, function, METHOD_BUFFERED=0, FILE_ANY_ACCESS=0)
    // CTL_CODE macro: (DeviceType << 16) | (Access << 14) | (Function << 2) | Method
    public const uint MonitorIoctlEnableMonitor  = (0x12u << 16) | (0u << 14) | (0x1u << 2) | 0u; // 0x120004
    public const uint MonitorIoctlDisableMonitor = (0x12u << 16) | (0u << 14) | (0x2u << 2) | 0u; // 0x120008
    public const uint MonitorIoctlDequeueEvent   = (0x12u << 16) | (0u << 14) | (0x3u << 2) | 0u; // 0x12000C
}

// -------------------------------------------------------------------------
//  Enums (from ioctl.h)
// -------------------------------------------------------------------------
internal enum MonitorOperationMode : uint
{
    InvalidOperation = 0,
    MonitorTraffic = 1,
    MonitorOperationMax = 2,
}

internal enum MonitorEventType : uint
{
    Invalid = 0,
    Connect = 1,
    Disconnect = 2,
}

// -------------------------------------------------------------------------
//  Unmanaged structs
//
//  MONITOR_SETTINGS (ioctl.h):
//    { MONITOR_OPERATION_MODE monitorOperation; (4)
//      UINT32 flags;                            (4) }
//  Total = 8 bytes, natural alignment = 4.
//
//  MONITOR_EVENT (ioctl.h):
//    { MONITOR_EVENT_TYPE type;   offset  0, size 4
//      UINT32 flags;              offset  4, size 4
//      USHORT ipProto;            offset  8, size 2
//      USHORT localPort;          offset 10, size 2
//      USHORT remotePort;         offset 12, size 2
//      [2-byte MSVC padding]      offset 14
//      ULONG localAddressV4;      offset 16, size 4
//      ULONG remoteAddressV4;     offset 20, size 4 }
//  Total = 24 bytes.
//  The explicit _padding field below reproduces the MSVC structural padding.
// -------------------------------------------------------------------------
[StructLayout(LayoutKind.Sequential)]
internal struct MonitorSettings
{
    public MonitorOperationMode MonitorOperation;
    public uint Flags;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MonitorEvent
{
    public MonitorEventType Type;
    public uint Flags;          // stream flags for disconnect, 0 for connect
    public ushort IpProto;
    public ushort LocalPort;
    public ushort RemotePort;
    // 2 bytes of MSVC structural padding inserted to align the following ULONG to 4 bytes.
    // C# Sequential layout does NOT insert this automatically, so we add it explicitly.
    private ushort _padding;
    public uint LocalAddressV4;
    public uint RemoteAddressV4;

    // Compile-time guard: must equal sizeof(MONITOR_EVENT) in ioctl.h (24 bytes).
    static unsafe MonitorEvent()
    {
        if (sizeof(MonitorEvent) != 24)
            throw new InvalidOperationException(
                $"MonitorEvent size mismatch: expected 24, got {sizeof(MonitorEvent)}. " +
                "Check struct padding against MONITOR_EVENT in ioctl.h.");
    }
}

// -------------------------------------------------------------------------
//  GUIDs (from mntrguid.h)
// -------------------------------------------------------------------------
internal static class MonitorGuids
{
    // b3241f1d-7cd2-4e7a-8721-2e97d07702e5
    public static readonly Guid MonitorSampleSublayer =
        new(0xb3241f1d, 0x7cd2, 0x4e7a, 0x87, 0x21, 0x2e, 0x97, 0xd0, 0x77, 0x02, 0xe5);

    // 3aaccbc0-2c29-455f-bb91-0e801c8994a4
    public static readonly Guid MonitorSampleFlowEstablishedCalloutV4 =
        new(0x3aaccbc0, 0x2c29, 0x455f, 0xbb, 0x91, 0x0e, 0x80, 0x1c, 0x89, 0x94, 0xa4);

    // cea0131a-6ed3-4ed6-b40c-8a8fe8434b0a
    public static readonly Guid MonitorSampleStreamCalloutV4 =
        new(0xcea0131a, 0x6ed3, 0x4ed6, 0xb4, 0x0c, 0x8a, 0x8f, 0xe8, 0x43, 0x4b, 0x0a);
}

// -------------------------------------------------------------------------
//  WFP interop structures
//
//  All layouts use LayoutKind.Explicit to match the native x64 ABI exactly.
//  Windows SDK: fwpmtypes.h, fwptypes.h
// -------------------------------------------------------------------------

// FWPM_DISPLAY_DATA0: { PWSTR name; PWSTR description; }
// x64: 0+8, 8+8 = 16 bytes
[StructLayout(LayoutKind.Explicit, Size = 16)]
internal struct FwpmDisplayData0
{
    [FieldOffset(0)]  public nint Name;        // PWSTR
    [FieldOffset(8)]  public nint Description; // PWSTR
}

// FWP_BYTE_BLOB: { UINT32 size; UINT8* data; }
// x64: 0+4, (4 pad) 8+8 = 16 bytes
[StructLayout(LayoutKind.Explicit, Size = 16)]
internal struct FwpByteBlob
{
    [FieldOffset(0)]  public uint Size;
    [FieldOffset(8)]  public nint Data; // UINT8*
}

// FWP_VALUE0: { FWP_DATA_TYPE type; union { ... largest = UINT64/pointer = 8 }; }
// x64: 0+4, (4 pad) 8+8 = 16 bytes
[StructLayout(LayoutKind.Explicit, Size = 16)]
internal struct FwpValue0
{
    [FieldOffset(0)]  public uint  Type;   // FWP_DATA_TYPE
    [FieldOffset(8)]  public ulong Value;  // union – uint8/16/32/64 or pointer
}

// FWP_CONDITION_VALUE0: same layout as FWP_VALUE0
// x64: 0+4, (4 pad) 8+8 = 16 bytes
[StructLayout(LayoutKind.Explicit, Size = 16)]
internal struct FwpConditionValue0
{
    [FieldOffset(0)]  public uint  Type;   // FWP_DATA_TYPE
    [FieldOffset(8)]  public ulong Value;  // union
}

// FWPM_ACTION0: { FWP_ACTION_TYPE type; union { GUID filterType; GUID calloutKey; }; }
// GUID has 4-byte alignment in MSVC, so the union follows immediately at offset 4.
// x64: type at 0 (4), calloutKey at 4 (16) = 20 bytes total.
[StructLayout(LayoutKind.Explicit, Size = 20)]
internal struct FwpmAction0
{
    [FieldOffset(0)]  public uint Type;        // FWP_ACTION_TYPE
    [FieldOffset(4)]  public Guid CalloutKey;  // union filterType / calloutKey
}

// FWPM_FILTER_CONDITION0:
//   { GUID fieldKey;            // offset 0,  size 16
//     FWP_MATCH_TYPE matchType; // offset 16, size 4 (+4 pad)
//     FWP_CONDITION_VALUE0;     // offset 24, size 16 }
// total = 40 bytes
[StructLayout(LayoutKind.Explicit, Size = 40)]
internal struct FwpmFilterCondition0
{
    [FieldOffset(0)]  public Guid FieldKey;
    [FieldOffset(16)] public uint MatchType;       // FWP_MATCH_TYPE
    [FieldOffset(24)] public FwpConditionValue0 ConditionValue;
}

// FWPM_SESSION0:
//   { GUID sessionKey;                   // 0,   16
//     FWPM_DISPLAY_DATA0 displayData;    // 16,  16
//     UINT32 flags;                      // 32,  4
//     UINT32 txnWaitTimeoutInMSec;       // 36,  4
//     DWORD processId;                   // 40,  4 (+4 pad)
//     SID* sid;                          // 48,  8
//     PWSTR username;                    // 56,  8
//     BOOL kernelMode;                   // 64,  4 (+4 pad)
//   } = 72 bytes
[StructLayout(LayoutKind.Explicit, Size = 72)]
internal struct FwpmSession0
{
    [FieldOffset(0)]  public Guid SessionKey;
    [FieldOffset(16)] public FwpmDisplayData0 DisplayData;
    [FieldOffset(32)] public uint Flags;
    [FieldOffset(36)] public uint TxnWaitTimeoutInMSec;
    [FieldOffset(40)] public uint ProcessId;
    [FieldOffset(48)] public nint Sid;       // PSID
    [FieldOffset(56)] public nint Username;  // PWSTR
    [FieldOffset(64)] public int  KernelMode; // BOOL
}

// FWPM_SUBLAYER0:
//   { GUID subLayerKey;                  // 0,   16
//     FWPM_DISPLAY_DATA0 displayData;    // 16,  16
//     UINT32 flags;                      // 32,  4 (+4 pad)
//     GUID* providerKey;                 // 40,  8
//     FWP_BYTE_BLOB providerData;        // 48,  16
//     UINT16 weight;                     // 64,  2 (+6 pad)
//   } = 72 bytes
[StructLayout(LayoutKind.Explicit, Size = 72)]
internal struct FwpmSublayer0
{
    [FieldOffset(0)]  public Guid SubLayerKey;
    [FieldOffset(16)] public FwpmDisplayData0 DisplayData;
    [FieldOffset(32)] public uint Flags;
    [FieldOffset(40)] public nint ProviderKey;     // GUID*
    [FieldOffset(48)] public FwpByteBlob ProviderData;
    [FieldOffset(64)] public ushort Weight;
}

// FWPM_CALLOUT0:
//   { GUID calloutKey;                   // 0,   16
//     FWPM_DISPLAY_DATA0 displayData;    // 16,  16
//     UINT32 flags;                      // 32,  4 (+4 pad)
//     GUID* providerKey;                 // 40,  8
//     FWP_BYTE_BLOB providerData;        // 48,  16
//     GUID applicableLayer;              // 64,  16
//     UINT32 calloutId;                  // 80,  4 (+4 pad)
//   } = 88 bytes
[StructLayout(LayoutKind.Explicit, Size = 88)]
internal struct FwpmCallout0
{
    [FieldOffset(0)]  public Guid CalloutKey;
    [FieldOffset(16)] public FwpmDisplayData0 DisplayData;
    [FieldOffset(32)] public uint Flags;
    [FieldOffset(40)] public nint ProviderKey;     // GUID*
    [FieldOffset(48)] public FwpByteBlob ProviderData;
    [FieldOffset(64)] public Guid ApplicableLayer;
    [FieldOffset(80)] public uint CalloutId;
}

// -------------------------------------------------------------------------
//  FWPM_FILTER0 — built as a raw byte buffer via helper methods
//  to guarantee the exact native x64 layout byte-for-byte.
//
//  Native x64 layout (from fwpmtypes.h, verified with MSVC offsetof):
//    Offset  Field
//      0     GUID filterKey                          (16)
//     16     FWPM_DISPLAY_DATA0 displayData          (16)
//     32     UINT32 flags                            (4 +4 pad)
//     40     GUID* providerKey                       (8)
//     48     FWP_BYTE_BLOB providerData              (16)
//     64     GUID layerKey                           (16)
//     80     GUID subLayerKey                        (16)
//     96     FWP_VALUE0 weight                       (16)
//    112     UINT32 numFilterConditions              (4 +4 pad)
//    120     FWPM_FILTER_CONDITION0* filterCondition (8)
//    128     FWPM_ACTION0 action                     (20 +4 pad to 8-align next field)
//    152     union { UINT64; GUID } context          (16)
//    168     GUID* reserved                          (8)
//    176     UINT64 filterId                         (8)
//    184     FWP_VALUE0 effectiveWeight              (16)
//    200     total
// -------------------------------------------------------------------------
internal static class FwpmFilter0
{
    public const int Size = 200;

    // Field offsets
    public const int FilterKey           = 0;
    public const int DisplayDataName     = 16;
    public const int DisplayDataDesc     = 24;
    public const int Flags               = 32;
    public const int ProviderKey         = 40;
    public const int ProviderData        = 48;
    public const int LayerKey            = 64;
    public const int SubLayerKey         = 80;
    public const int WeightType          = 96;
    public const int WeightValue         = 104;
    public const int NumFilterConditions = 112;
    public const int FilterCondition     = 120;
    public const int ActionType          = 128;
    // FWPM_ACTION0.calloutKey starts at ActionType+4 (GUID has 4-byte alignment,
    // immediately follows the 4-byte type field — no padding inserted by MSVC).
    public const int ActionCalloutKey    = 132;
    public const int RawContext          = 152;
    public const int Reserved            = 168;
    public const int FilterId            = 176;
    public const int EffectiveWeight     = 184;
}

// -------------------------------------------------------------------------
//  WFP constants
// -------------------------------------------------------------------------
internal static class Fwpm
{
    public const uint SessionFlagDynamic = 0x00000001;
    public const uint CalloutFlagPersistent = 0x00010000;
    public const uint RpcCAuthnWinnt = 10;

    // FWP_ACTION_TYPE
    // FWP_ACTION_FLAG_CALLOUT = 0x00004000
    // FWP_ACTION_CALLOUT_INSPECTION = 0x00000002 | FWP_ACTION_FLAG_CALLOUT
    public const uint FwpActionCalloutInspection = 0x00004002;

    // FWP_MATCH_TYPE
    public const uint FwpMatchEqual = 0;

    // FWP_DATA_TYPE enum (fwptypes.h)
    //   FWP_EMPTY              = 0
    //   FWP_UINT8              = 1
    //   FWP_UINT16             = 2
    //   FWP_UINT32             = 3
    //   FWP_UINT64             = 4
    //   FWP_INT8               = 5
    //   FWP_INT16              = 6 
    //   FWP_INT32              = 7
    //   FWP_INT64              = 8
    //   FWP_FLOAT              = 9
    //   FWP_DOUBLE             = 10
    //   FWP_BYTE_ARRAY16_TYPE  = 11
    //   FWP_BYTE_BLOB_TYPE     = 12
    //   FWP_SID                = 13
    public const uint FwpEmpty = 0;
    public const uint FwpUint8 = 1;
    public const uint FwpByteBlobType = 12;

    // WFP error codes (winerror.h)
    public const uint FwpEAlreadyExists = 0x80320024;

    // FWPM_LAYER GUIDs (from fwpmu.h)
    // FWPM_LAYER_ALE_FLOW_ESTABLISHED_V4 = c38d57d1-05a7-4c33-904f-7fbceee60e82
    public static readonly Guid LayerAleFlowEstablishedV4 =
        new(0xc38d57d1, 0x05a7, 0x4c33, 0x90, 0x4f, 0x7f, 0xbc, 0xee, 0xe6, 0x0e, 0x82);

    // FWPM_LAYER_STREAM_V4 = 3b89653c-c170-49e4-b1cd-e0eeeee19a3e
    public static readonly Guid LayerStreamV4 =
        new(0x3b89653c, 0xc170, 0x49e4, 0xb1, 0xcd, 0xe0, 0xee, 0xee, 0xe1, 0x9a, 0x3e);

    // FWPM_CONDITION_IP_PROTOCOL = 3971ef2b-623e-4f9a-8cb1-6e79b806b9a7
    public static readonly Guid ConditionIpProtocol =
        new(0x3971ef2b, 0x623e, 0x4f9a, 0x8c, 0xb1, 0x6e, 0x79, 0xb8, 0x06, 0xb9, 0xa7);

    // FWPM_CONDITION_ALE_APP_ID = d78e1e87-8644-4ea5-9437-d809ecefc971
    public static readonly Guid ConditionAleAppId =
        new(0xd78e1e87, 0x8644, 0x4ea5, 0x94, 0x37, 0xd8, 0x09, 0xec, 0xef, 0xc9, 0x71);

    public const byte IpProtoTcp = 6;
}

// -------------------------------------------------------------------------
//  Native P/Invoke – LibraryImport (source-generated, AOT-safe)
// -------------------------------------------------------------------------
internal static partial class NativeMethods
{
    public static readonly nint InvalidHandleValue = -1;

    // kernel32 ----------------------------------------------------------
    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW",
        StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    public static partial nint CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        nint lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        nint hTemplateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(nint hObject);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeviceIoControl(
        nint hDevice,
        uint dwIoControlCode,
        nint lpInBuffer,
        uint nInBufferSize,
        nint lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        nint lpOverlapped);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial nint CreateEventW(
        nint lpEventAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool bManualReset,
        [MarshalAs(UnmanagedType.Bool)] bool bInitialState,
        nint lpName);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetEvent(nint hEvent);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ResetEvent(nint hEvent);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial uint WaitForSingleObject(nint hHandle, uint dwMilliseconds);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial uint WaitForMultipleObjects(
        uint nCount,
        nint lpHandles,      // HANDLE[]
        [MarshalAs(UnmanagedType.Bool)] bool bWaitAll,
        uint dwMilliseconds);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetOverlappedResult(
        nint hFile,
        nint lpOverlapped,
        out uint lpNumberOfBytesTransferred,
        [MarshalAs(UnmanagedType.Bool)] bool bWait);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CancelIoEx(nint hFile, nint lpOverlapped);

    // fwpuclnt.dll (WFP user-mode API) ----------------------------------
    [LibraryImport("fwpuclnt.dll", SetLastError = true)]
    public static partial uint FwpmEngineOpen0(
        nint serverName,            // PCWSTR – NULL for local
        uint authnService,
        nint authIdentity,          // SEC_WINNT_AUTH_IDENTITY_W*
        nint session,               // const FWPM_SESSION0*
        out nint engineHandle);

    [LibraryImport("fwpuclnt.dll")]
    public static partial uint FwpmEngineClose0(nint engineHandle);

    [LibraryImport("fwpuclnt.dll")]
    public static partial uint FwpmTransactionBegin0(nint engineHandle, uint flags);

    [LibraryImport("fwpuclnt.dll")]
    public static partial uint FwpmTransactionCommit0(nint engineHandle);

    [LibraryImport("fwpuclnt.dll")]
    public static partial uint FwpmTransactionAbort0(nint engineHandle);

    [LibraryImport("fwpuclnt.dll")]
    public static partial uint FwpmSubLayerAdd0(
        nint engineHandle,
        nint subLayer,              // const FWPM_SUBLAYER0*
        nint sd);

    [LibraryImport("fwpuclnt.dll")]
    public static partial uint FwpmCalloutAdd0(
        nint engineHandle,
        nint callout,               // const FWPM_CALLOUT0*
        nint sd,
        nint id);                   // UINT32* (nullable)

    [LibraryImport("fwpuclnt.dll")]
    public static partial uint FwpmCalloutDeleteByKey0(
        nint engineHandle,
        nint key);                  // const GUID*

    [LibraryImport("fwpuclnt.dll")]
    public static partial uint FwpmFilterAdd0(
        nint engineHandle,
        nint filter,                // const FWPM_FILTER0*
        nint sd,
        nint filterId);             // UINT64* (nullable)

    [LibraryImport("fwpuclnt.dll", EntryPoint = "FwpmGetAppIdFromFileName0",
        StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint FwpmGetAppIdFromFileName0(
        string fileName,
        out nint appId);        // FWP_BYTE_BLOB**

    [LibraryImport("fwpuclnt.dll")]
    public static partial void FwpmFreeMemory0(ref nint p);

    // Win32 constants
    public const uint GenericRead  = 0x80000000;
    public const uint GenericWrite = 0x40000000;
    public const uint FileShareRead  = 0x00000001;
    public const uint FileShareWrite = 0x00000002;
    public const uint OpenExisting = 3;
    public const uint FileFlagOverlapped = 0x40000000;
    public const uint WaitObject0 = 0;
    public const uint WaitTimeout = 0x00000102;
    public const uint Infinite = 0xFFFFFFFF;
    public const int ErrorIoPending = 997;
    public const int ErrorOperationAborted = 995;
}

// -------------------------------------------------------------------------
//  Formatting helpers
// -------------------------------------------------------------------------
internal static class Formatting
{
    public static string FormatIpv4(uint address)
    {
        byte b1 = (byte)(address >> 24);
        byte b2 = (byte)(address >> 16);
        byte b3 = (byte)(address >> 8);
        byte b4 = (byte)(address);
        return $"{b1}.{b2}.{b3}.{b4}";
    }

    public static string FormatIpProto(ushort proto) => proto switch
    {
        1   => "ICMP",
        2   => "IGMP",
        6   => "TCP",
        17  => "UDP",
        41  => "IPv6",
        47  => "GRE",
        50  => "ESP",
        51  => "AH",
        58  => "ICMPv6",
        89  => "OSPF",
        132 => "SCTP",
        _   => proto.ToString(),
    };

    private static readonly (uint Bit, string Name)[] StreamFlagBits =
    [
        (0x00000001, "SEND"),
        (0x00000002, "RECV"),
        (0x00000004, "SEND_DISCONNECT"),
        (0x00000008, "RECV_DISCONNECT"),
        (0x00010000, "SEND_ABORT"),
        (0x00020000, "RECV_ABORT"),
        (0x00040000, "SEND_EXPEDITED"),
        (0x00080000, "RECV_EXPEDITED"),
    ];

    public static string FormatStreamFlags(uint flags)
    {
        if (flags == 0) return "0";

        var parts = new System.Collections.Generic.List<string>();
        uint remaining = flags;

        foreach (var (bit, name) in StreamFlagBits)
        {
            if ((flags & bit) != 0)
            {
                parts.Add(name);
                remaining &= ~bit;
            }
        }

        if (remaining != 0)
        {
            parts.Add($"0x{remaining:x}");
        }

        return string.Join('|', parts);
    }
}

// -------------------------------------------------------------------------
//  Application logic
// -------------------------------------------------------------------------
internal static class MonitorApp
{
    // -- Unsafe helpers for writing typed values into raw byte buffers ----
    private static unsafe void WriteGuid(byte* buf, int offset, Guid value)
        => *(Guid*)(buf + offset) = value;

    private static unsafe void WriteUInt32(byte* buf, int offset, uint value)
        => *(uint*)(buf + offset) = value;

    private static unsafe void WriteUInt64(byte* buf, int offset, ulong value)
        => *(ulong*)(buf + offset) = value;

    private static unsafe void WriteUInt8(byte* buf, int offset, byte value)
        => *(buf + offset) = value;

    private static unsafe void WritePtr(byte* buf, int offset, nint value)
        => *(nint*)(buf + offset) = value;

    // -- Device open / close ---------------------------------------------
    public static uint OpenMonitorDevice(out nint monitorDevice)
    {
        monitorDevice = NativeMethods.CreateFile(
            Constants.MonitorDosName,
            NativeMethods.GenericRead | NativeMethods.GenericWrite,
            NativeMethods.FileShareRead | NativeMethods.FileShareWrite,
            nint.Zero,
            NativeMethods.OpenExisting,
            NativeMethods.FileFlagOverlapped,
            nint.Zero);

        if (monitorDevice == NativeMethods.InvalidHandleValue)
        {
            monitorDevice = nint.Zero;
            return (uint)Marshal.GetLastWin32Error();
        }
        return 0; // NO_ERROR
    }

    public static bool CloseMonitorDevice(nint monitorDevice)
        => NativeMethods.CloseHandle(monitorDevice);

    // -- Enable / Disable monitoring -------------------------------------
    public static unsafe uint EnableMonitoring(nint monitorDevice, MonitorSettings settings)
    {
        // The device is opened with FILE_FLAG_OVERLAPPED, so all DeviceIoControl calls
        // must use a valid OVERLAPPED struct. Passing nint.Zero for lpOverlapped on an
        // overlapped handle results in undefined behaviour (potential hang / wrong error).
        // We use a synchronous OVERLAPPED pattern: event is signaled, bWait=true.
        nint ovEvent = NativeMethods.CreateEventW(nint.Zero, true, false, nint.Zero);
        if (ovEvent == nint.Zero)
            return (uint)Marshal.GetLastWin32Error();

        NativeOverlapped ov = default;
        ov.EventHandle = ovEvent;

        try
        {
            uint bytesReturned;
            bool ok = NativeMethods.DeviceIoControl(
                monitorDevice,
                Constants.MonitorIoctlEnableMonitor,
                (nint)(&settings),
                (uint)sizeof(MonitorSettings),
                nint.Zero,
                0,
                out bytesReturned,
                (nint)Unsafe.AsPointer(ref ov));

            if (!ok)
            {
                int err = Marshal.GetLastWin32Error();
                if (err == NativeMethods.ErrorIoPending)
                {
                    // Wait for synchronous-style completion.
                    if (!NativeMethods.GetOverlappedResult(monitorDevice,
                            (nint)Unsafe.AsPointer(ref ov), out bytesReturned, true))
                    {
                        return (uint)Marshal.GetLastWin32Error();
                    }
                }
                else
                {
                    return (uint)err;
                }
            }
            return 0;
        }
        finally
        {
            NativeMethods.CloseHandle(ovEvent);
        }
    }

    public static unsafe uint DisableMonitoring(nint monitorDevice)
    {
        // Same overlapped requirement as EnableMonitoring — the handle is FILE_FLAG_OVERLAPPED.
        nint ovEvent = NativeMethods.CreateEventW(nint.Zero, true, false, nint.Zero);
        if (ovEvent == nint.Zero)
            return (uint)Marshal.GetLastWin32Error();

        NativeOverlapped ov = default;
        ov.EventHandle = ovEvent;

        try
        {
            uint bytesReturned;
            bool ok = NativeMethods.DeviceIoControl(
                monitorDevice,
                Constants.MonitorIoctlDisableMonitor,
                nint.Zero,
                0,
                nint.Zero,
                0,
                out bytesReturned,
                (nint)Unsafe.AsPointer(ref ov));

            if (!ok)
            {
                int err = Marshal.GetLastWin32Error();
                if (err == NativeMethods.ErrorIoPending)
                {
                    if (!NativeMethods.GetOverlappedResult(monitorDevice,
                            (nint)Unsafe.AsPointer(ref ov), out bytesReturned, true))
                    {
                        return (uint)Marshal.GetLastWin32Error();
                    }
                }
                else
                {
                    return (uint)err;
                }
            }
            return 0;
        }
        finally
        {
            NativeMethods.CloseHandle(ovEvent);
        }
    }

    // -- Print event -----------------------------------------------------
    public static void PrintEvent(in MonitorEvent evt)
    {
        string localAddr  = Formatting.FormatIpv4(evt.LocalAddressV4);
        string remoteAddr = Formatting.FormatIpv4(evt.RemoteAddressV4);
        string proto      = Formatting.FormatIpProto(evt.IpProto);

        if (evt.Type == MonitorEventType.Connect)
        {
            Console.WriteLine($"[CONNECT] Proto={proto} Local={localAddr}:{evt.LocalPort} Remote={remoteAddr}:{evt.RemotePort}");
        }
        else if (evt.Type == MonitorEventType.Disconnect)
        {
            string flagStr = Formatting.FormatStreamFlags(evt.Flags);
            Console.WriteLine($"[DISCONNECT] Proto={proto} Flags={flagStr} Local={localAddr}:{evt.LocalPort} Remote={remoteAddr}:{evt.RemotePort}");
        }
    }

    // -- Stats -----------------------------------------------------------
    private static void PrintStats(
        long nowMs,
        ref long lastPrintMs,
        ref ulong lastReceived,
        ulong received,
        ulong asyncWaits,
        ulong canceled,
        ulong syncCompleted)
    {
        const long intervalMs = 1000;
        if (nowMs - lastPrintMs < intervalMs) return;

        long elapsedMs = nowMs - lastPrintMs;
        ulong delta = received - lastReceived;
        double rate = elapsedMs > 0 ? 1000.0 * delta / elapsedMs : 0.0;

        Console.WriteLine(
            $"[STATS] rate={rate:F1} ev/s received={received} asyncWaits={asyncWaits} canceled={canceled} syncCompleted={syncCompleted}");

        lastPrintMs = nowMs;
        lastReceived = received;
    }

    // -- Event thread ----------------------------------------------------
    public static unsafe void EventThread(nint monitorDevice, nint quitEvent)
    {
        ulong eventsReceived = 0;
        ulong asyncWaits = 0;
        ulong canceled = 0;
        ulong syncCompleted = 0;
        long lastPrintMs = Environment.TickCount64;
        ulong lastReceived = 0;

        nint ovEvent = NativeMethods.CreateEventW(nint.Zero, true, false, nint.Zero);
        if (ovEvent == nint.Zero)
        {
            Console.WriteLine($"CreateEvent failed: {Marshal.GetLastWin32Error()}");
            return;
        }

        NativeOverlapped ov = default;
        ov.EventHandle = ovEvent;

        // HANDLE[2] for WaitForMultipleObjects
        nint* waitHandles = stackalloc nint[2];
        waitHandles[0] = quitEvent;
        waitHandles[1] = ovEvent;

        try
        {
            while (true)
            {
                NativeMethods.ResetEvent(ovEvent);
                MonitorEvent evt = default;
                uint bytesReturned = 0;

                bool ok = NativeMethods.DeviceIoControl(
                    monitorDevice,
                    Constants.MonitorIoctlDequeueEvent,
                    nint.Zero,
                    0,
                    (nint)(&evt),
                    (uint)sizeof(MonitorEvent),
                    out bytesReturned,
                    (nint)Unsafe.AsPointer(ref ov));

                if (!ok)
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err == NativeMethods.ErrorIoPending)
                    {
                        asyncWaits++;

                        uint wait = NativeMethods.WaitForMultipleObjects(
                            2, (nint)waitHandles, false, 1000);

                        if (wait == NativeMethods.WaitObject0)
                        {
                            // Shutdown
                            NativeMethods.CancelIoEx(monitorDevice, (nint)Unsafe.AsPointer(ref ov));
                            NativeMethods.WaitForSingleObject(ovEvent, NativeMethods.Infinite);
                            canceled++;
                            break;
                        }
                        else if (wait == NativeMethods.WaitObject0 + 1)
                        {
                            // Event arrived
                            if (!NativeMethods.GetOverlappedResult(monitorDevice, (nint)Unsafe.AsPointer(ref ov), out bytesReturned, false))
                            {
                                err = Marshal.GetLastWin32Error();
                                if (err == NativeMethods.ErrorOperationAborted)
                                {
                                    canceled++;
                                    PrintStats(Environment.TickCount64, ref lastPrintMs, ref lastReceived,
                                               eventsReceived, asyncWaits, canceled, syncCompleted);
                                    continue;
                                }
                                break;
                            }
                            // Fall through to process
                        }
                        else if (wait == NativeMethods.WaitTimeout)
                        {
                            PrintStats(Environment.TickCount64, ref lastPrintMs, ref lastReceived,
                                       eventsReceived, asyncWaits, canceled, syncCompleted);
                            continue;
                        }
                        else
                        {
                            break;
                        }
                    }
                    else if (err == NativeMethods.ErrorOperationAborted)
                    {
                        canceled++;
                        if (NativeMethods.WaitForSingleObject(quitEvent, 0) == NativeMethods.WaitObject0)
                            break;
                        PrintStats(Environment.TickCount64, ref lastPrintMs, ref lastReceived,
                                   eventsReceived, asyncWaits, canceled, syncCompleted);
                        continue;
                    }
                    else
                    {
                        break;
                    }
                }
                else
                {
                    syncCompleted++;
                }

                if (bytesReturned == (uint)sizeof(MonitorEvent))
                {
                    eventsReceived++;
                    PrintEvent(in evt);
                }

                PrintStats(Environment.TickCount64, ref lastPrintMs, ref lastReceived,
                           eventsReceived, asyncWaits, canceled, syncCompleted);
            }
        }
        finally
        {
            NativeMethods.CloseHandle(ovEvent);
        }
    }

    // -- Build a 72-byte FWPM_SESSION0 on the native heap ----------------
    private const int SessionSize = 72;

    private static unsafe nint AllocSession(nint namePtr, nint descPtr, uint flags)
    {
        byte* buf = (byte*)NativeMemory.AllocZeroed((nuint)SessionSize);
        WritePtr(buf, 16, namePtr);     // displayData.name
        WritePtr(buf, 24, descPtr);     // displayData.description
        WriteUInt32(buf, 32, flags);    // flags
        return (nint)buf;
    }

    // -- Build an 88-byte FWPM_CALLOUT0 on the native heap ---------------
    private const int CalloutSize = 88;

    private static unsafe nint AllocCallout(Guid calloutKey, nint namePtr, nint descPtr,
                                             Guid applicableLayer, uint flags)
    {
        byte* buf = (byte*)NativeMemory.AllocZeroed((nuint)CalloutSize);
        WriteGuid(buf, 0, calloutKey);
        WritePtr(buf, 16, namePtr);
        WritePtr(buf, 24, descPtr);
        WriteUInt32(buf, 32, flags);
        WriteGuid(buf, 64, applicableLayer);
        return (nint)buf;
    }

    // -- Build a 72-byte FWPM_SUBLAYER0 on the native heap ---------------
    private const int SublayerSize = 72;

    private static unsafe nint AllocSublayer(Guid sublayerKey, nint namePtr, nint descPtr, ushort weight)
    {
        byte* buf = (byte*)NativeMemory.AllocZeroed((nuint)SublayerSize);
        WriteGuid(buf, 0, sublayerKey);
        WritePtr(buf, 16, namePtr);
        WritePtr(buf, 24, descPtr);
        *(ushort*)(buf + 64) = weight;
        return (nint)buf;
    }

    // -- Add callouts (persistent, non-dynamic session) ------------------
    public static unsafe uint AddCallouts()
    {
        uint result;
        nint engineHandle = nint.Zero;

        nint namePtr = Marshal.StringToHGlobalUni("Monitor Sample Non-Dynamic Session");
        nint descPtr = Marshal.StringToHGlobalUni("For Adding callouts");
        nint sessionPtr = AllocSession(namePtr, descPtr, 0);

        try
        {
            Console.WriteLine("Opening Filtering Engine");
            result = NativeMethods.FwpmEngineOpen0(nint.Zero, Fwpm.RpcCAuthnWinnt, nint.Zero, sessionPtr, out engineHandle);
            if (result != 0) return result;

            Console.WriteLine("Starting Transaction for adding callouts");
            result = NativeMethods.FwpmTransactionBegin0(engineHandle, 0);
            if (result != 0) { Abort(engineHandle); return result; }

            Console.WriteLine("Successfully started the Transaction");

            // Flow Established callout
            nint feNamePtr = Marshal.StringToHGlobalUni("Flow Established Callout");
            nint feDescPtr = Marshal.StringToHGlobalUni("Monitor Sample - Flow Established Callout");
            nint calloutPtr = AllocCallout(
                MonitorGuids.MonitorSampleFlowEstablishedCalloutV4,
                feNamePtr, feDescPtr,
                Fwpm.LayerAleFlowEstablishedV4,
                Fwpm.CalloutFlagPersistent);

            Console.WriteLine("Adding Persistent Flow Established callout through the Filtering Engine");
            result = NativeMethods.FwpmCalloutAdd0(engineHandle, calloutPtr, nint.Zero, nint.Zero);
            NativeMemory.Free((void*)calloutPtr);
            Marshal.FreeHGlobal(feNamePtr);
            Marshal.FreeHGlobal(feDescPtr);

            if (result != 0) { Abort(engineHandle); return result; }
            Console.WriteLine("Successfully Added Persistent Flow Established callout.");

            // Stream callout
            nint sNamePtr = Marshal.StringToHGlobalUni("Monitor Sample - Stream Callout");
            nint sDescPtr = Marshal.StringToHGlobalUni("Monitor Sample - Stream Callout");
            calloutPtr = AllocCallout(
                MonitorGuids.MonitorSampleStreamCalloutV4,
                sNamePtr, sDescPtr,
                Fwpm.LayerStreamV4,
                Fwpm.CalloutFlagPersistent);

            Console.WriteLine("Adding Persistent Stream callout through the Filtering Engine");
            result = NativeMethods.FwpmCalloutAdd0(engineHandle, calloutPtr, nint.Zero, nint.Zero);
            NativeMemory.Free((void*)calloutPtr);
            Marshal.FreeHGlobal(sNamePtr);
            Marshal.FreeHGlobal(sDescPtr);

            if (result != 0) { Abort(engineHandle); return result; }
            Console.WriteLine("Successfully Added Persistent Stream callout.");

            Console.WriteLine("Committing Transaction");
            result = NativeMethods.FwpmTransactionCommit0(engineHandle);
            if (result == 0)
                Console.WriteLine("Successfully Committed Transaction.");

            return result;
        }
        finally
        {
            NativeMemory.Free((void*)sessionPtr);
            Marshal.FreeHGlobal(namePtr);
            Marshal.FreeHGlobal(descPtr);
            if (engineHandle != nint.Zero)
                NativeMethods.FwpmEngineClose0(engineHandle);
        }
    }

    // -- Remove callouts -------------------------------------------------
    public static unsafe uint RemoveCallouts()
    {
        uint result;
        nint engineHandle = nint.Zero;

        nint namePtr = Marshal.StringToHGlobalUni("Monitor Sample Non-Dynamic Session");
        nint descPtr = Marshal.StringToHGlobalUni("For Adding callouts");
        nint sessionPtr = AllocSession(namePtr, descPtr, 0);

        try
        {
            Console.WriteLine("Opening Filtering Engine");
            result = NativeMethods.FwpmEngineOpen0(nint.Zero, Fwpm.RpcCAuthnWinnt, nint.Zero, sessionPtr, out engineHandle);
            if (result != 0) return result;

            Console.WriteLine("Starting Transaction for Removing callouts");
            result = NativeMethods.FwpmTransactionBegin0(engineHandle, 0);
            if (result != 0) { Abort(engineHandle); return result; }
            Console.WriteLine("Successfully started the Transaction");

            Guid feKey = MonitorGuids.MonitorSampleFlowEstablishedCalloutV4;
            Console.WriteLine("Deleting Flow Established callout");
            result = NativeMethods.FwpmCalloutDeleteByKey0(engineHandle, (nint)Unsafe.AsPointer(ref feKey));
            if (result != 0) { Abort(engineHandle); return result; }
            Console.WriteLine("Successfully Deleted Flow Established callout");

            Guid sKey = MonitorGuids.MonitorSampleStreamCalloutV4;
            Console.WriteLine("Deleting Stream callout");
            result = NativeMethods.FwpmCalloutDeleteByKey0(engineHandle, (nint)Unsafe.AsPointer(ref sKey));
            if (result != 0) { Abort(engineHandle); return result; }
            Console.WriteLine("Successfully Deleted Stream callout");

            Console.WriteLine("Committing Transaction");
            result = NativeMethods.FwpmTransactionCommit0(engineHandle);
            if (result == 0)
                Console.WriteLine("Successfully Committed Transaction.");

            return result;
        }
        finally
        {
            NativeMemory.Free((void*)sessionPtr);
            Marshal.FreeHGlobal(namePtr);
            Marshal.FreeHGlobal(descPtr);
            if (engineHandle != nint.Zero)
                NativeMethods.FwpmEngineClose0(engineHandle);
        }
    }

    // -- Add WFP filters (dynamic session) --------------------------------
    public static unsafe uint AddFilters(nint engineHandle, nint applicationId)
    {
        uint result;

        nint slNamePtr = Marshal.StringToHGlobalUni("Monitor Sample Sub layer");
        nint slDescPtr = Marshal.StringToHGlobalUni("Monitor Sample Sub layer");

        nint feFilterName = Marshal.StringToHGlobalUni("Flow established filter.");
        nint feFilterDesc = Marshal.StringToHGlobalUni("Sets up flow for traffic that we are interested in.");
        nint stFilterName = Marshal.StringToHGlobalUni("Stream Layer Filter");
        nint stFilterDesc = Marshal.StringToHGlobalUni("Monitors TCP traffic.");

        nint sublayerPtr = AllocSublayer(MonitorGuids.MonitorSampleSublayer, slNamePtr, slDescPtr, 0);

        // Allocate filter conditions on native heap (2 × 40 = 80 bytes)
        byte* conditions = (byte*)NativeMemory.AllocZeroed(2 * 40);

        // Allocate FWPM_FILTER0 on native heap (200 bytes)
        byte* filterBuf = (byte*)NativeMemory.AllocZeroed((nuint)FwpmFilter0.Size);

        try
        {
            Console.WriteLine("Starting Transaction");
            result = NativeMethods.FwpmTransactionBegin0(engineHandle, 0);
            if (result != 0) { Abort(engineHandle); return result; }
            Console.WriteLine("Successfully Started Transaction");

            // Sublayer
            Console.WriteLine("Adding Sublayer");
            result = NativeMethods.FwpmSubLayerAdd0(engineHandle, sublayerPtr, nint.Zero);
            if (result != 0 && result != Fwpm.FwpEAlreadyExists)
            {
                Abort(engineHandle); return result;
            }
            if (result == Fwpm.FwpEAlreadyExists)
                Console.WriteLine("Sublayer already exists — continuing");
            else
                Console.WriteLine("Successfully added Sublayer");

            // --- Build filter conditions ---
            // Condition 0: IP_PROTOCOL == TCP
            byte* cond0 = conditions;
            WriteGuid(cond0, 0, Fwpm.ConditionIpProtocol);    // fieldKey
            WriteUInt32(cond0, 16, Fwpm.FwpMatchEqual);        // matchType
            WriteUInt32(cond0, 24, Fwpm.FwpUint8);             // conditionValue.type
            WriteUInt8(cond0, 32, Fwpm.IpProtoTcp);            // conditionValue.uint8 (union)

            uint numConditions = 1;

            if (applicationId != nint.Zero)
            {
                // Condition 1: ALE_APP_ID == applicationPath
                byte* cond1 = conditions + 40;
                WriteGuid(cond1, 0, Fwpm.ConditionAleAppId);
                WriteUInt32(cond1, 16, Fwpm.FwpMatchEqual);
                WriteUInt32(cond1, 24, Fwpm.FwpByteBlobType);
                WriteUInt64(cond1, 32, (ulong)applicationId);
                numConditions = 2;
            }

            // --- Flow Established filter ---
            NativeMemory.Clear(filterBuf, (nuint)FwpmFilter0.Size);
            var feFilterKey = Guid.NewGuid();
            WriteGuid(filterBuf,   FwpmFilter0.FilterKey,           feFilterKey);
            WritePtr(filterBuf,    FwpmFilter0.DisplayDataName,     feFilterName);
            WritePtr(filterBuf,    FwpmFilter0.DisplayDataDesc,     feFilterDesc);
            WriteGuid(filterBuf,   FwpmFilter0.LayerKey,            Fwpm.LayerAleFlowEstablishedV4);
            WriteGuid(filterBuf,   FwpmFilter0.SubLayerKey,         MonitorGuids.MonitorSampleSublayer);
            WriteUInt32(filterBuf, FwpmFilter0.WeightType,          Fwpm.FwpEmpty);
            WriteUInt32(filterBuf, FwpmFilter0.NumFilterConditions, numConditions);
            WritePtr(filterBuf,    FwpmFilter0.FilterCondition,     (nint)conditions);
            WriteUInt32(filterBuf, FwpmFilter0.ActionType,          Fwpm.FwpActionCalloutInspection);
            WriteGuid(filterBuf,   FwpmFilter0.ActionCalloutKey,    MonitorGuids.MonitorSampleFlowEstablishedCalloutV4);

            Console.WriteLine(applicationId != nint.Zero
                ? "Adding Flow Established Filter (app-scoped)"
                : "Adding Flow Established Filter (all processes)");

            result = NativeMethods.FwpmFilterAdd0(engineHandle, (nint)filterBuf, nint.Zero, nint.Zero);
            if (result != 0 && result != Fwpm.FwpEAlreadyExists) { Abort(engineHandle); return result; }
            Console.WriteLine(result == Fwpm.FwpEAlreadyExists
                ? "Flow Established filter already exists — continuing"
                : "Successfully added Flow Established filter");

            // --- Stream filter ---
            // numFilterConditions=0 and filterCondition=NULL is valid per WFP spec;
            // the native C++ reference code uses a non-null pointer here only because
            // it reuses a stack buffer — the count=0 means the pointer is ignored by the engine.
            NativeMemory.Clear(filterBuf, (nuint)FwpmFilter0.Size);
            var stFilterKey = Guid.NewGuid();
            WriteGuid(filterBuf,   FwpmFilter0.FilterKey,           stFilterKey);
            WritePtr(filterBuf,    FwpmFilter0.DisplayDataName,     stFilterName);
            WritePtr(filterBuf,    FwpmFilter0.DisplayDataDesc,     stFilterDesc);
            WriteGuid(filterBuf,   FwpmFilter0.LayerKey,            Fwpm.LayerStreamV4);
            WriteGuid(filterBuf,   FwpmFilter0.SubLayerKey,         MonitorGuids.MonitorSampleSublayer);
            WriteUInt32(filterBuf, FwpmFilter0.WeightType,          Fwpm.FwpEmpty);
            WriteUInt32(filterBuf, FwpmFilter0.NumFilterConditions, 0);
            // filterCondition pointer is left NULL (zeroed buffer) — valid when numFilterConditions=0.
            WriteUInt32(filterBuf, FwpmFilter0.ActionType,          Fwpm.FwpActionCalloutInspection);
            WriteGuid(filterBuf,   FwpmFilter0.ActionCalloutKey,    MonitorGuids.MonitorSampleStreamCalloutV4);

            Console.WriteLine("Adding Stream Filter");
            result = NativeMethods.FwpmFilterAdd0(engineHandle, (nint)filterBuf, nint.Zero, nint.Zero);
            if (result != 0 && result != Fwpm.FwpEAlreadyExists) { Abort(engineHandle); return result; }
            Console.WriteLine(result == Fwpm.FwpEAlreadyExists
                ? "Stream filter already exists — continuing"
                : "Successfully added Stream filter");

            Console.WriteLine("Committing Transaction");
            result = NativeMethods.FwpmTransactionCommit0(engineHandle);
            if (result == 0)
                Console.WriteLine("Successfully Committed Transaction");

            return result;
        }
        finally
        {
            NativeMemory.Free(filterBuf);
            NativeMemory.Free(conditions);
            NativeMemory.Free((void*)sublayerPtr);
            Marshal.FreeHGlobal(slNamePtr);
            Marshal.FreeHGlobal(slDescPtr);
            Marshal.FreeHGlobal(feFilterName);
            Marshal.FreeHGlobal(feFilterDesc);
            Marshal.FreeHGlobal(stFilterName);
            Marshal.FreeHGlobal(stFilterDesc);
        }
    }

    // -- Main monitoring flow --------------------------------------------
    public static unsafe uint DoMonitoring(string? appPath)
    {
        nint monitorDevice = nint.Zero;
        nint engineHandle = nint.Zero;
        nint applicationId = nint.Zero;
        nint quitEvent = nint.Zero;
        Thread? eventThread = null;
        uint result = 0;

        nint sNamePtr = Marshal.StringToHGlobalUni("Monitor Sample Session");
        nint sDescPtr = Marshal.StringToHGlobalUni("Monitors traffic at the Stream layer.");
        nint sessionPtr = AllocSession(sNamePtr, sDescPtr, Fwpm.SessionFlagDynamic);

        try
        {
            Console.WriteLine("Opening Filtering Engine");
            result = NativeMethods.FwpmEngineOpen0(nint.Zero, Fwpm.RpcCAuthnWinnt, nint.Zero, sessionPtr, out engineHandle);

            if (result != 0) return result;
            Console.WriteLine("Successfully opened Filtering Engine");

            if (appPath != null)
            {
                Console.WriteLine("Looking up Application ID from BFE");
                result = NativeMethods.FwpmGetAppIdFromFileName0(appPath, out applicationId);
                if (result != 0) return result;
                Console.WriteLine("Successfully retrieved Application ID");
            }
            else
            {
                Console.WriteLine("No application path specified — monitoring all processes");
            }

            Console.WriteLine("Opening Monitor Sample Device");
            result = OpenMonitorDevice(out monitorDevice);
            if (result != 0) return result;
            Console.WriteLine("Successfully opened Monitor Device");

            Console.WriteLine("Adding Filters through the Filtering Engine");
            result = AddFilters(engineHandle, applicationId);
            if (result != 0) return result;
            Console.WriteLine("Successfully added Filters through the Filtering Engine");

            Console.WriteLine("Enabling monitoring through the Monitor Sample Device");
            var settings = new MonitorSettings
            {
                MonitorOperation = MonitorOperationMode.MonitorTraffic,
            };
            result = EnableMonitoring(monitorDevice, settings);
            if (result != 0) return result;
            Console.WriteLine("Successfully enabled monitoring.");

            quitEvent = NativeMethods.CreateEventW(nint.Zero, true, false, nint.Zero);
            if (quitEvent == nint.Zero)
            {
                result = (uint)Marshal.GetLastWin32Error();
                return result;
            }

            // Capture for the thread closure
            nint dev = monitorDevice;
            nint quit = quitEvent;
            eventThread = new Thread(() => EventThread(dev, quit))
            {
                IsBackground = true,
                Name = "MonitorEventThread",
            };
            eventThread.Start();

            Console.WriteLine("Monitoring... press any key to exit and cleanup filters.");
            Console.ReadKey(intercept: true);

            NativeMethods.SetEvent(quitEvent);
            NativeMethods.CancelIoEx(monitorDevice, nint.Zero);
            eventThread.Join();

            return 0;
        }
        finally
        {
            if (result != 0)
                Console.WriteLine($"Monitor.\tError 0x{result:x} occurred during execution");

            NativeMemory.Free((void*)sessionPtr);
            Marshal.FreeHGlobal(sNamePtr);
            Marshal.FreeHGlobal(sDescPtr);

            if (quitEvent != nint.Zero)
                NativeMethods.CloseHandle(quitEvent);

            if (monitorDevice != nint.Zero)
                CloseMonitorDevice(monitorDevice);

            if (applicationId != nint.Zero)
                NativeMethods.FwpmFreeMemory0(ref applicationId);

            if (engineHandle != nint.Zero)
                NativeMethods.FwpmEngineClose0(engineHandle);
        }
    }

    // -- Helpers ---------------------------------------------------------
    private static void Abort(nint engineHandle)
    {
        Console.WriteLine("Aborting Transaction");
        uint r = NativeMethods.FwpmTransactionAbort0(engineHandle);
        if (r == 0)
            Console.WriteLine("Successfully Aborted Transaction.");
    }
}

// -------------------------------------------------------------------------
//  Entry point
// -------------------------------------------------------------------------
internal static class Program
{
    private static void PrintUsage()
    {
        Console.WriteLine("Usage: monitor ( addcallouts | delcallouts | monitor [targetApp.exe] )");
    }

    private static int Main(string[] args)
    {
        uint result;

        if (args.Length == 1)
        {
            if (string.Equals(args[0], "addcallouts", StringComparison.OrdinalIgnoreCase))
            {
                result = MonitorApp.AddCallouts();
                return (int)result;
            }
            if (string.Equals(args[0], "delcallouts", StringComparison.OrdinalIgnoreCase))
            {
                result = MonitorApp.RemoveCallouts();
                return (int)result;
            }
            if (string.Equals(args[0], "monitor", StringComparison.OrdinalIgnoreCase))
            {
                result = MonitorApp.DoMonitoring(null);
                return (int)result;
            }
        }

        if (args.Length == 2)
        {
            if (string.Equals(args[0], "monitor", StringComparison.OrdinalIgnoreCase))
            {
                result = MonitorApp.DoMonitoring(args[1]);
                return (int)result;
            }
        }

        PrintUsage();
        return 87; // ERROR_INVALID_PARAMETER
    }
}