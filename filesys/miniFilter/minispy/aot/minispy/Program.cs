/*
 * C# port of mspyUser.c / mspyLog.c for MiniSpy.
 * Targets .NET 8.
 * All Win32/filter-manager interop is declared inline via P/Invoke.
 */

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace MiniSpy;

// -------------------------------------------------------------------------
//  Constants (from minispy.h / mspyLog.h)
// -------------------------------------------------------------------------
internal static class Constants
{
    public const string MiniSpyPortName = @"\MiniSpyPort";
    public const string MiniSpyName = "MiniSpy";

    public const int BufferSize = 4096;
    public const int RecordSize = 1024;
    public const int PollIntervalMs = 200;

    // MINISPY_COMMAND
    public const uint GetMiniSpyLog = 0;
    public const uint GetMiniSpyVersion = 1;

    // RECORD_TYPE flags
    public const uint RecordTypeNormal = 0x00000000;
    public const uint RecordTypeFiletag = 0x00000004;
    public const uint RecordTypeFlagStatic = 0x80000000;
    public const uint RecordTypeFlagExceedMemory = 0x20000000;
    public const uint RecordTypeFlagOutOfMemory = 0x10000000;
    public const uint RecordTypeFlagMask = 0xffff0000;

    // IRP_MJ codes (subset used for display)
    public const byte IrpMjCreate = 0x00;
    public const byte IrpMjCreateNamedPipe = 0x01;
    public const byte IrpMjClose = 0x02;
    public const byte IrpMjRead = 0x03;
    public const byte IrpMjWrite = 0x04;
    public const byte IrpMjQueryInformation = 0x05;
    public const byte IrpMjSetInformation = 0x06;
    public const byte IrpMjQueryEa = 0x07;
    public const byte IrpMjSetEa = 0x08;
    public const byte IrpMjFlushBuffers = 0x09;
    public const byte IrpMjQueryVolumeInformation = 0x0a;
    public const byte IrpMjSetVolumeInformation = 0x0b;
    public const byte IrpMjDirectoryControl = 0x0c;
    public const byte IrpMjFileSystemControl = 0x0d;
    public const byte IrpMjDeviceControl = 0x0e;
    public const byte IrpMjInternalDeviceControl = 0x0f;
    public const byte IrpMjShutdown = 0x10;
    public const byte IrpMjLockControl = 0x11;
    public const byte IrpMjCleanup = 0x12;
    public const byte IrpMjCreateMailslot = 0x13;
    public const byte IrpMjQuerySecurity = 0x14;
    public const byte IrpMjSetSecurity = 0x15;
    public const byte IrpMjPower = 0x16;
    public const byte IrpMjSystemControl = 0x17;
    public const byte IrpMjDeviceChange = 0x18;
    public const byte IrpMjQueryQuota = 0x19;
    public const byte IrpMjSetQuota = 0x1a;
    public const byte IrpMjPnp = 0x1b;

    // FltMgr pseudo-major codes (cast from negative UCHAR)
    public const byte IrpMjAcquireForSectionSync = 0xFF;
    public const byte IrpMjReleaseForSectionSync = 0xFE;
    public const byte IrpMjAcquireForModWrite = 0xFD;
    public const byte IrpMjReleaseForModWrite = 0xFC;
    public const byte IrpMjAcquireForCcFlush = 0xFB;
    public const byte IrpMjReleaseForCcFlush = 0xFA;
    public const byte IrpMjNotifyStreamFoCreation = 0xF9;
    public const byte IrpMjFastIoCheckIfPossible = 0xF3;
    public const byte IrpMjNetworkQueryOpen = 0xF2;
    public const byte IrpMjMdlRead = 0xF1;
    public const byte IrpMjMdlReadComplete = 0xF0;
    public const byte IrpMjPrepareMdlWrite = 0xEF;
    public const byte IrpMjMdlWriteComplete = 0xEE;
    public const byte IrpMjVolumeMount = 0xED;
    public const byte IrpMjVolumeDismount = 0xEC;
    public const byte IrpMjTransactionNotify = 0xD8;

    // IRP minor codes
    public const byte IrpMnNormal = 0x00;
    public const byte IrpMnDpc = 0x01;
    public const byte IrpMnMdl = 0x02;
    public const byte IrpMnComplete = 0x03;
    public const byte IrpMnCompressed = 0x04;
    public const byte IrpMnMdlDpc = 0x05;
    public const byte IrpMnCompleteMdl = 0x06;
    public const byte IrpMnCompleteMdlDpc = 0x07;

    public const byte IrpMnQueryDirectory = 0x01;
    public const byte IrpMnNotifyChangeDirectory = 0x02;

    public const byte IrpMnUserFsRequest = 0x00;
    public const byte IrpMnMountVolume = 0x01;
    public const byte IrpMnVerifyVolume = 0x02;
    public const byte IrpMnLoadFileSystem = 0x03;
    public const byte IrpMnTrackLink = 0x04;

    public const byte IrpMnScsiClass = 0x01;

    public const byte IrpMnLock = 0x01;
    public const byte IrpMnUnlockSingle = 0x02;
    public const byte IrpMnUnlockAll = 0x03;
    public const byte IrpMnUnlockAllByKey = 0x04;

