/*
 * C# port of mspyUser.c / mspyLog.c for MiniSpy.
 * Targets .NET 8 with Native AOT.
 * All Win32/filter-manager interop uses LibraryImport (source-generated P/Invoke).
 */

using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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

    // IRP_MJ codes
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

    // FltMgr pseudo-major codes
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

    // IRP_MJ_CREATE IoStatus.Information values
    public const ulong FileSuperseded  = 0; // New file created by superseding existing
    public const ulong FileOpened      = 1; // Existing file opened
    public const ulong FileCreated     = 2; // New file created
    public const ulong FileOverwritten = 3; // Existing file overwritten
    public const ulong FileExists      = 4; // File exists but was not opened (e.g. FILE_CREATE disposition)
    public const ulong FileDoesNotExist = 5; // File does not exist
}

// -------------------------------------------------------------------------
//  LOG_CONTEXT
// -------------------------------------------------------------------------
internal sealed class LogContext
{
    public nint Port = NativeMethods.InvalidHandleValue;
    public bool LogToScreen = false;
    public bool LogToFile = false;
    public StreamWriter? OutputFile = null;
    public bool NextLogToScreen = true;
    public volatile bool CleaningUp = false;
    public Semaphore? ShutDown = null;

    /// <summary>
    /// When non-null, only records whose file name starts with this prefix
    /// (case-insensitive, path-separator normalised) are reported.
    /// </summary>
    public volatile string? FolderFilter = null;

    /// <summary>
    /// When false (default), suppresses noisy fast-path operations such as
    /// IRP_MJ_NETWORK_QUERY_OPEN with NETWORK_QUERY_OPEN_DECLINED and
    /// IRP_MJ_ACQUIRE/RELEASE_FOR_SECTION_SYNCHRONIZATION.
    /// </summary>
    public volatile bool Verbose = false;

    /// <summary>
    /// When true, only IRP_MJ_CREATE operations where an existing file was
    /// opened (Information == FILE_OPENED == 1) are reported.  Creates of
    /// new files, supersedes, overwrites etc. are suppressed.
    /// </summary>
    public volatile bool OpenOnly = false;
}

// -------------------------------------------------------------------------
//  Unmanaged struct – must be blittable for AOT unsafe pointer cast
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
//  P/Invoke – LibraryImport (source-generated, AOT-safe)
// -------------------------------------------------------------------------
internal static partial class NativeMethods
{
    public static readonly nint InvalidHandleValue = -1;

