/*++

Copyright (c) Microsoft Corporation. All rights reserved

Abstract:

    Monitor Sample driver notification routines

Environment:

    Kernel mode
    
--*/

#include <ntddk.h>
#include <wdf.h>

#include <fwpmk.h>

#pragma warning(push)
#pragma warning(disable:4201)       // unnamed struct/union

#include <fwpsk.h>

#pragma warning(pop)


#include "ioctl.h"

#include "msnmntr.h"

#include "notify.h"

//
// Software Tracing Definitions 
//
#define WPP_CONTROL_GUIDS \
    WPP_DEFINE_CONTROL_GUID(MsnMntrNotify,(aca2f74a, 7a0d, 4f47, be4b, 66900813b8e5),  \
        WPP_DEFINE_BIT(TRACE_CLIENT_SERVER)                 \
        WPP_DEFINE_BIT(TRACE_PEER_TO_PEER)                  \
        WPP_DEFINE_BIT(TRACE_UNKNOWN)                       \
        WPP_DEFINE_BIT(TRACE_ALL_TRAFFIC) )

#include "notify.tmh" //  This file will be auto generated


#define TAG_NAME_NOTIFY 'oNnM'

typedef struct _MONITOR_EVENT_ENTRY
{
   LIST_ENTRY     listEntry;
   MONITOR_EVENT  event;
} MONITOR_EVENT_ENTRY;

KSPIN_LOCK g_EventQueueLock;
LIST_ENTRY g_EventQueue;
WDFREQUEST g_PendingEventRequest;

// Lightweight runtime observability for the IOCTL event queue.
// These are best-effort counters used for ETW/WPP tracing.
volatile LONG g_EventQueueDepth;
volatile LONG g_EventsEnqueued;
volatile LONG g_EventsDequeued;
volatile LONG g_EventsCompletedToPending;
volatile LONG g_PendingRequestsCreated;
volatile LONG g_PendingRequestsCanceled;

NTSTATUS
MonitorNfInitialize(
   _In_ DEVICE_OBJECT* deviceObject)
{
   UNREFERENCED_PARAMETER(deviceObject);

   KeInitializeSpinLock(&g_EventQueueLock);
   InitializeListHead(&g_EventQueue);
   g_PendingEventRequest = NULL;

   g_EventQueueDepth = 0;
   g_EventsEnqueued = 0;
   g_EventsDequeued = 0;
   g_EventsCompletedToPending = 0;
   g_PendingRequestsCreated = 0;
   g_PendingRequestsCanceled = 0;

   DoTraceMessage(TRACE_ALL_TRAFFIC, "EventQueue init");

   return STATUS_SUCCESS;
}

NTSTATUS
MonitorNfUninitialize(void)
{
   KLOCK_QUEUE_HANDLE lockHandle;
   WDFREQUEST pendingRequest = NULL;

   KeAcquireInStackQueuedSpinLock(&g_EventQueueLock, &lockHandle);

   if (g_PendingEventRequest)
   {
      pendingRequest = g_PendingEventRequest;
      g_PendingEventRequest = NULL;
   }

   while (!IsListEmpty(&g_EventQueue))
   {
      PLIST_ENTRY entry = RemoveHeadList(&g_EventQueue);
      MONITOR_EVENT_ENTRY* evt = CONTAINING_RECORD(entry, MONITOR_EVENT_ENTRY, listEntry);
      ExFreePoolWithTag(evt, TAG_NOTIFY);
      InterlockedDecrement(&g_EventQueueDepth);
   }

   KeReleaseInStackQueuedSpinLock(&lockHandle);

   if (pendingRequest)
   {
      if (WdfRequestUnmarkCancelable(pendingRequest) != STATUS_CANCELLED)
      {
         WdfRequestComplete(pendingRequest, STATUS_CANCELLED);
      }
   }

   return STATUS_SUCCESS;
}

__forceinline
void*
MonitorNfpFindCharacters(
   _In_reads_bytes_(streamLength) const char* stream,
   _In_ size_t streamLength,
   _In_reads_bytes_(subStreamLength) const char* subStream,
   _In_ size_t subStreamLength,
   _Out_ size_t* bytesLeft)
{
   size_t currentOffset = 0;
   void* subStreamPtr = NULL;
   
   *bytesLeft = streamLength;

   if (subStreamLength > streamLength)
   {
      return NULL;
   }

   while (currentOffset+subStreamLength <= streamLength)
   {
      if (0 == memcmp((void*)(stream+currentOffset), subStream, subStreamLength))
      {
         subStreamPtr = (void*)(char*)(stream+currentOffset);
         *bytesLeft = streamLength;
         *bytesLeft -= currentOffset;
         *bytesLeft -= subStreamLength;
         break;
      }
      currentOffset += subStreamLength;
   }
   
   return subStreamPtr;
}