    public const byte IrpMnWaitWake = 0x00;
    public const byte IrpMnPowerSequence = 0x01;
    public const byte IrpMnSetPower = 0x02;
    public const byte IrpMnQueryPower = 0x03;

    public const byte IrpMnQueryAllData = 0x00;
    public const byte IrpMnQuerySingleInstance = 0x01;
    public const byte IrpMnChangeSingleInstance = 0x02;
    public const byte IrpMnChangeSingleItem = 0x03;
    public const byte IrpMnEnableEvents = 0x04;
    public const byte IrpMnDisableEvents = 0x05;
    public const byte IrpMnEnableCollection = 0x06;
    public const byte IrpMnDisableCollection = 0x07;
    public const byte IrpMnReginfo = 0x08;
    public const byte IrpMnExecuteMethod = 0x09;

    public const byte IrpMnPnpStart = 0x00;

    // FLT_CALLBACK_DATA flags
    public const uint FltCallbackDataIrpOperation = 0x00000001;
    public const uint FltCallbackDataFastIoOperation = 0x00000002;
    public const uint FltCallbackDataFsFilterOp = 0x00000004;

    // IO_REPARSE_TAG_MOUNT_POINT
    public const uint IoReparseTagMountPoint = 0xA0000003;

    // InterpretCommand return values
    public const int Success = 0;
    public const int UsageError = 1;
    public const int ExitInterpreter = 2;
    public const int ExitProgram = 4;

    public const string InterpreterExitCommand1 = "go";
    public const string InterpreterExitCommand2 = "g";
    public const string ProgramExitCommand = "exit";

    public const int CmdlineSize = 256;
    public const int NumParams = 40;

    public const int InstanceNameMaxChars = 255;
}

// -------------------------------------------------------------------------
//  LOG_CONTEXT  (mirrors the C struct)
// -------------------------------------------------------------------------
internal sealed class LogContext
{
    public IntPtr Port = NativeMethods.InvalidHandleValue;
    public bool LogToScreen = false;
    public bool LogToFile = false;
    public StreamWriter? OutputFile = null;
    public bool NextLogToScreen = true;
    public volatile bool CleaningUp = false;
    public Semaphore? ShutDown = null;
}

// -------------------------------------------------------------------------
//  Unmanaged structs – laid out to match the kernel structures
// -------------------------------------------------------------------------
[StructLayout(LayoutKind.Sequential)]
internal struct RecordData
{
    public long OriginatingTime;
    public long CompletionTime;
    public ulong DeviceObject;
    public ulong FileObject;
    public ulong Transaction;
    public ulong ProcessId;
    public ulong ThreadId;
    public ulong Information;
    public int Status;
    public uint IrpFlags;
    public uint Flags;
    public byte CallbackMajorId;
    public byte CallbackMinorId;
    public byte Reserved0;
    public byte Reserved1;
    public ulong Arg1;
    public ulong Arg2;
    public ulong Arg3;
    public ulong Arg4;
    public ulong Arg5;
    public long Arg6;
    public uint EcpCount;
    public uint KnownEcpMask;
}

// -------------------------------------------------------------------------
//  P/Invoke declarations
// -------------------------------------------------------------------------
internal static class NativeMethods
{
    public static readonly IntPtr InvalidHandleValue = new IntPtr(-1);

    // fltlib.dll
    [DllImport("fltlib.dll", CharSet = CharSet.Unicode)]
    public static extern int FilterConnectCommunicationPort(
        [MarshalAs(UnmanagedType.LPWStr)] string portName,
        uint options,
        IntPtr context,
        ushort sizeOfContext,
        IntPtr securityAttributes,
        out IntPtr port);

    [DllImport("fltlib.dll", CharSet = CharSet.Unicode)]
    public static extern int FilterSendMessage(
        IntPtr port,
        IntPtr inBuffer,
        uint inBufferSize,
        IntPtr outBuffer,
        uint outBufferSize,
        out uint bytesReturned);

    [DllImport("fltlib.dll", CharSet = CharSet.Unicode)]
    public static extern int FilterAttach(
        [MarshalAs(UnmanagedType.LPWStr)] string filterName,
        [MarshalAs(UnmanagedType.LPWStr)] string volumeName,
        [MarshalAs(UnmanagedType.LPWStr)] string? instanceName,
        uint createdInstanceNameLength,
        [MarshalAs(UnmanagedType.LPWStr)] StringBuilder createdInstanceName);

    [DllImport("fltlib.dll", CharSet = CharSet.Unicode)]
    public static extern int FilterDetach(
        [MarshalAs(UnmanagedType.LPWStr)] string filterName,
        [MarshalAs(UnmanagedType.LPWStr)] string volumeName,
        [MarshalAs(UnmanagedType.LPWStr)] string? instanceName);

    [DllImport("fltlib.dll", CharSet = CharSet.Unicode)]
    public static extern int FilterVolumeFindFirst(
        uint informationClass,
        IntPtr buffer,
        uint bufferSize,
        out uint bytesReturned,
        out IntPtr volumeIterator);

    [DllImport("fltlib.dll", CharSet = CharSet.Unicode)]
    public static extern int FilterVolumeFindNext(
        IntPtr volumeIterator,
        uint informationClass,
        IntPtr buffer,
        uint bufferSize,
        out uint bytesReturned);

