using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace PcAssistant.Windows;

internal static class ShellApplications
{
    internal sealed record PackagedApp(string Name, string AppUserModelId);

    internal static IReadOnlyList<PackagedApp> Discover(CancellationToken cancellationToken)
    {
        object? shell = null, folder = null, items = null;
        try
        {
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application", throwOnError: true)!);
            folder = ((dynamic)shell!).NameSpace("shell:AppsFolder");
            if (folder is null) throw new InvalidOperationException("The Windows applications folder is unavailable.");
            items = ((dynamic)folder).Items();
            var result = new List<PackagedApp>();
            var count = Math.Min((int)((dynamic)items).Count, 2000);
            for (var i = 0; i < count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                object? item = null;
                try
                {
                    item = ((dynamic)items).Item(i);
                    var id = ((dynamic)item).ExtendedProperty("System.AppUserModel.ID") as string;
                    // Packaged application identifiers only; desktop shortcut command strings are never launched.
                    if (id is not null && id.Length <= 256 && id.Contains('!'))
                        result.Add(new PackagedApp((string)((dynamic)item).Name, id));
                }
                finally { Release(item); }
            }
            return result;
        }
        finally { Release(items); Release(folder); Release(shell); }
    }

    internal static uint Activate(string appUserModelId)
    {
        object? instance = null;
        try
        {
            instance = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C"), throwOnError: true)!);
            ((IApplicationActivationManager)instance!).ActivateApplication(appUserModelId, null, 0, out var processId);
            return processId;
        }
        finally { Release(instance); }
    }

    private static void Release(object? value) { if (value is not null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value); }

    [ComImport, Guid("2e941141-7f97-4756-ba1d-9decde894a3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationActivationManager
    {
        void ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId, [MarshalAs(UnmanagedType.LPWStr)] string? arguments, uint options, out uint processId);
        void ActivateForFile([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId, nint itemArray, [MarshalAs(UnmanagedType.LPWStr)] string? verb, out uint processId);
        void ActivateForProtocol([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId, nint itemArray, out uint processId);
    }
}
