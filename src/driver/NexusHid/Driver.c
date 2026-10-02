/*
 * Driver.c - the Nexus HID source driver.
 *
 * A UMDF2 driver that publishes up to three virtual HID devices through the Windows Virtual HID
 * Framework, and does nothing else. It runs in WUDFHost.exe in user mode; there is no kernel
 * component in this package, which is why installing it does not require Code Integrity signing
 * and does not interact with HVCI. See docs/virtual-hid.md sections 1 and 18.
 *
 * INSTALLED AND PROVEN. VhfCreate and VhfStart succeed for all three devices and Windows
 * enumerates them: keyboard.inf binds the keyboard, msmouse.inf the mouse and input.inf the touch
 * screen. Writing this file from documentation alone got two things wrong that only the compiler
 * could find - WdfIoTargetWdmGetTargetFileHandle returns the handle rather than taking an
 * out-parameter, and the handle has to come from a target opened WdfIoTargetOpenLocalTargetByFile
 * rather than from the device's default I/O target - and the first real install found three more
 * that only a running driver could show:
 *
 *   1. Nothing ever wrote the slot's identity fields, so every create returned an empty identity
 *      and PC Core refused three devices the driver had successfully published. Assigning identity
 *      here through VHF_CONFIG.InstanceID and HardwareIDs then stopped enumeration entirely - both
 *      are undocumented - so the driver now names nothing and the client establishes identity from
 *      the device tree, which Windows does document.
 *   2. Handle cleanup neutralised but never deleted, and the slot state lives in the per-device
 *      context that outlives every client handle. The devices from a dead client stayed Ready
 *      forever and every later client was refused STATUS_INVALID_DEVICE_REQUEST by the
 *      already-exists guard. Slots are now owned by the file object that created them and are
 *      released when it closes.
 *   3. SubmitReport hardcoded reportId 0 on the reasoning that the descriptors declare no report
 *      ids. That stopped being true when the digitizer gained REPORT_ID 1, which Windows requires
 *      on a touch screen.
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
#include <hidclass.h>   /* HID_XFER_PACKET. vhf.h includes nothing and depends on this. */
#include <vhf.h>
#include <initguid.h>

#include "NexusHidProtocol.h"

#define NEXUS_HID_DRIVER_VERSION 0x00090204  /* 0.9.2.4, stamped to match the package */

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

    /*
     * The input report id, or 0 when the descriptor declares none.
     *
     * Load-bearing for neutralisation. The digitizer declares REPORT_ID 1 because Windows requires
     * one on a touch screen; the mouse and keyboard declare none. An all-zero neutral report is
     * therefore correct for two of the three devices and WRONG for the third, where byte zero is
     * the report id and zero is not a valid one.
     */
    UINT8                   ReportId;

    UINT16                  DescriptorLength;
    UINT8                   Descriptor[NEXUS_HID_MAX_DESCRIPTOR_LENGTH];

    /*
     * Which control handle created this device, and therefore which one may submit to it and whose
     * closing destroys it.
     *
     * WHY OWNERSHIP IS PER FILE OBJECT AND NOT PER DEVICE
     *
     * The control context belongs to the WDFDEVICE and outlives every client. Without an owner
     * recorded here, a slot created by one PC Core process stayed Ready after that process died,
     * and the next one was refused by the already-exists guard - permanently, until the machine
     * rebooted. It also meant any second handle, including one opened purely to read status, could
     * neutralise or delete devices it had nothing to do with. An owner answers both: cleanup
     * releases exactly what that handle made, and nothing else can touch it.
     */
    WDFFILEOBJECT           Owner;
} NEXUS_HID_DEVICE;

typedef struct _NEXUS_CONTROL_CONTEXT {
    WDFDEVICE        Device;
    WDFWAITLOCK      Lock;

    /*
     * The local I/O target whose file handle VHF is given. Opened once and kept: VHF holds the
     * handle for the lifetime of every device created against it, so a target opened per create
     * call and closed afterwards would pull the handle out from under a live device.
     */
    WDFIOTARGET      IoTarget;

    NEXUS_HID_DEVICE Devices[NEXUS_HID_MAX_DEVICES];
} NEXUS_CONTROL_CONTEXT;

WDF_DECLARE_CONTEXT_TYPE_WITH_NAME(NEXUS_CONTROL_CONTEXT, ControlGetContext)