    [DllImport("fltlib.dll", CharSet = CharSet.Unicode)]
    public static extern int FilterVolumeFindClose(IntPtr volumeIterator);

    [DllImport("fltlib.dll", CharSet = CharSet.Unicode)]
    public static extern int FilterVolumeInstanceFindFirst(
        [MarshalAs(UnmanagedType.LPWStr)] string volumeName,
        uint informationClass,
        IntPtr buffer,
        uint bufferSize,
        out uint bytesReturned,
        out IntPtr instanceIterator);

    [DllImport("fltlib.dll", CharSet = CharSet.Unicode)]
    public static extern int FilterVolumeInstanceFindNext(
        IntPtr instanceIterator,
        uint informationClass,
        IntPtr buffer,
        uint bufferSize,
        out uint bytesReturned);

    [DllImport("fltlib.dll", CharSet = CharSet.Unicode)]
    public static extern int FilterVolumeInstanceFindClose(IntPtr instanceIterator);

    [DllImport("fltlib.dll", CharSet = CharSet.Unicode)]
    public static extern int FilterGetDosName(
        [MarshalAs(UnmanagedType.LPWStr)] string volumeName,
        [MarshalAs(UnmanagedType.LPWStr)] StringBuilder dosName,
        uint dosNameBufferSize);

