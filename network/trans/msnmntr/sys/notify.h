/*++

Copyright (c) Microsoft Corporation. All rights reserved

Abstract:

    Monitor Sample driver notification header

Environment:

    Kernel mode
    
--*/

#pragma once

#define TAG_NOTIFY 'yftN'

__forceinline
const char*
MonitorNfProtoToString(_In_ USHORT ipProto)
{
   switch (ipProto)
   {
   case 1:  return "ICMP";
   case 6:  return "TCP";
   case 17: return "UDP";
   case 41: return "IPv6";
   case 47: return "GRE";
   case 58: return "ICMPv6";
   default: return "Unknown";
   }
}

NTSTATUS
MonitorNfInitialize(
   _In_ DEVICE_OBJECT* deviceObject);

NTSTATUS
MonitorNfUninitialize(void);

NTSTATUS MonitorNfNotifyMessage(
   _In_ const FWPS_STREAM_DATA* streamBuffer,
   _In_ BOOLEAN inbound,
   _In_ USHORT localPort,
   _In_ USHORT remotePort,
   _In_ ULONG localAddressV4,
   _In_ ULONG remoteAddressV4,
   _In_ USHORT ipProto);

void MonitorNfNotifyDisconnect(
   _In_ UINT32 streamFlags,
   _In_ USHORT localPort,
   _In_ USHORT remotePort,
   _In_ ULONG localAddressV4,
   _In_ ULONG remoteAddressV4,
   _In_ USHORT ipProto);