static NTSTATUS
MonitorNfpCompleteRequestWithEvent(
   _In_ WDFREQUEST request,
   _In_ const MONITOR_EVENT* event)
{
   MONITOR_EVENT* userBuffer;
   size_t bufferSize;
   NTSTATUS status;

   status = WdfRequestRetrieveOutputBuffer(request,
                                           sizeof(MONITOR_EVENT),
                                           (void**)&userBuffer,
                                           &bufferSize);
   if (NT_SUCCESS(status))
   {
      UNREFERENCED_PARAMETER(bufferSize);
      RtlCopyMemory(userBuffer, event, sizeof(MONITOR_EVENT));
      WdfRequestCompleteWithInformation(request, STATUS_SUCCESS, sizeof(MONITOR_EVENT));
   }
   else
   {
      WdfRequestComplete(request, status);
   }

   return status;
}

static VOID
MonitorNfpCancelPendingEventRequest(
   _In_ WDFREQUEST request)
{
   KLOCK_QUEUE_HANDLE lockHandle;
   BOOLEAN owned = FALSE;

   KeAcquireInStackQueuedSpinLock(&g_EventQueueLock, &lockHandle);

   if (g_PendingEventRequest == request)
   {
      g_PendingEventRequest = NULL;
      owned = TRUE;
   }

   KeReleaseInStackQueuedSpinLock(&lockHandle);

   if (owned)
   {
      InterlockedIncrement(&g_PendingRequestsCanceled);
      DoTraceMessage(TRACE_ALL_TRAFFIC, "EventQueue cancel pending request");
      WdfRequestComplete(request, STATUS_CANCELLED);
   }
}

static NTSTATUS
MonitorNfpQueueEvent(
   _In_ const MONITOR_EVENT* event)
{
   MONITOR_EVENT_ENTRY* entry;
   KLOCK_QUEUE_HANDLE lockHandle;
   WDFREQUEST pendingRequest = NULL;
   NTSTATUS status = STATUS_SUCCESS;

   entry = (MONITOR_EVENT_ENTRY*) ExAllocatePoolZero(NonPagedPool,
                                                     sizeof(MONITOR_EVENT_ENTRY),
                                                     TAG_NOTIFY);
   if (!entry)
   {
      return STATUS_NO_MEMORY;
   }

   RtlCopyMemory(&entry->event, event, sizeof(MONITOR_EVENT));
   InitializeListHead(&entry->listEntry);

   KeAcquireInStackQueuedSpinLock(&g_EventQueueLock, &lockHandle);

   if (g_PendingEventRequest)
   {
      pendingRequest = g_PendingEventRequest;
      g_PendingEventRequest = NULL;
   }
   else
   {
      InsertTailList(&g_EventQueue, &entry->listEntry);
      entry = NULL; // ownership transferred to queue
      InterlockedIncrement(&g_EventQueueDepth);
      InterlockedIncrement(&g_EventsEnqueued);
   }

   KeReleaseInStackQueuedSpinLock(&lockHandle);

   if (pendingRequest)
   {
      // entry is still owned by us here; complete the pending request then free.
      if (WdfRequestUnmarkCancelable(pendingRequest) != STATUS_CANCELLED)
      {
         LARGE_INTEGER now;
         KeQuerySystemTimePrecise(&now);

         InterlockedIncrement(&g_EventsCompletedToPending);
         DoTraceMessage(TRACE_ALL_TRAFFIC,
                        "EventQueue complete pending: qDepth=%ld enq=%ld deq=%ld toPending=%ld t=%I64u",
                        g_EventQueueDepth,
                        g_EventsEnqueued,
                        g_EventsDequeued,
                        g_EventsCompletedToPending,
                        (unsigned __int64)now.QuadPart);

         status = MonitorNfpCompleteRequestWithEvent(pendingRequest, &entry->event);
      }
      else
      {
         status = STATUS_CANCELLED;
      }

      ExFreePoolWithTag(entry, TAG_NOTIFY);
   }

   return status;
}