    // kernel32.dll
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern uint FormatMessage(
        uint dwFlags,
        IntPtr lpSource,
        uint dwMessageId,
        uint dwLanguageId,
        StringBuilder lpBuffer,
        uint nSize,
        IntPtr arguments);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern uint GetSystemDirectory(
        StringBuilder lpBuffer,
        uint uSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr LoadLibraryEx(
        string lpFileName,
        IntPtr hFile,
        uint dwFlags);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool FreeLibrary(IntPtr hModule);

    public const uint FORMAT_MESSAGE_FROM_SYSTEM = 0x00001000;
    public const uint FORMAT_MESSAGE_FROM_HMODULE = 0x00000800;
    public const uint LOAD_LIBRARY_AS_DATAFILE = 0x00000002;

    // HRESULT helpers
    public static bool IsError(int hr) => hr < 0;
    public static bool Succeeded(int hr) => hr >= 0;
    public static int HResultFromWin32(int e) => e <= 0 ? e : (int)(((uint)e & 0x0000FFFF) | 0x80070000);

    public static readonly int ERROR_NO_MORE_ITEMS = HResultFromWin32(259);
    public static readonly int ERROR_INVALID_HANDLE_HR = HResultFromWin32(6);
}

// -------------------------------------------------------------------------
//  Logging / display helpers  (port of mspyLog.c)
// -------------------------------------------------------------------------
internal static class MspyLog
{
    private static string GetIrpString(byte major, byte minor, out string? minorString)
    {
        minorString = null;
        switch (major)
        {
            case Constants.IrpMjCreate: return "IRP_MJ_CREATE";
            case Constants.IrpMjCreateNamedPipe: return "IRP_MJ_CREATE_NAMED_PIPE";
            case Constants.IrpMjClose: return "IRP_MJ_CLOSE";
            case Constants.IrpMjRead:
                minorString = GetReadWriteMinor(minor);
                return "IRP_MJ_READ";
            case Constants.IrpMjWrite:
                minorString = GetReadWriteMinor(minor);
                return "IRP_MJ_WRITE";
            case Constants.IrpMjQueryInformation: return "IRP_MJ_QUERY_INFORMATION";
            case Constants.IrpMjSetInformation: return "IRP_MJ_SET_INFORMATION";
            case Constants.IrpMjQueryEa: return "IRP_MJ_QUERY_EA";
            case Constants.IrpMjSetEa: return "IRP_MJ_SET_EA";
            case Constants.IrpMjFlushBuffers: return "IRP_MJ_FLUSH_BUFFERS";
            case Constants.IrpMjQueryVolumeInformation: return "IRP_MJ_QUERY_VOLUME_INFORMATION";
            case Constants.IrpMjSetVolumeInformation: return "IRP_MJ_SET_VOLUME_INFORMATION";
            case Constants.IrpMjDirectoryControl:
                minorString = minor switch
                {
                    Constants.IrpMnQueryDirectory => "IRP_MN_QUERY_DIRECTORY",
                    Constants.IrpMnNotifyChangeDirectory => "IRP_MN_NOTIFY_CHANGE_DIRECTORY",
                    _ => $"Unknown Irp minor code ({minor})"
                };
                return "IRP_MJ_DIRECTORY_CONTROL";
            case Constants.IrpMjFileSystemControl:
                minorString = minor switch
                {
                    Constants.IrpMnUserFsRequest => "IRP_MN_USER_FS_REQUEST",
                    Constants.IrpMnMountVolume => "IRP_MN_MOUNT_VOLUME",
                    Constants.IrpMnVerifyVolume => "IRP_MN_VERIFY_VOLUME",
                    Constants.IrpMnLoadFileSystem => "IRP_MN_LOAD_FILE_SYSTEM",
                    Constants.IrpMnTrackLink => "IRP_MN_TRACK_LINK",
                    _ => $"Unknown Irp minor code ({minor})"
                };
                return "IRP_MJ_FILE_SYSTEM_CONTROL";
            case Constants.IrpMjDeviceControl:
                minorString = minor == Constants.IrpMnScsiClass
                    ? "IRP_MN_SCSI_CLASS"
                    : $"Unknown Irp minor code ({minor})";
                return "IRP_MJ_DEVICE_CONTROL";
            case Constants.IrpMjInternalDeviceControl: return "IRP_MJ_INTERNAL_DEVICE_CONTROL";
            case Constants.IrpMjShutdown: return "IRP_MJ_SHUTDOWN";
            case Constants.IrpMjLockControl:
                minorString = minor switch
                {
                    Constants.IrpMnLock => "IRP_MN_LOCK",
                    Constants.IrpMnUnlockSingle => "IRP_MN_UNLOCK_SINGLE",
                    Constants.IrpMnUnlockAll => "IRP_MN_UNLOCK_ALL",
                    Constants.IrpMnUnlockAllByKey => "IRP_MN_UNLOCK_ALL_BY_KEY",
                    _ => $"Unknown Irp minor code ({minor})"
                };
                return "IRP_MJ_LOCK_CONTROL";
            case Constants.IrpMjCleanup: return "IRP_MJ_CLEANUP";
            case Constants.IrpMjCreateMailslot: return "IRP_MJ_CREATE_MAILSLOT";
            case Constants.IrpMjQuerySecurity: return "IRP_MJ_QUERY_SECURITY";
            case Constants.IrpMjSetSecurity: return "IRP_MJ_SET_SECURITY";
            case Constants.IrpMjPower:
                minorString = minor switch
                {
                    Constants.IrpMnWaitWake => "IRP_MN_WAIT_WAKE",
                    Constants.IrpMnPowerSequence => "IRP_MN_POWER_SEQUENCE",
                    Constants.IrpMnSetPower => "IRP_MN_SET_POWER",
                    Constants.IrpMnQueryPower => "IRP_MN_QUERY_POWER",
                    _ => $"Unknown Irp minor code ({minor})"
                };
                return "IRP_MJ_POWER";
            case Constants.IrpMjSystemControl:
                minorString = minor switch
                {
                    Constants.IrpMnQueryAllData => "IRP_MN_QUERY_ALL_DATA",
                    Constants.IrpMnQuerySingleInstance => "IRP_MN_QUERY_SINGLE_INSTANCE",
                    Constants.IrpMnChangeSingleInstance => "IRP_MN_CHANGE_SINGLE_INSTANCE",
                    Constants.IrpMnChangeSingleItem => "IRP_MN_CHANGE_SINGLE_ITEM",
                    Constants.IrpMnEnableEvents => "IRP_MN_ENABLE_EVENTS",
                    Constants.IrpMnDisableEvents => "IRP_MN_DISABLE_EVENTS",
                    Constants.IrpMnEnableCollection => "IRP_MN_ENABLE_COLLECTION",
                    Constants.IrpMnDisableCollection => "IRP_MN_DISABLE_COLLECTION",
                    Constants.IrpMnReginfo => "IRP_MN_REGINFO",
                    Constants.IrpMnExecuteMethod => "IRP_MN_EXECUTE_METHOD",
                    _ => $"Unknown Irp minor code ({minor})"
                };
                return "IRP_MJ_SYSTEM_CONTROL";
            case Constants.IrpMjDeviceChange: return "IRP_MJ_DEVICE_CHANGE";
            case Constants.IrpMjQueryQuota: return "IRP_MJ_QUERY_QUOTA";
            case Constants.IrpMjSetQuota: return "IRP_MJ_SET_QUOTA";
            case Constants.IrpMjPnp: return "IRP_MJ_PNP";

            // FltMgr pseudo-major codes
            case Constants.IrpMjAcquireForSectionSync: return "IRP_MJ_ACQUIRE_FOR_SECTION_SYNCHRONIZATION";
            case Constants.IrpMjReleaseForSectionSync: return "IRP_MJ_RELEASE_FOR_SECTION_SYNCHRONIZATION";
            case Constants.IrpMjAcquireForModWrite: return "IRP_MJ_ACQUIRE_FOR_MOD_WRITE";
            case Constants.IrpMjReleaseForModWrite: return "IRP_MJ_RELEASE_FOR_MOD_WRITE";
            case Constants.IrpMjAcquireForCcFlush: return "IRP_MJ_ACQUIRE_FOR_CC_FLUSH";
            case Constants.IrpMjReleaseForCcFlush: return "IRP_MJ_RELEASE_FOR_CC_FLUSH";
            case Constants.IrpMjNotifyStreamFoCreation: return "IRP_MJ_NOTIFY_STREAM_FO_CREATION";
            case Constants.IrpMjFastIoCheckIfPossible: return "IRP_MJ_FAST_IO_CHECK_IF_POSSIBLE";
            case Constants.IrpMjNetworkQueryOpen: return "IRP_MJ_NETWORK_QUERY_OPEN";
            case Constants.IrpMjMdlRead: return "IRP_MJ_MDL_READ";
            case Constants.IrpMjMdlReadComplete: return "IRP_MJ_MDL_READ_COMPLETE";
            case Constants.IrpMjPrepareMdlWrite: return "IRP_MJ_PREPARE_MDL_WRITE";
            case Constants.IrpMjMdlWriteComplete: return "IRP_MJ_MDL_WRITE_COMPLETE";
            case Constants.IrpMjVolumeMount: return "IRP_MJ_VOLUME_MOUNT";
            case Constants.IrpMjVolumeDismount: return "IRP_MJ_VOLUME_DISMOUNT";
            case Constants.IrpMjTransactionNotify: return "IRP_MJ_TRANSACTION_NOTIFY";

            default:
                return $"UNKNOWN_MJ (0x{major:X2})";
        }
    }