EVT_WDF_DRIVER_DEVICE_ADD          NexusHidEvtDeviceAdd;
EVT_WDF_IO_QUEUE_IO_DEVICE_CONTROL NexusHidEvtIoDeviceControl;
EVT_WDF_DEVICE_CONTEXT_CLEANUP     NexusHidEvtDeviceCleanup;
EVT_WDF_FILE_CLEANUP               NexusHidEvtFileCleanup;

/*
 * Owner == NULL means "every slot, whoever owns it", which is only ever correct on device cleanup:
 * the device itself is going away, so there is no owner left to respect.
 */
static VOID NexusHidNeutralizeOwned(_In_ NEXUS_CONTROL_CONTEXT* Control, _In_opt_ WDFFILEOBJECT Owner);
static VOID NexusHidReleaseOwned(_In_ NEXUS_CONTROL_CONTEXT* Control, _In_opt_ WDFFILEOBJECT Owner);

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

    /*
     * vhf owns this stack's power policy, not this driver.
     *
     * The INF installs vhf as the function driver (VhfMode=2), and a function driver is the power
     * policy owner. Left at the default, this driver claimed ownership too, so on every hibernate,
     * restart and shutdown the D3 request vhf made passed through here unrequested: a
     * WUDFVerifierFailure at FxPkgFdo::DispatchDeviceSetPower, WUDFHost terminated, the device
     * offline after resume, and every other driver sharing that host taken down with it. While
     * 0.9.2.0 also loaded vhf twice, the kernel bugchecked on the same violation first and hid this.
     *
     * Ownership only. The device is not made a filter: WdfFdoInitSetFilter would also start
     * forwarding create and close to vhf, and the control handle PC Core opens belongs here.
     */
    WdfDeviceInitSetPowerPolicyOwnership(DeviceInit, FALSE);

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

/* -------------------------------------------------------------- identity */

/*
 * WHAT NEXUS CAN AND CANNOT TELL WINDOWS ABOUT ITS DEVICES.
 *
 * ContainerID is the whole list, and that is not an oversight - it is the only identity member of
 * VHF_CONFIG that Microsoft documents. The structure also carries InstanceID, InstanceIDLength,
 * HardwareIDs and HardwareIDsLength, and the reference page for VHF_CONFIG lists all four with
 * empty descriptions: no format, no units, no constraints, while every other member is described.
 *
 * They were used here, briefly, to give each device a deterministic hardware id containing Nexus's
 * vendor and product ids so a caller could find it inside a Raw Input device name. The result was
 * that VhfCreate and VhfStart both returned STATUS_SUCCESS and VHF never reported a device node at
 * all: no new devnode over five create cycles, and no entry in setupapi.dev.log, because Windows
 * was never asked to install anything. Undocumented fields, silent failure.
 *
 * So the driver names nothing. Identity is established on the client side from something Windows
 * does document - the device tree. Every device Nexus publishes descends from this control device,
 * and nothing else does, so ancestry answers "is this ours?" without the driver having to assert
 * anything a caller could also assert. See NexusHidPnpAncestry.
 *
 * ContainerID stays because it is documented, optional, and does something worth having: it tells
 * Windows the three devices are three functions of one piece of hardware rather than three unrelated
 * ones. {4E585330-...} begins with the ASCII bytes of "NXS0", the same tag the injection path uses
 * in dwExtraInfo.
 */
static const GUID NexusHidContainerId =
    { 0x4e585330, 0x1a2b, 0x4c3d, { 0x9e, 0x5f, 0x60, 0x71, 0x82, 0x93, 0xa4, 0xb5 } };

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
    _In_ UINT16 Length,
    _Out_ UINT8* ReportId
    )
{
    UINT32 bits = 0;
    UINT32 reportSize = 0;
    UINT32 reportCount = 0;
    UINT16 index = 0;
    BOOLEAN hasReportId = FALSE;

    *ReportId = 0;

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
        case 0x84:  /* REPORT_ID    */
            hasReportId = TRUE;

            /*
             * The first declared id is the input report's, because the descriptors put the input
             * collection before the feature report. Later ids belong to feature reports and must not
             * overwrite it.
             */
            if (*ReportId == 0) {
                *ReportId = (UINT8)value;
            }
            break;
        case 0x80:  /* INPUT        */ bits += reportSize * reportCount; break;
        default: break;
        }

        if (bits > NEXUS_HID_MAX_REPORT_LENGTH * 8) {
            return 0;   /* a descriptor claiming more than the transport allows */
        }
    }

    /*
     * A descriptor that declares report ids puts the id in the first byte of every report, and the
     * buffer has to allow for it. The digitizer uses ids; the mouse and keyboard do not.
     */
    return (UINT16)(((bits + 7) / 8) + (hasReportId ? 1 : 0));
}

