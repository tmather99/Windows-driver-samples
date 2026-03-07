/*++

Copyright (c) Microsoft Corporation. All rights reserved

Abstract:

    Stream monitor sample executable (.NET 8 / Native AOT)

Environment:

    User mode

--*/

using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;

// ──────────────────────────────────────────────────────────────
//  IOCTL codes  (mirror of ioctl.h)
// ──────────────────────────────────────────────────────────────
internal static class Ioctl
{
    // FILE_DEVICE_NETWORK = 0x12, METHOD_BUFFERED = 0, FILE_ANY_ACCESS = 0
    // CTL_CODE(t,f,m,a) = (t<<16)|(a<<14)|(f<<2)|m
    private static uint CtlCode(uint deviceType, uint func, uint method, uint access)
        => (deviceType << 16) | (access << 14) | (func << 2) | method;

    public static readonly uint MONITOR_IOCTL_ENABLE_MONITOR = CtlCode(0x12, 0x1, 0, 0);
    public static readonly uint MONITOR_IOCTL_DISABLE_MONITOR = CtlCode(0x12, 0x2, 0, 0);
    public static readonly uint MONITOR_IOCTL_DEQUEUE_EVENT = CtlCode(0x12, 0x3, 0, 0);

    public const string MONITOR_DOS_NAME = "\\\\.\\MonitorSample";
}

// ──────────────────────────────────────────────────────────────
//  Shared structs / enums  (mirror of ioctl.h)
// ──────────────────────────────────────────────────────────────
internal enum MONITOR_OPERATION_MODE : uint
{
    InvalidOperation = 0,
    MonitorTraffic = 1,
    MonitorOperationMax = 2,  // sentinel — mirrors monitorOperationMax in ioctl.h
}

[StructLayout(LayoutKind.Sequential)]
internal struct MONITOR_SETTINGS
{
    public MONITOR_OPERATION_MODE monitorOperation;
    public uint flags;
}

internal enum MONITOR_EVENT_TYPE : uint
{
    Invalid = 0,
    Connect = 1,  // monitorEventConnect
    Disconnect = 2,  // monitorEventDisconnect
}

// Native MONITOR_EVENT layout (x64):
//   offset  0: type            UINT32  (4)
//   offset  4: flags           UINT32  (4)
//   offset  8: ipProto         USHORT  (2)
//   offset 10: localPort       USHORT  (2)
//   offset 12: remotePort      USHORT  (2)
//   offset 14: <2 bytes pad>         — align ULONG to 4
//   offset 16: localAddressV4  ULONG   (4)
//   offset 20: remoteAddressV4 ULONG   (4)
//   total: 24 bytes
[StructLayout(LayoutKind.Explicit, Size = 24)]
internal struct MONITOR_EVENT
{
    [FieldOffset(0)] public MONITOR_EVENT_TYPE type;
    [FieldOffset(4)] public uint flags;
    [FieldOffset(8)] public ushort ipProto;
    [FieldOffset(10)] public ushort localPort;
    [FieldOffset(12)] public ushort remotePort;
    [FieldOffset(16)] public uint localAddressV4;
    [FieldOffset(20)] public uint remoteAddressV4;
}

// ──────────────────────────────────────────────────────────────
//  WFP structs (minimal subset used by this tool)
// ──────────────────────────────────────────────────────────────
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct FWPM_DISPLAY_DATA
{
    [MarshalAs(UnmanagedType.LPWStr)] public string? name;
    [MarshalAs(UnmanagedType.LPWStr)] public string? description;
}

