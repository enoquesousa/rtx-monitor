using System.Buffers;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace RtxMonitor.Lab;

public sealed record LabDeviceMetadata(
    string? Gpu,
    string? DriverVersion,
    string? VbiosVersion);

public sealed record LabArtifactManifest(
    string RelativePath,
    string OriginalFileName,
    long SizeBytes,
    string Sha256);

public sealed record LabPackageManifest(
    int SchemaVersion,
    string SourceKind,
    LabArtifactManifest Artifact,
    LabDeviceMetadata Device);

public sealed record LabPackageResult(
    string PackagePath,
    string ManifestSha256,
    LabPackageManifest Manifest);

public sealed class LabPackageException : Exception
{
    public LabPackageException(string message)
        : base(message)
    {
    }

    public LabPackageException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

internal sealed record LabPackageCreateTestHooks(
    Action<string, string> WhileFilesLocked,
    Action<string, string> AfterFilesUnlockedBeforeMove);

public static class LabPackage
{
    public const int ManifestSchemaVersion = 1;
    public const string SourceKind = "user_provided_local_file";
    public const string ManifestFileName = "manifest.json";
    public const string ArtifactDirectoryName = "artifact";
    public const string ArtifactFileName = "payload.bin";
    public const string ArtifactRelativePath = "artifact/payload.bin";
    public const long MaximumPayloadSizeBytes = 256L * 1024 * 1024;

    private const int CopyBufferSize = 128 * 1024;
    private const long MaximumManifestSize = 64 * 1024;
    private const int MaximumWindowsPathBuffer = 32_768;
    private const string ExtendedPathPrefix = @"\\?\";
    private const string ExtendedUncPrefix = @"\\?\UNC\";

    public static LabPackageResult Create(
        string inputPath,
        string packagePath,
        LabDeviceMetadata? device = null) =>
        CreateCore(inputPath, packagePath, device, testHooks: null);

    internal static LabPackageResult CreateForTesting(
        string inputPath,
        string packagePath,
        LabDeviceMetadata? device,
        LabPackageCreateTestHooks testHooks)
    {
        ArgumentNullException.ThrowIfNull(testHooks);
        return CreateCore(inputPath, packagePath, device, testHooks);
    }

    private static LabPackageResult CreateCore(
        string inputPath,
        string packagePath,
        LabDeviceMetadata? device,
        LabPackageCreateTestHooks? testHooks)
    {
        EnsureSupportedPlatform();
        string resolvedInput = ResolvePath(inputPath, "input artifact");
        ValidateExistingPathComponents(
            resolvedInput,
            "input artifact",
            requireLeaf: true,
            expectedLeafKind: PathEntryKind.RegularFile);
        RejectAlternateDataStreams(resolvedInput, "input artifact");
        string resolvedPackage = ResolvePackagePath(packagePath);
        ValidateExistingPathComponents(
            resolvedPackage,
            "package output path",
            requireLeaf: false,
            expectedLeafKind: PathEntryKind.Any);
        EnsurePackageDoesNotExist(resolvedPackage);

        string originalFileName = ValidateOriginalFileName(Path.GetFileName(resolvedInput));
        LabDeviceMetadata normalizedDevice = NormalizeDevice(device ?? new(null, null, null));
        string parentDirectory = Path.GetDirectoryName(resolvedPackage)
            ?? throw new LabPackageException("The package path must have a parent directory.");
        Directory.CreateDirectory(parentDirectory);
        ValidateExistingPathComponents(
            parentDirectory,
            "package parent directory",
            requireLeaf: true,
            expectedLeafKind: PathEntryKind.Directory);

        string packageName = Path.GetFileName(resolvedPackage);
        string stagingPath = Path.Combine(
            parentDirectory,
            $".{packageName}.staging-{Guid.NewGuid():N}");

        try
        {
            EnsurePackageDoesNotExist(stagingPath);
            Directory.CreateDirectory(stagingPath);
            ValidateExistingPathComponents(
                stagingPath,
                "staging directory",
                requireLeaf: true,
                expectedLeafKind: PathEntryKind.Directory);
            if (Directory.EnumerateFileSystemEntries(stagingPath).Any())
            {
                throw new LabPackageException("The new staging directory is not empty.");
            }

            string artifactDirectory = Path.Combine(stagingPath, ArtifactDirectoryName);
            Directory.CreateDirectory(artifactDirectory);
            ValidateExistingPathComponents(
                artifactDirectory,
                "staging artifact directory",
                requireLeaf: true,
                expectedLeafKind: PathEntryKind.Directory);
            string artifactPath = Path.Combine(artifactDirectory, ArtifactFileName);

            string manifestPath = Path.Combine(stagingPath, ManifestFileName);
            LabPackageManifest manifest;
            string manifestSha256;
            WindowsFileIdentity manifestIdentity;
            WindowsFileIdentity artifactIdentity;
            byte[] expectedManifestHash;
            byte[] expectedArtifactHash;
            long sizeBytes;

            // NTFS does not allow renaming a directory while descendant files are open.
            // Keep both staged files exclusively locked until the last possible moment,
            // then re-open and re-hash both files immediately after publication.
            {
                using FileStream source = OpenRegularFileForRead(
                    resolvedInput,
                    "input artifact",
                    MaximumPayloadSizeBytes,
                    allowEmpty: true,
                    requireSingleLink: true,
                    out long sourceLength,
                    out WindowsFileIdentity sourceIdentity);
                using FileStream artifact = CreateNewFileForValidation(artifactPath);
                (sizeBytes, string sha256, artifactIdentity) = CopyAndHash(
                    source,
                    sourceLength,
                    sourceIdentity,
                    resolvedInput,
                    artifact,
                    artifactPath);
                expectedArtifactHash = Convert.FromHexString(sha256);
                manifest = new LabPackageManifest(
                    ManifestSchemaVersion,
                    SourceKind,
                    new LabArtifactManifest(
                        ArtifactRelativePath,
                        originalFileName,
                        sizeBytes,
                        sha256),
                    normalizedDevice);

                byte[] manifestBytes = LabJson.SerializeManifestUtf8(
                    manifest,
                    appendNewLine: true);
                using FileStream manifestFile = CreateNewFileForValidation(manifestPath);
                manifestIdentity = WriteNewFile(
                    manifestFile,
                    manifestPath,
                    manifestBytes,
                    "staged manifest");
                expectedManifestHash = SHA256.HashData(manifestBytes);
                manifestSha256 = Convert.ToHexString(expectedManifestHash).ToLowerInvariant();

                ValidateExistingPathComponents(
                    manifestPath,
                    "staged manifest",
                    requireLeaf: true,
                    expectedLeafKind: PathEntryKind.RegularFile);
                ValidateExistingPathComponents(
                    artifactPath,
                    "staged artifact payload",
                    requireLeaf: true,
                    expectedLeafKind: PathEntryKind.RegularFile);
                RejectAlternateDataStreams(stagingPath, "staging directory");
                RejectAlternateDataStreams(artifactDirectory, "staging artifact directory");
                RejectAlternateDataStreams(manifestPath, "staged manifest");
                RejectAlternateDataStreams(artifactPath, "staged artifact payload");
                ValidateOpenedFileIdentity(
                    manifestFile,
                    manifestPath,
                    "staged manifest",
                    manifestIdentity,
                    requireSingleLink: true);
                ValidateOpenedFileIdentity(
                    artifact,
                    artifactPath,
                    "staged artifact payload",
                    artifactIdentity,
                    requireSingleLink: true);

                testHooks?.WhileFilesLocked(manifestPath, artifactPath);
            }

            testHooks?.AfterFilesUnlockedBeforeMove(manifestPath, artifactPath);

            ValidateExistingPathComponents(
                parentDirectory,
                "package parent directory",
                requireLeaf: true,
                expectedLeafKind: PathEntryKind.Directory);
            EnsurePackageDoesNotExist(resolvedPackage);
            Directory.Move(stagingPath, resolvedPackage);
            stagingPath = string.Empty;

            string publishedManifestPath = Path.Combine(resolvedPackage, ManifestFileName);
            string publishedArtifactPath = Path.Combine(
                resolvedPackage,
                ArtifactDirectoryName,
                ArtifactFileName);
            try
            {
                ValidatePublishedPackage(
                    resolvedPackage,
                    publishedManifestPath,
                    publishedArtifactPath,
                    manifestIdentity,
                    artifactIdentity,
                    expectedManifestHash,
                    expectedArtifactHash);
            }
            catch (Exception error) when (
                error is LabPackageException or
                    IOException or
                    UnauthorizedAccessException or
                    CryptographicException)
            {
                throw new LabPackageException(
                    $"Published package validation failed: {error.Message} " +
                    "The package was retained without a success result; treat it as untrusted.",
                    error);
            }

            return new LabPackageResult(resolvedPackage, manifestSha256, manifest);
        }
        catch (LabPackageException)
        {
            throw;
        }
        catch (Exception error) when (
            error is IOException or UnauthorizedAccessException or CryptographicException)
        {
            throw new LabPackageException($"Could not create the lab package: {error.Message}", error);
        }
        finally
        {
            if (stagingPath.Length > 0)
            {
                DeleteOwnedStagingDirectory(stagingPath, parentDirectory, packageName);
            }
        }
    }