    private static string GetReadWriteMinor(byte minor) => minor switch
    {
        Constants.IrpMnNormal => "IRP_MN_NORMAL",
        Constants.IrpMnDpc => "IRP_MN_DPC",
        Constants.IrpMnMdl => "IRP_MN_MDL",
        Constants.IrpMnComplete => "IRP_MN_COMPLETE",
        Constants.IrpMnCompressed => "IRP_MN_COMPRESSED",
        Constants.IrpMnMdlDpc => "IRP_MN_MDL_DPC",
        Constants.IrpMnCompleteMdl => "IRP_MN_COMPLETE_MDL",
        Constants.IrpMnCompleteMdlDpc => "IRP_MN_COMPLETE_MDL_DPC",
        _ => $"Unknown Irp minor code ({minor})"
    };

    private static string FormatTime(long fileTime)
    {
        try
        {
            DateTime dt = DateTime.FromFileTimeUtc(fileTime).ToLocalTime();
            return dt.ToString("HH:mm:ss.fff");
        }
        catch
        {
            return "time error";
        }
    }

    public static void ScreenDump(uint sequenceNumber, string name, ref RecordData data)
    {
        string irpMajor = GetIrpString(data.CallbackMajorId, data.CallbackMinorId, out string? irpMinor);

        string originTime = FormatTime(data.OriginatingTime);
        string complTime = data.CompletionTime == 0 ? "" : FormatTime(data.CompletionTime);

        Console.Write($"{sequenceNumber,8:X}: {name,-40} {originTime} {irpMajor,-10} Status={(uint)data.Status:X8} Info={data.Information:X16}");

        if (!string.IsNullOrEmpty(irpMinor))
            Console.Write($" ({irpMinor})");

        if (!string.IsNullOrEmpty(complTime))
            Console.Write($" Completed={complTime}");

        Console.WriteLine();
    }

    public static void FileDump(uint sequenceNumber, string name, ref RecordData data, StreamWriter file)
    {
        string irpMajor = GetIrpString(data.CallbackMajorId, data.CallbackMinorId, out string? irpMinor);

        string originTime = FormatTime(data.OriginatingTime);
        string complTime = data.CompletionTime == 0 ? "" : FormatTime(data.CompletionTime);

        string line = $"{sequenceNumber:X8}\t{name}\t{originTime}\t{irpMajor}\t{irpMinor ?? ""}\t{(uint)data.Status:X8}\t{data.Information:X16}";

        if (!string.IsNullOrEmpty(complTime))
            line += $"\t{complTime}";

        file.WriteLine(line);
    }