NTSTATUS
MonitorNfDequeueNextEvent(
   _In_ WDFREQUEST request,
   _In_ size_t outputBufferLength,
   _Out_ BOOLEAN* requestCompleted)
{
   KLOCK_QUEUE_HANDLE lockHandle;
   MONITOR_EVENT_ENTRY* entry = NULL;
   NTSTATUS status = STATUS_SUCCESS;

   *requestCompleted = FALSE;

   // Validate the output buffer size before touching the queue.
   if (outputBufferLength < sizeof(MONITOR_EVENT))
   {
      WdfRequestComplete(request, STATUS_BUFFER_TOO_SMALL);
      *requestCompleted = TRUE;
      return STATUS_BUFFER_TOO_SMALL;
   }

   KeAcquireInStackQueuedSpinLock(&g_EventQueueLock, &lockHandle);

   if (!IsListEmpty(&g_EventQueue))
   {
      PLIST_ENTRY listEntry = RemoveHeadList(&g_EventQueue);
      entry = CONTAINING_RECORD(listEntry, MONITOR_EVENT_ENTRY, listEntry);
      InterlockedDecrement(&g_EventQueueDepth);
      InterlockedIncrement(&g_EventsDequeued);
   }
   else if (!g_PendingEventRequest)
   {
      status = WdfRequestMarkCancelableEx(request, MonitorNfpCancelPendingEventRequest);
      if (status == STATUS_SUCCESS)
      {
         g_PendingEventRequest = request;
         request = NULL;
         status = STATUS_PENDING;
         InterlockedIncrement(&g_PendingRequestsCreated);
         DoTraceMessage(TRACE_ALL_TRAFFIC,
                        "EventQueue pending request created: qDepth=%ld",
                        g_EventQueueDepth);
      }
   }
   else
   {
      status = STATUS_DEVICE_BUSY;
   }

   KeReleaseInStackQueuedSpinLock(&lockHandle);

   if (entry)
   {
      LARGE_INTEGER now;
      KeQuerySystemTimePrecise(&now);

      DoTraceMessage(TRACE_ALL_TRAFFIC,
                     "EventQueue dequeue: qDepth=%ld enq=%ld deq=%ld t=%I64u",
                     g_EventQueueDepth,
                     g_EventsEnqueued,
                     g_EventsDequeued,
                     (unsigned __int64)now.QuadPart);

      status = MonitorNfpCompleteRequestWithEvent(request, &entry->event);
      *requestCompleted = TRUE;
      ExFreePoolWithTag(entry, TAG_NOTIFY);
      return status;
   }

   if (request == NULL)
   {
      // Request stored as pending, will be completed by MonitorNfpQueueEvent.
      return status;
   }

   if (!NT_SUCCESS(status))
   {
      WdfRequestComplete(request, status);
      *requestCompleted = TRUE;
   }

   return status;
}

NTSTATUS
MonitorNfParseMessageInbound(
   _In_reads_bytes_(streamLength) BYTE* stream,
   _In_ size_t streamLength,
   _In_ USHORT localPort,
   _In_ USHORT remotePort,
   _In_ ULONG localAddressV4,
   _In_ ULONG remoteAddressV4,
   _In_ USHORT ipProto)
{
   UNREFERENCED_PARAMETER(stream);

   DoTraceMessage(TRACE_CLIENT_SERVER,
               "%Id bytes received. Local: %d.%d.%d.%d:%d Remote: %d.%d.%d.%d:%d Proto: %d.",
               streamLength,
               (localAddressV4 >> 24) & 0xFF,
               (localAddressV4 >> 16) & 0xFF,
               (localAddressV4 >> 8) & 0xFF,
               localAddressV4 & 0xFF,
               localPort,
               (remoteAddressV4 >> 24) & 0xFF,
               (remoteAddressV4 >> 16) & 0xFF,
               (remoteAddressV4 >> 8) & 0xFF,
               remoteAddressV4 & 0xFF,
               remotePort,
               ipProto);
   return STATUS_SUCCESS;
}

NTSTATUS
MonitorNfParseMessageInboundHttpHeader(
   _In_reads_bytes_(streamLength) BYTE* stream,
   _In_ size_t streamLength,
   _In_ USHORT localPort,
   _In_ USHORT remotePort,
   _In_ ULONG localAddressV4,
   _In_ ULONG remoteAddressV4,
   _In_ USHORT ipProto)
{
   BYTE* msgStart = NULL;
   size_t bytesLeft;
   NTSTATUS status = STATUS_INVALID_PARAMETER;
   
   // Walk past the HTTP header.
   msgStart = (BYTE*) MonitorNfpFindCharacters((char*)stream, 
                                               streamLength,
                                               "\r\n\r\n",
                                               (ULONG)strlen("\r\n\r\n"),
                                               &bytesLeft);
   if (msgStart && (bytesLeft > 0))
   {
      size_t msgLength;

      msgStart += 4; // step past \r\n\r\n.

      msgLength = streamLength - (ULONG)(ULONG_PTR)(msgStart - stream);
      
      // Do the final inbound message processing.
      status = MonitorNfParseMessageInbound(msgStart,
                                            msgLength,
                                            localPort,
                                            remotePort,
                                            localAddressV4,
                                            remoteAddressV4,
                                            ipProto);
   }

   return status;
}

