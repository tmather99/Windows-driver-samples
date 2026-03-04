/*++

Copyright (c) Microsoft Corporation. All rights reserved

Abstract:

    Stream monitor sample executable

Environment:

    User mode
    
--*/

#include "windows.h"
#include "winioctl.h"
#include "strsafe.h"

#ifndef _CTYPE_DISABLE_MACROS
#define _CTYPE_DISABLE_MACROS
#endif

#include "fwpmu.h"

#include "winsock2.h"
#include "ws2def.h"
 
#include <conio.h>
#include <stdio.h>
#include <process.h>
#include <stdint.h>

#include "ioctl.h"
#define INITGUID
#include <guiddef.h>
#include "mntrguid.h"


#define MONITOR_FLOW_ESTABLISHED_CALLOUT_DESCRIPTION L"Monitor Sample - Flow Established Callout"
#define MONITOR_FLOW_ESTABLISHED_CALLOUT_NAME L"Flow Established Callout"   

#define MONITOR_STREAM_CALLOUT_DESCRIPTION L"Monitor Sample - Stream Callout"
#define MONITOR_STREAM_CALLOUT_NAME L"Stream Callout"

HANDLE quitEvent;

typedef struct _EVENT_THREAD_CONTEXT
{
   HANDLE monitorDevice;
   HANDLE quitEvent;
} EVENT_THREAD_CONTEXT;

static void
MonitorAppFormatIpv4(
   _In_ ULONG address,
   _Out_writes_(bufferCch) char* buffer,
   _In_ size_t bufferCch)
{
   unsigned char b1 = (unsigned char)(address >> 24);
   unsigned char b2 = (unsigned char)(address >> 16);
   unsigned char b3 = (unsigned char)(address >> 8);
   unsigned char b4 = (unsigned char)(address);

   StringCchPrintfA(buffer, bufferCch, "%u.%u.%u.%u", b1, b2, b3, b4);
}

static const char*
MonitorAppFormatIpProto(
   _In_ USHORT proto,
   _Out_writes_(bufferCch) char* buffer,
   _In_ size_t bufferCch)
{
   // Common IANA-assigned protocol numbers (RFC 5237 / IANA Protocol Numbers registry).
   switch (proto)
   {
      case 1:   return "ICMP";
      case 2:   return "IGMP";
      case 6:   return "TCP";
      case 17:  return "UDP";
      case 41:  return "IPv6";
      case 47:  return "GRE";
      case 50:  return "ESP";
      case 51:  return "AH";
      case 58:  return "ICMPv6";
      case 89:  return "OSPF";
      case 132: return "SCTP";
      default:
         StringCchPrintfA(buffer, bufferCch, "%hu", proto);
         return buffer;
   }
}

static void
MonitorAppFormatStreamFlags(
   _In_ UINT32 flags,
   _Out_writes_(bufferCch) char* buffer,
   _In_ size_t bufferCch)
{
   // FWPS_STREAM_FLAG_* bit definitions (from fwpsk.h)
   static const struct { UINT32 bit; const char* name; } k_bits[] =
   {
      { 0x00000001, "SEND"              },
      { 0x00000002, "RECV"              },
      { 0x00000004, "SEND_DISCONNECT"   },  // outbound FIN
      { 0x00000008, "RECV_DISCONNECT"   },  // inbound FIN
      { 0x00010000, "SEND_ABORT"        },  // outbound RST
      { 0x00020000, "RECV_ABORT"        },  // inbound RST
      { 0x00040000, "SEND_EXPEDITED"    },
      { 0x00080000, "RECV_EXPEDITED"    },
   };

   buffer[0] = '\0';
   UINT32 remaining = flags;

   for (size_t i = 0; i < ARRAYSIZE(k_bits); ++i)
   {
      if (flags & k_bits[i].bit)
      {
         if (buffer[0] != '\0')
         {
            StringCchCatA(buffer, bufferCch, "|");
         }
         StringCchCatA(buffer, bufferCch, k_bits[i].name);
         remaining &= ~k_bits[i].bit;
      }
   }

   // Append any unknown bits as raw hex so nothing is silently lost.
   if (remaining != 0)
   {
      char tmp[16];
      StringCchPrintfA(tmp, ARRAYSIZE(tmp), "%s0x%x", buffer[0] ? "|" : "", remaining);
      StringCchCatA(buffer, bufferCch, tmp);
   }

   if (buffer[0] == '\0')
   {
      StringCchCopyA(buffer, bufferCch, "0");
   }
}