    // -----------------------------------------------------------------------
    //  RetrieveLogRecords  – runs on a background thread
    // -----------------------------------------------------------------------
    public static void RetrieveLogRecords(object? parameter)
    {
        LogContext context = (LogContext)parameter!;

        // Command message buffer: MINISPY_COMMAND (uint) + Reserved (uint) = 8 bytes
        const int cmdSize = 8;
        IntPtr cmdBuffer = Marshal.AllocHGlobal(cmdSize);
        IntPtr outBuffer = Marshal.AllocHGlobal(Constants.BufferSize);

        int recordDataOffset = 16; // offset of RECORD_DATA within LOG_RECORD
        int recordDataSize = Marshal.SizeOf<RecordData>();
        int nameBaseOffset = recordDataOffset + recordDataSize;

        try
        {
            while (!context.CleaningUp)
            {
                Marshal.WriteInt32(cmdBuffer, 0, (int)Constants.GetMiniSpyLog);
                Marshal.WriteInt32(cmdBuffer, 4, 0); // Reserved

                int hr = NativeMethods.FilterSendMessage(
                    context.Port,
                    cmdBuffer,
                    cmdSize,
                    outBuffer,
                    Constants.BufferSize,
                    out uint bytesReturned);

                if (NativeMethods.IsError(hr))
                {
                    if (hr == NativeMethods.ERROR_INVALID_HANDLE_HR)
                    {
                        Console.WriteLine("The kernel component of minispy has unloaded. Exiting");
                        Environment.Exit(0);
                    }
                    else
                    {
                        if (hr != NativeMethods.ERROR_NO_MORE_ITEMS)
                            Console.WriteLine($"UNEXPECTED ERROR received: {hr:X}");

                        Thread.Sleep(Constants.PollIntervalMs);
                    }
                    continue;
                }

                uint used = 0;
                IntPtr cursor = outBuffer;

                while (true)
                {
                    if (used + (uint)recordDataOffset > bytesReturned)
                        break;

                    uint length = (uint)Marshal.ReadInt32(cursor, 0);
                    uint seqNum = (uint)Marshal.ReadInt32(cursor, 4);
                    uint recordType = (uint)Marshal.ReadInt32(cursor, 8);

                    if (length < (uint)(nameBaseOffset + 2))
                    {
                        Console.WriteLine($"UNEXPECTED LOG_RECORD->Length: length={length} expected>={nameBaseOffset + 2}");
                        break;
                    }

                    used += length;

                    if (used > bytesReturned)
                    {
                        Console.WriteLine($"UNEXPECTED LOG_RECORD size: used={used} bytesReturned={bytesReturned}");
                        break;
                    }

                    RecordData data = Marshal.PtrToStructure<RecordData>(
                        IntPtr.Add(cursor, recordDataOffset));

                    string? name = Marshal.PtrToStringUni(IntPtr.Add(cursor, nameBaseOffset));

                    if ((recordType & Constants.RecordTypeFiletag) != 0)
                    {
                        cursor = IntPtr.Add(cursor, (int)length);
                        continue;
                    }

                    if (context.LogToScreen)
                        ScreenDump(seqNum, name ?? string.Empty, ref data);

                    if (context.LogToFile && context.OutputFile != null)
                        FileDump(seqNum, name ?? string.Empty, ref data, context.OutputFile);

                    if ((recordType & Constants.RecordTypeFlagOutOfMemory) != 0)
                    {
                        if (context.LogToScreen)
                            Console.WriteLine($"M:  {seqNum:X8} System Out of Memory");
                        if (context.LogToFile && context.OutputFile != null)
                            context.OutputFile.WriteLine($"M:\t0x{seqNum:X8}\tSystem Out of Memory");
                    }
                    else if ((recordType & Constants.RecordTypeFlagExceedMemory) != 0)
                    {
                        if (context.LogToScreen)
                            Console.WriteLine($"M:  {seqNum:X8} Exceeded Maximum Allowed Memory Buffers");
                        if (context.LogToFile && context.OutputFile != null)
                            context.OutputFile.WriteLine($"M:\t0x{seqNum:X8}\tExceeded Maximum Allowed Memory Buffers");
                    }

                    cursor = IntPtr.Add(cursor, (int)length);
                }

                if (bytesReturned == 0)
                    Thread.Sleep(Constants.PollIntervalMs);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(cmdBuffer);
            Marshal.FreeHGlobal(outBuffer);
        }

        Console.WriteLine("Log: Shutting down");
        context.ShutDown?.Release(1);
        Console.WriteLine("Log: All done");
    }
}

// -------------------------------------------------------------------------
//  Program  – port of mspyUser.c main() + helpers
// -------------------------------------------------------------------------
internal sealed class Program
{
    // -----------------------------------------------------------------------
    //  DisplayError
    // -----------------------------------------------------------------------
    private static void DisplayError(int code)
    {
        var buffer = new StringBuilder(260);
        uint count = NativeMethods.FormatMessage(
            NativeMethods.FORMAT_MESSAGE_FROM_SYSTEM,
            IntPtr.Zero,
            (uint)code,
            0,
            buffer,
            (uint)buffer.Capacity,
            IntPtr.Zero);

        if (count == 0)
        {
            var dir = new StringBuilder(260);
            uint dirLen = NativeMethods.GetSystemDirectory(dir, (uint)dir.Capacity);
            if (dirLen == 0 || dirLen > (uint)dir.Capacity)
            {
                Console.WriteLine($"    Could not translate error: {code}");
                return;
            }

            dir.Append(@"\fltlib.dll");

            IntPtr module = NativeMethods.LoadLibraryEx(dir.ToString(), IntPtr.Zero,
                NativeMethods.LOAD_LIBRARY_AS_DATAFILE);

            count = NativeMethods.FormatMessage(
                NativeMethods.FORMAT_MESSAGE_FROM_HMODULE,
                module,
                (uint)code,
                0,
                buffer,
                (uint)buffer.Capacity,
                IntPtr.Zero);

            if (module != IntPtr.Zero)
                NativeMethods.FreeLibrary(module);

            if (count == 0)
            {
                Console.WriteLine($"    Could not translate error: {code}");
                return;
            }
        }

        Console.WriteLine($"    {buffer}");
    }

    // -----------------------------------------------------------------------
    //  IsAttachedToVolume
    // -----------------------------------------------------------------------
    private static uint IsAttachedToVolume(string volumeName)
    {
        const int bufSize = 1024;
        const int apiBufferSize = bufSize - 2;
        IntPtr buf = Marshal.AllocHGlobal(bufSize);
        IntPtr instanceIterator = NativeMethods.InvalidHandleValue;
        uint instanceCount = 0;

        try
        {
            int hr = NativeMethods.FilterVolumeInstanceFindFirst(
                volumeName,
                1,
                buf,
                apiBufferSize,
                out _,
                out instanceIterator);

            if (NativeMethods.IsError(hr))
                return 0;

            do
            {
                ushort filterNameLength = (ushort)Marshal.ReadInt16(buf, 12);
                ushort filterNameBufferOffset = (ushort)Marshal.ReadInt16(buf, 14);

                if ((filterNameBufferOffset + filterNameLength + 2) > bufSize)
                {
                    hr = NativeMethods.FilterVolumeInstanceFindNext(
                        instanceIterator, 1, buf, apiBufferSize, out _);
                    continue;
                }

                string filterName = Marshal.PtrToStringUni(
                    IntPtr.Add(buf, filterNameBufferOffset),
                    filterNameLength / sizeof(char))!;

                if (string.Equals(filterName, Constants.MiniSpyName, StringComparison.OrdinalIgnoreCase))
                    instanceCount++;

                hr = NativeMethods.FilterVolumeInstanceFindNext(
                    instanceIterator, 1, buf, apiBufferSize, out _);

            } while (NativeMethods.Succeeded(hr));
        }
        finally
        {
            if (instanceIterator != NativeMethods.InvalidHandleValue)
                NativeMethods.FilterVolumeInstanceFindClose(instanceIterator);
            Marshal.FreeHGlobal(buf);
        }

        return instanceCount;
    }