NTSTATUS
MonitorNfParseMessageOutbound(
   _In_reads_bytes_(streamLength) BYTE* stream,
   _In_ size_t streamLength,
   _In_ USHORT localPort,
   _In_ USHORT remotePort,
   _In_ ULONG localAddressV4,
   _In_ ULONG remoteAddressV4,
   _In_ USHORT ipProto)
{
   UNREFERENCED_PARAMETER(stream);

   DoTraceMessage(TRACE_CLIENT_SERVER,
               "%Id bytes sent. Local: %d.%d.%d.%d:%d Remote: %d.%d.%d.%d:%d Proto: %d.",
               streamLength,
               (localAddressV4 >> 24) & 0xFF,
               (localAddressV4 >> 16) & 0xFF,
               (localAddressV4 >> 8) & 0xFF,
               localAddressV4 & 0xFF,
               localPort,
               (remoteAddressV4 >> 24) & 0xFF,
               (remoteAddressV4 >> 16) & 0xFF,
               (remoteAddressV4 >> 8) & 0xFF,
               remoteAddressV4 & 0xFF,
               remotePort,
               ipProto);
   return STATUS_SUCCESS;
}

NTSTATUS
MonitorNfParseMessageOutboundHttpHeader(
   _In_reads_bytes_(streamLength) BYTE* stream,
   _In_ size_t streamLength,
   _In_ USHORT localPort,
   _In_ USHORT remotePort,
   _In_ ULONG localAddressV4,
   _In_ ULONG remoteAddressV4,
   _In_ USHORT ipProto)
{
   BYTE* msgStart = NULL;
   size_t bytesLeft;
   NTSTATUS status = STATUS_SUCCESS;
   
   // Walk past the HTTP header.
   msgStart = (BYTE*) MonitorNfpFindCharacters((char*)stream, 
                                               streamLength,
                                               "\r\n\r\n",
                                               (ULONG)strlen("\r\n\r\n"),
                                               &bytesLeft);
   if (msgStart && (bytesLeft > 0))
   {
      size_t msgLength;

      msgStart += 4; // step past \r\n\r\n.

      msgLength = streamLength - (ULONG)(ULONG_PTR)(msgStart - stream);
      status = MonitorNfParseMessageOutbound(msgStart,
                                             msgLength,
                                             localPort,
                                             remotePort,
                                             localAddressV4,
                                             remoteAddressV4,
                                             ipProto);
   }
   
   return status;
}

NTSTATUS
MonitorNfParseStreamAndTraceMessage(
   _In_reads_bytes_(streamLength) BYTE* stream,
   _In_ size_t streamLength,
   _In_ BOOLEAN inbound,
   _In_ USHORT localPort,
   _In_ USHORT remotePort,
   _In_ ULONG localAddressV4,
   _In_ ULONG remoteAddressV4,
   _In_ USHORT ipProto)
{
   NTSTATUS status;

   if (!inbound)
   {
      if ((_strnicmp((const char*)stream, "POST", streamLength) == 0)
          || (_strnicmp((const char*)stream, "GET", streamLength) == 0))
      {
         if ((MonitorNfParseMessageOutboundHttpHeader(stream,
                                                      streamLength,
                                                      localPort,
                                                      remotePort,
                                                      localAddressV4,
                                                      remoteAddressV4,
                                                      ipProto)) != STATUS_SUCCESS)
           return STATUS_INSUFFICIENT_RESOURCES; 
      }
      else
      {
         if ((MonitorNfParseMessageOutbound(stream,
                                            streamLength,
                                            localPort,
                                            remotePort,
                                            localAddressV4,
                                            remoteAddressV4,
                                            ipProto)!= STATUS_SUCCESS))
           return STATUS_INSUFFICIENT_RESOURCES;
      }
   }
   else
   {
      if (_strnicmp((const char*)stream, "HTTP", streamLength) == 0)
      {
         if ((MonitorNfParseMessageInboundHttpHeader(stream,
                                                     streamLength,
                                                     localPort,
                                                     remotePort,
                                                     localAddressV4,
                                                     remoteAddressV4,
                                                     ipProto)) != STATUS_SUCCESS)
            return STATUS_INSUFFICIENT_RESOURCES;
      }
      else
      {
         if ((MonitorNfParseMessageInbound(stream,
                                           streamLength,
                                           localPort,
                                           remotePort,
                                           localAddressV4,
                                           remoteAddressV4,
                                           ipProto)) != STATUS_SUCCESS)
            return STATUS_INSUFFICIENT_RESOURCES;
      }
   }

   {
      status = STATUS_SUCCESS;
   }

   return status;
}