    public static LabPackageResult Verify(
        string packagePath,
        string expectedManifestSha256)
    {
        EnsureSupportedPlatform();
        ValidateSha256(expectedManifestSha256, "expected manifest SHA-256");
        try
        {
            string resolvedPackage = ResolvePath(packagePath, "package");
            ValidateExistingPathComponents(
                resolvedPackage,
                "package",
                requireLeaf: true,
                expectedLeafKind: PathEntryKind.Directory);
            VerifyPackageLayout(resolvedPackage);

            string manifestPath = Path.Combine(resolvedPackage, ManifestFileName);
            string artifactDirectory = Path.Combine(resolvedPackage, ArtifactDirectoryName);
            string artifactPath = Path.Combine(artifactDirectory, ArtifactFileName);

            (byte[] manifestBytes, byte[] actualManifestHash) = ReadManifestAndHash(
                manifestPath);
            byte[] expectedManifestHash = Convert.FromHexString(expectedManifestSha256);
            if (!CryptographicOperations.FixedTimeEquals(
                    expectedManifestHash,
                    actualManifestHash))
            {
                throw new LabPackageException(
                    "Manifest SHA-256 does not match the expected trust anchor.");
            }

            LabPackageManifest manifest = ParseManifest(manifestBytes);
            string resolvedFromManifest = ResolveContainedPath(
                resolvedPackage,
                manifest.Artifact.RelativePath);
            if (!string.Equals(resolvedFromManifest, artifactPath, PathComparison))
            {
                throw new LabPackageException(
                    "The manifest artifact path does not identify the package payload.");
            }

            ValidateExistingPathComponents(
                artifactDirectory,
                "artifact directory",
                requireLeaf: true,
                expectedLeafKind: PathEntryKind.Directory);
            using FileStream payload = OpenRegularFileForRead(
                artifactPath,
                "artifact payload",
                MaximumPayloadSizeBytes,
                allowEmpty: true,
                requireSingleLink: true,
                out long payloadLength,
                out WindowsFileIdentity payloadIdentity);
            if (payloadLength != manifest.Artifact.SizeBytes)
            {
                throw new LabPackageException(
                    $"Artifact size mismatch: expected {manifest.Artifact.SizeBytes}, found {payloadLength}.");
            }

            byte[] actualPayloadHash = HashBoundedStream(
                payload,
                payloadLength,
                MaximumPayloadSizeBytes,
                "artifact payload",
                artifactPath,
                payloadIdentity,
                requireSingleLink: true);
            byte[] expectedPayloadHash = Convert.FromHexString(manifest.Artifact.Sha256);
            if (!CryptographicOperations.FixedTimeEquals(
                    expectedPayloadHash,
                    actualPayloadHash))
            {
                throw new LabPackageException("Artifact SHA-256 mismatch.");
            }

            return new LabPackageResult(
                resolvedPackage,
                Convert.ToHexString(actualManifestHash).ToLowerInvariant(),
                manifest);
        }
        catch (LabPackageException)
        {
            throw;
        }
        catch (Exception error) when (
            error is IOException or UnauthorizedAccessException or CryptographicException)
        {
            throw new LabPackageException($"Could not verify the lab package: {error.Message}", error);
        }
    }

    private static (long SizeBytes, string Sha256, WindowsFileIdentity DestinationIdentity)
        CopyAndHash(
        FileStream source,
        long expectedLength,
        WindowsFileIdentity sourceIdentity,
        string sourcePath,
        FileStream destination,
        string destinationPath)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        byte[] buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        long totalBytes = 0;
        try
        {
            while (true)
            {
                int read = source.Read(buffer, 0, buffer.Length);
                if (read == 0)
                {
                    break;
                }

                if (totalBytes > MaximumPayloadSizeBytes - read)
                {
                    throw new LabPackageException(
                        $"The input artifact exceeds the {MaximumPayloadSizeBytes}-byte limit.");
                }

                destination.Write(buffer, 0, read);
                hash.AppendData(buffer, 0, read);
                totalBytes = checked(totalBytes + read);
            }

            destination.Flush(flushToDisk: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
            ArrayPool<byte>.Shared.Return(buffer);
        }

        if (totalBytes != expectedLength ||
            source.Length != expectedLength ||
            destination.Position != totalBytes)
        {
            throw new LabPackageException(
                "The input artifact changed while it was being copied.");
        }

        ValidateOpenedFileIdentity(
            source,
            sourcePath,
            "input artifact",
            sourceIdentity,
            requireSingleLink: true);
        WindowsFileIdentity destinationIdentity = CaptureOpenedFileIdentity(
            destination,
            destinationPath,
            "staged artifact payload",
            requireSingleLink: true);
        if (destinationIdentity.Length != totalBytes)
        {
            throw new LabPackageException(
                "The staged artifact payload changed while it was being written.");
        }

        return (
            totalBytes,
            Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(),
            destinationIdentity);
    }