static NTSTATUS
NexusHidCreateDevice(
    _In_ NEXUS_CONTROL_CONTEXT* Control,
    _In_ WDFFILEOBJECT Owner,
    _In_ const NEXUS_HID_CREATE_REQUEST* Request,
    _Out_ NEXUS_HID_CREATE_REPLY* Reply
    )
{
    NEXUS_HID_DEVICE* slot;
    VHF_CONFIG config;
    NTSTATUS status;
    UINT16 reportLength;
    UINT8 reportId;
    HANDLE fileHandle;

    RtlZeroMemory(Reply, sizeof(*Reply));

    if (Request->ProtocolVersion != NEXUS_HID_PROTOCOL_VERSION ||
        Request->DeviceKind >= NEXUS_HID_MAX_DEVICES ||
        Request->DescriptorLength == 0 ||
        Request->DescriptorLength > NEXUS_HID_MAX_DESCRIPTOR_LENGTH) {
        return STATUS_INVALID_DEVICE_REQUEST;
    }

    reportLength = NexusHidInputReportLength(Request->Descriptor, Request->DescriptorLength, &reportId);
    if (reportLength == 0 || reportLength > NEXUS_HID_MAX_REPORT_LENGTH) {
        return STATUS_INVALID_DEVICE_REQUEST;
    }

    slot = &Control->Devices[Request->DeviceKind];

    /*
     * A Ready slot belongs to somebody. If that is this caller, asking twice is a caller bug - the
     * device is already there. If it is another handle, this is still not an upgrade path: taking
     * the slot would delete a device another process is driving, possibly mid-gesture.
     *
     * Neither case survives a process exit any more, which is the point. This guard used to be
     * unreachable-by-design and reachable-in-fact: slots outlived the client that made them, so the
     * first PC Core run left three Ready slots behind and every later run was refused here, for the
     * life of the boot.
     */
    if (slot->State == NexusHidDeviceReady) {
        return slot->Owner == Owner ? STATUS_INVALID_DEVICE_REQUEST : STATUS_DEVICE_BUSY;
    }

    RtlCopyMemory(slot->Descriptor, Request->Descriptor, Request->DescriptorLength);
    slot->DescriptorLength = Request->DescriptorLength;
    slot->ExpectedReportLength = reportLength;
    slot->ReportId = reportId;

    /*
     * The user-mode VHF path. A UMDF driver passes a file handle where a KMDF driver passes a
     * device object - see VHF_CONFIG, whose FileHandle member is documented as "Required for
     * user-mode drivers".
     *
     * Two things here were wrong when this file was written from documentation alone, and the
     * compiler found both. WdfIoTargetWdmGetTargetFileHandle RETURNS the handle and takes a single
     * argument; it is not an NTSTATUS call with an out-parameter. And the handle must come from a
     * target opened with WdfIoTargetOpenLocalTargetByFile, not from the device's default I/O
     * target, which has no file object behind it.
     */
    if (Control->IoTarget == NULL) {
        WDFIOTARGET ioTarget;
        WDF_IO_TARGET_OPEN_PARAMS openParams;

        status = WdfIoTargetCreate(Control->Device, WDF_NO_OBJECT_ATTRIBUTES, &ioTarget);
        if (!NT_SUCCESS(status)) {
            goto Failed;
        }

        WDF_IO_TARGET_OPEN_PARAMS_INIT_OPEN_BY_FILE(&openParams, NULL);

        status = WdfIoTargetOpen(ioTarget, &openParams);
        if (!NT_SUCCESS(status)) {
            WdfObjectDelete(ioTarget);
            goto Failed;
        }

        Control->IoTarget = ioTarget;
    }

    fileHandle = WdfIoTargetWdmGetTargetFileHandle(Control->IoTarget);
    if (fileHandle == NULL) {
        status = STATUS_DEVICE_NOT_READY;
        goto Failed;
    }

    VHF_CONFIG_INIT(&config, fileHandle, slot->DescriptorLength, slot->Descriptor);

    config.VendorID      = Request->VendorId;
    config.ProductID     = Request->ProductId;
    config.VersionNumber = Request->VersionNumber;

    /*
     * The one identity field VHF documents. InstanceID and HardwareIDs are deliberately left unset -
     * see the note above NexusHidContainerId for what happened when they were not.
     */
    config.ContainerID = NexusHidContainerId;

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
    slot->Owner = Owner;

    Reply->State = NexusHidDeviceReady;
    Reply->Status = STATUS_SUCCESS;
    Reply->ContainerId = NexusHidContainerId;

    return STATUS_SUCCESS;

Failed:
    slot->State = NexusHidDeviceFailed;
    slot->Status = status;

    /* A failed create owns nothing. Leaving the owner set would make cleanup try to delete it. */
    slot->Owner = NULL;

    Reply->State = NexusHidDeviceFailed;
    Reply->Status = status;
    return STATUS_SUCCESS;   /* the IOCTL succeeded; the device did not. The reply says which. */
}