NTSTATUS MonitorNfNotifyMessage(
   _In_ const FWPS_STREAM_DATA* streamBuffer,
   _In_ BOOLEAN inbound,
   _In_ USHORT localPort,
   _In_ USHORT remotePort,
   _In_ ULONG localAddressV4,
   _In_ ULONG remoteAddressV4,
   _In_ USHORT ipProto
)
{
   NTSTATUS status = STATUS_SUCCESS;
   BYTE* stream = NULL;
   SIZE_T streamLength = streamBuffer->dataLength;
   SIZE_T bytesCopied = 0;

   if(streamLength == 0)
      return status;

   stream = ExAllocatePoolZero(NonPagedPool,
                               streamLength,
                               TAG_NAME_NOTIFY);
   if (!stream)
      return STATUS_INSUFFICIENT_RESOURCES;

   RtlZeroMemory(stream,streamLength);

   FwpsCopyStreamDataToBuffer(
      streamBuffer,
      stream,
      streamLength,
      &bytesCopied);

   NT_ASSERT(bytesCopied == streamLength);

   status = MonitorNfParseStreamAndTraceMessage(stream,
                                                streamLength,
                                                inbound,
                                                localPort,
                                                remotePort,
                                                localAddressV4,
                                                remoteAddressV4,
                                                ipProto);

   ExFreePoolWithTag(stream, TAG_NAME_NOTIFY);

   return status;
}

void
MonitorNfNotifyConnect(
   _In_ const FLOW_DATA* flowData)
{
   MONITOR_EVENT evt;

   evt.type = monitorEventConnect;
   evt.flags = 0;
   evt.ipProto = flowData->ipProto;
   evt.localPort = flowData->localPort;
   evt.remotePort = flowData->remotePort;
   evt.localAddressV4 = flowData->localAddressV4;
   evt.remoteAddressV4 = flowData->remoteAddressV4;

   (void) MonitorNfpQueueEvent(&evt);
}

void MonitorNfNotifyDisconnect(
   _In_ UINT32 streamFlags,
   _In_ USHORT localPort,
   _In_ USHORT remotePort,
   _In_ ULONG localAddressV4,
   _In_ ULONG remoteAddressV4,
   _In_ USHORT ipProto)
{
   const char* reason;
   MONITOR_EVENT evt;

   if (streamFlags & FWPS_STREAM_FLAG_SEND_DISCONNECT)
   {
      reason = "Send disconnect (FIN)";
   }
   else if (streamFlags & FWPS_STREAM_FLAG_RECEIVE_DISCONNECT)
   {
      reason = "Receive disconnect (FIN)";
   }
   else if (streamFlags & FWPS_STREAM_FLAG_SEND_ABORT)
   {
      reason = "Send abort (RST)";
   }
   else if (streamFlags & FWPS_STREAM_FLAG_RECEIVE_ABORT)
   {
      reason = "Receive abort (RST)";
   }
   else
   {
      return;
   }

   evt.type = monitorEventDisconnect;
   evt.flags = streamFlags;
   evt.ipProto = ipProto;
   evt.localPort = localPort;
   evt.remotePort = remotePort;
   evt.localAddressV4 = localAddressV4;
   evt.remoteAddressV4 = remoteAddressV4;

   (void) MonitorNfpQueueEvent(&evt);

   DoTraceMessage(TRACE_CLIENT_SERVER,
               "%s. Local: %d.%d.%d.%d:%d Remote: %d.%d.%d.%d:%d Proto: %s.",
               reason,
               (localAddressV4 >> 24) & 0xFF,
               (localAddressV4 >> 16) & 0xFF,
               (localAddressV4 >> 8) & 0xFF,
               localAddressV4 & 0xFF,
               localPort,
               (remoteAddressV4 >> 24) & 0xFF,
               (remoteAddressV4 >> 16) & 0xFF,
               (remoteAddressV4 >> 8) & 0xFF,
               remoteAddressV4 & 0xFF,
               remotePort,
               MonitorNfProtoToString(ipProto));
}