    private static FileStream CreateNewFileForValidation(string path) =>
        new(
            path,
            new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.ReadWrite,
                Share = FileShare.None,
                BufferSize = CopyBufferSize,
                Options = FileOptions.SequentialScan | FileOptions.WriteThrough,
            });

    private static FileStream OpenRegularFileForRead(
        string path,
        string description,
        long maximumLength,
        bool allowEmpty,
        bool requireSingleLink,
        out long length,
        out WindowsFileIdentity identity,
        FileShare fileShare = FileShare.Read)
    {
        ValidateExistingPathComponents(
            path,
            description,
            requireLeaf: true,
            expectedLeafKind: PathEntryKind.RegularFile);
        RejectAlternateDataStreams(path, description);
        var stream = new FileStream(
            path,
            new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = fileShare,
                BufferSize = CopyBufferSize,
                Options = FileOptions.SequentialScan,
            });
        try
        {
            if (!stream.CanRead || !stream.CanSeek)
            {
                throw new LabPackageException(
                    $"The {description} must be a seekable regular file.");
            }

            identity = CaptureOpenedFileIdentity(
                stream,
                path,
                description,
                requireSingleLink);
            length = identity.Length;
            if ((!allowEmpty && length == 0) || length < 0 || length > maximumLength)
            {
                string lowerBound = allowEmpty ? "0" : "1";
                throw new LabPackageException(
                    $"The {description} must contain between {lowerBound} and {maximumLength} bytes.");
            }

            ValidateExistingPathComponents(
                path,
                description,
                requireLeaf: true,
                expectedLeafKind: PathEntryKind.RegularFile);
            RejectAlternateDataStreams(path, description);
            ValidateOpenedFileIdentity(
                stream,
                path,
                description,
                identity,
                requireSingleLink);
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private static WindowsFileIdentity WriteNewFile(
        FileStream stream,
        string path,
        ReadOnlySpan<byte> content,
        string description)
    {
        stream.Write(content);
        stream.Flush(flushToDisk: true);
        WindowsFileIdentity identity = CaptureOpenedFileIdentity(
            stream,
            path,
            description,
            requireSingleLink: true);
        if (identity.Length != content.Length || stream.Position != content.Length)
        {
            throw new LabPackageException($"The {description} changed while it was being written.");
        }

        return identity;
    }

    private static void ValidateExpectedHash(
        FileStream stream,
        long expectedLength,
        long maximumLength,
        string description,
        string path,
        WindowsFileIdentity identity,
        ReadOnlySpan<byte> expectedHash)
    {
        stream.Position = 0;
        byte[] actualHash = HashBoundedStream(
            stream,
            expectedLength,
            maximumLength,
            description,
            path,
            identity,
            requireSingleLink: true);
        if (!CryptographicOperations.FixedTimeEquals(expectedHash, actualHash))
        {
            throw new LabPackageException($"The {description} SHA-256 changed during publication.");
        }
    }

    private static void ValidatePublishedPackage(
        string packagePath,
        string manifestPath,
        string artifactPath,
        WindowsFileIdentity expectedManifestIdentity,
        WindowsFileIdentity expectedArtifactIdentity,
        ReadOnlySpan<byte> expectedManifestHash,
        ReadOnlySpan<byte> expectedArtifactHash)
    {
        VerifyPackageLayout(packagePath);
        using FileStream manifest = OpenRegularFileForRead(
            manifestPath,
            "created package manifest",
            MaximumManifestSize,
            allowEmpty: false,
            requireSingleLink: true,
            out long manifestLength,
            out WindowsFileIdentity manifestIdentity,
            fileShare: FileShare.None);
        using FileStream artifact = OpenRegularFileForRead(
            artifactPath,
            "created package payload",
            MaximumPayloadSizeBytes,
            allowEmpty: true,
            requireSingleLink: true,
            out long artifactLength,
            out WindowsFileIdentity artifactIdentity,
            fileShare: FileShare.None);
        EnsureSameFileIdentity(
            expectedManifestIdentity,
            manifestIdentity,
            "created package manifest");
        EnsureSameFileIdentity(
            expectedArtifactIdentity,
            artifactIdentity,
            "created package payload");
        ValidateExpectedHash(
            manifest,
            manifestLength,
            MaximumManifestSize,
            "created package manifest",
            manifestPath,
            manifestIdentity,
            expectedManifestHash);
        ValidateExpectedHash(
            artifact,
            artifactLength,
            MaximumPayloadSizeBytes,
            "created package payload",
            artifactPath,
            artifactIdentity,
            expectedArtifactHash);
        VerifyPackageLayout(packagePath);
        ValidateOpenedFileIdentity(
            manifest,
            manifestPath,
            "created package manifest",
            manifestIdentity,
            requireSingleLink: true);
        ValidateOpenedFileIdentity(
            artifact,
            artifactPath,
            "created package payload",
            artifactIdentity,
            requireSingleLink: true);
    }

    private static (byte[] Content, byte[] Sha256) ReadManifestAndHash(
        string manifestPath)
    {
        using FileStream manifest = OpenRegularFileForRead(
            manifestPath,
            "package manifest",
            MaximumManifestSize,
            allowEmpty: false,
            requireSingleLink: true,
            out long manifestLength,
            out WindowsFileIdentity manifestIdentity);
        byte[] content = new byte[checked((int)manifestLength)];
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        int totalRead = 0;
        while (totalRead < content.Length)
        {
            int read = manifest.Read(content, totalRead, content.Length - totalRead);
            if (read == 0)
            {
                throw new LabPackageException(
                    "The package manifest changed while it was being read.");
            }

            hash.AppendData(content, totalRead, read);
            totalRead += read;
        }

        if (manifest.ReadByte() != -1 ||
            manifest.Length != manifestLength ||
            totalRead != manifestLength)
        {
            throw new LabPackageException("The package manifest changed while it was being read.");
        }

        ValidateOpenedFileIdentity(
            manifest,
            manifestPath,
            "package manifest",
            manifestIdentity,
            requireSingleLink: true);

        return (content, hash.GetHashAndReset());
    }

    private static byte[] HashBoundedStream(
        FileStream stream,
        long expectedLength,
        long maximumLength,
        string description,
        string path,
        WindowsFileIdentity identity,
        bool requireSingleLink)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        long totalRead = 0;
        try
        {
            while (true)
            {
                int read = stream.Read(buffer, 0, buffer.Length);
                if (read == 0)
                {
                    break;
                }

                if (totalRead > maximumLength - read)
                {
                    throw new LabPackageException(
                        $"The {description} exceeds the {maximumLength}-byte limit.");
                }

                hash.AppendData(buffer, 0, read);
                totalRead = checked(totalRead + read);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
            ArrayPool<byte>.Shared.Return(buffer);
        }

        if (totalRead != expectedLength || stream.Length != expectedLength)
        {
            throw new LabPackageException(
                $"The {description} changed while it was being verified.");
        }

        ValidateOpenedFileIdentity(
            stream,
            path,
            description,
            identity,
            requireSingleLink);

        return hash.GetHashAndReset();
    }

    private static WindowsFileIdentity CaptureOpenedFileIdentity(
        FileStream stream,
        string expectedPath,
        string description,
        bool requireSingleLink)
    {
        if (!GetFileInformationByHandle(stream.SafeFileHandle, out ByHandleFileInformation info))
        {
            int error = Marshal.GetLastPInvokeError();
            throw new LabPackageException(
                $"Could not inspect the opened {description} handle (Win32 {error}).");
        }

        FileAttributes attributes = info.FileAttributes;
        if ((attributes &
             (FileAttributes.Directory |
              FileAttributes.Device |
              FileAttributes.ReparsePoint)) != 0)
        {
            throw new LabPackageException($"The opened {description} is not a regular file.");
        }

        if (info.NumberOfLinks == 0)
        {
            throw new LabPackageException($"The opened {description} has no filesystem link.");
        }

        if (requireSingleLink && info.NumberOfLinks != 1)
        {
            throw new LabPackageException(
                $"The {description} must have exactly one filesystem link; found " +
                $"{info.NumberOfLinks}.");
        }

        ulong unsignedLength = ((ulong)info.FileSizeHigh << 32) | info.FileSizeLow;
        if (unsignedLength > long.MaxValue)
        {
            throw new LabPackageException($"The opened {description} is too large to inspect.");
        }

        long length = checked((long)unsignedLength);
        if (!stream.CanSeek || stream.Length != length)
        {
            throw new LabPackageException(
                $"The opened {description} changed while its identity was inspected.");
        }

        string resolvedExpected = Path.GetFullPath(expectedPath);
        string resolvedFromHandle = GetResolvedPathFromHandle(stream.SafeFileHandle, description);
        if (!string.Equals(resolvedExpected, resolvedFromHandle, PathComparison))
        {
            throw new LabPackageException(
                $"The opened {description} handle resolves to an unexpected path.");
        }

        uint expectedVolumeSerial = GetVolumeSerialForPath(resolvedExpected, description);
        if (expectedVolumeSerial != info.VolumeSerialNumber)
        {
            throw new LabPackageException(
                $"The opened {description} handle belongs to an unexpected volume.");
        }

        return new WindowsFileIdentity(
            info.VolumeSerialNumber,
            ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow,
            info.NumberOfLinks,
            length);
    }

    private static void ValidateOpenedFileIdentity(
        FileStream stream,
        string expectedPath,
        string description,
        WindowsFileIdentity expectedIdentity,
        bool requireSingleLink)
    {
        WindowsFileIdentity actualIdentity = CaptureOpenedFileIdentity(
            stream,
            expectedPath,
            description,
            requireSingleLink);
        EnsureSameFileIdentity(expectedIdentity, actualIdentity, description);
    }

    private static void EnsureSameFileIdentity(
        WindowsFileIdentity expected,
        WindowsFileIdentity actual,
        string description)
    {
        if (expected.VolumeSerialNumber != actual.VolumeSerialNumber ||
            expected.FileIndex != actual.FileIndex ||
            expected.Length != actual.Length)
        {
            throw new LabPackageException(
                $"The {description} changed while its opened-handle identity was validated.");
        }
    }

    private static string GetResolvedPathFromHandle(
        SafeFileHandle handle,
        string description)
    {
        var buffer = new StringBuilder(MaximumWindowsPathBuffer);
        uint length = GetFinalPathNameByHandleW(
            handle,
            buffer,
            checked((uint)buffer.Capacity),
            0);
        if (length == 0 || length >= buffer.Capacity)
        {
            int error = Marshal.GetLastPInvokeError();
            throw new LabPackageException(
                $"Could not resolve the opened {description} handle path (Win32 {error}).");
        }

        string resolved = buffer.ToString();
        if (resolved.StartsWith(ExtendedUncPrefix, StringComparison.OrdinalIgnoreCase))
        {
            resolved = @"\\" + resolved[ExtendedUncPrefix.Length..];
        }
        else if (resolved.StartsWith(ExtendedPathPrefix, StringComparison.OrdinalIgnoreCase))
        {
            resolved = resolved[ExtendedPathPrefix.Length..];
        }

        return Path.GetFullPath(resolved);
    }

    private static uint GetVolumeSerialForPath(string path, string description)
    {
        var volumePath = new StringBuilder(MaximumWindowsPathBuffer);
        if (!GetVolumePathNameW(path, volumePath, checked((uint)volumePath.Capacity)))
        {
            int error = Marshal.GetLastPInvokeError();
            throw new LabPackageException(
                $"Could not resolve the volume for the {description} (Win32 {error}).");
        }

        if (!GetVolumeInformationW(
                volumePath.ToString(),
                IntPtr.Zero,
                0,
                out uint volumeSerialNumber,
                out _,
                out _,
                IntPtr.Zero,
                0))
        {
            int error = Marshal.GetLastPInvokeError();
            throw new LabPackageException(
                $"Could not inspect the volume for the {description} (Win32 {error}).");
        }

        return volumeSerialNumber;
    }

    private static LabPackageManifest ParseManifest(ReadOnlyMemory<byte> bytes)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(
                bytes,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 8,
                });
            JsonElement root = document.RootElement;
            RequireObjectProperties(
                root,
                "manifest",
                "schema_version",
                "source_kind",
                "artifact",
                "device");

