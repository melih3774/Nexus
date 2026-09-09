/*
 * NexusHidProtocol.h - the entire interface between PC Core and the Nexus HID driver.
 *
 * This file is the ABI. It is compiled into the driver and mirrored, field for field, by
 * NexusHidProtocol.cs in PcCore.NativeInput; NexusHidProtocolAbiTests asserts the two agree on
 * every offset and size. A mismatch here is not a compile error in either language, it is a
 * driver reading a report out of the middle of a struct, so the layout is pinned by test.
 *
 * WHAT THIS DELIBERATELY IS NOT
 *
 * There is no passthrough IOCTL, no pointer field, no length the caller controls without a bound,
 * no path, no handle, and no opcode that means "do what this buffer says". A driver reachable from
 * user mode is an escalation surface, and the way this one stays small is by only being able to do
 * six things, each of which takes a fixed-size struct.
 *
 * Every operation is refused unless the request buffer is exactly the size the opcode expects.
 * Short buffer, long buffer, unknown opcode, unknown device id, wrong protocol version, report
 * longer than the descriptor: all fail closed with STATUS_INVALID_DEVICE_REQUEST and change no
 * state.
 */

#pragma once

#ifdef _KERNEL_MODE
#include <ntdef.h>
#else
#include <windows.h>
#endif

/*
 * The control device interface. PC Core opens this by interface GUID rather than by a fixed name,
 * so the device path is whatever Windows assigns and nothing depends on a hardcoded string.
 *
 * {6D1F4A2E-9C3B-4E7A-B5D8-1A2C3E4F5B60}
 */
DEFINE_GUID(GUID_DEVINTERFACE_NEXUS_HID_CONTROL,
    0x6d1f4a2e, 0x9c3b, 0x4e7a, 0xb5, 0xd8, 0x1a, 0x2c, 0x3e, 0x4f, 0x5b, 0x60);

/*
 * Bumped when any struct in this file changes shape or any opcode changes meaning. PC Core checks
 * it on connect and refuses to talk to a driver it does not match, because a version skew between
 * a driver and a control client is a wrong-report bug, not a graceful-degradation opportunity.
 */
#define NEXUS_HID_PROTOCOL_VERSION 3

/* Nexus-owned identity. See docs/virtual-hid.md section 5 for why these values and not others. */
#define NEXUS_HID_VENDOR_ID   0xF055
#define NEXUS_HID_PRODUCT_ID_MOUSE     0x0001
#define NEXUS_HID_PRODUCT_ID_KEYBOARD  0x0002
#define NEXUS_HID_PRODUCT_ID_TOUCH     0x0003
#define NEXUS_HID_VERSION_NUMBER 0x0100

/* Hard ceilings. Anything above these is a malformed request, not a large one. */
#define NEXUS_HID_MAX_DEVICES          3
#define NEXUS_HID_MAX_REPORT_LENGTH    64
#define NEXUS_HID_MAX_DESCRIPTOR_LENGTH 512

/* Which of the three Nexus devices a request is about. Values are ABI. */
typedef enum _NEXUS_HID_DEVICE_KIND {
    NexusHidDeviceMouse    = 0,
    NexusHidDeviceKeyboard = 1,
    NexusHidDeviceTouch    = 2,
} NEXUS_HID_DEVICE_KIND;

/* Lifecycle state of one published device, as the driver sees it. */
typedef enum _NEXUS_HID_DEVICE_STATE {
    NexusHidDeviceAbsent  = 0,  /* never created, or deleted */
    NexusHidDeviceReady   = 1,  /* VhfCreate + VhfStart succeeded; reports accepted */
    NexusHidDeviceFailed  = 2,  /* creation failed; Status carries the NTSTATUS */
} NEXUS_HID_DEVICE_STATE;

#define NEXUS_HID_IOCTL_BASE 0x800

#define NEXUS_HID_CTL_CODE(index) \
    CTL_CODE(FILE_DEVICE_UNKNOWN, NEXUS_HID_IOCTL_BASE + (index), METHOD_BUFFERED, FILE_WRITE_ACCESS)

/* The complete set. Six operations, and no seventh. */
#define IOCTL_NEXUS_HID_GET_VERSION   NEXUS_HID_CTL_CODE(0)
#define IOCTL_NEXUS_HID_CREATE_DEVICE NEXUS_HID_CTL_CODE(1)
#define IOCTL_NEXUS_HID_SUBMIT_REPORT NEXUS_HID_CTL_CODE(2)
#define IOCTL_NEXUS_HID_GET_STATUS    NEXUS_HID_CTL_CODE(3)
#define IOCTL_NEXUS_HID_NEUTRALIZE    NEXUS_HID_CTL_CODE(4)
#define IOCTL_NEXUS_HID_DELETE_DEVICE NEXUS_HID_CTL_CODE(5)

#include <pshpack1.h>

/* IOCTL_NEXUS_HID_GET_VERSION - output. No input. */
typedef struct _NEXUS_HID_VERSION_REPLY {
    UINT32 ProtocolVersion;
    UINT32 DriverVersion;
} NEXUS_HID_VERSION_REPLY;

/*
 * IOCTL_NEXUS_HID_CREATE_DEVICE - input.
 *
 * The descriptor travels inline in a fixed-size array rather than behind a pointer and a length.
 * That wastes a few hundred bytes per call, on a call made three times per boot, and in exchange
 * there is no user pointer for the driver to dereference and no length to get wrong.
 */
