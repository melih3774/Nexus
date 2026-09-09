/*
 * Driver.c - the Nexus HID source driver.
 *
 * A UMDF2 driver that publishes up to three virtual HID devices through the Windows Virtual HID
 * Framework, and does nothing else. It runs in WUDFHost.exe in user mode; there is no kernel
 * component in this package, which is why installing it does not require Code Integrity signing
 * and does not interact with HVCI. See docs/virtual-hid.md sections 1 and 18.
 *
 * NOT YET COMPILED. The WDK is not installed on the development machine, so this file has never
 * been through a compiler. It is written to be correct and is validated as far as text allows
 * (NexusHidPackageTests), but "it builds" is not among the things currently proven about it.
 *
 * THE SECURITY POSTURE OF THIS FILE
 *
 * Everything reachable from user mode is an IOCTL, and every IOCTL is refused unless it is exactly
 * right. The validation below is not defensive-programming ceremony: this driver is reachable by
 * any process that can open the control device, and the only thing standing between that and
 * synthetic input is that each request must name a known device, carry the exact struct size for
 * its opcode, and stay inside a report length the descriptor implies. Anything else fails closed
 * and changes no state.
 */

#include <windows.h>
#include <wdf.h>
#include <vhf.h>
#include <initguid.h>

#include "NexusHidProtocol.h"

#define NEXUS_HID_DRIVER_VERSION 0x00090000  /* 0.9.0.0, stamped to match the package */

/*
 * One published device. Three of these live in the control device's context.
 *
 * DescriptorLength is kept because VhfCreate does not retain a way to ask, and SubmitReport needs
 * it: a report is only accepted if its length matches what this device's descriptor implies, and
 * a driver that cannot answer that question has to either accept anything or reject everything.
 */
typedef struct _NEXUS_HID_DEVICE {
    VHFHANDLE               VhfHandle;
    NEXUS_HID_DEVICE_STATE  State;
    NTSTATUS                Status;
    UINT16                  ExpectedReportLength;
    UINT16                  DescriptorLength;
    UINT8                   Descriptor[NEXUS_HID_MAX_DESCRIPTOR_LENGTH];
    WCHAR                   InstanceId[200];
    UINT16                  InstanceIdLength;
} NEXUS_HID_DEVICE;

typedef struct _NEXUS_CONTROL_CONTEXT {
    WDFDEVICE        Device;
    WDFWAITLOCK      Lock;
    NEXUS_HID_DEVICE Devices[NEXUS_HID_MAX_DEVICES];
} NEXUS_CONTROL_CONTEXT;

WDF_DECLARE_CONTEXT_TYPE_WITH_NAME(NEXUS_CONTROL_CONTEXT, ControlGetContext)

EVT_WDF_DRIVER_DEVICE_ADD          NexusHidEvtDeviceAdd;
EVT_WDF_IO_QUEUE_IO_DEVICE_CONTROL NexusHidEvtIoDeviceControl;
EVT_WDF_DEVICE_CONTEXT_CLEANUP     NexusHidEvtDeviceCleanup;
EVT_WDF_FILEOBJECT_CLEANUP         NexusHidEvtFileCleanup;

static VOID NexusHidNeutralizeAll(_In_ NEXUS_CONTROL_CONTEXT* Control);

/* ------------------------------------------------------------------ entry */

NTSTATUS
DriverEntry(
    _In_ PDRIVER_OBJECT  DriverObject,
    _In_ PUNICODE_STRING RegistryPath
    )
{
    WDF_DRIVER_CONFIG config;

    WDF_DRIVER_CONFIG_INIT(&config, NexusHidEvtDeviceAdd);
    config.DriverPoolTag = 'sxeN';

    return WdfDriverCreate(DriverObject, RegistryPath, WDF_NO_OBJECT_ATTRIBUTES, &config, WDF_NO_HANDLE);
}