static void
MonitorAppPrintEvent(
   _In_ const MONITOR_EVENT* evt)
{
   char localAddr[32];
   char remoteAddr[32];
   char protoBuf[8];
   const char* protoStr;

   MonitorAppFormatIpv4(evt->localAddressV4, localAddr, ARRAYSIZE(localAddr));
   MonitorAppFormatIpv4(evt->remoteAddressV4, remoteAddr, ARRAYSIZE(remoteAddr));
   protoStr = MonitorAppFormatIpProto(evt->ipProto, protoBuf, ARRAYSIZE(protoBuf));

   if (evt->type == monitorEventConnect)
   {
      printf("[CONNECT] Proto=%s Local=%s:%hu Remote=%s:%hu\n",
             protoStr,
             localAddr,
             evt->localPort,
             remoteAddr,
             evt->remotePort);
   }
   else if (evt->type == monitorEventDisconnect)
   {
      char flagStr[128];
      MonitorAppFormatStreamFlags(evt->flags, flagStr, ARRAYSIZE(flagStr));

      printf("[DISCONNECT] Proto=%s Flags=%s Local=%s:%hu Remote=%s:%hu\n",
             protoStr,
             flagStr,
             localAddr,
             evt->localPort,
             remoteAddr,
             evt->remotePort);
   }
}

static ULONGLONG
MonitorAppGetTickMs()
{
   return GetTickCount64();
}

static void
MonitorAppPrintStats(
   _In_ ULONGLONG nowMs,
   _Inout_ ULONGLONG* lastPrintMs,
   _Inout_ uint64_t* lastReceived,
   _In_ uint64_t received,
   _In_ uint64_t asyncWaits,
   _In_ uint64_t canceled,
   _In_ uint64_t syncCompleted)
{
   const ULONGLONG intervalMs = 1000;
   if (nowMs - *lastPrintMs < intervalMs)
   {
      return;
   }

   const ULONGLONG elapsedMs = nowMs - *lastPrintMs;
   const uint64_t delta = received - *lastReceived;
   const double rate = elapsedMs ? (1000.0 * (double)delta / (double)elapsedMs) : 0.0;

   // asyncWaits: times we blocked waiting for a kernel event (queue was empty)
   // canceled:   IOCTLs aborted (shutdown or spurious)
   // syncCompleted: IOCTLs that returned data without waiting (queue was non-empty)
   printf("[STATS] rate=%.1f ev/s received=%llu asyncWaits=%llu canceled=%llu syncCompleted=%llu\n",
          rate,
          (unsigned long long)received,
          (unsigned long long)asyncWaits,
          (unsigned long long)canceled,
          (unsigned long long)syncCompleted);

   *lastPrintMs = nowMs;
   *lastReceived = received;
}