            int schemaVersion = RequireInt32(root, "schema_version", "manifest");
            if (schemaVersion != ManifestSchemaVersion)
            {
                throw new LabPackageException(
                    $"Unsupported manifest schema version: {schemaVersion}.");
            }

            string sourceKind = RequireString(root, "source_kind", "manifest");
            if (!string.Equals(sourceKind, SourceKind, StringComparison.Ordinal))
            {
                throw new LabPackageException($"Unsupported artifact source kind: {sourceKind}.");
            }

            JsonElement artifact = RequireObject(root, "artifact", "manifest");
            RequireObjectProperties(
                artifact,
                "manifest.artifact",
                "relative_path",
                "original_file_name",
                "size_bytes",
                "sha256");
            string relativePath = RequireString(
                artifact,
                "relative_path",
                "manifest.artifact");
            if (!string.Equals(relativePath, ArtifactRelativePath, StringComparison.Ordinal))
            {
                throw new LabPackageException(
                    $"The manifest artifact path must be '{ArtifactRelativePath}'.");
            }

            string originalFileName = ValidateOriginalFileName(
                RequireString(artifact, "original_file_name", "manifest.artifact"));
            long sizeBytes = RequireInt64(artifact, "size_bytes", "manifest.artifact");
            if (sizeBytes < 0 || sizeBytes > MaximumPayloadSizeBytes)
            {
                throw new LabPackageException(
                    $"The manifest artifact size must be between 0 and {MaximumPayloadSizeBytes} bytes.");
            }