typedef struct _NEXUS_HID_CREATE_REQUEST {
    UINT32 ProtocolVersion;
    UINT32 DeviceKind;                                  /* NEXUS_HID_DEVICE_KIND */
    UINT16 VendorId;
    UINT16 ProductId;
    UINT16 VersionNumber;
    UINT16 DescriptorLength;                            /* <= NEXUS_HID_MAX_DESCRIPTOR_LENGTH */
    UINT8  Descriptor[NEXUS_HID_MAX_DESCRIPTOR_LENGTH];
} NEXUS_HID_CREATE_REQUEST;

/*
 * IOCTL_NEXUS_HID_CREATE_DEVICE - output.
 *
 * WHY THIS CARRIES NO DEVICE PATH, AND WHY THAT IS CORRECT
 *
 * The obvious design has the driver report which PnP device it just created, because the driver is
 * the component that caused it to exist. It cannot. VhfCreate returns a VHFHANDLE, not a devnode;
 * the device is enumerated afterwards by PnP, and VHF exposes no supported way for a source driver
 * to ask what Windows called it.
 *
 * The next design has the driver *choose* the identity through VHF_CONFIG.InstanceID and
 * HardwareIDs, and report what it chose. Those members exist and are undocumented - the VHF_CONFIG
 * reference lists all four identity members with empty descriptions - and setting them made VhfCreate
 * and VhfStart both return success while VHF enumerated nothing at all.
 *
 * So identity is not asserted over this interface. The client establishes it from the device tree
 * instead: everything Nexus publishes descends from the Nexus control device and nothing else does.
 * That is stronger than anything the driver could have claimed here, because a claim in a reply is
 * only as trustworthy as the thing making it, whereas ancestry is Windows' own record of what
 * created what.
 *
 * ContainerID is reported because the driver does set it, it is documented, and it is worth having:
 * it tells Windows the three devices are one piece of hardware with three functions.
 */
typedef struct _NEXUS_HID_CREATE_REPLY {
    UINT32 State;                                       /* NEXUS_HID_DEVICE_STATE */
    INT32  Status;                                      /* NTSTATUS when State == Failed */
    GUID   ContainerId;
} NEXUS_HID_CREATE_REPLY;

/*
 * IOCTL_NEXUS_HID_SUBMIT_REPORT - input. The hot path, and the only one on it.
 *
 * ReportLength is checked against both NEXUS_HID_MAX_REPORT_LENGTH and the length the device's own
 * descriptor implies. A report of the wrong size for its device is refused rather than padded:
 * padding a HID report produces input, and producing input the caller did not ask for is the one
 * failure mode a virtual input device must never have.
 */
typedef struct _NEXUS_HID_SUBMIT_REQUEST {
    UINT32 DeviceKind;                                  /* NEXUS_HID_DEVICE_KIND */
    UINT16 ReportLength;                                /* <= NEXUS_HID_MAX_REPORT_LENGTH */
    UINT8  Report[NEXUS_HID_MAX_REPORT_LENGTH];
} NEXUS_HID_SUBMIT_REQUEST;

/* One device's line in the status reply. */
typedef struct _NEXUS_HID_DEVICE_STATUS {
    UINT32 DeviceKind;
    UINT32 State;
    INT32  Status;
    GUID   ContainerId;
} NEXUS_HID_DEVICE_STATUS;

/*
 * IOCTL_NEXUS_HID_GET_STATUS - output. No input.
 *
 * Reports what is actually published, which is the only claim PC Core is allowed to make about
 * readiness. The presence of driver files on disk is not evidence that a device exists.
 */
typedef struct _NEXUS_HID_STATUS_REPLY {
    UINT32 ProtocolVersion;
    UINT32 DriverVersion;
    UINT32 DeviceCount;
    NEXUS_HID_DEVICE_STATUS Devices[NEXUS_HID_MAX_DEVICES];
} NEXUS_HID_STATUS_REPLY;

/*
 * IOCTL_NEXUS_HID_NEUTRALIZE - input.
 *
 * Sends the neutral report for every published device: buttons up, keys up, contact lifted and out
 * of range. Takes no arguments on purpose - there is no partial neutralise, because every caller of
 * this is an error path and an error path should not have options.
 */
typedef struct _NEXUS_HID_NEUTRALIZE_REQUEST {
    UINT32 Reserved;                                    /* must be zero */
} NEXUS_HID_NEUTRALIZE_REQUEST;

/* IOCTL_NEXUS_HID_DELETE_DEVICE - input. */
typedef struct _NEXUS_HID_DELETE_REQUEST {
    UINT32 DeviceKind;
} NEXUS_HID_DELETE_REQUEST;

#include <poppack.h>

/*
 * The other half of the ABI check. NexusHidProtocol.cs declares the same structs with Pack = 1 and
 * NexusHidProtocolAbiTests asserts these same numbers from managed code. Both sides are pinned to
 * literals rather than to each other, so a change on either side fails a build or a test instead of
 * silently agreeing on a new and wrong layout.
 *
 * If one of these fires, do not edit the number. Find out which field moved.
 */
#ifdef _MSC_VER
C_ASSERT(sizeof(NEXUS_HID_VERSION_REPLY)      == 8);
C_ASSERT(sizeof(NEXUS_HID_CREATE_REQUEST)     == 528);
C_ASSERT(sizeof(NEXUS_HID_CREATE_REPLY)       == 24);
C_ASSERT(sizeof(NEXUS_HID_SUBMIT_REQUEST)     == 70);
C_ASSERT(sizeof(NEXUS_HID_DEVICE_STATUS)      == 28);
C_ASSERT(sizeof(NEXUS_HID_STATUS_REPLY)       == 96);
C_ASSERT(sizeof(NEXUS_HID_NEUTRALIZE_REQUEST) == 4);
C_ASSERT(sizeof(NEXUS_HID_DELETE_REQUEST)     == 4);
#endif
