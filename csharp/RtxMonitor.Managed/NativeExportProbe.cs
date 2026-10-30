using System.Reflection;
using System.Runtime.InteropServices;

namespace RtxMonitor.Managed;

internal static class NativeExportProbe
{
    internal static bool IsAvailable(
        string libraryName,
        string exportName,
        Assembly assembly) => Probe(
            () => NativeLibrary.TryLoad(
                libraryName,
                assembly,
                searchPath: null,
                out IntPtr handle)
                ? handle
                : IntPtr.Zero,
            handle => NativeLibrary.TryGetExport(handle, exportName, out _),
            NativeLibrary.Free);

    internal static bool Probe(
        Func<IntPtr> load,
        Func<IntPtr, bool> hasExport,
        Action<IntPtr> release)
    {
        ArgumentNullException.ThrowIfNull(load);
        ArgumentNullException.ThrowIfNull(hasExport);
        ArgumentNullException.ThrowIfNull(release);

        IntPtr handle = load();
        if (handle == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            return hasExport(handle);
        }
        finally
        {
            release(handle);
        }
    }

    internal static NativeStatus InvokeOptional(
        bool available,
        Func<NativeStatus> invoke)
    {
        ArgumentNullException.ThrowIfNull(invoke);
        if (!available)
        {
            return NativeStatus.NotSupported;
        }

        try
        {
            return invoke();
        }
        catch (EntryPointNotFoundException)
        {
            return NativeStatus.NotSupported;
        }
    }
}