    // -----------------------------------------------------------------------
    //  ListDevices
    // -----------------------------------------------------------------------
    private static void ListDevices()
    {
        const int bufSize = 1024;
        const int apiBufferSize = bufSize - 2;
        IntPtr buf = Marshal.AllocHGlobal(bufSize);
        IntPtr volumeIterator = NativeMethods.InvalidHandleValue;

        try
        {
            int hr = NativeMethods.FilterVolumeFindFirst(
                0,
                buf,
                apiBufferSize,
                out _,
                out volumeIterator);

            if (NativeMethods.IsError(hr))
                return;

            Console.WriteLine();
            Console.WriteLine("Dos Name        Volume Name                            Status ");
            Console.WriteLine("--------------  ------------------------------------  --------");

            do
            {
                ushort nameLength = (ushort)Marshal.ReadInt16(buf, 0);

                if ((2u + nameLength + 2u) > (uint)bufSize)
                {
                    Console.WriteLine($"Volume name length {nameLength} exceeds buffer; skipping.");
                    hr = NativeMethods.FilterVolumeFindNext(
                        volumeIterator, 0, buf, apiBufferSize, out _);
                    continue;
                }

                Marshal.WriteInt16(buf, 2 + nameLength, 0);

                string volName = Marshal.PtrToStringUni(
                    IntPtr.Add(buf, 2),
                    nameLength / sizeof(char))!;

                uint instanceCount = IsAttachedToVolume(volName);

                var dosName = new StringBuilder(15);
                string dosStr = NativeMethods.Succeeded(
                    NativeMethods.FilterGetDosName(volName, dosName, (uint)dosName.Capacity))
                    ? dosName.ToString()
                    : "";

                Console.Write($"{dosStr,-14}  {volName,-36}  {(instanceCount > 0 ? "Attached" : "")}");

                if (instanceCount > 1)
                    Console.WriteLine($" ({instanceCount})");
                else
                    Console.WriteLine();

                hr = NativeMethods.FilterVolumeFindNext(
                    volumeIterator, 0, buf, apiBufferSize, out _);

            } while (NativeMethods.Succeeded(hr));
        }
        finally
        {
            if (volumeIterator != NativeMethods.InvalidHandleValue)
                NativeMethods.FilterVolumeFindClose(volumeIterator);
            Marshal.FreeHGlobal(buf);
        }
    }