static NTSTATUS
NexusHidSubmitReport(
    _In_ NEXUS_CONTROL_CONTEXT* Control,
    _In_ WDFFILEOBJECT Owner,
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
     * Only the handle that created a device may drive it. Opening the control device is not
     * permission to move the pointer: without this, any process that could open it could submit
     * reports into devices PC Core published, and those reports arrive through the real HID stack
     * as hardware. Ownership is what makes "PC Core published this" mean "PC Core drives this".
     */
    if (slot->Owner != Owner) {
        return STATUS_ACCESS_DENIED;
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
    /*
     * The device's own report id, not zero.
     *
     * This said zero, with a comment explaining that the descriptors declare no report ids. That was
     * true when it was written and stopped being true when the digitizer gained REPORT_ID 1, which
     * Windows requires on a touch screen. Neutralisation was fixed at the time; this path was not,
     * so every real touch report would have gone out claiming to be report 0 - which the digitizer
     * does not declare. Two places had to agree and only one was changed.
     */
    packet.reportId = slot->ReportId;

    return VhfReadReportSubmit(slot->VhfHandle, &packet);
}

/*
 * Every published device is told to release everything. Called on control-channel loss, on file
 * handle close, and on device cleanup.
 *
 * Zero means released for every field that matters - buttons, modifiers, key slots, tip switch,
 * in range and contact count - so the body of a neutral report is simply zeroes.
 *
 * The exception is the report id, and it was a real defect. The digitizer declares REPORT_ID 1
 * because Windows requires one on a touch screen, so byte zero of its report is the id and zero is
 * not a valid id. An all-zero digitizer report is not a neutral touch report, it is a malformed
 * one - which is the worst possible failure for the path whose entire job is releasing a stuck
 * contact. Devices that declare no id keep a zero first byte, because for them that byte is data.
 */
static VOID
NexusHidNeutralizeOwned(
    _In_ NEXUS_CONTROL_CONTEXT* Control,
    _In_opt_ WDFFILEOBJECT Owner
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

        /* Someone else's device. Releasing it would be releasing input they are holding. */
        if (Owner != NULL && slot->Owner != Owner) {
            continue;
        }

        RtlZeroMemory(neutral, sizeof(neutral));

        if (slot->ReportId != 0) {
            neutral[0] = slot->ReportId;
        }

        RtlZeroMemory(&packet, sizeof(packet));
        packet.reportBuffer = neutral;
        packet.reportBufferLen = slot->ExpectedReportLength;
        packet.reportId = slot->ReportId;

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

    /*
     * Everything, not just the handle. A slot that kept its identity or its owner would be a slot
     * the next create could not cleanly reuse, and reuse is the whole point of deleting it.
     */
    slot->State = NexusHidDeviceAbsent;
    slot->Status = STATUS_SUCCESS;
    slot->ExpectedReportLength = 0;
    slot->ReportId = 0;
    slot->DescriptorLength = 0;
    slot->Owner = NULL;
}

/*
 * Release everything one owner published: neutralise first, then delete.
 *
 * The order is the whole reason this is one function. Neutralising after VhfDelete would be
 * submitting a report to a handle that no longer exists; deleting without neutralising first would
 * tear down a device with a button still down, and Windows would keep that button down until
 * something else happened to change it. Release means "let go, then leave".
 */
static VOID
NexusHidReleaseOwned(
    _In_ NEXUS_CONTROL_CONTEXT* Control,
    _In_opt_ WDFFILEOBJECT Owner
    )
{
    UINT32 kind;

    NexusHidNeutralizeOwned(Control, Owner);

    for (kind = 0; kind < NEXUS_HID_MAX_DEVICES; kind++) {
        if (Owner != NULL && Control->Devices[kind].Owner != Owner) {
            continue;
        }

        NexusHidDeleteDevice(Control, kind);
    }
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

    /*
     * Which handle is asking. Everything that creates, drives or destroys a device is answered
     * against this; the two read-only opcodes ignore it.
     */
    WDFFILEOBJECT owner = WdfRequestGetFileObject(Request);

    /*
     * No file object means no owner, and an unowned request must not be able to reach the owning
     * paths - NULL is the sentinel that means "every slot" inside them. Version and status stay
     * readable, because they change nothing.
     */
    if (owner == NULL &&
        IoControlCode != IOCTL_NEXUS_HID_GET_VERSION &&
        IoControlCode != IOCTL_NEXUS_HID_GET_STATUS) {
        WdfRequestComplete(Request, STATUS_INVALID_DEVICE_REQUEST);
        return;
    }

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
            owner,
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

        status = NexusHidSubmitReport(control, owner, (const NEXUS_HID_SUBMIT_REQUEST*)input);
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

            /*
             * The container id, which is the only identity the driver assigns. The instance id
             * stays zero because VHF gives a source driver no supported way to learn what Windows
             * called the device it created - the client resolves that from the device tree.
             */
            reply->Devices[kind].ContainerId = NexusHidContainerId;
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

        /* This caller's devices, not every device. Neutralising is releasing held input, and
         * releasing input another owner is holding is producing input they did not ask for. */
        NexusHidNeutralizeOwned(control, owner);
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

        /*
         * Only the owner may delete. Otherwise a second handle - a diagnostic tool, a stray
         * process - could remove a device PC Core is driving and leave it submitting into nothing.
         */
        if (control->Devices[request->DeviceKind].State != NexusHidDeviceAbsent &&
            control->Devices[request->DeviceKind].Owner != owner) {
            status = STATUS_ACCESS_DENIED;
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
 * The control handle closing is how the driver learns its owner is gone - whether it exited,
 * crashed, or was killed. Everything that handle published is released: neutralised, then deleted.
 *
 * DELETED, NOT JUST NEUTRALISED. This used to only neutralise, and the devices stayed. Because the
 * slot state lives in the control device's context - which belongs to the WDFDEVICE and outlives
 * every client handle - the three devices from a dead PC Core stayed Ready for the rest of the
 * boot, and every later PC Core was refused by the already-exists guard in NexusHidCreateDevice. A
 * driver that cannot let go of a dead client's devices can only ever serve one client per boot.
 *
 * Only this owner's devices. Another handle's devices are another handle's business, and a
 * diagnostic tool opening the control device to read status must not take three HID devices down
 * with it when it exits.
 *
 * This remains a watchdog in the only safe sense: it releases state, it never generates state. A
 * timer that decided on its own to press or move something would be a driver producing input nobody
 * asked for, which is precisely what this design exists to prevent.
 */
VOID
NexusHidEvtFileCleanup(
    _In_ WDFFILEOBJECT FileObject
    )
{
    NEXUS_CONTROL_CONTEXT* control = ControlGetContext(WdfFileObjectGetDevice(FileObject));

    WdfWaitLockAcquire(control->Lock, NULL);
    NexusHidReleaseOwned(control, FileObject);
    WdfWaitLockRelease(control->Lock);
}

VOID
NexusHidEvtDeviceCleanup(
    _In_ WDFOBJECT Object
    )
{
    NEXUS_CONTROL_CONTEXT* control = ControlGetContext((WDFDEVICE)Object);

    /* The device itself is going away, so there is no owner left to respect. */
    NexusHidReleaseOwned(control, NULL);
}
