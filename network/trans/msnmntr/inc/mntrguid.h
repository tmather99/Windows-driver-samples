/*++

Copyright (c) Microsoft Corporation. All rights reserved

Abstract:

    Monitor Sample callout driver IOCTL header

Environment:

    Kernel mode
    
--*/

#pragma once

// b3241f1d-7cd2-4e7a-8721-2e97d07702e5
DEFINE_GUID(
    MONITOR_SAMPLE_SUBLAYER,
    0x9f1e2c3b,
    0x4a7d,
    0x4b2a,
    0x9e, 0x13, 0xf3, 0x1a, 0x5c, 0x7d, 0x6b, 0x8e
);

// 3aaccbc0-2c29-455f-bb91-0e801c8994a4
DEFINE_GUID(
    MONITOR_SAMPLE_FLOW_ESTABLISHED_CALLOUT_V4,
    0x5c4f2e01,
    0x8b2c,
    0x4d3f,
    0x9a, 0xcb, 0x12, 0x34, 0x56, 0x78, 0x9a, 0xbc
);

// cea0131a-6ed3-4ed6-b40c-8a8fe8434b0a
DEFINE_GUID(
    MONITOR_SAMPLE_STREAM_CALLOUT_V4,
    0x7d3a9f2b,
    0x6c5d,
    0x4b2e,
    0x8f, 0x21, 0xde, 0xad, 0xbe, 0xef, 0x00, 0x11
);