static unsigned __stdcall
MonitorAppEventThread(
   _In_ void* parameter)
{
   EVENT_THREAD_CONTEXT* context = (EVENT_THREAD_CONTEXT*) parameter;
   OVERLAPPED ov;
   DWORD bytesReturned;
   MONITOR_EVENT evt;
   HANDLE waitHandles[2];

   // eventsReceived:  events successfully read from the driver queue
   // asyncWaits:      times we blocked in WaitForMultipleObjects (queue was empty ? STATUS_PENDING)
   // canceled:        IOCTLs that were aborted (shutdown CancelIoEx or spurious ERROR_OPERATION_ABORTED)
   // syncCompleted:   IOCTLs that returned data synchronously (queue was already non-empty)
   uint64_t eventsReceived = 0;
   uint64_t asyncWaits     = 0;
   uint64_t canceled       = 0;
   uint64_t syncCompleted  = 0;
   ULONGLONG lastPrintMs = MonitorAppGetTickMs();
   uint64_t lastReceived = 0;

   RtlZeroMemory(&ov, sizeof(ov));
   ov.hEvent = CreateEvent(NULL, TRUE, FALSE, NULL);
   if (!ov.hEvent)
   {
      return (unsigned) GetLastError();
   }

   waitHandles[0] = context->quitEvent;
   waitHandles[1] = ov.hEvent;

   for (;;)
   {
      ResetEvent(ov.hEvent);
      RtlZeroMemory(&evt, sizeof(evt));
      bytesReturned = 0;

      if (!DeviceIoControl(context->monitorDevice,
                           MONITOR_IOCTL_DEQUEUE_EVENT,
                           NULL,
                           0,
                           &evt,
                           sizeof(evt),
                           &bytesReturned,
                           &ov))
      {
         DWORD err = GetLastError();
         if (err == ERROR_IO_PENDING)
         {
            // Queue was empty; kernel parked our request. Block until an event
            // arrives or shutdown is signaled.
            ++asyncWaits;

            // Use a 1-second timeout so stats print even when no events arrive.
            DWORD wait = WaitForMultipleObjects(2, waitHandles, FALSE, 1000);
            if (wait == WAIT_OBJECT_0)
            {
               // Shutdown: cancel the pending IOCTL and drain the completion.
               CancelIoEx(context->monitorDevice, &ov);
               WaitForSingleObject(ov.hEvent, INFINITE);
               ++canceled;
               break;
            }
            else if (wait == WAIT_OBJECT_0 + 1)
            {
               // Event arrived and completed our pending IOCTL.
               if (!GetOverlappedResult(context->monitorDevice, &ov, &bytesReturned, FALSE))
               {
                  err = GetLastError();
                  if (err == ERROR_OPERATION_ABORTED)
                  {
                     ++canceled;
                     // Print stats before deciding whether to continue.
                     MonitorAppPrintStats(MonitorAppGetTickMs(), &lastPrintMs, &lastReceived,
                                         eventsReceived, asyncWaits, canceled, syncCompleted);
                     continue;
                  }
                  break;
               }
               // Fall through to process bytesReturned below.
            }
            else if (wait == WAIT_TIMEOUT)
            {
               // No event in the last second — print stats and keep waiting.
               MonitorAppPrintStats(MonitorAppGetTickMs(), &lastPrintMs, &lastReceived,
                                    eventsReceived, asyncWaits, canceled, syncCompleted);
               continue;
            }
            else
            {
               break;
            }
         }
         else if (err == ERROR_OPERATION_ABORTED)
         {
            ++canceled;
            if (WaitForSingleObject(context->quitEvent, 0) == WAIT_OBJECT_0)
            {
               break;
            }
            MonitorAppPrintStats(MonitorAppGetTickMs(), &lastPrintMs, &lastReceived,
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
         // IOCTL completed synchronously — queue had an event ready immediately.
         ++syncCompleted;
      }

      if (bytesReturned == sizeof(MONITOR_EVENT))
      {
         ++eventsReceived;
         MonitorAppPrintEvent(&evt);
      }

      MonitorAppPrintStats(MonitorAppGetTickMs(), &lastPrintMs, &lastReceived,
                           eventsReceived, asyncWaits, canceled, syncCompleted);
   }

   CloseHandle(ov.hEvent);
   return 0;
}

DWORD
MonitorAppOpenMonitorDevice(
   _Out_ HANDLE* monitorDevice)
/*++

Routine Description:

    Opens the Monitor Sample monitorDevice

Arguments:

    [out] HANDLE* monitorDevice

Return Value:

    NO_ERROR, ERROR_INVALID_PARAMETER or a CreateFile specific result.

--*/
{
    if (!monitorDevice)
    {
        return ERROR_INVALID_PARAMETER;
    }
    *monitorDevice = CreateFileW(MONITOR_DOS_NAME, 
                                 GENERIC_READ | GENERIC_WRITE, 
                                 FILE_SHARE_READ | FILE_SHARE_WRITE, 
                                 NULL, 
                                 OPEN_EXISTING, 
                                 FILE_FLAG_OVERLAPPED, 
                                 NULL);

    if (*monitorDevice == INVALID_HANDLE_VALUE)
    {
       return GetLastError();
    }

    return NO_ERROR;
}

BOOL MonitorAppCloseMonitorDevice(
   _In_ HANDLE monitorDevice)
/*++

Routine Description:

    Closes the Monitor Sample monitorDevice

Arguments:

Return Value:

    None.

--*/
{
    return CloseHandle(monitorDevice);
}

DWORD
MonitorAppAddCallouts()
/*++

Routine Description:

   Adds the callouts during installation

Arguments:
   
   [in]  PCWSTR AppPath - The path to the application to monitor.

Return Value:

    NO_ERROR or a specific FWP result.

--*/
{
   FWPM_CALLOUT callout;
   DWORD result;
   FWPM_DISPLAY_DATA displayData;
   HANDLE engineHandle = NULL;
   FWPM_SESSION session;
   RtlZeroMemory(&session, sizeof(FWPM_SESSION));

   session.displayData.name = L"Monitor Sample Non-Dynamic Session";
   session.displayData.description = L"For Adding callouts";

   printf("Opening Filtering Engine\n");
   result =  FwpmEngineOpen(
                            NULL,
                            RPC_C_AUTHN_WINNT,
                            NULL,
                            &session,
                            &engineHandle
                            );

   if (NO_ERROR != result)
   {
      goto cleanup;
   }

   printf("Starting Transaction for adding callouts\n");
   result = FwpmTransactionBegin(engineHandle, 0);
   if (NO_ERROR != result)
   {
      goto abort;
   }

   printf("Successfully started the Transaction\n");

   RtlZeroMemory(&callout, sizeof(FWPM_CALLOUT));
   displayData.description = MONITOR_FLOW_ESTABLISHED_CALLOUT_DESCRIPTION;
   displayData.name = MONITOR_FLOW_ESTABLISHED_CALLOUT_NAME;

   callout.calloutKey = MONITOR_SAMPLE_FLOW_ESTABLISHED_CALLOUT_V4;
   callout.displayData = displayData;
   callout.applicableLayer = FWPM_LAYER_ALE_FLOW_ESTABLISHED_V4;
   callout.flags = FWPM_CALLOUT_FLAG_PERSISTENT; // Make this a persistent callout.

   printf("Adding Persistent Flow Established callout through the Filtering Engine\n");

   result = FwpmCalloutAdd(engineHandle, &callout, NULL, NULL);
   if (NO_ERROR != result)
   {
      goto abort;
   }

   printf("Successfully Added Persistent Flow Established callout.\n");

   RtlZeroMemory(&callout, sizeof(FWPM_CALLOUT));

   displayData.description = MONITOR_STREAM_CALLOUT_DESCRIPTION;
   displayData.name = MONITOR_STREAM_CALLOUT_DESCRIPTION;

   callout.calloutKey = MONITOR_SAMPLE_STREAM_CALLOUT_V4;
   callout.displayData = displayData;
   callout.applicableLayer = FWPM_LAYER_STREAM_V4;
   callout.flags = FWPM_CALLOUT_FLAG_PERSISTENT; // Make this a persistent callout.

   printf("Adding Persistent Stream callout through the Filtering Engine\n");

   result = FwpmCalloutAdd(engineHandle, &callout, NULL, NULL);
   if (NO_ERROR != result)
   {
      goto abort;
   }

   printf("Successfully Added Persistent Stream callout.\n");
   
   printf("Committing Transaction\n");
   result = FwpmTransactionCommit(engineHandle);
   if (NO_ERROR == result)
   {
      printf("Successfully Committed Transaction.\n");
   }
   goto cleanup;

abort:
   printf("Aborting Transaction\n");
   result = FwpmTransactionAbort(engineHandle);
   if (NO_ERROR == result)
   {
      printf("Successfully Aborted Transaction.\n");
   }

cleanup:

   if (engineHandle)
   {
      FwpmEngineClose(engineHandle);
   }
   return result;
}

DWORD
MonitorAppRemoveCallouts()
/*++

Routine Description:

   Sets the kernel callout ID's through the Monitor Sample device

Arguments:
   
   [in] HANDLE monitorDevice - Monitor Sample device
   [in] CALLOUTS* callouts - Callout structure with ID's set
   [in] DWORD size - Size of the callout structure.

Return Value:

    NO_ERROR or a specific DeviceIoControl result.

--*/
{
   DWORD result;
   HANDLE engineHandle = NULL;
   FWPM_SESSION session;

   RtlZeroMemory(&session, sizeof(FWPM_SESSION));

   session.displayData.name = L"Monitor Sample Non-Dynamic Session";
   session.displayData.description = L"For Adding callouts";

   printf("Opening Filtering Engine\n");
   result =  FwpmEngineOpen(
                            NULL,
                            RPC_C_AUTHN_WINNT,
                            NULL,
                            &session,
                            &engineHandle
                            );

   if (NO_ERROR != result)
   {
      goto cleanup;
   }

   printf("Starting Transaction for Removing callouts\n");

   result = FwpmTransactionBegin(engineHandle, 0);
   if (NO_ERROR != result)
   {
      goto abort;
   }
   printf("Successfully started the Transaction\n");

   printf("Deleting Flow Established callout\n");
   result = FwpmCalloutDeleteByKey(engineHandle,
                                    &MONITOR_SAMPLE_FLOW_ESTABLISHED_CALLOUT_V4);
   if (NO_ERROR != result)
   {
      goto abort;
   }

   printf("Successfully Deleted Flow Established callout\n");

   printf("Deleting Stream callout\n");

   result = FwpmCalloutDeleteByKey(engineHandle,
                                    &MONITOR_SAMPLE_STREAM_CALLOUT_V4);
   if (NO_ERROR != result)
   {
      goto abort;
   }
   printf("Successfully Deleted Stream callout\n");

   printf("Committing Transaction\n");
   result = FwpmTransactionCommit(engineHandle);
   if (NO_ERROR == result)
   {
      printf("Successfully Committed Transaction.\n");
   }
   goto cleanup;
   
abort:
   printf("Aborting Transaction\n");
   result = FwpmTransactionAbort(engineHandle);
   if (NO_ERROR == result)
   {
      printf("Successfully Aborted Transaction.\n");
   }

cleanup:

   if (engineHandle)
   {
      FwpmEngineClose(engineHandle);
   }

   return result;
}

DWORD
MonitorAppEnableMonitoring(
   _In_    HANDLE            monitorDevice,
   _In_    MONITOR_SETTINGS* monitorSettings)
/*++

Routine Description:

   Enables monitoring on new connections.

Arguments:
   
   [in] HANDLE monitorDevice - Monitor Sample device
   [in] MONITOR_SETTINGS* monitorSettings - Settings for the Monitor Sample driver.

Return Value:

    NO_ERROR or a specific DeviceIoControl result.

--*/
{
   DWORD bytesReturned;
   
   if (!DeviceIoControl(monitorDevice,
                        MONITOR_IOCTL_ENABLE_MONITOR,
                        monitorSettings,
                        sizeof(MONITOR_SETTINGS),
                        NULL,
                        0,
                        &bytesReturned,
                        NULL))
   {
      return GetLastError();
   }

   return NO_ERROR;
}

DWORD
MonitorAppDisableMonitoring(
   _In_    HANDLE            monitorDevice)
/*++

Routine Description:

   Disables monitoring of new flows (existing flows will continue to be
   monitored until the driver is stopped or the flows end).

Arguments:

   [in] HANDLE monitorDevice - Monitor Sample device handle.
   
Return Value:

   NO_ERROR or DeviceIoControl specific code.

--*/
{
   DWORD bytesReturned;
   
   if (!DeviceIoControl(monitorDevice,
                        MONITOR_IOCTL_DISABLE_MONITOR,
                        NULL,
                        0,
                        NULL,
                        0,
                        &bytesReturned,
                        NULL))
   {
      return GetLastError();
   }

   return NO_ERROR;
}

DWORD
MonitorAppAddFilters(
   _In_     HANDLE         engineHandle,
   _In_opt_ FWP_BYTE_BLOB* applicationPath)
/*++

Routine Description:

    Adds the required sublayer, filters and callouts to the Windows
    Filtering Platform (WFP).

Arguments:
   
   [in] HANDLE engineHandle - Handle to the base Filtering engine
   [in] FWP_BYTE_BLOB* applicationPath - full path to the application including
                                         the NULL terminator and size also 
                                         including the NULL the terminator
   [in] CALLOUTS* callouts - The callouts that need to be added.

Return Value:

    NO_ERROR or a specific result

--*/
{
   DWORD result = NO_ERROR;
   FWPM_SUBLAYER monitorSubLayer;
   FWPM_FILTER filter;
   FWPM_FILTER_CONDITION filterConditions[2]; // We only need two for this call.

   RtlZeroMemory(&monitorSubLayer, sizeof(FWPM_SUBLAYER)); 

   monitorSubLayer.subLayerKey = MONITOR_SAMPLE_SUBLAYER;
   monitorSubLayer.displayData.name = L"Monitor Sample Sub layer";
   monitorSubLayer.displayData.description = L"Monitor Sample Sub layer";
   monitorSubLayer.flags = 0;
   // We don't really mind what the order of invocation is.
   monitorSubLayer.weight = 0;
   
   printf("Starting Transaction\n");

   result = FwpmTransactionBegin(engineHandle, 0);
   if (NO_ERROR != result)
   {
      goto abort;
   }
   printf("Successfully Started Transaction\n");

   printf("Adding Sublayer\n");

   result = FwpmSubLayerAdd(engineHandle, &monitorSubLayer, NULL);
   if (NO_ERROR != result)
   {
      goto abort;
   }
   
   printf("Sucessfully added Sublayer\n");
   
   RtlZeroMemory(&filter, sizeof(FWPM_FILTER));

   filter.layerKey = FWPM_LAYER_ALE_FLOW_ESTABLISHED_V4;
   filter.displayData.name = L"Flow established filter.";
   filter.displayData.description = L"Sets up flow for traffic that we are interested in.";
   filter.action.type = FWP_ACTION_CALLOUT_INSPECTION; // We're only doing inspection.
   filter.action.calloutKey = MONITOR_SAMPLE_FLOW_ESTABLISHED_CALLOUT_V4;
   filter.filterCondition = filterConditions;
   filter.subLayerKey = monitorSubLayer.subLayerKey;
   filter.weight.type = FWP_EMPTY; // auto-weight.

   RtlZeroMemory(filterConditions, sizeof(filterConditions));

   //
   // For the purposes of this sample, we will monitor TCP traffic only.
   //
   filterConditions[0].fieldKey = FWPM_CONDITION_IP_PROTOCOL;
   filterConditions[0].matchType = FWP_MATCH_EQUAL;
   filterConditions[0].conditionValue.type = FWP_UINT8;
   filterConditions[0].conditionValue.uint8 = IPPROTO_TCP;

   if (applicationPath)
   {
      //
      // Add the application path to the filter conditions.
      //
      filterConditions[1].fieldKey = FWPM_CONDITION_ALE_APP_ID;
      filterConditions[1].matchType = FWP_MATCH_EQUAL;
      filterConditions[1].conditionValue.type = FWP_BYTE_BLOB_TYPE;
      filterConditions[1].conditionValue.byteBlob = applicationPath;
      filter.numFilterConditions = 2;
   }
   else
   {
      // No app filter — match TCP traffic from any process.
      filter.numFilterConditions = 1;
   }

   printf("Adding Flow Established Filter%s\n",
          applicationPath ? " (app-scoped)" : " (all processes)");

   result = FwpmFilterAdd(engineHandle,
                       &filter,
                       NULL,
                       NULL);

   if (NO_ERROR != result)
   {
      goto abort;
   }

   printf("Successfully added Flow Established filter\n");
  
   RtlZeroMemory(&filter, sizeof(FWPM_FILTER));

   filter.layerKey = FWPM_LAYER_STREAM_V4;
   filter.action.type = FWP_ACTION_CALLOUT_INSPECTION; // We're only doing inspection.
   filter.action.calloutKey = MONITOR_SAMPLE_STREAM_CALLOUT_V4;
   filter.subLayerKey = monitorSubLayer.subLayerKey;
   filter.weight.type = FWP_EMPTY; // auto-weight.
   
   filter.numFilterConditions = 0;
   
   RtlZeroMemory(filterConditions, sizeof(filterConditions));

   filter.filterCondition = filterConditions;
   
   filter.displayData.name = L"Stream Layer Filter";
   filter.displayData.description = L"Monitors TCP traffic.";

   printf("Adding Stream Filter\n");

   result = FwpmFilterAdd(engineHandle,
                       &filter,
                       NULL,
                       NULL);

   if (NO_ERROR != result)
   {
      goto abort;
   }

   printf("Successfully added Stream filter\n");

   printf("Committing Transaction\n");
   result = FwpmTransactionCommit(engineHandle);
   if (NO_ERROR == result)
   {
      printf("Successfully Committed Transaction\n");
   }
   goto cleanup;

abort:
   printf("Aborting Transaction\n");
   result = FwpmTransactionAbort(engineHandle);
   if (NO_ERROR == result)
   {
      printf("Successfully Aborted Transaction\n");
   }

cleanup:
   
   return result;
}

DWORD
MonitorAppIDFromPath(
    _In_ PCWSTR fileName,
    _Out_ FWP_BYTE_BLOB** appId)
{
   DWORD result = NO_ERROR;
   
   result = FwpmGetAppIdFromFileName(fileName, appId);

   return result;
}

DWORD
MonitorAppDoMonitoring(_In_opt_ PCWSTR AppPath)
{
   HANDLE            monitorDevice = NULL;
   HANDLE            eventThread = NULL;
   HANDLE            engineHandle = NULL;
   DWORD             result;
   MONITOR_SETTINGS  monitorSettings;
   FWPM_SESSION      session;
   FWP_BYTE_BLOB*    applicationId = NULL;
   EVENT_THREAD_CONTEXT eventContext;

   RtlZeroMemory(&monitorSettings, sizeof(MONITOR_SETTINGS));
   RtlZeroMemory(&session, sizeof(FWPM_SESSION));

   session.displayData.name = L"Monitor Sample Session";
   session.displayData.description = L"Monitors traffic at the Stream layer.";

   // Let the Base Filtering Engine cleanup after us.
   session.flags = FWPM_SESSION_FLAG_DYNAMIC;

   printf("Opening Filtering Engine\n");
   result =  FwpmEngineOpen(
                            NULL,
                            RPC_C_AUTHN_WINNT,
                            NULL,
                            &session,
                            &engineHandle
                            );

   if (NO_ERROR != result)
   {
      goto cleanup;
   }

   printf("Successfully opened Filtering Engine\n");

   if (AppPath)
   {
      printf("Looking up Application ID from BFE\n");
      result = MonitorAppIDFromPath(AppPath, &applicationId);
      if (NO_ERROR != result)
      {
         goto cleanup;
      }
      printf("Successfully retrieved Application ID\n");
   }
   else
   {
      printf("No application path specified — monitoring all processes\n");
   }

   printf("Opening Monitor Sample Device\n");

   result = MonitorAppOpenMonitorDevice(&monitorDevice);
   if (NO_ERROR != result)
   {
      goto cleanup;
   }

   printf("Successfully opened Monitor Device\n");

   printf("Adding Filters through the Filtering Engine\n");

   result = MonitorAppAddFilters(engineHandle, 
                                applicationId);

   if (NO_ERROR != result)
   {
      goto cleanup;
   }

   printf("Successfully added Filters through the Filtering Engine\n");

   printf("Enabling monitoring through the Monitor Sample Device\n");

   monitorSettings.monitorOperation = monitorTraffic;
   
   result = MonitorAppEnableMonitoring(monitorDevice,
                                      &monitorSettings);
   if (NO_ERROR != result)
   {
      goto cleanup;
   }

   printf("Successfully enabled monitoring.\n");

   quitEvent = CreateEvent(NULL, TRUE, FALSE, NULL);
   if (!quitEvent)
   {
      result = GetLastError();
      goto cleanup;
   }

   eventContext.monitorDevice = monitorDevice;
   eventContext.quitEvent = quitEvent;

   eventThread = (HANDLE) _beginthreadex(NULL, 0, MonitorAppEventThread, &eventContext, 0, NULL);
   if (!eventThread)
   {
      result = GetLastError();
      goto cleanup;
   }

   printf("Monitoring... press any key to exit and cleanup filters.\n");

#pragma prefast(push)
#pragma prefast(disable:6031, "by design the return value of _getch() is ignored here")
   _getch();
#pragma prefast(pop)

   SetEvent(quitEvent);
   CancelIoEx(monitorDevice, NULL);
   WaitForSingleObject(eventThread, INFINITE);

cleanup:

   if (NO_ERROR != result)
   {
      printf("Monitor.\tError 0x%x occurred during execution\n", result);
   }

   if (eventThread)
   {
      CloseHandle(eventThread);
   }

   if (quitEvent)
   {
      CloseHandle(quitEvent);
      quitEvent = NULL;
   }

    if (monitorDevice)
   {
      MonitorAppCloseMonitorDevice(monitorDevice);
   }

   //
   // Free the application Id that we retrieved.
   //
   if (applicationId)
   {
      FwpmFreeMemory((void**)&applicationId);
   }
   
   if (engineHandle)
   {
      result =  FwpmEngineClose(engineHandle);
      engineHandle = NULL;
   }

   return result;
}

void
MonitorPrintUsage()
{
   wprintf(L"Usage: monitor ( addcallouts | delcallouts | monitor [targetApp.exe] )\n");
}

DWORD
MonitorAppProcessArguments(_In_ int argc, _In_reads_(argc) PCWSTR argv[])
{
   if (argc == 2)
   {
      if (_wcsicmp(argv[1], L"addcallouts") == 0)
      {
         return MonitorAppAddCallouts();
      }
      if (_wcsicmp(argv[1], L"delcallouts") == 0)
      {
         return MonitorAppRemoveCallouts();
      }
      if (_wcsicmp(argv[1], L"monitor") == 0)
      {
         // No target app — monitor all processes.
         return MonitorAppDoMonitoring(NULL);
      }
   }

   if (argc == 3)
   {
      if (_wcsicmp(argv[1], L"monitor") == 0)
      {
         return MonitorAppDoMonitoring(argv[2]);
      }
   }

   MonitorPrintUsage();
   return ERROR_INVALID_PARAMETER;
}

int __cdecl wmain(_In_ int argc, _In_reads_(argc) PCWSTR argv[])
{
   DWORD result;
   
   result = MonitorAppProcessArguments(argc, argv);

   return (int)result;
}