NTSTATUS
NexusHidEvtDeviceAdd(
    _In_    WDFDRIVER       Driver,
    _Inout_ PWDFDEVICE_INIT DeviceInit
    )
{
    NTSTATUS status;
    WDFDEVICE device;
    WDF_OBJECT_ATTRIBUTES attributes;
    WDF_FILEOBJECT_CONFIG fileConfig;
    WDF_IO_QUEUE_CONFIG queueConfig;
    NEXUS_CONTROL_CONTEXT* control;
    WDFQUEUE queue;

    UNREFERENCED_PARAMETER(Driver);

    /*
     * The control device is opened by PC Core, which runs as the signed-in owner at medium
     * integrity. It is not opened by anything else, and nothing about this driver is useful to a
     * process that cannot already inject input the ordinary way.
     */
    WDF_FILEOBJECT_CONFIG_INIT(&fileConfig, NULL, NULL, NexusHidEvtFileCleanup);
    WdfDeviceInitSetFileObjectConfig(DeviceInit, &fileConfig, WDF_NO_OBJECT_ATTRIBUTES);

    WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(&attributes, NEXUS_CONTROL_CONTEXT);
    attributes.EvtCleanupCallback = NexusHidEvtDeviceCleanup;

    status = WdfDeviceCreate(&DeviceInit, &attributes, &device);
    if (!NT_SUCCESS(status)) {
        return status;
    }

    control = ControlGetContext(device);
    RtlZeroMemory(control, sizeof(*control));
    control->Device = device;

    status = WdfWaitLockCreate(WDF_NO_OBJECT_ATTRIBUTES, &control->Lock);
    if (!NT_SUCCESS(status)) {
        return status;
    }

    WDF_IO_QUEUE_CONFIG_INIT_DEFAULT_QUEUE(&queueConfig, WdfIoQueueDispatchSequential);
    queueConfig.EvtIoDeviceControl = NexusHidEvtIoDeviceControl;

    status = WdfIoQueueCreate(device, &queueConfig, WDF_NO_OBJECT_ATTRIBUTES, &queue);
    if (!NT_SUCCESS(status)) {
        return status;
    }

    return WdfDeviceCreateDeviceInterface(device, &GUID_DEVINTERFACE_NEXUS_HID_CONTROL, NULL);
}

/* ------------------------------------------------------- device lifecycle */

/*
 * How many bytes one input report is, computed from the descriptor rather than taken on trust.
 *
 * The caller supplies the descriptor; the caller must not also supply the report size, or a caller
 * could declare a 7-byte mouse and then submit 64 bytes into it. Walking the descriptor is the
 * only way the driver knows what it agreed to.
 */
static UINT16
NexusHidInputReportLength(
    _In_reads_bytes_(Length) const UINT8* Descriptor,
    _In_ UINT16 Length
    )
{
    UINT32 bits = 0;
    UINT32 reportSize = 0;
    UINT32 reportCount = 0;
    UINT16 index = 0;

    while (index < Length) {
        UINT8 prefix = Descriptor[index++];
        UINT8 payload = (UINT8)(prefix & 0x03);
        UINT32 value = 0;
        UINT8 i;

        if (payload == 3) {
            payload = 4;
        }

        if ((UINT32)index + payload > Length) {
            return 0;   /* truncated item: refuse the descriptor entirely */
        }

        for (i = 0; i < payload; i++) {
            value |= (UINT32)Descriptor[index + i] << (8 * i);
        }

        index = (UINT16)(index + payload);

        switch (prefix & 0xFC) {
        case 0x74:  /* REPORT_SIZE  */ reportSize = value; break;
        case 0x94:  /* REPORT_COUNT */ reportCount = value; break;
        case 0x80:  /* INPUT        */ bits += reportSize * reportCount; break;
        default: break;
        }

        if (bits > NEXUS_HID_MAX_REPORT_LENGTH * 8) {
            return 0;   /* a descriptor claiming more than the transport allows */
        }
    }

    return (UINT16)((bits + 7) / 8);
}