// Blittable twin used inside FWPM_FILTER when the struct is passed via unsafe pointer.
// The runtime does not marshal embedded structs in unsafe contexts, so string fields
// must be raw wchar_t* pointers that we pin ourselves.
[StructLayout(LayoutKind.Sequential)]
internal struct FWPM_DISPLAY_DATA_NATIVE
{
    public IntPtr name;        // pinned wchar_t*
    public IntPtr description; // pinned wchar_t*
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct FWPM_SESSION
{
    public Guid sessionKey;
    public FWPM_DISPLAY_DATA displayData;
    public uint flags;
    public uint txnWaitTimeoutInMSec;
    public uint processId;
    public IntPtr sid;
    [MarshalAs(UnmanagedType.LPWStr)] public string? username;
    [MarshalAs(UnmanagedType.Bool)] public bool kernelMode;
}

[StructLayout(LayoutKind.Sequential)]
internal struct FWPM_CALLOUT
{
    public Guid calloutKey;
    public FWPM_DISPLAY_DATA displayData;
    public uint flags;
    public IntPtr providerKey;
    public FWP_BYTE_BLOB providerData;
    public Guid applicableLayer;
    public uint calloutId;
}

[StructLayout(LayoutKind.Sequential)]
internal struct FWPM_SUBLAYER
{
    public Guid subLayerKey;
    public FWPM_DISPLAY_DATA displayData;
    public uint flags;
    public IntPtr providerKey;
    public FWP_BYTE_BLOB providerData;
    public ushort weight;
}

[StructLayout(LayoutKind.Sequential)]
internal struct FWP_BYTE_BLOB
{
    public uint size;
    public IntPtr data;
}

// FWP_VALUE union — we only need the uint8 / byte-blob variants.
//
// Native x64 layout (fwptypes.h):
//   offset 0: type  (UINT32, 4 bytes)
//   offset 4: <4 bytes padding — union aligns to 8 because it contains a pointer>
//   offset 8: union { UINT8 uint8; ... FWP_BYTE_BLOB *byteBlob; ... }
//   total: 16 bytes
//
// The union members MUST be at offset 8, not 4.  Placing them at offset 4
// causes every filter condition to write its value 4 bytes early:
//   - uint8 (IPPROTO_TCP=6) lands where the driver reads padding -> driver sees 0
//     -> IP-protocol condition matches nothing -> no flows ever established
//   - byteBlob pointer lands at the wrong offset -> driver dereferences garbage
[StructLayout(LayoutKind.Explicit, Size = 16)]
internal struct FWP_VALUE
{
    [FieldOffset(0)] public uint type;       // FWP_DATA_TYPE
    [FieldOffset(8)] public byte uint8;
    [FieldOffset(8)] public IntPtr byteBlob;
}

[StructLayout(LayoutKind.Sequential)]
internal struct FWPM_FILTER_CONDITION
{
    public Guid fieldKey;
    public uint matchType;   // FWP_MATCH_TYPE
    public FWP_VALUE conditionValue;
}

// FWP_ACTION union
[StructLayout(LayoutKind.Explicit)]
internal struct FWPM_ACTION
{
    [FieldOffset(0)] public uint type;       // FWP_ACTION_TYPE
    [FieldOffset(4)] public Guid filterType;
    [FieldOffset(4)] public Guid calloutKey;
}

// FWP_VALUE used as weight — native size on x64 is 16:
// UINT32 type (4) + 4 pad + largest union member (pointer, 8) = 16
[StructLayout(LayoutKind.Explicit, Size = 16)]
internal struct FWP_VALUE0
{
    [FieldOffset(0)] public uint type;
}

[StructLayout(LayoutKind.Explicit)]
internal unsafe struct FWPM_FILTER
{
    [FieldOffset(0)] public Guid filterKey;
    [FieldOffset(16)] public FWPM_DISPLAY_DATA_NATIVE displayData;
    [FieldOffset(32)] public uint flags;
    // 4 bytes pad at 36 (providerKey is a pointer, aligns to 8)
    [FieldOffset(40)] public IntPtr providerKey;
    [FieldOffset(48)] public FWP_BYTE_BLOB providerData;
    [FieldOffset(64)] public Guid layerKey;
    [FieldOffset(80)] public Guid subLayerKey;
    [FieldOffset(96)] public FWP_VALUE0 weight;           // size=16
    [FieldOffset(112)] public uint numFilterConditions;
    // 4 bytes pad at 116 (filterCondition pointer aligns to 8)
    [FieldOffset(120)] public FWPM_FILTER_CONDITION* filterCondition;
    [FieldOffset(128)] public FWPM_ACTION action;          // size=20
    // 4 bytes pad at 148 (rawContext union aligns to 8)
    [FieldOffset(152)] public ulong rawContext;
    [FieldOffset(168)] public IntPtr reserved;
    [FieldOffset(176)] public ulong filterId;
    [FieldOffset(184)] public FWP_VALUE0 effectiveWeight;  // size=16
}

[StructLayout(LayoutKind.Sequential)]
internal struct OVERLAPPED
{
    public UIntPtr Internal;
    public UIntPtr InternalHigh;
    public uint OffsetLow;
    public uint OffsetHigh;
    public IntPtr hEvent;
}

// ──────────────────────────────────────────────────────────────
//  P/Invoke declarations
// ──────────────────────────────────────────────────────────────
[SupportedOSPlatform("windows")]
internal static partial class NativeMethods
{
    // ── kernel32 — LibraryImport (source-generated, AOT-safe) ──
    //
    // LibraryImport replaces DllImport for kernel32:
    //   - Marshaling stubs are source-generated at compile time (no runtime reflection)
    //   - Required for Native AOT; DllImport stubs cannot be generated at runtime in AOT
    //   - Marshal.GetLastPInvokeError() replaces GetLastError() P/Invoke
    //   - Environment.TickCount64 replaces GetTickCount64() P/Invoke