    // -----------------------------------------------------------------------
    //  InterpretCommand
    // -----------------------------------------------------------------------
    private static int InterpretCommand(string[] argv, LogContext context)
    {
        int returnValue = Constants.Success;

        for (int parmIndex = 0; parmIndex < argv.Length; parmIndex++)
        {
            string parm = argv[parmIndex];

            if (parm.Length > 0 && parm[0] == '/')
            {
                if (parm.Length < 2)
                    return PrintUsage();

                char sw = char.ToUpperInvariant(parm[1]);

                switch (sw)
                {
                    case 'A':
                        {
                            parmIndex++;
                            if (parmIndex >= argv.Length)
                                return PrintUsage();

                            string volume = argv[parmIndex];
                            Console.Write($"    Attaching to {volume}... ");

                            var instanceName = new StringBuilder(Constants.InstanceNameMaxChars + 1);
                            int hr = NativeMethods.FilterAttach(
                                Constants.MiniSpyName,
                                volume,
                                null,
                                (uint)((Constants.InstanceNameMaxChars + 1) * 2),
                                instanceName);

                            if (NativeMethods.Succeeded(hr))
                            {
                                Console.WriteLine($"    Instance name: {instanceName}");
                            }
                            else
                            {
                                Console.WriteLine($"\n    Could not attach to device: 0x{(uint)hr:X8}");
                                DisplayError(hr);
                            }
                            break;
                        }

                    case 'D':
                        {
                            parmIndex++;
                            if (parmIndex >= argv.Length)
                                return PrintUsage();

                            string volume = argv[parmIndex];
                            Console.WriteLine($"    Detaching from {volume}");

                            string? instanceString = null;
                            parmIndex++;

                            if (parmIndex < argv.Length)
                            {
                                if (argv[parmIndex][0] == '/')
                                    parmIndex--;
                                else
                                    instanceString = argv[parmIndex];
                            }

                            int hr = NativeMethods.FilterDetach(
                                Constants.MiniSpyName,
                                volume,
                                instanceString);

                            if (NativeMethods.IsError(hr))
                            {
                                Console.WriteLine($"    Could not detach from device: 0x{(uint)hr:X8}");
                                DisplayError(hr);
                            }
                            break;
                        }

                    case 'L':
                        ListDevices();
                        break;

                    case 'S':
                        Console.WriteLine(context.NextLogToScreen
                            ? "    Turning off logging to screen"
                            : "    Turning on logging to screen");
                        context.NextLogToScreen = !context.NextLogToScreen;
                        break;

                    case 'F':
                        {
                            if (context.LogToFile)
                            {
                                Console.WriteLine("    Stop logging to file ");
                                context.LogToFile = false;
                                context.OutputFile?.Close();
                                context.OutputFile = null;
                            }
                            else
                            {
                                parmIndex++;
                                if (parmIndex >= argv.Length)
                                    return PrintUsage();

                                string filePath = argv[parmIndex];
                                Console.WriteLine($"    Log to file {filePath}");
                                context.OutputFile = new StreamWriter(filePath, append: false);
                                context.LogToFile = true;
                            }
                            break;
                        }

                    default:
                        return PrintUsage();
                }
            }
            else
            {
                if (string.Equals(parm, Constants.InterpreterExitCommand1, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(parm, Constants.InterpreterExitCommand2, StringComparison.OrdinalIgnoreCase))
                {
                    return Constants.ExitInterpreter;
                }

                if (string.Equals(parm, Constants.ProgramExitCommand, StringComparison.OrdinalIgnoreCase))
                    return Constants.ExitProgram;

                return PrintUsage();
            }
        }

        return returnValue;
    }

    private static int PrintUsage()
    {
        Console.WriteLine(
            "Valid switches: [/a <drive>] [/d <drive>] [/l] [/s] [/f [<file name>]]\n" +
            "    [/a <drive>] starts monitoring <drive>\n" +
            "    [/d <drive> [<instance id>]] detaches filter <instance id> from <drive>\n" +
            "    [/l] lists all the drives the monitor is currently attached to\n" +
            "    [/s] turns on and off showing logging output on the screen\n" +
            "    [/f [<file name>]] turns on and off logging to the specified file\n" +
            "  If you are in command mode:\n" +
            "    [enter] will enter command mode\n" +
            "    [go|g] will exit command mode\n" +
            "    [exit] will terminate this program");
        return Constants.UsageError;
    }

    // -----------------------------------------------------------------------
    //  Main
    // -----------------------------------------------------------------------
    static int Main(string[] args)
    {
        IntPtr port = NativeMethods.InvalidHandleValue;
        LogContext context = new LogContext();

        try
        {
            Console.WriteLine("Connecting to filter's port...");

            int hResult = NativeMethods.FilterConnectCommunicationPort(
                Constants.MiniSpyPortName,
                0,
                IntPtr.Zero,
                0,
                IntPtr.Zero,
                out port);

            if (NativeMethods.IsError(hResult))
            {
                Console.WriteLine($"Could not connect to filter: 0x{(uint)hResult:X8}");
                DisplayError(hResult);
                return 1;
            }

            context.Port = port;
            context.ShutDown = new Semaphore(0, 1, "MiniSpy shut down");
            context.CleaningUp = false;
            context.LogToFile = false;
            context.LogToScreen = false;
            context.NextLogToScreen = true;
            context.OutputFile = null;

            if (args.Length > 0)
            {
                if (InterpretCommand(args, context) == Constants.UsageError)
                    return 1;
            }

            Console.WriteLine("Creating logging thread...");
            Thread loggingThread = new Thread(MspyLog.RetrieveLogRecords)
            {
                IsBackground = false
            };
            loggingThread.Start(context);

            ListDevices();

            Console.WriteLine("\nHit [Enter] to begin command mode...\n");
            Console.Out.Flush();

            context.LogToScreen = context.NextLogToScreen;

            bool exitProgram = false;
            while (!exitProgram)
            {
                int ch = Console.Read();
                if (ch < 0)
                    break;

                if ((char)ch != '\n')
                    continue;

                context.NextLogToScreen = context.LogToScreen;
                context.LogToScreen = false;

                int returnValue = Constants.Success;
                while (returnValue != Constants.ExitInterpreter)
                {
                    Console.Write(">");

                    string? line = Console.ReadLine();
                    if (line == null)
                    {
                        exitProgram = true;
                        break;
                    }

                    string trimmed = line.Trim();
                    if (trimmed.Length == 0)
                        continue;

                    string[] parts = trimmed.Split(
                        ' ',
                        Constants.NumParams,
                        StringSplitOptions.RemoveEmptyEntries);

                    returnValue = InterpretCommand(parts, context);

                    if (returnValue == Constants.ExitProgram)
                    {
                        exitProgram = true;
                        break;
                    }
                }

                context.LogToScreen = context.NextLogToScreen;

                if (context.LogToScreen)
                    Console.WriteLine("Should be logging to screen...");
            }

            Console.WriteLine("Cleaning up...");
            context.CleaningUp = true;
            context.ShutDown.WaitOne(Timeout.Infinite);

            if (context.LogToFile)
            {
                context.OutputFile?.Close();
                context.OutputFile = null;
            }
        }
        finally
        {
            context.ShutDown?.Dispose();

            if (port != NativeMethods.InvalidHandleValue)
                NativeMethods.CloseHandle(port);
        }

        return 0;
    }
}