static NTSTATUS
NexusHidCreateDevice(
    _In_ NEXUS_CONTROL_CONTEXT* Control,
    _In_ const NEXUS_HID_CREATE_REQUEST* Request,
    _Out_ NEXUS_HID_CREATE_REPLY* Reply
    )
{
    NEXUS_HID_DEVICE* slot;
    VHF_CONFIG config;
    NTSTATUS status;
    UINT16 reportLength;
    HANDLE fileHandle;

    RtlZeroMemory(Reply, sizeof(*Reply));

    if (Request->ProtocolVersion != NEXUS_HID_PROTOCOL_VERSION ||
        Request->DeviceKind >= NEXUS_HID_MAX_DEVICES ||
        Request->DescriptorLength == 0 ||
        Request->DescriptorLength > NEXUS_HID_MAX_DESCRIPTOR_LENGTH) {
        return STATUS_INVALID_DEVICE_REQUEST;
    }

    reportLength = NexusHidInputReportLength(Request->Descriptor, Request->DescriptorLength);
    if (reportLength == 0 || reportLength > NEXUS_HID_MAX_REPORT_LENGTH) {
        return STATUS_INVALID_DEVICE_REQUEST;
    }

    slot = &Control->Devices[Request->DeviceKind];

    /* Creating a device that already exists is a caller bug, not an upgrade path. */
    if (slot->State == NexusHidDeviceReady) {
        return STATUS_INVALID_DEVICE_REQUEST;
    }

    RtlCopyMemory(slot->Descriptor, Request->Descriptor, Request->DescriptorLength);
    slot->DescriptorLength = Request->DescriptorLength;
    slot->ExpectedReportLength = reportLength;

    /*
     * The user-mode VHF path. A UMDF driver passes a file handle where a KMDF driver passes a
     * device object - see VHF_CONFIG, whose FileHandle member is documented as "Required for
     * user-mode drivers".
     */
    status = WdfIoTargetWdmGetTargetFileHandle(
        WdfDeviceGetIoTarget(Control->Device),
        &fileHandle);
    if (!NT_SUCCESS(status)) {
        goto Failed;
    }

    VHF_CONFIG_INIT(&config, fileHandle, slot->DescriptorLength, slot->Descriptor);

    config.VendorID      = Request->VendorId;
    config.ProductID     = Request->ProductId;
    config.VersionNumber = Request->VersionNumber;

    status = VhfCreate(&config, &slot->VhfHandle);
    if (!NT_SUCCESS(status)) {
        goto Failed;
    }

    status = VhfStart(slot->VhfHandle);
    if (!NT_SUCCESS(status)) {
        VhfDelete(slot->VhfHandle, TRUE);
        slot->VhfHandle = NULL;
        goto Failed;
    }

    slot->State = NexusHidDeviceReady;
    slot->Status = STATUS_SUCCESS;

    Reply->State = NexusHidDeviceReady;
    Reply->Status = STATUS_SUCCESS;
    Reply->DeviceInstanceIdLength = slot->InstanceIdLength;
    RtlCopyMemory(Reply->DeviceInstanceId, slot->InstanceId, sizeof(Reply->DeviceInstanceId));

    return STATUS_SUCCESS;

Failed:
    slot->State = NexusHidDeviceFailed;
    slot->Status = status;
    Reply->State = NexusHidDeviceFailed;
    Reply->Status = status;
    return STATUS_SUCCESS;   /* the IOCTL succeeded; the device did not. The reply says which. */
}

static NTSTATUS
NexusHidSubmitReport(
    _In_ NEXUS_CONTROL_CONTEXT* Control,
    _In_ const NEXUS_HID_SUBMIT_REQUEST* Request
    )
{
    NEXUS_HID_DEVICE* slot;
    HID_XFER_PACKET packet;

    if (Request->DeviceKind >= NEXUS_HID_MAX_DEVICES) {
        return STATUS_INVALID_DEVICE_REQUEST;
    }

    slot = &Control->Devices[Request->DeviceKind];

    if (slot->State != NexusHidDeviceReady) {
        return STATUS_DEVICE_NOT_READY;
    }

    /*
     * Exact length, not "at most". A short report padded to the descriptor's size produces input
     * the caller did not ask for, and producing unrequested input is the one failure a virtual
     * input device must never have.
     */
    if (Request->ReportLength != slot->ExpectedReportLength) {
        return STATUS_INVALID_DEVICE_REQUEST;
    }

    RtlZeroMemory(&packet, sizeof(packet));
    packet.reportBuffer = (PUCHAR)Request->Report;
    packet.reportBufferLen = Request->ReportLength;
    packet.reportId = 0;    /* the descriptors are report-ID free: one device, one collection */

    return VhfReadReportSubmit(slot->VhfHandle, &packet);
}

/*
 * Every published device is told to release everything. Called on control-channel loss, on file
 * handle close, and on device cleanup.
 *
 * The neutral report is derived from the report length rather than from a per-device table: a
 * report of all zeroes is buttons up, keys up, tip switch clear and in-range clear for all three
 * descriptors. That is a property of how the descriptors were written, and it is why they were
 * written that way.
 */
