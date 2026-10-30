using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace RtxMonitor.Lab;

public sealed record WindowsHandleIdentityReport(
    int SchemaVersion,
    string SourceKind,
    string CapturedUtc,
    int ProcessId,
    string ProcessImageName,
    string ProcessImageSha256,
    string Handle,
    string ObjectType,
    string? ObjectName,
    string? DosDeviceAlias,
    string Warning);

public static class WindowsHandleIdentity
{
    private const uint ProcessDuplicateHandle = 0x0040;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint DuplicateSameAccess = 0x00000002;
    private const int ObjectNameInformation = 1;
    private const int ObjectTypeInformation = 2;
    private const int StatusInfoLengthMismatch = unchecked((int)0xc0000004);
    private const int StatusBufferOverflow = unchecked((int)0x80000005);
    private const int StatusBufferTooSmall = unchecked((int)0xc0000023);
    private const int ErrorInsufficientBuffer = 122;
    private const int InitialNativeBufferSize = 4096;
    private const int MaximumNativeBufferSize = 1024 * 1024;

    public static WindowsHandleIdentityReport Resolve(int processId, string handle)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Windows handle identity resolution is supported only on Windows.");
        }

        if (processId <= 0)
        {
            throw new WindowsHandleIdentityException(
                "The process ID must be a positive integer.");
        }

        nint sourceHandle = ParseHandle(handle);
        nint processHandle = OpenProcess(
            ProcessDuplicateHandle | ProcessQueryLimitedInformation,
            inheritHandle: false,
            processId);
        if (processHandle == 0)
        {
            throw Win32Failure("OpenProcess", Marshal.GetLastPInvokeError());
        }

        nint duplicate = 0;
        try
        {
            if (!DuplicateHandle(
                    processHandle,
                    sourceHandle,
                    GetCurrentProcess(),
                    out duplicate,
                    desiredAccess: 0,
                    inheritHandle: false,
                    DuplicateSameAccess))
            {
                throw Win32Failure("DuplicateHandle", Marshal.GetLastPInvokeError());
            }

            string imagePath = QueryProcessImagePath(processHandle);
            string objectType = QueryUnicodeObjectInformation(
                    duplicate,
                    ObjectTypeInformation)
                ?? throw new WindowsHandleIdentityException(
                    "NtQueryObject returned an empty object type.");
            string? objectName = QueryUnicodeObjectInformation(
                duplicate,
                ObjectNameInformation);
            string? alias = objectName is null ? null : FindDosDeviceAlias(objectName);

            return new WindowsHandleIdentityReport(
                SchemaVersion: 1,
                SourceKind: "windows_handle_identity",
                CapturedUtc: DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                ProcessId: processId,
                ProcessImageName: Path.GetFileName(imagePath),
                ProcessImageSha256: Sha256File(imagePath),
                Handle: NormalizeHandle(sourceHandle),
                ObjectType: objectType,
                ObjectName: objectName,
                DosDeviceAlias: alias,
                Warning: "Resolving a handle name proves the operating-system object used by the observed call. It does not identify the IOCTL ABI, returned fields, units, or a physical sensor.");
        }
        finally
        {
            if (duplicate != 0)
            {
                _ = CloseHandle(duplicate);
            }

            _ = CloseHandle(processHandle);
        }
    }

    internal static nint ParseHandle(string value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !value.StartsWith("0x", StringComparison.Ordinal) ||
            value.Length is < 3 or > 18 ||
            !ulong.TryParse(
                value.AsSpan(2),
                NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture,
                out ulong parsed) ||
            parsed == 0 ||
            parsed > (ulong)nint.MaxValue)
        {
            throw new WindowsHandleIdentityException(
                "The handle must be a non-zero hexadecimal value prefixed with '0x'.");
        }

        return (nint)parsed;
    }

    internal static string NormalizeHandle(nint handle) =>
        IntPtr.Size == 8
            ? $"0x{(ulong)handle:x16}"
            : $"0x{(uint)handle:x8}";

    private static string QueryProcessImagePath(nint processHandle)
    {
        var path = new StringBuilder(32768);
        int length = path.Capacity;
        if (!QueryFullProcessImageName(processHandle, flags: 0, path, ref length))
        {
            throw Win32Failure(
                "QueryFullProcessImageName",
                Marshal.GetLastPInvokeError());
        }

        return path.ToString(0, length);
    }

    private static string? QueryUnicodeObjectInformation(
        nint handle,
        int informationClass)
    {
        int capacity = InitialNativeBufferSize;
        for (int attempt = 0; attempt < 8; attempt++)
        {
            nint buffer = Marshal.AllocHGlobal(capacity);
            try
            {
                int status = NtQueryObject(
                    handle,
                    informationClass,
                    buffer,
                    capacity,
                    out int requiredLength);
                if (status == 0)
                {
                    NativeUnicodeString value =
                        Marshal.PtrToStructure<NativeUnicodeString>(buffer);
                    return value.Length == 0 || value.Buffer == 0
                        ? null
                        : Marshal.PtrToStringUni(value.Buffer, value.Length / 2);
                }

                if (status is not (StatusInfoLengthMismatch or
                    StatusBufferOverflow or StatusBufferTooSmall))
                {
                    throw new WindowsHandleIdentityException(
                        $"NtQueryObject failed with NTSTATUS 0x{(uint)status:x8}.");
                }

                int proposed = Math.Max(requiredLength, checked(capacity * 2));
                if (proposed <= capacity || proposed > MaximumNativeBufferSize)
                {
                    throw new WindowsHandleIdentityException(
                        "NtQueryObject requested an invalid or excessive buffer size.");
                }

                capacity = proposed;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        throw new WindowsHandleIdentityException(
            "NtQueryObject did not converge within the bounded retry count.");
    }

    private static string? FindDosDeviceAlias(string objectName)
    {
        foreach (string alias in EnumerateDosDeviceAliases())
        {
            foreach (string target in QueryDosDeviceTargets(alias))
            {
                if (string.Equals(target, objectName, StringComparison.OrdinalIgnoreCase))
                {
                    return @"\\.\" + alias;
                }
            }
        }

        return null;
    }

    private static IReadOnlyList<string> EnumerateDosDeviceAliases()
    {
        var buffer = new char[InitialNativeBufferSize];
        while (true)
        {
            uint length = QueryDosDevice(null, buffer, buffer.Length);
            if (length != 0)
            {
                return ParseMultiString(buffer, checked((int)length));
            }

            int error = Marshal.GetLastPInvokeError();
            if (error != ErrorInsufficientBuffer ||
                buffer.Length >= MaximumNativeBufferSize)
            {
                throw Win32Failure("QueryDosDevice", error);
            }

            buffer = new char[checked(buffer.Length * 2)];
        }
    }

    private static IReadOnlyList<string> QueryDosDeviceTargets(string alias)
    {
        var buffer = new char[InitialNativeBufferSize];
        while (true)
        {
            uint length = QueryDosDevice(alias, buffer, buffer.Length);
            if (length != 0)
            {
                return ParseMultiString(buffer, checked((int)length));
            }

            int error = Marshal.GetLastPInvokeError();
            if (error != ErrorInsufficientBuffer ||
                buffer.Length >= MaximumNativeBufferSize)
            {
                return Array.Empty<string>();
            }

            buffer = new char[checked(buffer.Length * 2)];
        }
    }

    private static IReadOnlyList<string> ParseMultiString(char[] buffer, int length)
    {
        var values = new List<string>();
        int start = 0;
        for (int index = 0; index < length; index++)
        {
            if (buffer[index] != '\0')
            {
                continue;
            }

            if (index == start)
            {
                break;
            }

            values.Add(new string(buffer, start, index - start));
            start = index + 1;
        }

        return values;
    }

    private static string Sha256File(string path)
    {
        using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static WindowsHandleIdentityException Win32Failure(
        string operation,
        int error) =>
        new($"{operation} failed: {new Win32Exception(error).Message} (Win32 {error}).");

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativeUnicodeString
    {
        internal readonly ushort Length;
        internal readonly ushort MaximumLength;
        internal readonly nint Buffer;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateHandle(
        nint sourceProcessHandle,
        nint sourceHandle,
        nint targetProcessHandle,
        out nint targetHandle,
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        uint options);

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(
        nint processHandle,
        uint flags,
        StringBuilder executableName,
        ref int size);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryObject(
        nint handle,
        int objectInformationClass,
        nint objectInformation,
        int objectInformationLength,
        out int returnLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint QueryDosDevice(
        string? deviceName,
        [Out] char[] targetPath,
        int maximumLength);
}

public sealed class WindowsHandleIdentityException : Exception
{
    public WindowsHandleIdentityException(string message)
        : base(message)
    {
    }
}