            string sha256 = RequireString(artifact, "sha256", "manifest.artifact");
            ValidateSha256(sha256, "artifact SHA-256");

            JsonElement deviceElement = RequireObject(root, "device", "manifest");
            RequireObjectProperties(
                deviceElement,
                "manifest.device",
                "gpu",
                "driver_version",
                "vbios_version");
            LabDeviceMetadata device = NormalizeDevice(
                new LabDeviceMetadata(
                    ReadNullableString(deviceElement, "gpu", "manifest.device"),
                    ReadNullableString(
                        deviceElement,
                        "driver_version",
                        "manifest.device"),
                    ReadNullableString(
                        deviceElement,
                        "vbios_version",
                        "manifest.device")),
                requireCanonical: true);

            return new LabPackageManifest(
                schemaVersion,
                sourceKind,
                new LabArtifactManifest(relativePath, originalFileName, sizeBytes, sha256),
                device);
        }
        catch (JsonException error)
        {
            throw new LabPackageException("The package manifest is not valid JSON.", error);
        }
    }

    private static void VerifyPackageLayout(string packagePath)
    {
        ValidateExistingPathComponents(
            packagePath,
            "package",
            requireLeaf: true,
            expectedLeafKind: PathEntryKind.Directory);
        RejectAlternateDataStreams(packagePath, "package directory");
        string[] rootEntries = Directory.EnumerateFileSystemEntries(packagePath)
            .Take(3)
            .Select(path => Path.GetFileName(path) ?? string.Empty)
            .ToArray();
        if (rootEntries.Length != 2 ||
            !rootEntries.Contains(ArtifactDirectoryName, StringComparer.Ordinal) ||
            !rootEntries.Contains(ManifestFileName, StringComparer.Ordinal))
        {
            throw new LabPackageException(
                "The package layout must contain only manifest.json and artifact/.");
        }

        string artifactDirectory = Path.Combine(packagePath, ArtifactDirectoryName);
        ValidateExistingPathComponents(
            artifactDirectory,
            "artifact directory",
            requireLeaf: true,
            expectedLeafKind: PathEntryKind.Directory);
        RejectAlternateDataStreams(artifactDirectory, "artifact directory");

        string[] artifactEntries = Directory.EnumerateFileSystemEntries(artifactDirectory)
            .Take(2)
            .Select(path => Path.GetFileName(path) ?? string.Empty)
            .ToArray();
        if (artifactEntries.Length != 1 ||
            !string.Equals(artifactEntries[0], ArtifactFileName, StringComparison.Ordinal))
        {
            throw new LabPackageException(
                "The artifact directory must contain only payload.bin.");
        }

        string manifestPath = Path.Combine(packagePath, ManifestFileName);
        string payloadPath = Path.Combine(artifactDirectory, ArtifactFileName);
        ValidateExistingPathComponents(
            manifestPath,
            "package manifest",
            requireLeaf: true,
            expectedLeafKind: PathEntryKind.RegularFile);
        ValidateExistingPathComponents(
            payloadPath,
            "artifact payload",
            requireLeaf: true,
            expectedLeafKind: PathEntryKind.RegularFile);
        RejectAlternateDataStreams(manifestPath, "package manifest");
        RejectAlternateDataStreams(payloadPath, "artifact payload");
    }

    private static string ResolveContainedPath(string packagePath, string relativePath)
    {
        if (Path.IsPathRooted(relativePath) ||
            relativePath.Contains('\\', StringComparison.Ordinal) ||
            relativePath.Split('/').Any(segment =>
                segment.Length == 0 || segment is "." or ".."))
        {
            throw new LabPackageException("The manifest contains an unsafe artifact path.");
        }

        string resolvedRoot = EnsureTrailingSeparator(Path.GetFullPath(packagePath));
        string resolved = Path.GetFullPath(
            Path.Combine(packagePath, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!resolved.StartsWith(resolvedRoot, PathComparison))
        {
            throw new LabPackageException("The manifest artifact path escapes the package.");
        }

        return resolved;
    }

    private static string ResolvePackagePath(string path)
    {
        string resolved = ResolvePath(path, "package output path");
        string root = Path.GetPathRoot(resolved)
            ?? throw new LabPackageException("The package output path has no filesystem root.");
        string trimmed = resolved.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        string trimmedRoot = root.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        if (string.Equals(trimmed, trimmedRoot, PathComparison) ||
            Path.GetFileName(trimmed).Length == 0)
        {
            throw new LabPackageException("The package output path cannot be a filesystem root.");
        }

        return trimmed;
    }

    private static string ResolvePath(string path, string description)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new LabPackageException($"The {description} is required.");
        }

        try
        {
            string resolved = Path.GetFullPath(path);
            RejectSpecialWindowsPathSyntax(resolved, description);
            return resolved;
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException)
        {
            throw new LabPackageException($"The {description} is invalid.", error);
        }
    }

    private static void EnsurePackageDoesNotExist(string path)
    {
        if (TryGetAttributes(path, out _))
        {
            throw new LabPackageException(
                $"Refusing to overwrite an existing package path: {path}");
        }
    }

    private static LabDeviceMetadata NormalizeDevice(
        LabDeviceMetadata device,
        bool requireCanonical = false)
    {
        ArgumentNullException.ThrowIfNull(device);
        return new LabDeviceMetadata(
            NormalizeMetadata(device.Gpu, "GPU", 256, requireCanonical),
            NormalizeMetadata(
                device.DriverVersion,
                "driver version",
                128,
                requireCanonical),
            NormalizeMetadata(
                device.VbiosVersion,
                "VBIOS version",
                128,
                requireCanonical));
    }

    private static string? NormalizeMetadata(
        string? value,
        string fieldName,
        int maximumLength,
        bool requireCanonical)
    {
        if (value is null)
        {
            return null;
        }

        string normalized = value.Trim();
        if (normalized.Length == 0)
        {
            throw new LabPackageException($"The {fieldName} cannot be empty.");
        }

        int scalarCount = CountUnicodeScalarsAndRejectControls(normalized, fieldName);
        if (scalarCount > maximumLength)
        {
            throw new LabPackageException(
                $"The {fieldName} cannot exceed {maximumLength} Unicode scalar values.");
        }

        if (requireCanonical && !string.Equals(value, normalized, StringComparison.Ordinal))
        {
            throw new LabPackageException($"The {fieldName} is not in canonical form.");
        }

        return normalized;
    }

    private static string ValidateOriginalFileName(string fileName)
    {
        int scalarCount = CountUnicodeScalarsAndRejectControls(
            fileName,
            "original artifact file name");
        if (string.IsNullOrWhiteSpace(fileName) ||
            fileName is "." or ".." ||
            fileName.Contains('/') ||
            fileName.Contains('\\') ||
            fileName.Contains(':') ||
            scalarCount > 255)
        {
            throw new LabPackageException("The original artifact file name is unsafe.");
        }

        return fileName;
    }

    private static int CountUnicodeScalarsAndRejectControls(
        string value,
        string fieldName)
    {
        int count = 0;
        ReadOnlySpan<char> remaining = value.AsSpan();
        while (!remaining.IsEmpty)
        {
            OperationStatus status = Rune.DecodeFromUtf16(
                remaining,
                out Rune rune,
                out int charsConsumed);
            if (status != OperationStatus.Done)
            {
                throw new LabPackageException(
                    $"The {fieldName} contains invalid Unicode.");
            }

            if (Rune.GetUnicodeCategory(rune) == UnicodeCategory.Control)
            {
                throw new LabPackageException(
                    $"The {fieldName} cannot contain control characters.");
            }

            count++;
            remaining = remaining[charsConsumed..];
        }

        return count;
    }

    private static void ValidateSha256(string value, string description)
    {
        if (value is null)
        {
            throw new LabPackageException($"The {description} is required.");
        }

        if (value.Length != 64 ||
            value.Any(character =>
                character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            throw new LabPackageException(
                $"The {description} must contain exactly 64 lowercase hexadecimal characters.");
        }
    }

    private static void RequireObjectProperties(
        JsonElement element,
        string context,
        params string[] expectedProperties)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new LabPackageException($"{context} must be a JSON object.");
        }

        var expected = new HashSet<string>(expectedProperties, StringComparer.Ordinal);
        var observed = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!observed.Add(property.Name))
            {
                throw new LabPackageException(
                    $"{context} contains duplicate property '{property.Name}'.");
            }

            if (!expected.Contains(property.Name))
            {
                throw new LabPackageException(
                    $"{context} contains unsupported property '{property.Name}'.");
            }
        }

        string? missing = expected.FirstOrDefault(property => !observed.Contains(property));
        if (missing is not null)
        {
            throw new LabPackageException($"{context} is missing property '{missing}'.");
        }
    }

    private static JsonElement RequireObject(
        JsonElement parent,
        string propertyName,
        string context)
    {
        JsonElement value = parent.GetProperty(propertyName);
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new LabPackageException($"{context}.{propertyName} must be a JSON object.");
        }

        return value;
    }

    private static string RequireString(
        JsonElement parent,
        string propertyName,
        string context)
    {
        JsonElement value = parent.GetProperty(propertyName);
        if (value.ValueKind != JsonValueKind.String)
        {
            throw new LabPackageException($"{context}.{propertyName} must be a JSON string.");
        }

        return value.GetString()
            ?? throw new LabPackageException($"{context}.{propertyName} cannot be null.");
    }

    private static string? ReadNullableString(
        JsonElement parent,
        string propertyName,
        string context)
    {
        JsonElement value = parent.GetProperty(propertyName);
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw new LabPackageException(
                $"{context}.{propertyName} must be a JSON string or null.");
        }

        return value.GetString();
    }

    private static int RequireInt32(
        JsonElement parent,
        string propertyName,
        string context)
    {
        long number = RequireSemanticInt64(parent, propertyName, context);
        if (number < int.MinValue || number > int.MaxValue)
        {
            throw new LabPackageException($"{context}.{propertyName} must be a 32-bit integer.");
        }

        return checked((int)number);
    }

    private static long RequireInt64(
        JsonElement parent,
        string propertyName,
        string context) =>
        RequireSemanticInt64(parent, propertyName, context);

    private static long RequireSemanticInt64(
        JsonElement parent,
        string propertyName,
        string context)
    {
        JsonElement value = parent.GetProperty(propertyName);
        if (value.ValueKind != JsonValueKind.Number)
        {
            throw new LabPackageException($"{context}.{propertyName} must be a 64-bit integer.");
        }

        string raw = value.GetRawText();
        if (!TryParseSemanticInt64(raw, out long result))
        {
            throw new LabPackageException(
                $"{context}.{propertyName} must be a mathematically integral JSON number " +
                "within the 64-bit signed integer range.");
        }

        return result;
    }

    private static bool TryParseSemanticInt64(string raw, out long result)
    {
        result = 0;
        ReadOnlySpan<char> number = raw.AsSpan();
        bool negative = number[0] == '-';
        if (negative)
        {
            number = number[1..];
        }

        int exponentSeparator = number.IndexOfAny('e', 'E');
        ReadOnlySpan<char> coefficient = exponentSeparator >= 0
            ? number[..exponentSeparator]
            : number;
        ReadOnlySpan<char> exponentText = exponentSeparator >= 0
            ? number[(exponentSeparator + 1)..]
            : ReadOnlySpan<char>.Empty;

        int decimalPoint = coefficient.IndexOf('.');
        int fractionalDigits = decimalPoint >= 0
            ? coefficient.Length - decimalPoint - 1
            : 0;
        string coefficientDigits = decimalPoint >= 0
            ? string.Concat(coefficient[..decimalPoint], coefficient[(decimalPoint + 1)..])
            : coefficient.ToString();
        int firstNonZero = coefficientDigits.AsSpan().IndexOfAnyExcept('0');
        if (firstNonZero < 0)
        {
            return true;
        }

        ReadOnlySpan<char> significantDigits = coefficientDigits.AsSpan(firstNonZero);
        int exponent = ParseSaturatedDecimalExponent(
            exponentText,
            saturation: raw.Length + 32);
        int decimalShift = exponent - fractionalDigits;
        if (decimalShift < 0)
        {
            int removedDigits = -decimalShift;
            if (removedDigits >= significantDigits.Length ||
                significantDigits[^removedDigits..].IndexOfAnyExcept('0') >= 0)
            {
                return false;
            }

            significantDigits = significantDigits[..^removedDigits];
            decimalShift = 0;
        }

        if (significantDigits.Length + decimalShift > 19)
        {
            return false;
        }

        string integralDigits = decimalShift == 0
            ? significantDigits.ToString()
            : string.Concat(significantDigits, new string('0', decimalShift));
        ReadOnlySpan<char> limit = negative
            ? "9223372036854775808"
            : "9223372036854775807";
        if (integralDigits.Length > limit.Length ||
            (integralDigits.Length == limit.Length &&
             integralDigits.AsSpan().SequenceCompareTo(limit) > 0))
        {
            return false;
        }

        ulong magnitude = 0;
        foreach (char digit in integralDigits)
        {
            magnitude = (magnitude * 10) + checked((uint)(digit - '0'));
        }

        if (!negative)
        {
            result = checked((long)magnitude);
            return true;
        }

        const ulong minimumMagnitude = 9_223_372_036_854_775_808UL;
        result = magnitude == minimumMagnitude
            ? long.MinValue
            : -checked((long)magnitude);
        return true;
    }

    private static int ParseSaturatedDecimalExponent(
        ReadOnlySpan<char> exponent,
        int saturation)
    {
        if (exponent.IsEmpty)
        {
            return 0;
        }

        bool negative = exponent[0] == '-';
        if (negative || exponent[0] == '+')
        {
            exponent = exponent[1..];
        }

        int value = 0;
        foreach (char digitCharacter in exponent)
        {
            int digit = digitCharacter - '0';
            if (value > (saturation - digit) / 10)
            {
                return negative ? -saturation : saturation;
            }

            value = (value * 10) + digit;
        }

        return negative ? -value : value;
    }

    private static void ValidateExistingPathComponents(
        string path,
        string description,
        bool requireLeaf,
        PathEntryKind expectedLeafKind)
    {
        string resolved = Path.GetFullPath(path);
        RejectSpecialWindowsPathSyntax(resolved, description);
        string root = Path.GetPathRoot(resolved)
            ?? throw new LabPackageException($"The {description} has no filesystem root.");

        if (!TryGetAttributes(root, out FileAttributes rootAttributes) ||
            (rootAttributes & FileAttributes.Directory) == 0)
        {
            throw new LabPackageException($"The filesystem root for the {description} is unavailable.");
        }

        RejectReparseAttributes(rootAttributes, description, root);
        string[] components = resolved[root.Length..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        string current = root;
        for (int index = 0; index < components.Length; index++)
        {
            string component = components[index];
            current = Path.Combine(current, component);
            bool isLeaf = index == components.Length - 1;
            if (!TryGetAttributes(current, out FileAttributes attributes))
            {
                if (requireLeaf)
                {
                    throw new LabPackageException(
                        $"The {description} does not exist: {resolved}");
                }

                return;
            }

            RejectReparseAttributes(attributes, description, current);
            if (!isLeaf && (attributes & FileAttributes.Directory) == 0)
            {
                throw new LabPackageException(
                    $"A non-directory component blocks the {description}: {current}");
            }

            if (isLeaf)
            {
                ValidateLeafKind(attributes, expectedLeafKind, description);
            }
        }

        if (components.Length == 0 && requireLeaf)
        {
            ValidateLeafKind(rootAttributes, expectedLeafKind, description);
        }
    }

    private static void EnsureSupportedPlatform()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "The v0.8 lab package CLI is restricted to Windows until regular-file " +
                "identity can be proven from an opened handle on every supported Unix ABI.");
        }
    }

    private static void ValidateLeafKind(
        FileAttributes attributes,
        PathEntryKind expectedKind,
        string description)
    {
        bool isDirectory = (attributes & FileAttributes.Directory) != 0;
        bool isDevice = (attributes & FileAttributes.Device) != 0;
        if (expectedKind == PathEntryKind.Directory && !isDirectory)
        {
            throw new LabPackageException($"The {description} must be a directory.");
        }

        if (expectedKind == PathEntryKind.RegularFile && (isDirectory || isDevice))
        {
            throw new LabPackageException($"The {description} must be a regular file.");
        }
    }

    private static void RejectReparseAttributes(
        FileAttributes attributes,
        string description,
        string component)
    {
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new LabPackageException(
                $"The {description} traverses a reparse point: {component}");
        }
    }

    private static bool TryGetAttributes(
        string path,
        out FileAttributes attributes)
    {
        try
        {
            attributes = File.GetAttributes(path);
            return true;
        }
        catch (FileNotFoundException)
        {
            attributes = default;
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            attributes = default;
            return false;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new LabPackageException($"Could not inspect path '{path}': {error.Message}", error);
        }
    }

    private static void RejectSpecialWindowsPathSyntax(
        string path,
        string description)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        if (path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            throw new LabPackageException(
                $"The {description} must use a local drive, not a UNC path.");
        }

        if (path.StartsWith(@"\\?\", StringComparison.Ordinal) ||
            path.StartsWith(@"\\.\", StringComparison.Ordinal) ||
            path.StartsWith(@"\??\", StringComparison.Ordinal))
        {
            throw new LabPackageException(
                $"The {description} cannot use a Windows device or extended path namespace.");
        }

        string root = Path.GetPathRoot(path)
            ?? throw new LabPackageException($"The {description} has no filesystem root.");
        DriveType driveType = GetDriveTypeW(root);
        if (driveType == DriveType.Remote)
        {
            throw new LabPackageException(
                $"The {description} must not use a mapped or remote drive.");
        }

        if (driveType is DriveType.Unknown or DriveType.NoRootDirectory)
        {
            throw new LabPackageException(
                $"The local drive for the {description} is unavailable.");
        }

        string remainder = path[root.Length..];
        string[] components = remainder.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        foreach (string component in components)
        {
            if (component.Contains(':'))
            {
                throw new LabPackageException(
                    $"The {description} cannot address an NTFS alternate data stream.");
            }

            if (component.EndsWith(' ') || component.EndsWith('.'))
            {
                throw new LabPackageException(
                    $"The {description} contains a non-canonical Windows path component.");
            }
        }
    }

    private static void RejectAlternateDataStreams(string path, string description)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using SafeFindStreamHandle handle = FindFirstStreamW(
            path,
            StreamInfoLevels.FindStreamInfoStandard,
            out Win32FindStreamData streamData,
            0);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastPInvokeError();
            if (error is ErrorHandleEof or ErrorInvalidParameter)
            {
                return;
            }

            throw new LabPackageException(
                $"Could not enumerate alternate streams for the {description} (Win32 {error}).");
        }

        EnsureDefaultDataStream(streamData.StreamName, description);
        while (FindNextStreamW(handle, out streamData))
        {
            EnsureDefaultDataStream(streamData.StreamName, description);
        }

        int finalError = Marshal.GetLastPInvokeError();
        if (finalError != ErrorHandleEof && finalError != ErrorNoMoreFiles)
        {
            throw new LabPackageException(
                $"Could not finish alternate-stream enumeration for the {description} " +
                $"(Win32 {finalError}).");
        }
    }

    private static void EnsureDefaultDataStream(string streamName, string description)
    {
        if (!string.Equals(streamName, "::$DATA", StringComparison.Ordinal))
        {
            throw new LabPackageException(
                $"The {description} contains an alternate data stream.");
        }
    }

    private static void DeleteOwnedStagingDirectory(
        string stagingPath,
        string parentDirectory,
        string packageName)
    {
        string resolvedParent = EnsureTrailingSeparator(Path.GetFullPath(parentDirectory));
        string resolvedStaging = Path.GetFullPath(stagingPath);
        string stagingName = Path.GetFileName(resolvedStaging);
        string expectedNamePrefix = $".{packageName}.staging-";
        if (!resolvedStaging.StartsWith(resolvedParent, PathComparison) ||
            !stagingName.StartsWith(expectedNamePrefix, StringComparison.Ordinal) ||
            !Guid.TryParseExact(stagingName[expectedNamePrefix.Length..], "N", out _))
        {
            return;
        }

        try
        {
            ValidateExistingPathComponents(
                parentDirectory,
                "staging parent directory",
                requireLeaf: true,
                expectedLeafKind: PathEntryKind.Directory);
            if (!TryGetAttributes(resolvedStaging, out FileAttributes stagingAttributes))
            {
                return;
            }

            if ((stagingAttributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) !=
                FileAttributes.Directory)
            {
                return;
            }

            RejectAlternateDataStreams(resolvedStaging, "staging directory");
            string artifactDirectory = Path.Combine(
                resolvedStaging,
                ArtifactDirectoryName);
            string manifestPath = Path.Combine(resolvedStaging, ManifestFileName);
            string[] rootEntries = Directory.EnumerateFileSystemEntries(resolvedStaging)
                .Take(3)
                .Select(path => Path.GetFileName(path) ?? string.Empty)
                .ToArray();
            if (rootEntries.Length > 2 ||
                rootEntries.Any(name =>
                    !string.Equals(name, ArtifactDirectoryName, StringComparison.Ordinal) &&
                    !string.Equals(name, ManifestFileName, StringComparison.Ordinal)))
            {
                return;
            }

            string? payloadPath = null;
            if (TryGetAttributes(artifactDirectory, out FileAttributes artifactAttributes))
            {
                if ((artifactAttributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) !=
                    FileAttributes.Directory)
                {
                    return;
                }

                RejectAlternateDataStreams(artifactDirectory, "staging artifact directory");
                string[] artifactEntries = Directory.EnumerateFileSystemEntries(artifactDirectory)
                    .Take(2)
                    .Select(path => Path.GetFileName(path) ?? string.Empty)
                    .ToArray();
                if (artifactEntries.Length > 1 ||
                    artifactEntries.Any(name =>
                        !string.Equals(name, ArtifactFileName, StringComparison.Ordinal)))
                {
                    return;
                }

                payloadPath = Path.Combine(artifactDirectory, ArtifactFileName);
                if (TryGetAttributes(payloadPath, out FileAttributes payloadAttributes))
                {
                    if ((payloadAttributes &
                         (FileAttributes.Directory |
                          FileAttributes.Device |
                          FileAttributes.ReparsePoint)) != 0)
                    {
                        return;
                    }

                    RejectAlternateDataStreams(payloadPath, "staging artifact payload");
                }
            }

            if (TryGetAttributes(manifestPath, out FileAttributes manifestAttributes))
            {
                if ((manifestAttributes &
                     (FileAttributes.Directory |
                      FileAttributes.Device |
                      FileAttributes.ReparsePoint)) != 0)
                {
                    return;
                }

                RejectAlternateDataStreams(manifestPath, "staging manifest");
            }

            DeleteExactFileIfPresent(payloadPath);
            if (TryGetAttributes(artifactDirectory, out _) &&
                !Directory.EnumerateFileSystemEntries(artifactDirectory).Any())
            {
                Directory.Delete(artifactDirectory);
            }

            DeleteExactFileIfPresent(manifestPath);
            if (!Directory.EnumerateFileSystemEntries(resolvedStaging).Any())
            {
                Directory.Delete(resolvedStaging);
            }
        }
        catch (LabPackageException)
        {
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void DeleteExactFileIfPresent(string? path)
    {
        if (path is null || !TryGetAttributes(path, out FileAttributes attributes))
        {
            return;
        }

        if ((attributes &
             (FileAttributes.Directory |
              FileAttributes.Device |
              FileAttributes.ReparsePoint)) != 0)
        {
            throw new LabPackageException("Refusing to clean an unexpected staging entry.");
        }

        ValidateExistingPathComponents(
            path,
            "staging cleanup file",
            requireLeaf: true,
            expectedLeafKind: PathEntryKind.RegularFile);
        RejectAlternateDataStreams(path, "staging cleanup file");
        using (var stream = new FileStream(
                   path,
                   new FileStreamOptions
                   {
                       Mode = FileMode.Open,
                       Access = FileAccess.Read,
                       Share = FileShare.None,
                       BufferSize = 1,
                       Options = FileOptions.None,
                   }))
        {
            WindowsFileIdentity identity = CaptureOpenedFileIdentity(
                stream,
                path,
                "staging cleanup file",
                requireSingleLink: true);
            ValidateOpenedFileIdentity(
                stream,
                path,
                "staging cleanup file",
                identity,
                requireSingleLink: true);
        }

        File.Delete(path);
    }

    private static string EnsureTrailingSeparator(string path) =>
        path.EndsWith(Path.DirectorySeparatorChar) ? path : path + Path.DirectorySeparatorChar;

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private readonly record struct WindowsFileIdentity(
        uint VolumeSerialNumber,
        ulong FileIndex,
        uint NumberOfLinks,
        long Length);

    private enum PathEntryKind
    {
        Any,
        Directory,
        RegularFile,
    }

    private enum StreamInfoLevels
    {
        FindStreamInfoStandard = 0,
    }

    private enum DriveType : uint
    {
        Unknown = 0,
        NoRootDirectory = 1,
        Removable = 2,
        Fixed = 3,
        Remote = 4,
        CdRom = 5,
        RamDisk = 6,
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Win32FindStreamData
    {
        internal long StreamSize;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 296)]
        internal string StreamName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        internal FileAttributes FileAttributes;
        internal System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        internal System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        internal System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        internal uint VolumeSerialNumber;
        internal uint FileSizeHigh;
        internal uint FileSizeLow;
        internal uint NumberOfLinks;
        internal uint FileIndexHigh;
        internal uint FileIndexLow;
    }

    private sealed class SafeFindStreamHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private SafeFindStreamHandle()
            : base(ownsHandle: true)
        {
        }

        protected override bool ReleaseHandle() => FindClose(handle);
    }

    private const int ErrorNoMoreFiles = 18;
    private const int ErrorHandleEof = 38;
    private const int ErrorInvalidParameter = 87;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern DriveType GetDriveTypeW(string rootPathName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out ByHandleFileInformation fileInformation);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(
        SafeFileHandle file,
        StringBuilder filePath,
        uint filePathLength,
        uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumePathNameW(
        string fileName,
        StringBuilder volumePathName,
        uint bufferLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformationW(
        string rootPathName,
        IntPtr volumeNameBuffer,
        uint volumeNameSize,
        out uint volumeSerialNumber,
        out uint maximumComponentLength,
        out uint fileSystemFlags,
        IntPtr fileSystemNameBuffer,
        uint fileSystemNameSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFindStreamHandle FindFirstStreamW(
        string fileName,
        StreamInfoLevels infoLevel,
        out Win32FindStreamData findStreamData,
        uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindNextStreamW(
        SafeFindStreamHandle findStreamHandle,
        out Win32FindStreamData findStreamData);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindClose(IntPtr findFileHandle);
}