static VOID
NexusHidNeutralizeAll(
    _In_ NEXUS_CONTROL_CONTEXT* Control
    )
{
    UINT32 kind;

    for (kind = 0; kind < NEXUS_HID_MAX_DEVICES; kind++) {
        NEXUS_HID_DEVICE* slot = &Control->Devices[kind];
        UINT8 neutral[NEXUS_HID_MAX_REPORT_LENGTH];
        HID_XFER_PACKET packet;

        if (slot->State != NexusHidDeviceReady || slot->ExpectedReportLength == 0) {
            continue;
        }

        RtlZeroMemory(neutral, sizeof(neutral));
        RtlZeroMemory(&packet, sizeof(packet));
        packet.reportBuffer = neutral;
        packet.reportBufferLen = slot->ExpectedReportLength;
        packet.reportId = 0;

        (VOID)VhfReadReportSubmit(slot->VhfHandle, &packet);
    }
}

static VOID
NexusHidDeleteDevice(
    _In_ NEXUS_CONTROL_CONTEXT* Control,
    _In_ UINT32 Kind
    )
{
    NEXUS_HID_DEVICE* slot;

    if (Kind >= NEXUS_HID_MAX_DEVICES) {
        return;
    }

    slot = &Control->Devices[Kind];

    if (slot->VhfHandle != NULL) {
        VhfDelete(slot->VhfHandle, TRUE);
        slot->VhfHandle = NULL;
    }

    slot->State = NexusHidDeviceAbsent;
    slot->ExpectedReportLength = 0;
    slot->DescriptorLength = 0;
}

/* ------------------------------------------------------------------ IOCTL */