    // fltlib.dll
    [LibraryImport("fltlib.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int FilterConnectCommunicationPort(
        string portName,
        uint options,
        nint context,
        ushort sizeOfContext,
        nint securityAttributes,
        out nint port);

    [LibraryImport("fltlib.dll")]
    public static partial int FilterSendMessage(
        nint port,
        nint inBuffer,
        uint inBufferSize,
        nint outBuffer,
        uint outBufferSize,
        out uint bytesReturned);

    [LibraryImport("fltlib.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int FilterAttach(
        string filterName,
        string volumeName,
        string? instanceName,
        uint createdInstanceNameLength,
        char[]? createdInstanceName);

    [LibraryImport("fltlib.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int FilterDetach(
        string filterName,
        string volumeName,
        string? instanceName);

    [LibraryImport("fltlib.dll")]
    public static partial int FilterVolumeFindFirst(
        uint informationClass,
        nint buffer,
        uint bufferSize,
        out uint bytesReturned,
        out nint volumeIterator);

    [LibraryImport("fltlib.dll")]
    public static partial int FilterVolumeFindNext(
        nint volumeIterator,
        uint informationClass,
        nint buffer,
        uint bufferSize,
        out uint bytesReturned);

    [LibraryImport("fltlib.dll")]
    public static partial int FilterVolumeFindClose(nint volumeIterator);

    [LibraryImport("fltlib.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int FilterVolumeInstanceFindFirst(
        string volumeName,
        uint informationClass,
        nint buffer,
        uint bufferSize,
        out uint bytesReturned,
        out nint instanceIterator);

    [LibraryImport("fltlib.dll")]
    public static partial int FilterVolumeInstanceFindNext(
        nint instanceIterator,
        uint informationClass,
        nint buffer,
        uint bufferSize,
        out uint bytesReturned);

    [LibraryImport("fltlib.dll")]
    public static partial int FilterVolumeInstanceFindClose(nint instanceIterator);

    [LibraryImport("fltlib.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int FilterGetDosName(
        string volumeName,
        char[] dosName,
        uint dosNameBufferSize);

    // kernel32.dll
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(nint handle);

    [LibraryImport("kernel32.dll", EntryPoint = "FormatMessageW",
        StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    public static partial uint FormatMessage(
        uint dwFlags,
        nint lpSource,
        uint dwMessageId,
        uint dwLanguageId,
        char[] lpBuffer,
        uint nSize,
        nint arguments);

    [LibraryImport("kernel32.dll", EntryPoint = "GetSystemDirectoryW",
        StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    public static partial uint GetSystemDirectory(
        char[] lpBuffer,
        uint uSize);

    [LibraryImport("kernel32.dll", EntryPoint = "LoadLibraryExW",
        StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    public static partial nint LoadLibraryEx(
        string lpFileName,
        nint hFile,
        uint dwFlags);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FreeLibrary(nint hModule);

    public const uint FORMAT_MESSAGE_FROM_SYSTEM = 0x00001000;
    public const uint FORMAT_MESSAGE_FROM_HMODULE = 0x00000800;
    public const uint LOAD_LIBRARY_AS_DATAFILE = 0x00000002;

    public static bool IsError(int hr) => hr < 0;
    public static bool Succeeded(int hr) => hr >= 0;
    public static int HResultFromWin32(int e) => e <= 0 ? e : (int)(((uint)e & 0x0000FFFF) | 0x80070000);

    public static readonly int ERROR_NO_MORE_ITEMS = HResultFromWin32(259);
    public static readonly int ERROR_INVALID_HANDLE_HR = HResultFromWin32(6);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryDosDeviceW",
        StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    public static partial uint QueryDosDevice(
        string? lpDeviceName,
        char[] lpTargetPath,
        uint ucchMax);

    [LibraryImport("ntdll.dll", EntryPoint = "RtlNtStatusToDosError")]
    public static partial uint RtlNtStatusToDosError(int status);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial nint OpenProcess(
        uint dwDesiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle,
        uint dwProcessId);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW",
        StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool QueryFullProcessImageName(
        nint hProcess,
        uint dwFlags,
        char[] lpExeName,
        ref uint lpdwSize);

    // Sufficient for QueryFullProcessImageName; works even for protected processes.
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
}

// -------------------------------------------------------------------------
//  Logging helpers
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
            default: return $"UNKNOWN_MJ (0x{major:X2})";
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
            return DateTime.FromFileTimeUtc(fileTime).ToLocalTime().ToString("HH:mm:ss.fff");
        }
        catch
        {
            return "time error";
        }
    }

    // -----------------------------------------------------------------------
    //  NormalizeFolderPrefix – resolves a user-supplied DOS path such as
    //  "C:\Foo\Bar" to its NT device form "\Device\HarddiskVolume3\Foo\Bar\"
    //  so it can be matched against the names the kernel reports.
    //  Falls back to the original path (with trailing backslash) if the drive
    //  letter cannot be resolved via QueryDosDevice.
    // -----------------------------------------------------------------------
    public static string NormalizeFolderPrefix(string path)
    {
        // Normalise separators first
        path = path.Replace('/', '\\').TrimEnd('\\');

        // Try to resolve a leading drive letter, e.g. "C:" -> "\Device\HarddiskVolume3"
        if (path.Length >= 2 && char.IsLetter(path[0]) && path[1] == ':')
        {
            string drive = path.Substring(0, 2); // "C:"
            char[] ntBuf = new char[1024];
            uint len = NativeMethods.QueryDosDevice(drive, ntBuf, (uint)ntBuf.Length);
            if (len > 2)
            {
                // QueryDosDevice returns double-null-terminated list; first entry is what we want
                string ntDevice = new string(ntBuf).Split('\0', StringSplitOptions.RemoveEmptyEntries)[0];
                string rest = path.Length > 2 ? path.Substring(2) : string.Empty; // "\Foo\Bar" or ""
                path = ntDevice + rest;
            }
        }

        return path + '\\';
    }

    // -----------------------------------------------------------------------
    //  IsMatchingFolder – returns true when name falls under folderPrefix.
    //  The kernel names look like "\Device\HarddiskVolume3\foo\bar.txt" or
    //  the DOS-device form "C:\foo\bar.txt".  We do a simple prefix match
    //  after normalising separators.
    // -----------------------------------------------------------------------
    private static bool IsMatchingFolder(string name, string folderPrefix) =>
        name.StartsWith(folderPrefix, StringComparison.OrdinalIgnoreCase);

    public static void ScreenDump(uint sequenceNumber, string name, ref RecordData data)
    {
        string irpMajor = GetIrpString(data.CallbackMajorId, data.CallbackMinorId, out string? irpMinor);
        string originTime = FormatTime(data.OriginatingTime);
        string complTime = data.CompletionTime == 0 ? "" : FormatTime(data.CompletionTime);
        string statusStr = GetNtStatusName(data.Status);
        string infoStr = GetInfoString(data.CallbackMajorId, data.Status, data.Information);
        string processName = GetProcessName(data.ProcessId);
        string dosPath = ResolveDosPath(name);

        var sb = new System.Text.StringBuilder();
        sb.Append($"Seq={sequenceNumber:X}");
        sb.Append($"\tPath={dosPath}");
        sb.Append($"\tPreOp={originTime}");
        sb.Append($"\tPID={data.ProcessId}:{processName}");
        sb.Append($"\tIRP={irpMajor}");
        if (!string.IsNullOrEmpty(irpMinor))
            sb.Append($"\tMinor={irpMinor}");
        sb.Append($"\tStatus={statusStr}");
        sb.Append($"\tInfo={infoStr}");
        if (!string.IsNullOrEmpty(complTime))
            sb.Append($"\tPostOp={complTime}");

        Console.WriteLine(sb.ToString());
    }

    public static void FileDump(uint sequenceNumber, string name, ref RecordData data, StreamWriter file)
    {
        string irpMajor = GetIrpString(data.CallbackMajorId, data.CallbackMinorId, out string? irpMinor);
        string originTime = FormatTime(data.OriginatingTime);
        string complTime = data.CompletionTime == 0 ? "" : FormatTime(data.CompletionTime);
        string dosPath = ResolveDosPath(name);

        string line = $"{sequenceNumber:X8}\t{dosPath}\t{originTime}\t{irpMajor}\t{irpMinor ?? ""}\t{(uint)data.Status:X8}\t{data.Information:X16}";
        if (!string.IsNullOrEmpty(complTime))
            line += $"\t{complTime}";

        file.WriteLine(line);
    }

    // -----------------------------------------------------------------------
    //  RetrieveLogRecords – AOT-safe: unsafe pointer cast replaces
    //  Marshal.PtrToStructure, sizeof(T) replaces Marshal.SizeOf<T>
    // -----------------------------------------------------------------------
    public static unsafe void RetrieveLogRecords(object? parameter)
    {
        LogContext context = (LogContext)parameter!;

        const int cmdSize = 8;
        // Layout: MINISPY_COMMAND(4) + Reserved(4)
        uint* cmdBuffer = (uint*)NativeMemory.Alloc(cmdSize);
        byte* outBuffer = (byte*)NativeMemory.Alloc(Constants.BufferSize);

        // Offsets within LOG_RECORD:
        //   0: Length(4), 4: SequenceNumber(4), 8: RecordType(4), 12: Reserved(4)
        //  16: RecordData, 16+sizeof(RecordData): Name (WCHAR[])
        const int recordDataOffset = 16;
        int nameBaseOffset = recordDataOffset + sizeof(RecordData);

        try
        {
            while (!context.CleaningUp)
            {
                cmdBuffer[0] = Constants.GetMiniSpyLog;
                cmdBuffer[1] = 0; // Reserved

                int hr = NativeMethods.FilterSendMessage(
                    context.Port,
                    (nint)cmdBuffer,
                    cmdSize,
                    (nint)outBuffer,
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
                byte* cursor = outBuffer;

                while (true)
                {
                    if (used + (uint)recordDataOffset > bytesReturned)
                        break;

                    uint* header = (uint*)cursor;
                    uint length     = header[0];
                    uint seqNum     = header[1];
                    uint recordType = header[2];

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

                    // AOT-safe: direct pointer cast instead of Marshal.PtrToStructure
                    RecordData data = Unsafe.ReadUnaligned<RecordData>(cursor + recordDataOffset);

                    // Read null-terminated UTF-16 name
                    string name = new string((char*)(cursor + nameBaseOffset));

                    if ((recordType & Constants.RecordTypeFiletag) != 0)
                    {
                        cursor += length;
                        continue;
                    }

                    // -----------------------------------------------------------
                    //  Folder filter: skip records that don't match the prefix.
                    //  Snapshot the volatile field once per record for consistency.
                    // -----------------------------------------------------------
                    string? folderFilter = context.FolderFilter;
                    if (folderFilter != null && !IsMatchingFolder(name, folderFilter))
                    {
                        cursor += length;
                        continue;
                    }

                    // -----------------------------------------------------------
                    //  Verbose filter: suppress high-frequency infrastructure ops
                    //  unless /v was specified.
                    // -----------------------------------------------------------
                    if (!context.Verbose && IsSuppressedWhenQuiet(data.CallbackMajorId, data.Status))
                    {
                        cursor += length;
                        continue;
                    }

                    // -----------------------------------------------------------
                    //  Open-only filter: when /o is active, report only
                    //  IRP_MJ_CREATE operations that resulted in an open handle
                    //  (superseded, opened, created, or overwritten).
                    // -----------------------------------------------------------
                    if (context.OpenOnly && !IsFileCreateOrOpen(data.CallbackMajorId, data.Status, data.Information))
                    {
                        cursor += length;
                        continue;
                    }

                    if (context.LogToScreen)
                        ScreenDump(seqNum, name, ref data);

                    if (context.LogToFile && context.OutputFile != null)
                        FileDump(seqNum, name, ref data, context.OutputFile);

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

                    cursor += length;
                }

                if (bytesReturned == 0)
                    Thread.Sleep(Constants.PollIntervalMs);
            }
        }
        finally
        {
            NativeMemory.Free(cmdBuffer);
            NativeMemory.Free(outBuffer);
        }

        Console.WriteLine("Log: Shutting down");
        context.ShutDown?.Release(1);
        Console.WriteLine("Log: All done");
    }

    // -----------------------------------------------------------------------
    //  GetNtStatusName – returns a short symbolic name for common NTSTATUS
    //  values, falling back to the raw hex representation.
    // -----------------------------------------------------------------------
    private static string GetNtStatusName(int status) => (uint)status switch
    {
        0x00000000 => "SUCCESS",
        0x00000001 => "PENDING",
        0x00000103 => "NOTIFY_ENUM_DIR",              // Directory change notification buffer filled
        0x40000000 => "OBJECT_NAME_EXISTS",            // Open succeeded; file already existed
        0x40000006 => "REPARSE",                       // Reparse point (junction/symlink) encountered
        0x80000005 => "BUFFER_OVERFLOW",
        0x80000006 => "NO_MORE_FILES",
        0xC0000001 => "UNSUCCESSFUL",
        0xC0000005 => "ACCESS_VIOLATION",
        0xC0000008 => "INVALID_HANDLE",
        0xC000000D => "INVALID_PARAMETER",
        0xC000000F => "NO_SUCH_FILE",
        0xC0000010 => "INVALID_DEVICE_REQUEST",
        0xC0000011 => "END_OF_FILE",
        0xC0000022 => "ACCESS_DENIED",
        0xC0000033 => "OBJECT_NAME_INVALID",
        0xC0000034 => "OBJECT_NAME_NOT_FOUND",
        0xC0000035 => "OBJECT_NAME_COLLISION",
        0xC0000039 => "OBJECT_PATH_INVALID",
        0xC000003A => "OBJECT_PATH_NOT_FOUND",
        0xC0000043 => "SHARING_VIOLATION",
        0xC0000056 => "DELETE_PENDING",
        0xC000007F => "DISK_FULL",
        0xC00000BA => "FILE_IS_A_DIRECTORY",
        0xC00000D0 => "OPLOCK_NOT_GRANTED",
        0xC0000101 => "DIRECTORY_NOT_EMPTY",
        0xC000010A => "DELETE_PENDING",
        0xC0000120 => "CANCELLED",
        0xC0000184 => "INVALID_DEVICE_STATE",
        0xC0000193 => "ACCOUNT_EXPIRED",
        0xC01C0004 => "NETWORK_QUERY_OPEN_DECLINED",   // Fast I/O declined; I/O manager retries via IRP_MJ_CREATE
        _ => $"{(uint)status:X8}"
    };

    // -----------------------------------------------------------------------
    //  IsSuppressedWhenQuiet – returns true for high-frequency infrastructure
    //  operations that add noise without actionable signal in normal use:
    //    • IRP_MJ_NETWORK_QUERY_OPEN declined (fast-path miss before every CREATE)
    //    • Section-synchronisation acquire/release (paging activity)
    //    • Mod-write acquire/release (cache manager flushing)
    //    • CC-flush acquire/release (cache manager flushing)
    // -----------------------------------------------------------------------
    private static bool IsSuppressedWhenQuiet(byte major, int status) =>
        (major == Constants.IrpMjNetworkQueryOpen  && (uint)status == 0xC01C0004) ||
        major == Constants.IrpMjAcquireForSectionSync ||
        major == Constants.IrpMjReleaseForSectionSync ||
        major == Constants.IrpMjAcquireForModWrite     ||
        major == Constants.IrpMjReleaseForModWrite     ||
        major == Constants.IrpMjAcquireForCcFlush      ||
        major == Constants.IrpMjReleaseForCcFlush;
    
    // -----------------------------------------------------------------------
    //  IsOpenOfExistingFile – returns true only when an IRP_MJ_CREATE
    //  completed with FILE_OPENED (an existing file was opened, not created).
    // -----------------------------------------------------------------------
    private static bool IsOpenOfExistingFile(byte major, int status, ulong information) =>
        major == Constants.IrpMjCreate &&
        (uint)status == 0x00000000 &&           // STATUS_SUCCESS
        information == Constants.FileOpened;    // FILE_OPENED == 1

    // -----------------------------------------------------------------------
    //  IsFileCreateOrOpen – returns true when an IRP_MJ_CREATE completed
    //  successfully with any of the actionable dispositions:
    //    FILE_SUPERSEDED (0) – existing file replaced
    //    FILE_OPENED     (1) – existing file opened
    //    FILE_CREATED    (2) – new file created
    //    FILE_OVERWRITTEN(3) – existing file opened and truncated
    //  FILE_EXISTS (4) and FILE_DOES_NOT_EXIST (5) are probes that did not
    //  result in an open handle and are excluded.
    // -----------------------------------------------------------------------
    private static bool IsFileCreateOrOpen(byte major, int status, ulong information) =>
        major == Constants.IrpMjCreate &&
        (uint)status == 0x00000000 &&       // STATUS_SUCCESS
        information <= Constants.FileOverwritten; // 0..3 all represent an open handle
    

    // -----------------------------------------------------------------------
    //  GetInfoString – translates the IoStatus.Information value to a human-
    //  readable string for the IRP types where it has a well-known meaning.
    //  Falls back to a 16-digit hex value for all other cases.
    // -----------------------------------------------------------------------
    private static string GetInfoString(byte major, int status, ulong information)
    {
        if (major == Constants.IrpMjCreate && (uint)status == 0x00000000)
        {
            return information switch
            {
                Constants.FileSuperseded   => "FILE_SUPERSEDED",
                Constants.FileOpened       => "FILE_OPENED",
                Constants.FileCreated      => "FILE_CREATED",
                Constants.FileOverwritten  => "FILE_OVERWRITTEN",
                Constants.FileExists       => "FILE_EXISTS",
                Constants.FileDoesNotExist => "FILE_DOES_NOT_EXIST",
                _                          => $"{information:X16}"
            };
        }

        if (major == Constants.IrpMjRead || major == Constants.IrpMjWrite)
            return $"{information} bytes";

        return $"{information:X16}";
    }

    // -----------------------------------------------------------------------
    //  PID → process name cache.
    //  Keyed by PID; null value means the name could not be resolved.
    //  PIDs are recycled by the OS but accurate enough for a monitoring session.
    // -----------------------------------------------------------------------
    private static readonly System.Collections.Generic.Dictionary<ulong, string?> s_pidCache = new();

    private static string GetProcessName(ulong pid)
    {
        if (!s_pidCache.TryGetValue(pid, out string? cached))
        {
            cached = ResolveProcessName(pid);
            s_pidCache[pid] = cached;
        }
        return cached ?? $"{pid}";
    }

    private static string ResolveProcessName(ulong pid)
    {
        if (pid == 0) return "Idle";
        if (pid == 4) return "System";

        nint handle = NativeMethods.OpenProcess(
            NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);

        if (handle == 0)
            return null!;

        try
        {
            char[] buf = new char[1024];
            uint size = (uint)buf.Length;
            if (!NativeMethods.QueryFullProcessImageName(handle, 0, buf, ref size))
                return null!;

            string full = new string(buf, 0, (int)size);
            int slash = full.LastIndexOf('\\');
            return slash >= 0 ? full.Substring(slash + 1) : full;
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }

    // -----------------------------------------------------------------------
    //  NT device path → DOS path cache.
    //  Built once on first use by enumerating all drive letters via
    //  QueryDosDevice.  Key = NT prefix (e.g. "\Device\HarddiskVolume3"),
    //  Value = DOS drive letter with colon (e.g. "C:").
    // -----------------------------------------------------------------------
    private static System.Collections.Generic.Dictionary<string, string>? s_driveMap;

    private static System.Collections.Generic.Dictionary<string, string> GetDriveMap()
    {
        if (s_driveMap != null)
            return s_driveMap;

        var map = new System.Collections.Generic.Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase);

        char[] target = new char[1024];
        for (char c = 'A'; c <= 'Z'; c++)
        {
            string drive = $"{c}:";
            uint len = NativeMethods.QueryDosDevice(drive, target, (uint)target.Length);
            if (len > 2)
            {
                // QueryDosDevice returns a double-null-terminated list; take the first entry.
                string ntDevice = new string(target).Split(
                    '\0', StringSplitOptions.RemoveEmptyEntries)[0];
                map[ntDevice] = drive;
            }
        }

        s_driveMap = map;
        return map;
    }

    // -----------------------------------------------------------------------
    //  ResolveDosPath – converts an NT kernel path such as
    //  "\Device\HarddiskVolume3\WatchedFolder\x" to its DOS equivalent
    //  "C:\WatchedFolder\x".  Falls back to the original string when no
    //  matching drive letter is found.
    // -----------------------------------------------------------------------
    private static string ResolveDosPath(string ntPath)
    {
        var map = GetDriveMap();
        foreach (var kvp in map)
        {
            if (ntPath.StartsWith(kvp.Key, StringComparison.OrdinalIgnoreCase))
                return kvp.Value + ntPath.Substring(kvp.Key.Length);
        }
        return ntPath;
    }
}

// -------------------------------------------------------------------------
//  Program
// -------------------------------------------------------------------------
internal sealed class Program
{
    private static unsafe void DisplayError(int code)
    {
        char[] buffer = new char[260];

        // If this looks like an NTSTATUS (facility != 0 and not a Win32 HRESULT),
        // convert to a Win32 error code first so FormatMessage can resolve it.
        // HRESULT Win32 errors have facility 0x7 (0x8007xxxx); pure NTSTATUS values do not.
        bool isNtStatus = (code & 0x0FFF0000) != 0x00070000 && (uint)code >= 0x80000000u;
        if (isNtStatus)
        {
            uint win32 = NativeMethods.RtlNtStatusToDosError(code);
            if (win32 != 0x13D) // ERROR_MR_MID_NOT_FOUND means no mapping exists
                code = NativeMethods.HResultFromWin32((int)win32);
        }

        fixed (char* pBuf = buffer)
        {
            uint count = NativeMethods.FormatMessage(
                NativeMethods.FORMAT_MESSAGE_FROM_SYSTEM,
                0,
                (uint)code,
                0,
                buffer,
                (uint)buffer.Length,
                0);

            if (count == 0)
            {
                char[] dirBuf = new char[260];
                uint dirLen = NativeMethods.GetSystemDirectory(dirBuf, (uint)dirBuf.Length);
                if (dirLen == 0 || dirLen > (uint)dirBuf.Length)
                {
                    Console.WriteLine($"    Could not translate error: {code}");
                    return;
                }

                string path = new string(dirBuf, 0, (int)dirLen) + @"\fltlib.dll";
                nint module = NativeMethods.LoadLibraryEx(path, 0, NativeMethods.LOAD_LIBRARY_AS_DATAFILE);

                count = NativeMethods.FormatMessage(
                    NativeMethods.FORMAT_MESSAGE_FROM_HMODULE,
                    module,
                    (uint)code,
                    0,
                    buffer,
                    (uint)buffer.Length,
                    0);

                if (module != 0)
                    NativeMethods.FreeLibrary(module);

                if (count == 0)
                {
                    Console.WriteLine($"    Could not translate error: {code}");
                    return;
                }
            }

            Console.WriteLine($"    {new string(buffer).TrimEnd('\0', '\r', '\n')}");
        }
    }

    private static unsafe uint IsAttachedToVolume(string volumeName)
    {
        const int bufSize = 1024;
        const int apiBufferSize = bufSize - 2;
        byte* buf = (byte*)NativeMemory.Alloc(bufSize);
        nint instanceIterator = NativeMethods.InvalidHandleValue;
        uint instanceCount = 0;

        try
        {
            int hr = NativeMethods.FilterVolumeInstanceFindFirst(
                volumeName, 1, (nint)buf, apiBufferSize, out _, out instanceIterator);

            if (NativeMethods.IsError(hr))
                return 0;

            do
            {
                // INSTANCE_FULL_INFORMATION offsets:
                // 12: FilterNameLength (USHORT), 14: FilterNameBufferOffset (USHORT)
                ushort filterNameLength       = *(ushort*)(buf + 12);
                ushort filterNameBufferOffset = *(ushort*)(buf + 14);

                if ((filterNameBufferOffset + filterNameLength + 2) > bufSize)
                {
                    hr = NativeMethods.FilterVolumeInstanceFindNext(
                        instanceIterator, 1, (nint)buf, apiBufferSize, out _);
                    continue;
                }

                string filterName = new string((char*)(buf + filterNameBufferOffset),
                    0, filterNameLength / sizeof(char));

                if (string.Equals(filterName, Constants.MiniSpyName, StringComparison.OrdinalIgnoreCase))
                    instanceCount++;

                hr = NativeMethods.FilterVolumeInstanceFindNext(
                    instanceIterator, 1, (nint)buf, apiBufferSize, out _);

            } while (NativeMethods.Succeeded(hr));
        }
        finally
        {
            if (instanceIterator != NativeMethods.InvalidHandleValue)
                NativeMethods.FilterVolumeInstanceFindClose(instanceIterator);
            NativeMemory.Free(buf);
        }

        return instanceCount;
    }

    private static unsafe void ListDevices()
    {
        const int bufSize = 1024;
        const int apiBufferSize = bufSize - 2;
        byte* buf = (byte*)NativeMemory.Alloc(bufSize);
        nint volumeIterator = NativeMethods.InvalidHandleValue;

        try
        {
            int hr = NativeMethods.FilterVolumeFindFirst(
                0, (nint)buf, apiBufferSize, out _, out volumeIterator);

            if (NativeMethods.IsError(hr))
                return;

            Console.WriteLine();
            Console.WriteLine("Dos Name        Volume Name                            Status ");
            Console.WriteLine("--------------  ------------------------------------  --------");

            do
            {
                // FILTER_VOLUME_BASIC_INFORMATION: 0=FilterVolumeNameLength(USHORT), 2=FilterVolumeName(WCHAR[])
                ushort nameLength = *(ushort*)buf;

                if ((2u + nameLength + 2u) > (uint)bufSize)
                {
                    Console.WriteLine($"Volume name length {nameLength} exceeds buffer; skipping.");
                    hr = NativeMethods.FilterVolumeFindNext(
                        volumeIterator, 0, (nint)buf, apiBufferSize, out _);
                    continue;
                }

                // Null-terminate in place
                *(char*)(buf + 2 + nameLength) = '\0';

                string volName = new string((char*)(buf + 2), 0, nameLength / sizeof(char));

                uint instanceCount = IsAttachedToVolume(volName);

                char[] dosNameBuf = new char[15];
                string dosStr = NativeMethods.Succeeded(
                    NativeMethods.FilterGetDosName(volName, dosNameBuf, (uint)dosNameBuf.Length))
                    ? new string(dosNameBuf).TrimEnd('\0')
                    : "";

                Console.Write($"{dosStr,-14}  {volName,-36}  {(instanceCount > 0 ? "Attached" : "")}");

                if (instanceCount > 1)
                    Console.WriteLine($" ({instanceCount})");
                else
                    Console.WriteLine();

                hr = NativeMethods.FilterVolumeFindNext(
                    volumeIterator, 0, (nint)buf, apiBufferSize, out _);

            } while (NativeMethods.Succeeded(hr));
        }
        finally
        {
            if (volumeIterator != NativeMethods.InvalidHandleValue)
                NativeMethods.FilterVolumeFindClose(volumeIterator);
            NativeMemory.Free(buf);
        }
    }

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

                            char[] instanceName = new char[Constants.InstanceNameMaxChars + 1];
                            int hr = NativeMethods.FilterAttach(
                                Constants.MiniSpyName,
                                volume,
                                null,
                                (uint)(instanceName.Length * sizeof(char)),
                                instanceName);

                            if (NativeMethods.Succeeded(hr))
                                Console.WriteLine($"    Instance name: {new string(instanceName).TrimEnd('\0')}");
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
                                Constants.MiniSpyName, volume, instanceString);

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
                        {
                            // /s      – toggle
                            // /s+     – force on
                            // /s-     – force off
                            if (parm.Length >= 3)
                            {
                                bool turnOn = parm[2] == '+';
                                context.NextLogToScreen = turnOn;
                                Console.WriteLine(turnOn
                                    ? "    Turning on logging to screen"
                                    : "    Turning off logging to screen");
                            }
                            else
                            {
                                Console.WriteLine(context.NextLogToScreen
                                    ? "    Turning off logging to screen"
                                    : "    Turning on logging to screen");
                                context.NextLogToScreen = !context.NextLogToScreen;
                            }
                            break;
                        }

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

                    case 'P':
                        {
                            // /p <folder>  – set or replace the folder filter
                            // /p           – clear the folder filter
                            bool hasArg = (parmIndex + 1 < argv.Length) &&
                                          argv[parmIndex + 1][0] != '/';

                            if (hasArg)
                            {
                                parmIndex++;
                                string normalized = MspyLog.NormalizeFolderPrefix(argv[parmIndex]);
                                context.FolderFilter = normalized;
                                Console.WriteLine($"    Folder filter set to: {normalized}");
                            }
                            else
                            {
                                context.FolderFilter = null;
                                Console.WriteLine("    Folder filter cleared – reporting all paths");
                            }
                            break;
                        }

                    case 'V':
                        context.Verbose = !context.Verbose;
                        Console.WriteLine(context.Verbose
                            ? "    Verbose mode on – all operations reported"
                            : "    Verbose mode off – infrastructure operations suppressed");
                        break;

                    case 'O':
                        context.OpenOnly = !context.OpenOnly;
                        Console.WriteLine(context.OpenOnly
                            ? "    Create/open mode on – reporting only successful IRP_MJ_CREATE (FILE_SUPERSEDED/OPENED/CREATED/OVERWRITTEN)"
                            : "    Create/open mode off – reporting all operations");
                        break;

                    default:
                        return PrintUsage();
                }
            }
            else
            {
                if (string.Equals(parm, Constants.InterpreterExitCommand1, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(parm, Constants.InterpreterExitCommand2, StringComparison.OrdinalIgnoreCase))
                    return Constants.ExitInterpreter;

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
            "Valid switches: [/a <drive>] [/d <drive>] [/l] [/s] [/f [<file name>]] [/p [<folder>]] [/v] [/o]\n" +
            "    [/a <drive>] starts monitoring <drive>\n" +
            "    [/d <drive> [<instance id>]] detaches filter <instance id> from <drive>\n" +
            "    [/l] lists all the drives the monitor is currently attached to\n" +
            "    [/s] turns on and off showing logging output on the screen\n" +
            "    [/f [<file name>]] turns on and off logging to the specified file\n" +
            "    [/p [<folder>]] restricts logging to <folder> and its subtree;\n" +
            "                    omit <folder> to clear the filter and report all paths\n" +
            "    [/v] toggles verbose mode; when off (default) suppresses\n" +
            "         IRP_MJ_NETWORK_QUERY_OPEN and section/cache-manager IRPs\n" +
            "    [/o] toggles create/open mode; when on, reports only successful\n" +
            "         IRP_MJ_CREATE operations that produced an open handle\n" +
            "         (FILE_SUPERSEDED, FILE_OPENED, FILE_CREATED, FILE_OVERWRITTEN)\n" +
            "  If you are in command mode:\n" +
            "    [enter] will enter command mode\n" +
            "    [go|g] will exit command mode\n" +
            "    [exit] will terminate this program");
        return Constants.UsageError;
    }

    static int Main(string[] args)
    {
        nint port = NativeMethods.InvalidHandleValue;
        LogContext context = new LogContext();

        try
        {
            Console.WriteLine("Connecting to filter's port...");

            int hResult = NativeMethods.FilterConnectCommunicationPort(
                Constants.MiniSpyPortName, 0, 0, 0, 0, out port);

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
            context.FolderFilter = null;

            if (args.Length > 0)
            {
                if (InterpretCommand(args, context) == Constants.UsageError)
                    return 1;
            }

            Console.WriteLine("Creating logging thread...");
            Thread loggingThread = new Thread(MspyLog.RetrieveLogRecords) { IsBackground = false };
            loggingThread.Start(context);

            ListDevices();

            Console.WriteLine("\nHit [Enter] to begin command mode...\n");
            Console.Out.Flush();

            context.LogToScreen = context.NextLogToScreen;

            bool exitProgram = false;
            while (!exitProgram)
            {
                int ch = Console.Read();
                if (ch < 0) break;
                if ((char)ch != '\n') continue;

                context.NextLogToScreen = context.LogToScreen;
                context.LogToScreen = false;

                int returnValue = Constants.Success;
                while (returnValue != Constants.ExitInterpreter)
                {
                    Console.Write(">");

                    string? line = Console.ReadLine();
                    if (line == null) { exitProgram = true; break; }

                    string trimmed = line.Trim();
                    if (trimmed.Length == 0) continue;

                    string[] parts = trimmed.Split(
                        ' ', Constants.NumParams, StringSplitOptions.RemoveEmptyEntries);

                    returnValue = InterpretCommand(parts, context);

                    if (returnValue == Constants.ExitProgram) { exitProgram = true; break; }
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