    [LibraryImport("kernel32", EntryPoint = "CreateFileW",
        SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial IntPtr CreateFileW(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition,
        uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [LibraryImport("kernel32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(IntPtr hObject);

    [LibraryImport("kernel32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static unsafe partial bool DeviceIoControl(
        IntPtr hDevice, uint dwIoControlCode,
        void* lpInBuffer, uint nInBufferSize,
        void* lpOutBuffer, uint nOutBufferSize,
        out uint lpBytesReturned,
        OVERLAPPED* lpOverlapped);

    [LibraryImport("kernel32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static unsafe partial bool GetOverlappedResult(
        IntPtr hFile, OVERLAPPED* lpOverlapped,
        out uint lpNumberOfBytesTransferred,
        [MarshalAs(UnmanagedType.Bool)] bool bWait);

    [LibraryImport("kernel32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static unsafe partial bool CancelIoEx(IntPtr hFile, OVERLAPPED* lpOverlapped);

    [LibraryImport("kernel32", SetLastError = true, EntryPoint = "CreateEventW")]
    public static partial IntPtr CreateEvent(
        IntPtr lpEventAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool bManualReset,
        [MarshalAs(UnmanagedType.Bool)] bool bInitialState,
        IntPtr lpName);

    [LibraryImport("kernel32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetEvent(IntPtr hEvent);

    [LibraryImport("kernel32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ResetEvent(IntPtr hEvent);

    [LibraryImport("kernel32")]
    public static partial uint WaitForMultipleObjects(
        uint nCount, IntPtr[] lpHandles,
        [MarshalAs(UnmanagedType.Bool)] bool bWaitAll,
        uint dwMilliseconds);

    [LibraryImport("kernel32")]
    public static partial uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    public const uint GENERIC_READ = 0x80000000u;
    public const uint GENERIC_WRITE = 0x40000000u;
    public const uint FILE_SHARE_READ = 0x00000001u;
    public const uint FILE_SHARE_WRITE = 0x00000002u;
    public const uint OPEN_EXISTING = 3;
    public const uint FILE_FLAG_OVERLAPPED = 0x40000000u;
    public static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);
    public const uint WAIT_OBJECT_0 = 0;
    public const uint INFINITE = 0xFFFFFFFF;
    public const uint WAIT_TIMEOUT = 0x00000102;
    public const uint ERROR_IO_PENDING = 997;
    public const uint ERROR_OPERATION_ABORTED = 995;

    // ── fwpuclnt — DllImport retained ──────────────────────────
    //
    // These functions accept structs that contain [MarshalAs(UnmanagedType.LPWStr)]
    // string fields (FWPM_SESSION, FWPM_CALLOUT, etc.).  LibraryImport source
    // generation cannot emit the required complex marshaling for those types today;
    // DllImport with runtime marshaling handles them correctly.

    [DllImport("fwpuclnt", SetLastError = false, EntryPoint = "FwpmEngineOpen0")]
    public static extern uint FwpmEngineOpen(
        IntPtr serverName, uint authnService,
        IntPtr authIdentity,
        ref FWPM_SESSION session,
        out IntPtr engineHandle);

    [DllImport("fwpuclnt", SetLastError = false, EntryPoint = "FwpmEngineClose0")]
    public static extern uint FwpmEngineClose(IntPtr engineHandle);

    [DllImport("fwpuclnt", SetLastError = false, EntryPoint = "FwpmTransactionBegin0")]
    public static extern uint FwpmTransactionBegin(IntPtr engineHandle, uint flags);

    [DllImport("fwpuclnt", SetLastError = false, EntryPoint = "FwpmTransactionCommit0")]
    public static extern uint FwpmTransactionCommit(IntPtr engineHandle);

    [DllImport("fwpuclnt", SetLastError = false, EntryPoint = "FwpmTransactionAbort0")]
    public static extern uint FwpmTransactionAbort(IntPtr engineHandle);

    [DllImport("fwpuclnt", SetLastError = false, EntryPoint = "FwpmCalloutAdd0")]
    public static extern uint FwpmCalloutAdd(
        IntPtr engineHandle, ref FWPM_CALLOUT callout,
        IntPtr sd, IntPtr id);

    [DllImport("fwpuclnt", SetLastError = false, EntryPoint = "FwpmCalloutDeleteByKey0")]
    public static extern uint FwpmCalloutDeleteByKey(
        IntPtr engineHandle, ref Guid calloutKey);

    [DllImport("fwpuclnt", SetLastError = false, EntryPoint = "FwpmSubLayerAdd0")]
    public static extern uint FwpmSubLayerAdd(
        IntPtr engineHandle, ref FWPM_SUBLAYER subLayer, IntPtr sd);

    [DllImport("fwpuclnt", SetLastError = false, EntryPoint = "FwpmFilterAdd0")]
    public static extern unsafe uint FwpmFilterAdd(
        IntPtr engineHandle, FWPM_FILTER* filter,
        IntPtr sd, ulong* id);

    [DllImport("fwpuclnt", SetLastError = false, EntryPoint = "FwpmGetAppIdFromFileName0")]
    public static extern uint FwpmGetAppIdFromFileName(
        [MarshalAs(UnmanagedType.LPWStr)] string fileName,
        out IntPtr appId);           // FWP_BYTE_BLOB**

    [DllImport("fwpuclnt", SetLastError = false, EntryPoint = "FwpmFreeMemory0")]
    public static extern void FwpmFreeMemory(ref IntPtr p);

    public const uint RPC_C_AUTHN_WINNT = 10;
    public const uint FWPM_SESSION_FLAG_DYNAMIC = 0x00000001;
    // FWP_ACTION_CALLOUT_INSPECTION = FWP_ACTION_FLAG_CALLOUT | FWP_ACTION_FLAG_NON_TERMINATING | 0x4
    public const uint FWP_ACTION_CALLOUT_INSPECTION = 0x00006004;
    public const uint FWP_MATCH_EQUAL = 0;
    public const uint FWP_EMPTY = 0;
    public const uint FWP_UINT8 = 1;
    public const uint FWP_BYTE_BLOB_TYPE = 12; // ordinal 12 in FWP_DATA_TYPE enum
    public const uint FWPM_CALLOUT_FLAG_PERSISTENT = 0x00010000;
    public const byte IPPROTO_TCP = 6;
    public const uint FWP_E_ALREADY_EXISTS = 0x80320009;
    // FWP_E_BUILTIN_OBJECT — object is built-in and cannot be deleted
    public const uint FWP_E_BUILTIN_OBJECT = 0x80320017;
    // FWP_E_INVALID_ENUMERATOR — returned by FwpmFilterAdd when action.type is invalid
    public const uint FWP_E_INVALID_ENUMERATOR = 0x8032001d;
}

// ──────────────────────────────────────────────────────────────
//  Well-known GUIDs  (mirror of mntrguid.h)
// ──────────────────────────────────────────────────────────────
internal static class MonitorGuids
{
    // b3241f1d-7cd2-4e7a-8721-2e97d07702e5
    public static readonly Guid MONITOR_SAMPLE_SUBLAYER =
        new Guid(0x9f1e2c3b, 0x4a7d, 0x4b2a, 0x9e, 0x13, 0xf3, 0x1a, 0x5c, 0x7d, 0x6b, 0x8e);

    // 3aaccbc0-2c29-455f-bb91-0e801c8994a4
    public static readonly Guid MONITOR_SAMPLE_FLOW_ESTABLISHED_CALLOUT_V4 =
        new Guid(0x5c4f2e01, 0x8b2c, 0x4d3f, 0x9a, 0xcb, 0x12, 0x34, 0x56, 0x78, 0x9a, 0xbc);

    // cea0131a-6ed3-4ed6-b40c-8a8fe8434b0a
    public static readonly Guid MONITOR_SAMPLE_STREAM_CALLOUT_V4 =
        new Guid(0x7d3a9f2b, 0x6c5d, 0x4b2e, 0x8f, 0x21, 0xde, 0xad, 0xbe, 0xef, 0x00, 0x11);

    // FWPM_LAYER_ALE_FLOW_ESTABLISHED_V4 = af80470a-5596-4c13-9992-539e6fe57967
    public static readonly Guid FWPM_LAYER_ALE_FLOW_ESTABLISHED_V4 =
        new Guid(0xaf80470a, 0x5596, 0x4c13, 0x99, 0x92, 0x53, 0x9e, 0x6f, 0xe5, 0x79, 0x67);

    // FWPM_LAYER_STREAM_V4 = 3b89653c-c170-49e4-b1cd-e0eeeee19a3e
    public static readonly Guid FWPM_LAYER_STREAM_V4 =
        new Guid(0x3b89653c, 0xc170, 0x49e4, 0xb1, 0xcd, 0xe0, 0xee, 0xee, 0xe1, 0x9a, 0x3e);

    // FWPM_CONDITION_IP_PROTOCOL  = 3971ef2b-623e-4f9a-8cb1-6e79b806b9a7
    public static readonly Guid FWPM_CONDITION_IP_PROTOCOL =
        new Guid(0x3971ef2b, 0x623e, 0x4f9a, 0x8c, 0xb1, 0x6e, 0x79, 0xb8, 0x06, 0xb9, 0xa7);

    // FWPM_CONDITION_ALE_APP_ID   = d78e1e87-8644-4ea5-9437-d809ecefc971
    public static readonly Guid FWPM_CONDITION_ALE_APP_ID =
        new Guid(0xd78e1e87, 0x8644, 0x4ea5, 0x94, 0x37, 0xd8, 0x09, 0xec, 0xef, 0xc9, 0x71);
}

// ──────────────────────────────────────────────────────────────
//  Helper formatting functions
// ──────────────────────────────────────────────────────────────
internal static class MonitorFormat
{
    public static string FormatIpv4(uint address)
    {
        byte b1 = (byte)(address >> 24);
        byte b2 = (byte)(address >> 16);
        byte b3 = (byte)(address >> 8);
        byte b4 = (byte)address;
        return $"{b1}.{b2}.{b3}.{b4}";
    }

    public static string FormatIpProto(ushort proto) => proto switch
    {
        1 => "ICMP",
        2 => "IGMP",
        6 => "TCP",
        17 => "UDP",
        41 => "IPv6",
        47 => "GRE",
        50 => "ESP",
        51 => "AH",
        58 => "ICMPv6",
        89 => "OSPF",
        132 => "SCTP",
        _ => proto.ToString(),
    };

    private static readonly (uint bit, string name)[] s_streamFlagBits =
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

        var sb = new StringBuilder();
        uint remaining = flags;

        foreach (var (bit, name) in s_streamFlagBits)
        {
            if ((flags & bit) != 0)
            {
                if (sb.Length > 0) sb.Append('|');
                sb.Append(name);
                remaining &= ~bit;
            }
        }

        // Append any unknown bits as raw hex so nothing is silently lost.
        if (remaining != 0)
        {
            if (sb.Length > 0) sb.Append('|');
            sb.Append($"0x{remaining:x}");
        }

        return sb.Length > 0 ? sb.ToString() : "0";
    }

    public static void PrintEvent(in MONITOR_EVENT evt)
    {
        string localAddr = FormatIpv4(evt.localAddressV4);
        string remoteAddr = FormatIpv4(evt.remoteAddressV4);
        string proto = FormatIpProto(evt.ipProto);

        if (evt.type == MONITOR_EVENT_TYPE.Connect)
        {
            Console.WriteLine($"[CONNECT]    Proto={proto} Local={localAddr}:{evt.localPort} Remote={remoteAddr}:{evt.remotePort}");
        }
        else if (evt.type == MONITOR_EVENT_TYPE.Disconnect)
        {
            string flagStr = FormatStreamFlags(evt.flags);
            Console.WriteLine($"[DISCONNECT] Proto={proto} Flags={flagStr} Local={localAddr}:{evt.localPort} Remote={remoteAddr}:{evt.remotePort}");
        }
    }
}

// ──────────────────────────────────────────────────────────────
//  WFP callout management
// ──────────────────────────────────────────────────────────────
[SupportedOSPlatform("windows")]
internal static class MonitorCallouts
{
    private const string FLOW_ESTABLISHED_CALLOUT_NAME = "Flow Established Callout";
    private const string FLOW_ESTABLISHED_CALLOUT_DESCRIPTION = "Monitor Sample - Flow Established Callout";
    private const string STREAM_CALLOUT_NAME = "Stream Callout";
    private const string STREAM_CALLOUT_DESCRIPTION = "Monitor Sample - Stream Callout";

    public static uint Add()
    {
        var session = new FWPM_SESSION
        {
            displayData = new FWPM_DISPLAY_DATA
            {
                name = "Monitor Sample Non-Dynamic Session",
                description = "For Adding callouts",
            }
        };

        Console.WriteLine("Opening Filtering Engine");
        uint result = NativeMethods.FwpmEngineOpen(IntPtr.Zero, NativeMethods.RPC_C_AUTHN_WINNT,
                                                   IntPtr.Zero, ref session, out IntPtr engine);
        if (result != 0) return result;

        bool inTransaction = false;
        try
        {
            Console.WriteLine("Starting Transaction for adding callouts");
            result = NativeMethods.FwpmTransactionBegin(engine, 0);
            if (result != 0) return result;
            inTransaction = true;
            Console.WriteLine("Successfully started the Transaction");

            var callout = new FWPM_CALLOUT
            {
                calloutKey = MonitorGuids.MONITOR_SAMPLE_FLOW_ESTABLISHED_CALLOUT_V4,
                displayData = new FWPM_DISPLAY_DATA { name = FLOW_ESTABLISHED_CALLOUT_NAME, description = FLOW_ESTABLISHED_CALLOUT_DESCRIPTION },
                applicableLayer = MonitorGuids.FWPM_LAYER_ALE_FLOW_ESTABLISHED_V4,
                flags = NativeMethods.FWPM_CALLOUT_FLAG_PERSISTENT,
            };

            Console.WriteLine("Adding Persistent Flow Established callout through the Filtering Engine");
            result = NativeMethods.FwpmCalloutAdd(engine, ref callout, IntPtr.Zero, IntPtr.Zero);
            if (result != 0) return result;
            Console.WriteLine("Successfully Added Persistent Flow Established callout.");

            callout = new FWPM_CALLOUT
            {
                calloutKey = MonitorGuids.MONITOR_SAMPLE_STREAM_CALLOUT_V4,
                displayData = new FWPM_DISPLAY_DATA { name = STREAM_CALLOUT_NAME, description = STREAM_CALLOUT_DESCRIPTION },
                applicableLayer = MonitorGuids.FWPM_LAYER_STREAM_V4,
                flags = NativeMethods.FWPM_CALLOUT_FLAG_PERSISTENT,
            };

            Console.WriteLine("Adding Persistent Stream callout through the Filtering Engine");
            result = NativeMethods.FwpmCalloutAdd(engine, ref callout, IntPtr.Zero, IntPtr.Zero);
            if (result != 0) return result;
            Console.WriteLine("Successfully Added Persistent Stream callout.");

            Console.WriteLine("Committing Transaction");
            result = NativeMethods.FwpmTransactionCommit(engine);
            inTransaction = false;
            if (result == 0) Console.WriteLine("Successfully Committed Transaction.");
            return result;
        }
        finally
        {
            if (inTransaction) Abort(engine);
            NativeMethods.FwpmEngineClose(engine);
        }
    }

    public static uint Remove()
    {
        var session = new FWPM_SESSION
        {
            displayData = new FWPM_DISPLAY_DATA
            {
                name = "Monitor Sample Non-Dynamic Session",
                description = "For Adding callouts",
            }
        };

        Console.WriteLine("Opening Filtering Engine");
        uint result = NativeMethods.FwpmEngineOpen(IntPtr.Zero, NativeMethods.RPC_C_AUTHN_WINNT,
                                                   IntPtr.Zero, ref session, out IntPtr engine);
        if (result != 0) return result;

        bool inTransaction = false;
        try
        {
            Console.WriteLine("Starting Transaction for Removing callouts");
            result = NativeMethods.FwpmTransactionBegin(engine, 0);
            if (result != 0) return result;
            inTransaction = true;
            Console.WriteLine("Successfully started the Transaction");

            Console.WriteLine("Deleting Flow Established callout");
            Guid feKey = MonitorGuids.MONITOR_SAMPLE_FLOW_ESTABLISHED_CALLOUT_V4;
            result = NativeMethods.FwpmCalloutDeleteByKey(engine, ref feKey);
            if (result != 0) return result;
            Console.WriteLine("Successfully Deleted Flow Established callout");

            Console.WriteLine("Deleting Stream callout");
            Guid scKey = MonitorGuids.MONITOR_SAMPLE_STREAM_CALLOUT_V4;
            result = NativeMethods.FwpmCalloutDeleteByKey(engine, ref scKey);
            if (result != 0) return result;
            Console.WriteLine("Successfully Deleted Stream callout");

            Console.WriteLine("Committing Transaction");
            result = NativeMethods.FwpmTransactionCommit(engine);
            inTransaction = false;
            if (result == 0) Console.WriteLine("Successfully Committed Transaction.");
            return result;
        }
        finally
        {
            if (inTransaction) Abort(engine);
            NativeMethods.FwpmEngineClose(engine);
        }
    }

    private static void Abort(IntPtr engine)
    {
        Console.WriteLine("Aborting Transaction");
        uint r = NativeMethods.FwpmTransactionAbort(engine);
        if (r == 0) Console.WriteLine("Successfully Aborted Transaction.");
    }
}

// ──────────────────────────────────────────────────────────────
//  WFP filter management
// ──────────────────────────────────────────────────────────────
[SupportedOSPlatform("windows")]
internal static class MonitorFilters
{
    public static unsafe uint Add(IntPtr engine, IntPtr appIdBlob)
    {
        var subLayer = new FWPM_SUBLAYER
        {
            subLayerKey = MonitorGuids.MONITOR_SAMPLE_SUBLAYER,
            displayData = new FWPM_DISPLAY_DATA
            {
                name = "Monitor Sample Sub layer",
                description = "Monitor Sample Sub layer",
            },
        };

        bool inTransaction = false;
        uint result;
        try
        {
            Console.WriteLine("Starting Transaction");
            result = NativeMethods.FwpmTransactionBegin(engine, 0);
            if (result != 0) return result;
            inTransaction = true;
            Console.WriteLine("Successfully Started Transaction");

            Console.WriteLine("Adding Sublayer");
            result = NativeMethods.FwpmSubLayerAdd(engine, ref subLayer, IntPtr.Zero);
            if (result == NativeMethods.FWP_E_ALREADY_EXISTS)
            {
                Console.WriteLine("Sublayer already exists, reusing it");
                result = 0;
            }
            if (result != 0) return result;
            Console.WriteLine("Sucessfully added Sublayer");

            // ── Flow Established filter ──────────────────────────
            var conditions = stackalloc FWPM_FILTER_CONDITION[2];
            conditions[0].fieldKey = MonitorGuids.FWPM_CONDITION_IP_PROTOCOL;
            conditions[0].matchType = NativeMethods.FWP_MATCH_EQUAL;
            conditions[0].conditionValue.type = NativeMethods.FWP_UINT8;
            conditions[0].conditionValue.uint8 = NativeMethods.IPPROTO_TCP;

            uint numConditions = 1;
            if (appIdBlob != IntPtr.Zero)
            {
                conditions[1].fieldKey = MonitorGuids.FWPM_CONDITION_ALE_APP_ID;
                conditions[1].matchType = NativeMethods.FWP_MATCH_EQUAL;
                conditions[1].conditionValue.type = NativeMethods.FWP_BYTE_BLOB_TYPE;
                conditions[1].conditionValue.byteBlob = appIdBlob;
                numConditions = 2;
            }

            // Pin display-name strings for the duration of each FwpmFilterAdd call.
            // FWPM_FILTER is passed as an unsafe pointer so the runtime does not
            // marshal embedded structs — we must provide raw wchar_t* ourselves.
            fixed (char* feNamePtr = "Flow established filter.",
                         feDescPtr = "Sets up flow for traffic that we are interested in.",
                         stNamePtr = "Stream Layer Filter",
                         stDescPtr = "Monitors TCP traffic.")
            {
                var filter = new FWPM_FILTER
                {
                    layerKey = MonitorGuids.FWPM_LAYER_ALE_FLOW_ESTABLISHED_V4,
                    displayData = new FWPM_DISPLAY_DATA_NATIVE
                    {
                        name = (IntPtr)feNamePtr,
                        description = (IntPtr)feDescPtr,
                    },
                    subLayerKey = MonitorGuids.MONITOR_SAMPLE_SUBLAYER,
                    numFilterConditions = numConditions,
                    filterCondition = conditions,
                };
                filter.action.type = NativeMethods.FWP_ACTION_CALLOUT_INSPECTION;
                filter.action.calloutKey = MonitorGuids.MONITOR_SAMPLE_FLOW_ESTABLISHED_CALLOUT_V4;
                filter.weight.type = NativeMethods.FWP_EMPTY;

                Console.WriteLine(appIdBlob != IntPtr.Zero
                    ? "Adding Flow Established Filter (app-scoped)"
                    : "Adding Flow Established Filter (all processes)");

                result = NativeMethods.FwpmFilterAdd(engine, &filter, IntPtr.Zero, null);
                if (result != 0)
                {
                    Console.Error.WriteLine($"FwpmFilterAdd (flow established) failed: 0x{result:x}");
                    if (result == NativeMethods.FWP_E_BUILTIN_OBJECT)
                        Console.Error.WriteLine(
                            "  FWP_E_BUILTIN_OBJECT: the kernel driver (msnmntr.sys) is not loaded " +
                            "or has not registered its callouts. " +
                            "Run 'addcallouts' first, then load the driver before running 'monitor'.");
                    return result;
                }
                Console.WriteLine("Successfully added Flow Established filter");

                // ── Stream filter ────────────────────────────────────
                filter = new FWPM_FILTER
                {
                    layerKey = MonitorGuids.FWPM_LAYER_STREAM_V4,
                    subLayerKey = MonitorGuids.MONITOR_SAMPLE_SUBLAYER,
                    displayData = new FWPM_DISPLAY_DATA_NATIVE
                    {
                        name = (IntPtr)stNamePtr,
                        description = (IntPtr)stDescPtr,
                    },
                    numFilterConditions = 0,
                    filterCondition = conditions,
                };
                filter.action.type = NativeMethods.FWP_ACTION_CALLOUT_INSPECTION;
                filter.action.calloutKey = MonitorGuids.MONITOR_SAMPLE_STREAM_CALLOUT_V4;
                filter.weight.type = NativeMethods.FWP_EMPTY;

                Console.WriteLine("Adding Stream Filter");
                result = NativeMethods.FwpmFilterAdd(engine, &filter, IntPtr.Zero, null);
                if (result != 0) return result;
                Console.WriteLine("Successfully added Stream filter");
            }

            Console.WriteLine("Committing Transaction");
            result = NativeMethods.FwpmTransactionCommit(engine);
            inTransaction = false;
            if (result == 0) Console.WriteLine("Successfully Committed Transaction");
            return result;
        }
        finally
        {
            if (inTransaction) Abort(engine);
        }
    }

    private static void Abort(IntPtr engine)
    {
        Console.WriteLine("Aborting Transaction");
        uint r = NativeMethods.FwpmTransactionAbort(engine);
        if (r == 0) Console.WriteLine("Successfully Aborted Transaction");
    }
}

// ──────────────────────────────────────────────────────────────
//  Event-dequeue thread context
//  Mirrors EVENT_THREAD_CONTEXT in monitor.cpp.
// ──────────────────────────────────────────────────────────────
internal struct EventThreadContext
{
    public IntPtr MonitorDevice;
    public IntPtr QuitEvent;
}

// ──────────────────────────────────────────────────────────────
//  Monitor operation
//  All app-level logic lives here, mirroring the flat set of
//  MonitorApp* free functions in monitor.cpp.
// ──────────────────────────────────────────────────────────────
[SupportedOSPlatform("windows")]
internal static class MonitorApp
{
    // ── Device helpers ──────────────────────────────────────
    private static uint MonitorAppOpenMonitorDevice(out IntPtr monitorDevice)
    {
        monitorDevice = NativeMethods.CreateFileW(
            Ioctl.MONITOR_DOS_NAME,
            NativeMethods.GENERIC_READ | NativeMethods.GENERIC_WRITE,
            NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE,
            IntPtr.Zero,
            NativeMethods.OPEN_EXISTING,
            NativeMethods.FILE_FLAG_OVERLAPPED,
            IntPtr.Zero);

        if (monitorDevice == NativeMethods.INVALID_HANDLE_VALUE)
        {
            monitorDevice = IntPtr.Zero;
            return (uint)Marshal.GetLastPInvokeError();
        }

        return 0;
    }

    private static bool MonitorAppCloseMonitorDevice(IntPtr monitorDevice)
        => NativeMethods.CloseHandle(monitorDevice);

    private static unsafe uint MonitorAppEnableMonitoring(
        IntPtr monitorDevice, MONITOR_SETTINGS monitorSettings)
    {
        if (!NativeMethods.DeviceIoControl(
                monitorDevice,
                Ioctl.MONITOR_IOCTL_ENABLE_MONITOR,
                &monitorSettings, (uint)sizeof(MONITOR_SETTINGS),
                null, 0,
                out _,
                null))
        {
            return (uint)Marshal.GetLastPInvokeError();
        }
        return 0;
    }

    private static unsafe uint MonitorAppDisableMonitoring(IntPtr monitorDevice)
    {
        if (!NativeMethods.DeviceIoControl(
                monitorDevice,
                Ioctl.MONITOR_IOCTL_DISABLE_MONITOR,
                null, 0, null, 0,
                out _,
                null))
        {
            return (uint)Marshal.GetLastPInvokeError();
        }
        return 0;
    }

    // ── Stats helpers ────────────────────────────────────────
    private static ulong MonitorAppGetTickMs() => (ulong)Environment.TickCount64;

    private static void MonitorAppPrintStats(
        ulong nowMs, ref ulong lastPrintMs, ref ulong lastReceived,
        ulong received, ulong asyncWaits, ulong canceled, ulong syncCompleted)
    {
        const ulong intervalMs = 1000;
        if (nowMs - lastPrintMs < intervalMs) return;

        ulong elapsedMs = nowMs - lastPrintMs;
        ulong delta = received - lastReceived;
        double rate = elapsedMs != 0 ? 1000.0 * delta / elapsedMs : 0.0;

        Console.WriteLine(
            $"[STATS] rate={rate:F1} ev/s received={received} asyncWaits={asyncWaits} canceled={canceled} syncCompleted={syncCompleted}");

        lastPrintMs = nowMs;
        lastReceived = received;
    }

    // ── Event thread ─────────────────────────────────────────
    private static unsafe void MonitorAppEventThread(object? parameter)
    {
        var context = (EventThreadContext)parameter!;

        ulong eventsReceived = 0;
        ulong asyncWaits = 0;
        ulong canceled = 0;
        ulong syncCompleted = 0;
        ulong lastPrintMs = MonitorAppGetTickMs();
        ulong lastReceived = 0;

        IntPtr ovEvent = NativeMethods.CreateEvent(IntPtr.Zero, true, false, IntPtr.Zero);
        if (ovEvent == IntPtr.Zero) return;

        OVERLAPPED ov = default;
        ov.hEvent = ovEvent;

        IntPtr[] waitHandles = [context.QuitEvent, ovEvent];

        for (; ; )
        {
            NativeMethods.ResetEvent(ovEvent);
            MONITOR_EVENT evt = default;
            uint bytesReturned = 0;

            bool ok = NativeMethods.DeviceIoControl(
                context.MonitorDevice,
                Ioctl.MONITOR_IOCTL_DEQUEUE_EVENT,
                null, 0,
                &evt, (uint)sizeof(MONITOR_EVENT),
                out bytesReturned,
                &ov);

            if (!ok)
            {
                uint err = (uint)Marshal.GetLastPInvokeError();

                if (err == NativeMethods.ERROR_IO_PENDING)
                {
                    // Queue was empty; kernel parked our request.  Block until an
                    // event arrives or shutdown is signaled.  INFINITE avoids the
                    // periodic cancel/re-issue cycle that a finite timeout causes
                    // (visible as spurious trace lines in the kernel log).
                    ++asyncWaits;

                    // Use a finite timeout so we can periodically print stats even when
                    // no events arrive.  Wake every 1s.
                    uint wait = NativeMethods.WaitForMultipleObjects(2, waitHandles, false, 1000);

                    if (wait == NativeMethods.WAIT_OBJECT_0)
                    {
                        // Shutdown: cancel the pending IOCTL and drain the completion.
                        NativeMethods.CancelIoEx(context.MonitorDevice, &ov);
                        NativeMethods.WaitForSingleObject(ovEvent, NativeMethods.INFINITE);
                        ++canceled;
                        break;
                    }
                    else if (wait == NativeMethods.WAIT_OBJECT_0 + 1)
                    {
                        // Event arrived and completed our pending IOCTL.
                        if (!NativeMethods.GetOverlappedResult(context.MonitorDevice, &ov, out bytesReturned, false))
                        {
                            err = (uint)Marshal.GetLastPInvokeError();
                            if (err == NativeMethods.ERROR_OPERATION_ABORTED)
                            {
                                ++canceled;
                                continue;
                            }
                            break;
                        }
                        // Fall through to process bytesReturned below.
                    }
                    else if (wait == NativeMethods.WAIT_TIMEOUT)
                    {
                        // Timeout: no event yet. Print periodic stats and continue waiting.
                        MonitorAppPrintStats(MonitorAppGetTickMs(), ref lastPrintMs, ref lastReceived,
                                             eventsReceived, asyncWaits, canceled, syncCompleted);
                        continue;
                    }
                     else
                     {
                         break;
                     }
                }
                else if (err == NativeMethods.ERROR_OPERATION_ABORTED)
                {
                    ++canceled;
                    if (NativeMethods.WaitForSingleObject(context.QuitEvent, 0) == NativeMethods.WAIT_OBJECT_0)
                        break;

                    continue;
                }
                else
                {
                    break;
                }
            }
            else
            {
                // IOCTL completed synchronously — queue had an event ready immediately.
                ++syncCompleted;
            }

            if (bytesReturned == (uint)sizeof(MONITOR_EVENT))
            {
                ++eventsReceived;
                MonitorFormat.PrintEvent(evt);
            }

            MonitorAppPrintStats(MonitorAppGetTickMs(), ref lastPrintMs, ref lastReceived,
                                 eventsReceived, asyncWaits, canceled, syncCompleted);
        }

        NativeMethods.CloseHandle(ovEvent);
    }

    // ── Main monitoring entry point ───────────────────────────
    public static uint DoMonitoring(string? appPath)
    {
        IntPtr engineHandle = IntPtr.Zero;
        IntPtr monitorDevice = IntPtr.Zero;
        IntPtr quitEvent = IntPtr.Zero;
        IntPtr applicationId = IntPtr.Zero;
        Thread? eventThread = null;
        uint result = 0;

        var session = new FWPM_SESSION
        {
            displayData = new FWPM_DISPLAY_DATA
            {
                name = "Monitor Sample Session",
                description = "Monitors traffic at the Stream layer.",
            },
            // Let the Base Filtering Engine clean up after us.
            flags = NativeMethods.FWPM_SESSION_FLAG_DYNAMIC,
        };

        try
        {
            Console.WriteLine("Opening Filtering Engine");
            result = NativeMethods.FwpmEngineOpen(IntPtr.Zero, NativeMethods.RPC_C_AUTHN_WINNT,
                                                  IntPtr.Zero, ref session, out engineHandle);
            if (result != 0) return result;
            Console.WriteLine("Successfully opened Filtering Engine");

            if (appPath is not null)
            {
                Console.WriteLine("Looking up Application ID from BFE");
                result = NativeMethods.FwpmGetAppIdFromFileName(appPath, out applicationId);
                if (result != 0) return result;
                Console.WriteLine("Successfully retrieved Application ID");
            }
            else
            {
                Console.WriteLine("No application path specified — monitoring all processes");
            }

            Console.WriteLine("Opening Monitor Sample Device");
            result = MonitorAppOpenMonitorDevice(out monitorDevice);
            if (result != 0) return result;
            Console.WriteLine("Successfully opened Monitor Device");

            Console.WriteLine("Adding Filters through the Filtering Engine");
            result = MonitorFilters.Add(engineHandle, applicationId);
            if (result != 0) return result;
            Console.WriteLine("Successfully added Filters through the Filtering Engine");

            Console.WriteLine("Enabling monitoring through the Monitor Sample Device");
            var monitorSettings = new MONITOR_SETTINGS
            {
                monitorOperation = MONITOR_OPERATION_MODE.MonitorTraffic,
            };
            result = MonitorAppEnableMonitoring(monitorDevice, monitorSettings);
            if (result != 0) return result;
            Console.WriteLine("Successfully enabled monitoring.");

            quitEvent = NativeMethods.CreateEvent(IntPtr.Zero, true, false, IntPtr.Zero);
            if (quitEvent == IntPtr.Zero)
            {
                result = (uint)Marshal.GetLastPInvokeError();
                return result;
            }

            var eventContext = new EventThreadContext
            {
                MonitorDevice = monitorDevice,
                QuitEvent = quitEvent,
            };
            eventThread = new Thread(MonitorAppEventThread);
            eventThread.Start(eventContext);

            Console.WriteLine("Monitoring... press any key to exit and cleanup filters.");

            // Console.ReadKey does not echo and does not require Enter.
            // If stdin is redirected (not a TTY), ReadKey throws InvalidOperationException;
            // fall back to sleeping until the process is signaled externally.
            try
            {
                Console.ReadKey(intercept: true);
            }
            catch (InvalidOperationException)
            {
                Thread.Sleep(Timeout.Infinite);
            }

            NativeMethods.SetEvent(quitEvent);
            unsafe { NativeMethods.CancelIoEx(monitorDevice, null); }
            eventThread.Join();
            eventThread = null;

            return result;
        }
        finally
        {
            if (result != 0)
                Console.WriteLine($"Monitor.\tError 0x{result:x} occurred during execution");

            if (quitEvent != IntPtr.Zero)
                NativeMethods.CloseHandle(quitEvent);

            if (monitorDevice != IntPtr.Zero)
            {
                uint disableErr = MonitorAppDisableMonitoring(monitorDevice);
                if (disableErr != 0)
                    Console.WriteLine($"DisableMonitoring.\tError 0x{disableErr:x} occurred during cleanup");

                MonitorAppCloseMonitorDevice(monitorDevice);
            }

            if (applicationId != IntPtr.Zero) NativeMethods.FwpmFreeMemory(ref applicationId);
            if (engineHandle != IntPtr.Zero) NativeMethods.FwpmEngineClose(engineHandle);
        }
    }
}

// ──────────────────────────────────────────────────────────────
//  Entry point
// ──────────────────────────────────────────────────────────────
internal static class Program
{
    static int Main(string[] args)
    {
        if (args.Length == 1)
        {
            if (string.Equals(args[0], "addcallouts", StringComparison.OrdinalIgnoreCase))
                return (int)MonitorCallouts.Add();

            if (string.Equals(args[0], "delcallouts", StringComparison.OrdinalIgnoreCase))
                return (int)MonitorCallouts.Remove();

            if (string.Equals(args[0], "monitor", StringComparison.OrdinalIgnoreCase))
                return (int)MonitorApp.DoMonitoring(null);
        }

        if (args.Length == 2 && string.Equals(args[0], "monitor", StringComparison.OrdinalIgnoreCase))
            return (int)MonitorApp.DoMonitoring(args[1]);

        Console.Error.WriteLine("Usage: monitor ( addcallouts | delcallouts | monitor [targetApp.exe] )");
        return unchecked((int)0x80070057u); // ERROR_INVALID_PARAMETER as HRESULT
    }
}