VOID
NexusHidEvtIoDeviceControl(
    _In_ WDFQUEUE   Queue,
    _In_ WDFREQUEST Request,
    _In_ size_t     OutputBufferLength,
    _In_ size_t     InputBufferLength,
    _In_ ULONG      IoControlCode
    )
{
    NEXUS_CONTROL_CONTEXT* control = ControlGetContext(WdfIoQueueGetDevice(Queue));
    NTSTATUS status = STATUS_INVALID_DEVICE_REQUEST;
    size_t written = 0;
    PVOID input = NULL;
    PVOID output = NULL;

    WdfWaitLockAcquire(control->Lock, NULL);

    switch (IoControlCode) {

    case IOCTL_NEXUS_HID_GET_VERSION: {
        NEXUS_HID_VERSION_REPLY* reply;

        if (OutputBufferLength != sizeof(*reply)) {
            break;
        }
        if (!NT_SUCCESS(WdfRequestRetrieveOutputBuffer(Request, sizeof(*reply), &output, NULL))) {
            break;
        }

        reply = (NEXUS_HID_VERSION_REPLY*)output;
        reply->ProtocolVersion = NEXUS_HID_PROTOCOL_VERSION;
        reply->DriverVersion = NEXUS_HID_DRIVER_VERSION;
        written = sizeof(*reply);
        status = STATUS_SUCCESS;
        break;
    }

    case IOCTL_NEXUS_HID_CREATE_DEVICE: {
        if (InputBufferLength != sizeof(NEXUS_HID_CREATE_REQUEST) ||
            OutputBufferLength != sizeof(NEXUS_HID_CREATE_REPLY)) {
            break;
        }
        if (!NT_SUCCESS(WdfRequestRetrieveInputBuffer(Request, sizeof(NEXUS_HID_CREATE_REQUEST), &input, NULL)) ||
            !NT_SUCCESS(WdfRequestRetrieveOutputBuffer(Request, sizeof(NEXUS_HID_CREATE_REPLY), &output, NULL))) {
            break;
        }

        status = NexusHidCreateDevice(
            control,
            (const NEXUS_HID_CREATE_REQUEST*)input,
            (NEXUS_HID_CREATE_REPLY*)output);

        if (NT_SUCCESS(status)) {
            written = sizeof(NEXUS_HID_CREATE_REPLY);
        }
        break;
    }

    case IOCTL_NEXUS_HID_SUBMIT_REPORT: {
        if (InputBufferLength != sizeof(NEXUS_HID_SUBMIT_REQUEST)) {
            break;
        }
        if (!NT_SUCCESS(WdfRequestRetrieveInputBuffer(Request, sizeof(NEXUS_HID_SUBMIT_REQUEST), &input, NULL))) {
            break;
        }

        status = NexusHidSubmitReport(control, (const NEXUS_HID_SUBMIT_REQUEST*)input);
        break;
    }

    case IOCTL_NEXUS_HID_GET_STATUS: {
        NEXUS_HID_STATUS_REPLY* reply;
        UINT32 kind;

        if (OutputBufferLength != sizeof(*reply)) {
            break;
        }
        if (!NT_SUCCESS(WdfRequestRetrieveOutputBuffer(Request, sizeof(*reply), &output, NULL))) {
            break;
        }

        reply = (NEXUS_HID_STATUS_REPLY*)output;
        RtlZeroMemory(reply, sizeof(*reply));
        reply->ProtocolVersion = NEXUS_HID_PROTOCOL_VERSION;
        reply->DriverVersion = NEXUS_HID_DRIVER_VERSION;
        reply->DeviceCount = NEXUS_HID_MAX_DEVICES;

        for (kind = 0; kind < NEXUS_HID_MAX_DEVICES; kind++) {
            reply->Devices[kind].DeviceKind = kind;
            reply->Devices[kind].State = control->Devices[kind].State;
            reply->Devices[kind].Status = control->Devices[kind].Status;
            reply->Devices[kind].DeviceInstanceIdLength = control->Devices[kind].InstanceIdLength;
            RtlCopyMemory(
                reply->Devices[kind].DeviceInstanceId,
                control->Devices[kind].InstanceId,
                sizeof(reply->Devices[kind].DeviceInstanceId));
        }

        written = sizeof(*reply);
        status = STATUS_SUCCESS;
        break;
    }

    case IOCTL_NEXUS_HID_NEUTRALIZE: {
        const NEXUS_HID_NEUTRALIZE_REQUEST* request;

        if (InputBufferLength != sizeof(*request)) {
            break;
        }
        if (!NT_SUCCESS(WdfRequestRetrieveInputBuffer(Request, sizeof(*request), &input, NULL))) {
            break;
        }

        request = (const NEXUS_HID_NEUTRALIZE_REQUEST*)input;
        if (request->Reserved != 0) {
            break;
        }

        NexusHidNeutralizeAll(control);
        status = STATUS_SUCCESS;
        break;
    }

    case IOCTL_NEXUS_HID_DELETE_DEVICE: {
        const NEXUS_HID_DELETE_REQUEST* request;

        if (InputBufferLength != sizeof(*request)) {
            break;
        }
        if (!NT_SUCCESS(WdfRequestRetrieveInputBuffer(Request, sizeof(*request), &input, NULL))) {
            break;
        }

        request = (const NEXUS_HID_DELETE_REQUEST*)input;
        if (request->DeviceKind >= NEXUS_HID_MAX_DEVICES) {
            break;
        }

        NexusHidDeleteDevice(control, request->DeviceKind);
        status = STATUS_SUCCESS;
        break;
    }

    default:
        /* Unknown opcode. Nothing happens and nothing is logged that a caller controls. */
        break;
    }

    WdfWaitLockRelease(control->Lock);
    WdfRequestCompleteWithInformation(Request, status, written);
}

/* ---------------------------------------------------------------- cleanup */

/*
 * The control handle closing is how the driver learns PC Core is gone - whether it exited, crashed,
 * or was killed. Everything published is neutralised immediately.
 *
 * This is a watchdog in the only safe sense: it releases state, it never generates state. A timer
 * that decided on its own to press or move something would be a driver producing input nobody
 * asked for, which is precisely what this design exists to prevent.
 */
VOID
NexusHidEvtFileCleanup(
    _In_ WDFFILEOBJECT FileObject
    )
{
    NEXUS_CONTROL_CONTEXT* control = ControlGetContext(WdfFileObjectGetDevice(FileObject));

    WdfWaitLockAcquire(control->Lock, NULL);
    NexusHidNeutralizeAll(control);
    WdfWaitLockRelease(control->Lock);
}

VOID
NexusHidEvtDeviceCleanup(
    _In_ WDFOBJECT Object
    )
{
    NEXUS_CONTROL_CONTEXT* control = ControlGetContext((WDFDEVICE)Object);
    UINT32 kind;

    NexusHidNeutralizeAll(control);

    for (kind = 0; kind < NEXUS_HID_MAX_DEVICES; kind++) {
        NexusHidDeleteDevice(control, kind);
    }
}
