using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RtxMonitor.Lab.Tests;

internal static class Program
{
    private static int failures;

    private static int Main()
    {
        if (!OperatingSystem.IsWindows())
        {
            Run("unsupported Unix platform fails before file access", TestUnsupportedPlatform);
            return Finish();
        }

        Run("create and anchored verify", TestCreateAndVerify);
        Run("tampered artifact fails verification", TestTamperedArtifact);
        Run("existing package is never overwritten", TestExistingPackageIsNotOverwritten);
        Run("manifest traversal is rejected", TestManifestTraversalIsRejected);
        Run("unknown manifest fields fail closed", TestUnknownManifestFieldFailsClosed);
        Run("layout enumeration is bounded", TestBoundedLayoutEnumeration);
        Run("ancestor reparse points are rejected", TestAncestorReparsePointIsRejected);
        Run("manifest swap and oversize are rejected", TestManifestSwapAndOversize);
        Run("payload size limit is enforced", TestPayloadSizeLimit);
        Run("tampered payload and manifest need a new anchor", TestTamperedPairNeedsNewAnchor);
        Run("concurrent staging tamper cannot publish success", TestConcurrentStagingTamper);
        Run("Unicode scalar and control rules", TestUnicodeScalarAndControlRules);
        Run("semantic JSON integers align with schema", TestSemanticJsonIntegers);
        Run("NTFS alternate streams are rejected", TestAlternateDataStreams);
        Run("hard-linked package files are rejected", TestHardLinkedPackageFilesAreRejected);
        Run("UNC paths are rejected syntactically", TestUncPathsAreRejected);
        Run("CLI create and anchored verify", TestCliCreateAndVerify);
        Run("CLI requires the manifest anchor", TestCliRequiresManifestAnchor);
        Run("GPU-Z log preserves channels and source scopes", TestGpuzLogAnalysis);
        Run("GPU-Z log rejects malformed rows", TestGpuzLogRejectsMalformedRows);
        Run(
            "CSV parsers reject excess sample rows during tokenization",
            TestCsvParsersRejectExcessSampleRows);
        Run(
            "CSV parsers reject excess fields during tokenization",
            TestCsvParsersRejectExcessFields);
        Run(
            "CSV parsers reject excess appended sessions during tokenization",
            TestCsvParsersRejectExcessSessions);
        Run(
            "CSV parsers tolerate blank lines without materializing rows",
            TestCsvParsersIgnoreBlankLines);
        Run("GPU-Z log accepts identical appended sessions", TestGpuzAppendedSessions);
        Run("GPU-Z log rejects changed appended layouts", TestGpuzChangedSessionLayout);
        Run("CLI emits GPU-Z reference JSON", TestCliGpuzLogAnalysis);
        Run("experiment marker has synchronized clocks", TestExperimentMarker);
        Run("CLI marker validates scenario and phase", TestCliExperimentMarker);
        Run(
            "experiment manifest and analysis producers are anchored end to end",
            TestExperimentManifestAndSeriesAnalysis);
        Run(
            "experiment manifest rejects an untrusted package anchor",
            TestExperimentManifestRejectsUntrustedPackage);
        Run(
            "experiment manifest input requires a stable single-link snapshot",
            TestExperimentManifestInputSnapshot);
        Run(
            "analysis uses the anchored payload snapshot after pathname tamper",
            TestExperimentAnalysisUsesVerifiedPayloadSnapshot);
        Run(
            "experiment packages enforce scenario binding and completed evidence",
            TestExperimentManifestScenarioBinding);
        Run(
            "analysis enforces scenario windows and bounded correlation work",
            TestExperimentAnalysisScenarioWindowAndWorkLimit);
        Run(
            "analysis rejects non-finite derived values through the CLI contract",
            TestExperimentAnalysisRejectsNonFiniteDerivedValues);
        Run("GPU-Z correlation ranks numeric co-movement", TestGpuzCorrelation);
        Run("GPU-Z correlation handles non-computable series", TestGpuzCorrelationLimits);
        Run("GPU-Z correlation isolates appended sessions", TestGpuzSessionCorrelation);
        Run(
            "NVAPI thermal channels correlate with die and hotspot references",
            TestThermChannelCorrelation);
        Run(
            "NVAPI thermal v2 isolates sessions and requires the sealed reference",
            TestThermChannelCorrelationV2);
        Run(
            "NVAPI voltage v2 correlates GPU-Z and optional growing HWiNFO",
            TestVoltageStatusCorrelationV2);
        Run(
            "NVAPI voltage v2 isolates the bounded GPU-Z session layout",
            TestVoltageStatusCorrelationV2ChangedEarlierLayout);
        Run(
            "NVAPI voltage v2 rejects profile and raw-layout drift",
            TestVoltageStatusCorrelationV2RejectsProfileDrift);
        Run(
            "NVAPI voltage v2 rejects unanchored or inconsistent references",
            TestVoltageStatusCorrelationV2RejectsReferenceDrift);
        Run(
            "NVAPI voltage v2 rejects oversized observations before parsing",
            TestVoltageStatusCorrelationV2RejectsOversizedObservation);
        Run(
            "ordered resampling remains bounded at maximum contract sizes",
            TestOrderedResamplingAtContractLimits);
        Run(
            "external voltage prefixes reject invalid exact-channel values",
            TestGpuzVoltagePrefixIsolatesInvalidValues);
        Run("NVAPI IDs are classified against an offline public table", TestNvapiClassification);
        Run("NVAPI classification rejects inconsistent observations", TestNvapiClassificationRejectsCounts);
        Run("NVAPI candidate inventory joins catalog and binary origin", TestNvapiCandidateInventory);
        Run("Windows handle identity resolves a duplicated kernel object", TestWindowsHandleIdentity);

        return Finish();
    }

    private static int Finish()
    {
        if (failures == 0)
        {
            Console.WriteLine("RtxMonitor.Lab tests passed");
        }

        return failures == 0 ? 0 : 1;
    }

    private static void TestUnsupportedPlatform()
    {
        Check(
            Throws<PlatformNotSupportedException>(
                () => LabPackage.Create("/dev/null", "/tmp/rtxmon-lab-package")),
            "non-Windows create must fail before opening a device or FIFO");
        Check(
            Throws<PlatformNotSupportedException>(
                () => LabPackage.Verify(
                    "/tmp/rtxmon-lab-package",
                    new string('0', 64))),
            "non-Windows verify must fail before opening package paths");
        Check(
            Throws<PlatformNotSupportedException>(
                () => ExperimentManifestProducer.FinalizeFile(
                    "/dev/null",
                    "/tmp")),
            "non-Windows manifest finalization must fail before opening its input path");
        Check(
            Throws<PlatformNotSupportedException>(
                () => ExperimentSeriesAnalyzer.Analyze(
                    "/dev/null",
                    new string('0', 64),
                    "/tmp",
                    "series-package",
                    maximumLagSamples: 0,
                    Guid.Empty,
                    DateTimeOffset.UnixEpoch)),
            "non-Windows analysis must fail before opening its manifest path");
    }

    private static void TestCreateAndVerify()
    {
        using var temporary = new TemporaryLabDirectory();
        byte[] content = SyntheticArtifact();
        string input = temporary.WriteInput("synthetic.rom", content);
        string firstPackage = temporary.PackagePath("package-a");
        string secondPackage = temporary.PackagePath("package-b");
        var device = new LabDeviceMetadata(
            "NVIDIA GeForce RTX 3060",
            "610.88",
            "94.06.25.00.fc");

        LabPackageResult created = LabPackage.Create(input, firstPackage, device);
        LabPackageResult verified = LabPackage.Verify(
            firstPackage,
            created.ManifestSha256);
        LabPackageResult second = LabPackage.Create(input, secondPackage, device);

        string expectedHash = Sha256Hex(content);
        Check(created.Manifest.SchemaVersion == 1, "manifest must declare schema v1");
        Check(
            created.Manifest.SourceKind == "user_provided_local_file",
            "manifest must declare a user-provided local source");
        Check(created.Manifest.Artifact.SizeBytes == content.LongLength, "size must match");
        Check(created.Manifest.Artifact.Sha256 == expectedHash, "SHA-256 must match");
        Check(
            created.Manifest.Artifact.OriginalFileName == "synthetic.rom",
            "original file name must be retained without a path");
        Check(created.Manifest.Device == device, "optional GPU provenance must be retained");
        Check(
            created.ManifestSha256 == verified.ManifestSha256,
            "verify must reproduce the trusted manifest digest");
        Check(
            File.ReadAllBytes(PayloadPath(firstPackage)).SequenceEqual(content),
            "package must contain an exact local copy");
        Check(
            (File.GetAttributes(PayloadPath(firstPackage)) & FileAttributes.ReadOnly) == 0 &&
            (File.GetAttributes(ManifestPath(firstPackage)) & FileAttributes.ReadOnly) == 0,
            "package safety must not rely on a hardlink-wide read-only attribute");
        Check(
            File.ReadAllText(ManifestPath(firstPackage)) ==
            File.ReadAllText(ManifestPath(secondPackage)),
            "equal inputs and metadata must produce byte-identical manifests");
        Check(
            created.ManifestSha256 == second.ManifestSha256,
            "equal manifests must have an equal digest");
    }

    private static void TestTamperedArtifact()
    {
        using var temporary = new TemporaryLabDirectory();
        string input = temporary.WriteInput("artifact.bin", SyntheticArtifact());
        string package = temporary.PackagePath("tampered-package");
        LabPackageResult created = LabPackage.Create(input, package);

        string payload = PayloadPath(package);
        using (var stream = new FileStream(payload, FileMode.Open, FileAccess.Write))
        {
            stream.Position = 0;
            stream.WriteByte(0xff);
        }

        Check(
            Throws<LabPackageException>(
                () => LabPackage.Verify(package, created.ManifestSha256)),
            "verification must reject a changed payload");
    }

    private static void TestExistingPackageIsNotOverwritten()
    {
        using var temporary = new TemporaryLabDirectory();
        string firstInput = temporary.WriteInput("first.bin", [1, 2, 3]);
        string secondInput = temporary.WriteInput("second.bin", [9, 8, 7]);
        string package = temporary.PackagePath("existing-package");
        LabPackageResult first = LabPackage.Create(firstInput, package);

        Check(
            Throws<LabPackageException>(() => LabPackage.Create(secondInput, package)),
            "create must fail when the package path already exists");
        LabPackageResult verified = LabPackage.Verify(package, first.ManifestSha256);
        Check(
            verified.ManifestSha256 == first.ManifestSha256,
            "the existing package must remain unchanged");
    }

    private static void TestManifestTraversalIsRejected()
    {
        using var temporary = new TemporaryLabDirectory();
        string input = temporary.WriteInput("artifact.bin", SyntheticArtifact());
        string package = temporary.PackagePath("traversal-package");
        LabPackage.Create(input, package);

        string changed = ReadManifest(package).Replace(
            "artifact/payload.bin",
            "../outside.bin",
            StringComparison.Ordinal);
        string changedAnchor = WriteManifest(package, changed);
        Check(
            Throws<LabPackageException>(() => LabPackage.Verify(package, changedAnchor)),
            "verification must reject traversal even when the modified manifest is anchored");
    }

    private static void TestUnknownManifestFieldFailsClosed()
    {
        using var temporary = new TemporaryLabDirectory();
        string input = temporary.WriteInput("artifact.bin", SyntheticArtifact());
        string package = temporary.PackagePath("unknown-field-package");
        LabPackage.Create(input, package);

        string changed = ReadManifest(package).Replace(
            "\"device\":{",
            "\"unexpected\":true,\"device\":{",
            StringComparison.Ordinal);
        string changedAnchor = WriteManifest(package, changed);
        Check(
            Throws<LabPackageException>(() => LabPackage.Verify(package, changedAnchor)),
            "verification must reject unsupported manifest fields");
    }

    private static void TestBoundedLayoutEnumeration()
    {
        using var temporary = new TemporaryLabDirectory();
        string input = temporary.WriteInput("artifact.bin", SyntheticArtifact());
        string rootExtrasPackage = temporary.PackagePath("root-extras-package");
        LabPackageResult rootCreated = LabPackage.Create(input, rootExtrasPackage);
        for (int index = 0; index < 16; index++)
        {
            temporary.WriteFile(
                Path.Combine(rootExtrasPackage, $"extra-{index:D2}.bin"),
                [checked((byte)index)]);
        }

        Check(
            Throws<LabPackageException>(
                () => LabPackage.Verify(rootExtrasPackage, rootCreated.ManifestSha256)),
            "verify must fail after a bounded root enumeration sees an extra entry");

        string artifactExtrasPackage = temporary.PackagePath("artifact-extras-package");
        LabPackageResult artifactCreated = LabPackage.Create(input, artifactExtrasPackage);
        string artifactDirectory = Path.Combine(
            artifactExtrasPackage,
            LabPackage.ArtifactDirectoryName);
        for (int index = 0; index < 16; index++)
        {
            temporary.WriteFile(
                Path.Combine(artifactDirectory, $"extra-{index:D2}.bin"),
                [checked((byte)index)]);
        }

        Check(
            Throws<LabPackageException>(
                () => LabPackage.Verify(artifactExtrasPackage, artifactCreated.ManifestSha256)),
            "verify must fail after a bounded artifact enumeration sees an extra entry");
    }

    private static void TestAncestorReparsePointIsRejected()
    {
        using var temporary = new TemporaryLabDirectory();
        string target = temporary.CreateDirectory("junction-target");
        string input = temporary.WriteFile(
            Path.Combine(target, "inside.bin"),
            SyntheticArtifact());
        string link = temporary.CreateDirectoryLink("junction-ancestor", target);

        Check(
            Throws<LabPackageException>(
                () => LabPackage.Create(
                    Path.Combine(link, "inside.bin"),
                    temporary.PackagePath("input-link-package"))),
            "create must reject an input whose ancestor is a reparse point");
        Check(
            Throws<LabPackageException>(
                () => LabPackage.Create(input, Path.Combine(link, "output-package"))),
            "create must reject an output whose ancestor is a reparse point");

        string directPackage = temporary.TrackPackage(
            Path.Combine(target, "direct-package"));
        LabPackageResult created = LabPackage.Create(input, directPackage);
        Check(
            Throws<LabPackageException>(
                () => LabPackage.Verify(
                    Path.Combine(link, "direct-package"),
                    created.ManifestSha256)),
            "verify must reject a package whose ancestor is a reparse point");
    }

    private static void TestManifestSwapAndOversize()
    {
        using var temporary = new TemporaryLabDirectory();
        string input = temporary.WriteInput("artifact.bin", SyntheticArtifact());
        string package = temporary.PackagePath("manifest-swap-package");
        LabPackageResult created = LabPackage.Create(input, package);
        string manifest = ManifestPath(package);
        string savedManifest = temporary.TrackFile(
            Path.Combine(temporary.RootPath, "saved-manifest.json"));
        File.Move(manifest, savedManifest);
        File.WriteAllBytes(manifest, new byte[(64 * 1024) + 1]);

        Check(
            Throws<LabPackageException>(
                () => LabPackage.Verify(package, created.ManifestSha256)),
            "verify must reject an oversized manifest swapped into place");
    }

    private static void TestPayloadSizeLimit()
    {
        using var temporary = new TemporaryLabDirectory();
        string oversizedInput = temporary.CreateSparseInput(
            "oversized.bin",
            LabPackage.MaximumPayloadSizeBytes + 1);
        Check(
            Throws<LabPackageException>(
                () => LabPackage.Create(
                    oversizedInput,
                    temporary.PackagePath("oversized-create-package"))),
            "create must reject a payload over 256 MiB before copying it");

        string smallInput = temporary.WriteInput("small.bin", [1, 2, 3]);
        string package = temporary.PackagePath("oversized-verify-package");
        LabPackageResult created = LabPackage.Create(smallInput, package);
        string payload = PayloadPath(package);
        using (var stream = new FileStream(payload, FileMode.Open, FileAccess.Write))
        {
            stream.SetLength(LabPackage.MaximumPayloadSizeBytes + 1);
        }

        Check(
            Throws<LabPackageException>(
                () => LabPackage.Verify(package, created.ManifestSha256)),
            "verify must reject an actual payload over 256 MiB");
    }

    private static void TestTamperedPairNeedsNewAnchor()
    {
        using var temporary = new TemporaryLabDirectory();
        byte[] content = SyntheticArtifact();
        string input = temporary.WriteInput("artifact.bin", content);
        string package = temporary.PackagePath("tampered-pair-package");
        LabPackageResult created = LabPackage.Create(input, package);

        byte[] changedPayload = content.ToArray();
        changedPayload[0] ^= 0xff;
        string payload = PayloadPath(package);
        File.WriteAllBytes(payload, changedPayload);
        string changedPayloadHash = Sha256Hex(changedPayload);
        string changedManifest = ReadManifest(package).Replace(
            created.Manifest.Artifact.Sha256,
            changedPayloadHash,
            StringComparison.Ordinal);
        string changedManifestHash = WriteManifest(package, changedManifest);

        Check(changedManifestHash != created.ManifestSha256, "tampering must change the anchor");
        Check(
            Throws<LabPackageException>(
                () => LabPackage.Verify(package, created.ManifestSha256)),
            "a payload and matching tampered manifest cannot validate against the old anchor");
    }

    private static void TestConcurrentStagingTamper()
    {
        using var temporary = new TemporaryLabDirectory();
        byte[] content = SyntheticArtifact();
        string input = temporary.WriteInput("concurrent-source.bin", content);
        string package = temporary.PackagePath("concurrent-package");
        using var lockedWindow = new ManualResetEventSlim(initialState: false);
        using var lockedAttemptCompleted = new ManualResetEventSlim(initialState: false);
        using var publicationGap = new ManualResetEventSlim(initialState: false);
        using var gapTamperCompleted = new ManualResetEventSlim(initialState: false);
        bool lockedWriteSucceeded = false;
        bool gapWriteSucceeded = false;
        Exception? attackerFailure = null;
        string? observedPayload = null;

        Task attacker = Task.Run(
            () =>
            {
                try
                {
                    if (!lockedWindow.Wait(TimeSpan.FromSeconds(10)))
                    {
                        throw new TimeoutException("The locked publication window was not observed.");
                    }

                    try
                    {
                        using FileStream writer = new(
                            observedPayload!,
                            FileMode.Open,
                            FileAccess.Write,
                            FileShare.ReadWrite);
                        writer.Position = 0;
                        writer.WriteByte(0x7f);
                        writer.Flush(flushToDisk: true);
                        lockedWriteSucceeded = true;
                    }
                    catch (IOException)
                    {
                    }
                    finally
                    {
                        lockedAttemptCompleted.Set();
                    }

                    if (!publicationGap.Wait(TimeSpan.FromSeconds(10)))
                    {
                        throw new TimeoutException("The unlocked publication gap was not observed.");
                    }

                    using (FileStream writer = new(
                               observedPayload!,
                               FileMode.Open,
                               FileAccess.Write,
                               FileShare.ReadWrite))
                    {
                        writer.Position = 0;
                        writer.WriteByte(0x7f);
                        writer.Flush(flushToDisk: true);
                        gapWriteSucceeded = true;
                    }
                }
                catch (Exception error)
                {
                    attackerFailure = error;
                }
                finally
                {
                    lockedAttemptCompleted.Set();
                    gapTamperCompleted.Set();
                }
            });

        var hooks = new LabPackageCreateTestHooks(
            WhileFilesLocked: (_, payloadPath) =>
            {
                observedPayload = payloadPath;
                lockedWindow.Set();
                if (!lockedAttemptCompleted.Wait(TimeSpan.FromSeconds(10)))
                {
                    throw new TimeoutException("The locked write attempt did not complete.");
                }
            },
            AfterFilesUnlockedBeforeMove: (_, payloadPath) =>
            {
                Check(
                    string.Equals(observedPayload, payloadPath, StringComparison.OrdinalIgnoreCase),
                    "the publication hooks must identify the same staged payload");
                publicationGap.Set();
                if (!gapTamperCompleted.Wait(TimeSpan.FromSeconds(10)))
                {
                    throw new TimeoutException("The publication-gap tamper did not complete.");
                }
            });

        LabPackageException? createFailure = null;
        try
        {
            _ = LabPackage.CreateForTesting(input, package, device: null, hooks);
        }
        catch (LabPackageException error)
        {
            createFailure = error;
        }

        attacker.GetAwaiter().GetResult();
        Check(attackerFailure is null, $"the concurrent writer failed: {attackerFailure}");
        Check(!lockedWriteSucceeded, "exclusive staged-file handles must block concurrent writes");
        Check(gapWriteSucceeded, "the test must alter the payload during the publication gap");
        Check(
            createFailure?.Message.Contains("SHA-256 changed during publication", StringComparison.Ordinal) ==
            true,
            "post-move validation must detect a same-length publication-gap tamper");
        Check(
            createFailure?.Message.Contains("treat it as untrusted", StringComparison.Ordinal) == true,
            "a failed post-move validation must explicitly mark the retained package as untrusted");
        Check(
            Directory.Exists(package),
            "post-move failure must fail-leak instead of deleting through a raced pathname");
        string retainedManifestHash = Sha256Hex(File.ReadAllBytes(ManifestPath(package)));
        Check(
            Throws<LabPackageException>(() => LabPackage.Verify(package, retainedManifestHash)),
            "the retained package must remain unverifiable after the detected tamper");
    }

    private static void TestUnicodeScalarAndControlRules()
    {
        using var temporary = new TemporaryLabDirectory();
        string input = temporary.WriteInput("artifact.bin", SyntheticArtifact());
        string acceptedPackage = temporary.PackagePath("unicode-accepted-package");
        string twoCodeUnitScalar = "\U0001F680";
        string acceptedGpu = string.Concat(Enumerable.Repeat(twoCodeUnitScalar, 256));
        LabPackageResult accepted = LabPackage.Create(
            input,
            acceptedPackage,
            new LabDeviceMetadata(acceptedGpu, null, null));
        LabPackage.Verify(acceptedPackage, accepted.ManifestSha256);

        string tooLongGpu = acceptedGpu + twoCodeUnitScalar;
        Check(
            Throws<LabPackageException>(
                () => LabPackage.Create(
                    input,
                    temporary.PackagePath("unicode-too-long-package"),
                    new LabDeviceMetadata(tooLongGpu, null, null))),
            "text limits must count Unicode scalar values rather than UTF-16 code units");
        Check(
            Throws<LabPackageException>(
                () => LabPackage.Create(
                    input,
                    temporary.PackagePath("unicode-control-package"),
                    new LabDeviceMetadata("GPU\u0085control", null, null))),
            "C1 controls must be rejected during create");

        string parserPackage = temporary.PackagePath("unicode-parser-package");
        LabPackage.Create(input, parserPackage, new LabDeviceMetadata("GPU", null, null));
        string controlManifest = ReadManifest(parserPackage).Replace(
            "\"gpu\":\"GPU\"",
            "\"gpu\":\"GPU\\u0085control\"",
            StringComparison.Ordinal);
        string controlAnchor = WriteManifest(parserPackage, controlManifest);
        Check(
            Throws<LabPackageException>(
                () => LabPackage.Verify(parserPackage, controlAnchor)),
            "C1 controls must be rejected by the manifest parser");
    }

    private static void TestSemanticJsonIntegers()
    {
        using var temporary = new TemporaryLabDirectory();
        string input = temporary.WriteInput("artifact.bin", SyntheticArtifact());
        foreach (string representation in new[]
                 {
                     "1.0",
                     "1e0",
                     "10e-1",
                     "100000000000000000000000000000e-29",
                 })
        {
            string package = temporary.PackagePath(
                $"semantic-{Guid.NewGuid():N}");
            LabPackage.Create(input, package);
            string changed = ReadManifest(package).Replace(
                "\"schema_version\":1",
                $"\"schema_version\":{representation}",
                StringComparison.Ordinal);
            string changedAnchor = WriteManifest(package, changed);
            Check(
                LabPackage.Verify(package, changedAnchor).Manifest.SchemaVersion == 1,
                $"numeric representation {representation} must be accepted as integer 1");
        }

        string sizePackage = temporary.PackagePath("semantic-size-package");
        LabPackageResult sizeCreated = LabPackage.Create(input, sizePackage);
        string semanticSizeManifest = ReadManifest(sizePackage).Replace(
            $"\"size_bytes\":{sizeCreated.Manifest.Artifact.SizeBytes}",
            $"\"size_bytes\":{sizeCreated.Manifest.Artifact.SizeBytes}.0",
            StringComparison.Ordinal);
        string semanticSizeAnchor = WriteManifest(sizePackage, semanticSizeManifest);
        Check(
            LabPackage.Verify(sizePackage, semanticSizeAnchor).Manifest.Artifact.SizeBytes ==
            sizeCreated.Manifest.Artifact.SizeBytes,
            "a decimal representation of an integral size must be accepted");

        foreach (string representation in new[] { "1.5", "1e-1", "9223372036854775808" })
        {
            string package = temporary.PackagePath($"non-integral-{Guid.NewGuid():N}");
            LabPackage.Create(input, package);
            string changed = ReadManifest(package).Replace(
                "\"schema_version\":1",
                $"\"schema_version\":{representation}",
                StringComparison.Ordinal);
            string changedAnchor = WriteManifest(package, changed);
            Check(
                Throws<LabPackageException>(() => LabPackage.Verify(package, changedAnchor)),
                $"numeric representation {representation} must be rejected");
        }
    }

    private static void TestAlternateDataStreams()
    {
        using var temporary = new TemporaryLabDirectory();
        string cleanInput = temporary.WriteInput("clean.bin", SyntheticArtifact());
        string baseInput = temporary.WriteInput("base.bin", SyntheticArtifact());
        File.WriteAllText(baseInput + ":secret", "hidden");

        Check(
            Throws<LabPackageException>(
                () => LabPackage.Create(
                    baseInput + ":secret",
                    temporary.PackagePath("ads-address-package"))),
            "an explicit NTFS stream path must be rejected");
        Check(
            Throws<LabPackageException>(
                () => LabPackage.Create(
                    baseInput,
                    temporary.PackagePath("ads-source-package"))),
            "a source carrying an alternate stream must be rejected");

        string package = temporary.PackagePath("ads-package");
        LabPackageResult created = LabPackage.Create(cleanInput, package);
        string payload = PayloadPath(package);
        File.WriteAllText(payload + ":secret", "hidden");
        Check(
            Throws<LabPackageException>(
                () => LabPackage.Verify(package, created.ManifestSha256)),
            "a package payload carrying an alternate stream must be rejected");
    }

    private static void TestHardLinkedPackageFilesAreRejected()
    {
        using var temporary = new TemporaryLabDirectory();
        byte[] content = SyntheticArtifact();
        string input = temporary.WriteInput("hardlink-input.bin", content);

        string sourceTarget = temporary.WriteInput("source-target.bin", content);
        FileAttributes sourceTargetAttributes = File.GetAttributes(sourceTarget);
        string sourceLink = temporary.TrackFile(
            Path.Combine(temporary.RootPath, "source-link.bin"));
        CreateHardLink(sourceLink, sourceTarget);
        Check(
            Throws<LabPackageException>(
                () => LabPackage.Create(
                    sourceLink,
                    temporary.PackagePath("hardlink-source-package"))),
            "create must reject an input artifact with more than one filesystem link");
        Check(
            File.GetAttributes(sourceTarget) == sourceTargetAttributes,
            "create must not mutate attributes through a source hardlink");

        string payloadPackage = temporary.PackagePath("hardlink-payload-package");
        LabPackageResult payloadCreated = LabPackage.Create(input, payloadPackage);
        string outsidePayload = temporary.WriteInput("outside-payload.bin", content);
        FileAttributes outsidePayloadAttributes = File.GetAttributes(outsidePayload);
        string payload = PayloadPath(payloadPackage);
        File.Delete(payload);
        CreateHardLink(payload, outsidePayload);

        Check(
            Throws<LabPackageException>(
                () => LabPackage.Verify(payloadPackage, payloadCreated.ManifestSha256)),
            "verify must reject a payload hard-linked to a file outside the package");
        Check(
            File.GetAttributes(outsidePayload) == outsidePayloadAttributes,
            "verification must not mutate attributes through an external hardlink");

        string manifestPackage = temporary.PackagePath("hardlink-manifest-package");
        LabPackageResult manifestCreated = LabPackage.Create(input, manifestPackage);
        string manifest = ManifestPath(manifestPackage);
        byte[] manifestBytes = File.ReadAllBytes(manifest);
        string outsideManifest = temporary.WriteInput("outside-manifest.json", manifestBytes);
        FileAttributes outsideManifestAttributes = File.GetAttributes(outsideManifest);
        File.Delete(manifest);
        CreateHardLink(manifest, outsideManifest);

        Check(
            Throws<LabPackageException>(
                () => LabPackage.Verify(manifestPackage, manifestCreated.ManifestSha256)),
            "verify must reject a manifest hard-linked to a file outside the package");
        Check(
            File.GetAttributes(outsideManifest) == outsideManifestAttributes,
            "manifest verification must not mutate attributes through an external hardlink");
    }

    private static void TestUncPathsAreRejected()
    {
        using var temporary = new TemporaryLabDirectory();
        string input = temporary.WriteInput("local.bin", SyntheticArtifact());
        const string uncArtifact = @"\\invalid.invalid\share\artifact.bin";
        const string uncPackage = @"\\invalid.invalid\share\package";

        Check(
            Throws<LabPackageException>(
                () => LabPackage.Create(
                    uncArtifact,
                    temporary.PackagePath("unc-input-package"))),
            "create must reject a UNC input before filesystem access");
        Check(
            Throws<LabPackageException>(() => LabPackage.Create(input, uncPackage)),
            "create must reject a UNC output before filesystem access");
        Check(
            Throws<LabPackageException>(
                () => LabPackage.Verify(uncPackage, new string('0', 64))),
            "verify must reject a UNC package before filesystem access");
    }

    private static void TestCliCreateAndVerify()
    {
        using var temporary = new TemporaryLabDirectory();
        string input = temporary.WriteInput("cli.bin", SyntheticArtifact());
        string package = temporary.PackagePath("cli-package");
        using var output = new StringWriter();
        using var error = new StringWriter();

        int createExit = LabCli.Run(
            [
                "create",
                "--input",
                input,
                "--output",
                package,
                "--gpu",
                "RTX 3060",
                "--driver-version",
                "610.88",
                "--vbios-version",
                "94.06.25.00.fc",
            ],
            output,
            error);
        Check(createExit == 0, "CLI create must succeed");
        Check(error.ToString().Length == 0, "CLI create must not write an error");
        string anchor;
        using (JsonDocument createJson = JsonDocument.Parse(output.ToString()))
        {
            Check(
                createJson.RootElement.GetProperty("status").GetString() == "created",
                "CLI create must emit deterministic JSON status");
            anchor = createJson.RootElement.GetProperty("manifest_sha256").GetString()
                ?? throw new InvalidOperationException("create response has no manifest anchor");
        }

        output.GetStringBuilder().Clear();
        int verifyExit = LabCli.Run(
            [
                "verify",
                "--package",
                package,
                "--expected-manifest-sha256",
                anchor,
            ],
            output,
            error);
        Check(verifyExit == 0, "CLI anchored verify must succeed");
        using JsonDocument verifyJson = JsonDocument.Parse(output.ToString());
        Check(
            verifyJson.RootElement.GetProperty("status").GetString() == "verified",
            "CLI verify must emit deterministic JSON status");
    }

    private static void TestCliRequiresManifestAnchor()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        int exitCode = LabCli.Run(
            ["verify", "--package", "package"],
            output,
            error);

        Check(exitCode == 2, "a missing trust anchor must return usage error 2");
        Check(output.ToString().Length == 0, "invalid arguments must not write success output");
        using JsonDocument errorJson = JsonDocument.Parse(error.ToString());
        Check(
            errorJson.RootElement.GetProperty("error_code").GetString() == "invalid_arguments",
            "a missing anchor must return a machine-readable argument error");

        using var malformedOutput = new StringWriter();
        using var malformedError = new StringWriter();
        int malformedExitCode = LabCli.Run(
            ["verify", "--package", "package", "--expected-manifest-sha256", "xyz"],
            malformedOutput,
            malformedError);
        using JsonDocument malformedErrorJson = JsonDocument.Parse(malformedError.ToString());
        Check(malformedExitCode == 2, "a malformed trust anchor must return usage error 2");
        Check(
            malformedErrorJson.RootElement.GetProperty("error_code").GetString() ==
            "invalid_arguments",
            "a malformed anchor must be classified as invalid arguments");

        using var helpOutput = new StringWriter();
        using var helpError = new StringWriter();
        int helpExitCode = LabCli.Run(
            ["--help", "unexpected"],
            helpOutput,
            helpError);
        using JsonDocument helpErrorJson = JsonDocument.Parse(helpError.ToString());
        Check(helpExitCode == 2, "help with extra arguments must return usage error 2");
        Check(helpOutput.ToString().Length == 0, "invalid help usage must not write help output");
        Check(
            helpErrorJson.RootElement.GetProperty("error_code").GetString() ==
            "invalid_arguments",
            "invalid help usage must return a machine-readable argument error");
    }

    private static void TestGpuzLogAnalysis()
    {
        byte[] content = Encoding.Latin1.GetBytes(
            " Date , GPU Temperature [°C] , Hot Spot [°C] , PerfCap Reason [] , " +
            "CPU Temperature [°C] , Unpublished Channel [raw] ,\r\n" +
            "2026-08-25 18:40:27 , 33.3 , 43.8 , 16 , 31.0 , alpha ,\r\n" +
            "2026-08-25 18:40:28 , 33.5 , 43.6 , 16 , 32.0 , beta ,\r\n");

        GpuzLogAnalysis analysis = GpuzSensorLog.Analyze(content, "sensors.txt");
        Check(analysis.SchemaVersion == 1, "GPU-Z analysis must declare schema v1");
        Check(
            analysis.SourceKind == "gpuz_sensor_log_reference",
            "GPU-Z analysis must remain an external reference");
        Check(analysis.SampleCount == 2, "both GPU-Z samples must be retained");
        Check(analysis.SessionCount == 1, "a simple GPU-Z log must have one session");
        Check(analysis.Channels.Count == 5, "all GPU-Z channels must be retained");
        Check(analysis.MedianIntervalMs == 1000, "sample cadence must be derived");
        Check(
            analysis.Artifact.TextEncoding == "iso-8859-1-fallback",
            "legacy GPU-Z degree symbols must use the bounded fallback decoder");

        GpuzChannelAnalysis hotSpot = analysis.Channels.Single(
            channel => channel.Name == "Hot Spot");
        Check(hotSpot.SourceScope == "gpu_board", "hotspot must be GPU-scoped");
        Check(hotSpot.Category == "temperature", "hotspot must be a temperature channel");
        Check(
            hotSpot.NumericStatistics?.Minimum == 43.6 &&
            hotSpot.NumericStatistics.Maximum == 43.8,
            "hotspot statistics must match the source values");

        GpuzChannelAnalysis perfCap = analysis.Channels.Single(
            channel => channel.Name == "PerfCap Reason");
        Check(
            perfCap.Representation == "raw_code",
            "PerfCap Reason must not infer a private label from its raw code");

        GpuzChannelAnalysis cpu = analysis.Channels.Single(
            channel => channel.Name == "CPU Temperature");
        Check(cpu.SourceScope == "host_system", "CPU temperature must be host-scoped");
        GpuzChannelAnalysis unpublished = analysis.Channels.Single(
            channel => channel.Name == "Unpublished Channel");
        Check(
            unpublished.SourceScope == "unknown" && unpublished.Representation == "text",
            "an unrecognized channel must retain text and an unknown scope");
        Check(
            analysis.Samples[0].Values[1] == "43.8",
            "raw sample alignment must be preserved");
    }

    private static void TestGpuzLogRejectsMalformedRows()
    {
        byte[] malformed = Encoding.UTF8.GetBytes(
            "Date,GPU Temperature [°C],Hot Spot [°C],\n" +
            "2026-08-25 18:40:27,33.3,\n");
        Check(
            Throws<GpuzLogException>(() => GpuzSensorLog.Analyze(malformed, "bad.txt")),
            "a truncated GPU-Z row must fail closed");
    }

    private static void TestCsvParsersRejectExcessSampleRows()
    {
        var gpuz = new StringBuilder("Date,GPU Voltage [V]\n");
        for (int index = 0; index <= GpuzSensorLog.MaximumSamples; index++)
        {
            gpuz.Append("x,0\n");
        }

        GpuzLogException? gpuzError = CaptureException<GpuzLogException>(() =>
            GpuzSensorLog.Analyze(
                Encoding.UTF8.GetBytes(gpuz.ToString()),
                "too-many-gpuz-samples.csv"));
        Check(
            gpuzError?.Message.Contains(
                $"more than {GpuzSensorLog.MaximumSamples} samples",
                StringComparison.Ordinal) == true,
            "GPU-Z must reject sample row 250001 inside CSV parsing");

        var hwinfo = new StringBuilder(
            "Date,Time,\"GPU Core Voltage [V]\",\"GPU Power [W]\"\n");
        for (int index = 0; index <= HwinfoVoltageSensorLog.MaximumSamples; index++)
        {
            hwinfo.Append("x,x,0,0\n");
        }

        HwinfoVoltageLogException? hwinfoError =
            CaptureException<HwinfoVoltageLogException>(() =>
                HwinfoVoltageSensorLog.Analyze(
                    Encoding.UTF8.GetBytes(hwinfo.ToString()),
                    "too-many-hwinfo-samples.csv"));
        Check(
            hwinfoError?.Message.Contains(
                $"{HwinfoVoltageSensorLog.MaximumSamples}-sample-row limit",
                StringComparison.Ordinal) == true,
            "HWiNFO must reject sample row 250001 inside CSV parsing");
    }

    private static void TestCsvParsersRejectExcessFields()
    {
        byte[] gpuz = Encoding.UTF8.GetBytes(
            "Date" + new string(',', GpuzSensorLog.MaximumChannels + 2) + "\n");
        GpuzLogException? gpuzError = CaptureException<GpuzLogException>(() =>
            GpuzSensorLog.Analyze(gpuz, "too-wide-gpuz.csv"));
        Check(
            gpuzError?.Message.Contains("field parser limit", StringComparison.Ordinal) == true,
            "GPU-Z must reject a field-count bomb before materializing its header row");

        byte[] hwinfo = Encoding.UTF8.GetBytes(
            "Date" + new string(',', HwinfoVoltageSensorLog.MaximumFields) + "\n");
        HwinfoVoltageLogException? hwinfoError =
            CaptureException<HwinfoVoltageLogException>(() =>
                HwinfoVoltageSensorLog.Analyze(hwinfo, "too-wide-hwinfo.csv"));
        Check(
            hwinfoError?.Message.Contains("field parser limit", StringComparison.Ordinal) == true,
            "HWiNFO must reject a field-count bomb before materializing its header row");
    }

    private static void TestCsvParsersRejectExcessSessions()
    {
        const string gpuzHeader = "Date,GPU Voltage [V]\n";
        var gpuz = new StringBuilder(gpuzHeader);
        for (int index = 0; index < GpuzSensorLog.MaximumSessions; index++)
        {
            gpuz.Append(gpuzHeader);
        }

        GpuzLogException? gpuzError = CaptureException<GpuzLogException>(() =>
            GpuzSensorLog.Analyze(
                Encoding.UTF8.GetBytes(gpuz.ToString()),
                "too-many-gpuz-sessions.csv"));
        Check(
            gpuzError?.Message.Contains(
                $"more than {GpuzSensorLog.MaximumSessions} sessions",
                StringComparison.Ordinal) == true,
            "GPU-Z must reject appended session 1025 before storing its header row");

        const string hwinfoHeader =
            "Date,Time,\"GPU Core Voltage [V]\",\"GPU Power [W]\"\n";
        var hwinfo = new StringBuilder(hwinfoHeader);
        for (int index = 0; index < HwinfoVoltageSensorLog.MaximumSessions; index++)
        {
            hwinfo.Append(hwinfoHeader);
        }

        HwinfoVoltageLogException? hwinfoError =
            CaptureException<HwinfoVoltageLogException>(() =>
                HwinfoVoltageSensorLog.Analyze(
                    Encoding.UTF8.GetBytes(hwinfo.ToString()),
                    "too-many-hwinfo-sessions.csv"));
        Check(
            hwinfoError?.Message.Contains(
                $"more than {HwinfoVoltageSensorLog.MaximumSessions} sessions",
                StringComparison.Ordinal) == true,
            "HWiNFO must reject appended session 1025 before storing its header row");
    }

    private static void TestCsvParsersIgnoreBlankLines()
    {
        byte[] gpuz = Encoding.UTF8.GetBytes(
            "\nDate,GPU Voltage [V]\n\n2026-08-26 18:18:31,1.0\n\n");
        GpuzLogAnalysis gpuzAnalysis = GpuzSensorLog.Analyze(gpuz, "blank-lines-gpuz.csv");
        Check(
            gpuzAnalysis.SampleCount == 1 && gpuzAnalysis.SessionCount == 1,
            "GPU-Z blank lines must not become stored CSV rows or sessions");

        byte[] hwinfo = Encoding.UTF8.GetBytes(
            "\nDate,Time,\"GPU Core Voltage [V]\",\"GPU Power [W]\"\n\n" +
            "26.8.2026,18:18:31.000,1.0,50.0\n\n");
        HwinfoVoltageLogAnalysis hwinfoAnalysis = HwinfoVoltageSensorLog.Analyze(
            hwinfo,
            "blank-lines-hwinfo.csv");
        Check(
            hwinfoAnalysis.Samples.Count == 1,
            "HWiNFO blank lines must not become stored CSV rows or samples");
    }

    private static void TestGpuzAppendedSessions()
    {
        byte[] content = Encoding.UTF8.GetBytes(
            "Date,GPU Temperature [°C],Hot Spot [°C],\n" +
            "2026-08-25 18:40:27,33.3,43.8,\n" +
            "Date,GPU Temperature [°C],Hot Spot [°C],\n" +
            "2026-08-25 18:58:24,33.0,43.4,\n");
        GpuzLogAnalysis analysis = GpuzSensorLog.Analyze(content, "sessions.txt");

        Check(analysis.SampleCount == 2, "samples from both appended sessions must be retained");
        Check(analysis.SessionCount == 2, "repeated headers must delimit appended sessions");
        Check(analysis.Samples[0].SessionIndex == 0 && analysis.Samples[1].SessionIndex == 1,
            "each appended sample must preserve its session index");
        Check(analysis.Warnings.Any(warning => warning.Contains("2 appended GPU-Z sessions")),
            "appended sessions must be disclosed in the analysis warnings");
    }

    private static void TestGpuzChangedSessionLayout()
    {
        byte[] content = Encoding.UTF8.GetBytes(
            "Date,GPU Temperature [°C],Hot Spot [°C],\n" +
            "2026-08-25 18:40:27,33.3,43.8,\n" +
            "Date,GPU Temperature [°C],Board Power Draw [W],\n" +
            "2026-08-25 18:58:24,33.0,35.0,\n");
        Check(
            Throws<GpuzLogException>(() => GpuzSensorLog.Analyze(content, "changed.txt")),
            "an appended session with a changed channel layout must fail closed");
    }

    private static void TestCliGpuzLogAnalysis()
    {
        using var temporary = new TemporaryLabDirectory();
        string input = temporary.WriteInput(
            "gpuz.txt",
            Encoding.UTF8.GetBytes(
                "Date,GPU Temperature [°C],Hot Spot [°C],\n" +
                "2026-08-25 18:40:27,33.3,43.8,\n"));
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var error = new StringWriter(CultureInfo.InvariantCulture);

        int exitCode = LabCli.Run(
            ["analyze-gpuz-log", "--input", input],
            output,
            error);
        Check(exitCode == 0, "GPU-Z CLI analysis must succeed");
        Check(error.ToString().Length == 0, "GPU-Z CLI success must not write stderr");
        using JsonDocument document = JsonDocument.Parse(output.ToString());
        Check(
            document.RootElement.GetProperty("source_kind").GetString() ==
            "gpuz_sensor_log_reference",
            "GPU-Z CLI JSON must declare an external reference source");
        Check(
            document.RootElement.GetProperty("channels").GetArrayLength() == 2,
            "GPU-Z CLI JSON must contain both channels");
    }

    private static void TestExperimentMarker()
    {
        long beforeUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        ExperimentMarker first = ExperimentMarkers.Create("idle.baseline", "begin", null);
        ExperimentMarker second = ExperimentMarkers.Create("idle.baseline", "note", "ready");
        long afterUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        Check(first.SchemaVersion == 1, "marker must declare schema v1");
        Check(first.ScenarioId == "idle.baseline", "marker must retain the scenario id");
        Check(first.Phase == "begin", "marker must retain the phase");
        Check(first.UtcUnixMs >= beforeUtcMs && first.UtcUnixMs <= afterUtcMs,
            "marker UTC time must be captured during creation");
        Check(first.MonotonicNs >= 0, "marker monotonic time must be non-negative");
        Check(first.MonotonicFrequencyHz == Stopwatch.Frequency,
            "marker must record the source monotonic frequency");
        Check(second.MonotonicNs >= first.MonotonicNs,
            "successive marker monotonic timestamps must not go backwards");
        Check(first.Note is null && second.Note == "ready",
            "marker notes must preserve null and textual values");
    }

    private static void TestCliExperimentMarker()
    {
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var error = new StringWriter(CultureInfo.InvariantCulture);
        int exitCode = LabCli.Run(
            ["mark", "--scenario", "power.step-1", "--phase", "begin", "--note", "start"],
            output,
            error);

        Check(exitCode == 0, "marker CLI must succeed for a valid marker");
        Check(error.ToString().Length == 0, "marker CLI success must not write stderr");
        using JsonDocument marker = JsonDocument.Parse(output.ToString());
        Check(marker.RootElement.GetProperty("scenario_id").GetString() == "power.step-1",
            "marker CLI must serialize the scenario id");
        Check(marker.RootElement.GetProperty("monotonic_ns").GetInt64() >= 0,
            "marker CLI must serialize the monotonic timestamp");

        using var invalidOutput = new StringWriter(CultureInfo.InvariantCulture);
        using var invalidError = new StringWriter(CultureInfo.InvariantCulture);
        int invalidExitCode = LabCli.Run(
            ["mark", "--scenario", "Uppercase", "--phase", "middle"],
            invalidOutput,
            invalidError);
        using JsonDocument invalid = JsonDocument.Parse(invalidError.ToString());
        Check(invalidExitCode == 2, "invalid marker arguments must return usage error 2");
        Check(invalidOutput.ToString().Length == 0,
            "invalid marker arguments must not write stdout");
        Check(invalid.RootElement.GetProperty("error_code").GetString() == "invalid_arguments",
            "invalid marker arguments must use the machine-readable argument error");
    }

    private static void TestExperimentManifestAndSeriesAnalysis()
    {
        using var temporary = new TemporaryLabDirectory();
        string seriesInput = temporary.WriteInput(
            "numeric-series-v1.json",
            FixtureBytes("numeric-series-v1.json"));
        string seriesPackage = temporary.PackagePath("series-package");
        LabPackageResult package = LabPackage.Create(seriesInput, seriesPackage);
        string draftText = Encoding.UTF8.GetString(
                FixtureBytes("experiment-manifest-draft-v1.json"))
            .Replace(new string('0', 64), package.ManifestSha256, StringComparison.Ordinal);
        string draft = temporary.WriteInput(
            "experiment-manifest-draft.json",
            Encoding.UTF8.GetBytes(draftText));
        using var manifestOutput = new StringWriter(CultureInfo.InvariantCulture);
        using var manifestError = new StringWriter(CultureInfo.InvariantCulture);
        int manifestExit = LabCli.Run(
            [
                "finalize-experiment-manifest",
                "--input",
                draft,
                "--package-root",
                temporary.RootPath,
            ],
            manifestOutput,
            manifestError);

        Check(manifestExit == 0, "manifest CLI producer must succeed");
        Check(manifestError.ToString().Length == 0,
            "manifest CLI producer must not write stderr");
        byte[] manifestBytes = Encoding.UTF8.GetBytes(manifestOutput.ToString());
        using (JsonDocument manifestJson = JsonDocument.Parse(manifestBytes))
        {
            Check(
                manifestJson.RootElement.GetProperty("experiment_id").GetString() ==
                    "11111111-2222-4333-8444-555555555555" &&
                manifestJson.RootElement.GetProperty("artifact_packages")[0]
                    .GetProperty("manifest_sha256").GetString() == package.ManifestSha256 &&
                manifestJson.RootElement.GetProperty("artifact_packages")[0]
                    .GetProperty("scenario_id").GetString() == "synthetic-transition",
                "manifest producer must preserve the experiment, scenario, and package anchor");
        }

        string manifest = temporary.WriteInput("experiment-manifest.json", manifestBytes);
        string manifestSha256 = Sha256Hex(manifestBytes);
        ExperimentAnalysisReport report = ExperimentSeriesAnalyzer.Analyze(
            manifest,
            manifestSha256,
            temporary.RootPath,
            "series-package",
            maximumLagSamples: 2,
            Guid.Parse("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee"),
            DateTimeOffset.Parse(
                "2026-08-27T12:20:00.0000000Z",
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind));
        AnalysisCandidate candidate = report.Candidates.Single();
        AnalysisCorrelation correlation = candidate.Correlations.Single();
        Check(
            candidate.Stage == "raw_unknown" &&
            candidate.PhysicalName is null &&
            candidate.ValueUnit == "V",
            "offline analysis must preserve units without promoting a sensor identity");
        Check(
            candidate.Statistics.SampleCount == 8 &&
            candidate.Statistics.Mean == 1.125 &&
            candidate.Statistics.UpdatePeriodMs == 1000 &&
            candidate.Statistics.MinimumDelta == -1 &&
            candidate.Statistics.MaximumDelta == 1 &&
            candidate.Statistics.MeanDelta == 0,
            "analysis must emit deterministic statistics, update period, and deltas");
        Check(
            correlation.Method == "cross_correlation" &&
            correlation.Coefficient == 1 &&
            correlation.LagMs == -1000 &&
            correlation.SampleCount == 7,
            "analysis must select the strongest bounded lag without changing identity stage");
        Check(
            Throws<ExperimentSeriesAnalysisException>(
                () => ExperimentSeriesAnalyzer.Analyze(
                    manifest,
                    new string('0', 64),
                    temporary.RootPath,
                    "series-package",
                    2,
                    Guid.NewGuid(),
                    DateTimeOffset.UtcNow)),
            "analysis must reject an untrusted experiment manifest anchor");

        using var analysisOutput = new StringWriter(CultureInfo.InvariantCulture);
        using var analysisError = new StringWriter(CultureInfo.InvariantCulture);
        int analysisExit = LabCli.Run(
            [
                "analyze-experiment-series",
                "--manifest",
                manifest,
                "--expected-manifest-sha256",
                manifestSha256,
                "--package-root",
                temporary.RootPath,
                "--series-package",
                "series-package",
                "--max-lag-samples",
                "2",
                "--analysis-id",
                "aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee",
                "--created-at-utc",
                "2026-08-27T12:20:00.0000000Z",
            ],
            analysisOutput,
            analysisError);
        Check(analysisExit == 0 && analysisError.ToString().Length == 0,
            "analysis CLI producer must succeed offline");
        using JsonDocument analysisJson = JsonDocument.Parse(analysisOutput.ToString());
        Check(
            analysisJson.RootElement.GetProperty("schema_version").GetInt32() == 1 &&
            analysisJson.RootElement.GetProperty("candidates")[0]
                .GetProperty("stage").GetString() == "raw_unknown" &&
            analysisJson.RootElement.GetProperty("candidates")[0]
                .GetProperty("value_unit").GetString() == "V" &&
            analysisJson.RootElement.GetProperty("candidates")[0]
                .GetProperty("statistics").GetProperty("mean_delta").GetDouble() == 0,
            "analysis CLI must serialize analysis-report-v1 without semantic promotion");
    }

    private static void TestExperimentManifestRejectsUntrustedPackage()
    {
        using var temporary = new TemporaryLabDirectory();
        string seriesInput = temporary.WriteInput(
            "numeric-series-v1.json",
            FixtureBytes("numeric-series-v1.json"));
        _ = LabPackage.Create(
            seriesInput,
            temporary.PackagePath("series-package"));
        string draft = temporary.WriteInput(
            "untrusted-experiment-manifest.json",
            FixtureBytes("experiment-manifest-draft-v1.json"));

        Check(
            Throws<ExperimentManifestException>(
                () => ExperimentManifestProducer.FinalizeFile(draft, temporary.RootPath)),
            "manifest production must verify every package against its supplied trust anchor");
    }

    private static void TestExperimentManifestInputSnapshot()
    {
        using var temporary = new TemporaryLabDirectory();
        string target = temporary.WriteInput(
            "manifest-target.json",
            FixtureBytes("experiment-manifest-draft-v1.json"));
        string hardLink = temporary.TrackFile(
            Path.Combine(temporary.RootPath, "manifest-hardlink.json"));
        CreateHardLink(hardLink, target);

        Check(
            Throws<ExperimentManifestException>(
                () => ExperimentManifestProducer.FinalizeFile(
                    hardLink,
                    temporary.RootPath)),
            "manifest finalization must reject an input with more than one filesystem link");
    }

    private static void TestExperimentAnalysisUsesVerifiedPayloadSnapshot()
    {
        using var temporary = new TemporaryLabDirectory();
        byte[] originalSeries = FixtureBytes("numeric-series-v1.json");
        string seriesInput = temporary.WriteInput(
            "numeric-series-v1.json",
            originalSeries);
        string seriesPackage = temporary.PackagePath("series-package");
        LabPackageResult package = LabPackage.Create(seriesInput, seriesPackage);
        string draftText = Encoding.UTF8.GetString(
                FixtureBytes("experiment-manifest-draft-v1.json"))
            .Replace(new string('0', 64), package.ManifestSha256, StringComparison.Ordinal);
        string draft = temporary.WriteInput(
            "experiment-manifest-draft.json",
            Encoding.UTF8.GetBytes(draftText));
        byte[] manifestBytes = Encoding.UTF8.GetBytes(
            ExperimentManifestProducer.FinalizeFile(draft, temporary.RootPath));
        string manifest = temporary.WriteInput("experiment-manifest.json", manifestBytes);
        string manifestSha256 = Sha256Hex(manifestBytes);
        string payload = PayloadPath(seriesPackage);
        bool tamperedAfterVerifiedRead = false;

        ExperimentAnalysisReport report = ExperimentSeriesAnalyzer.AnalyzeForTesting(
            manifest,
            manifestSha256,
            temporary.RootPath,
            "series-package",
            maximumLagSamples: 2,
            Guid.Parse("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee"),
            DateTimeOffset.Parse(
                "2026-08-27T12:20:00.0000000Z",
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind),
            afterVerifiedPayloadRead: () =>
            {
                File.WriteAllBytes(payload, Enumerable.Repeat((byte)' ', originalSeries.Length).ToArray());
                tamperedAfterVerifiedRead = true;
            });

        AnalysisCandidate candidate = report.Candidates.Single();
        Check(tamperedAfterVerifiedRead,
            "the regression hook must replace the package pathname after its verified read");
        Check(candidate.Statistics.SampleCount == 8 && candidate.Statistics.Mean == 1.125,
            "analysis must parse the exact anchored bytes already read, not the replaced pathname");
        Check(
            Throws<LabPackageException>(
                () => LabPackage.Verify(seriesPackage, package.ManifestSha256)),
            "a later verification must reject the payload replacement against the old anchor");
    }

    private static void TestExperimentManifestScenarioBinding()
    {
        using var temporary = new TemporaryLabDirectory();
        string seriesInput = temporary.WriteInput(
            "numeric-series-v1.json",
            FixtureBytes("numeric-series-v1.json"));
        string seriesPackage = temporary.PackagePath("series-package");
        LabPackageResult package = LabPackage.Create(seriesInput, seriesPackage);

        JsonObject unknownScenario = ExperimentManifestFixture(package.ManifestSha256);
        unknownScenario["artifact_packages"]![0]!["scenario_id"] = "missing-scenario";
        string unknownDraft = temporary.WriteInput(
            "unknown-scenario-draft.json",
            Encoding.UTF8.GetBytes(unknownScenario.ToJsonString()));
        Check(
            Throws<ExperimentManifestException>(() =>
                ExperimentManifestProducer.FinalizeFile(unknownDraft, temporary.RootPath)),
            "artifact packages must not reference an undeclared scenario");

        JsonObject emptyCompleted = ExperimentManifestFixture(package.ManifestSha256);
        emptyCompleted["artifact_packages"] = new JsonArray();
        string emptyDraft = temporary.WriteInput(
            "empty-completed-draft.json",
            Encoding.UTF8.GetBytes(emptyCompleted.ToJsonString()));
        Check(
            Throws<ExperimentManifestException>(() =>
                ExperimentManifestProducer.FinalizeFile(emptyDraft, temporary.RootPath)),
            "completed experiments must retain at least one artifact package");

        JsonObject supportingPackage = ExperimentManifestFixture(package.ManifestSha256);
        supportingPackage["artifact_packages"]![0]!["scenario_id"] = null;
        string supportingDraft = temporary.WriteInput(
            "supporting-package-draft.json",
            Encoding.UTF8.GetBytes(supportingPackage.ToJsonString()));
        byte[] supportingManifestBytes = Encoding.UTF8.GetBytes(
            ExperimentManifestProducer.FinalizeFile(supportingDraft, temporary.RootPath));
        string supportingManifest = temporary.WriteInput(
            "supporting-package-manifest.json",
            supportingManifestBytes);
        Check(
            Throws<ExperimentSeriesAnalysisException>(() =>
                ExperimentSeriesAnalyzer.Analyze(
                    supportingManifest,
                    Sha256Hex(supportingManifestBytes),
                    temporary.RootPath,
                    "series-package",
                    2,
                    Guid.NewGuid(),
                    DateTimeOffset.UtcNow)),
            "supporting or historical packages with null scenario_id must not be analyzed as a series");
    }

    private static void TestExperimentAnalysisScenarioWindowAndWorkLimit()
    {
        using var temporary = new TemporaryLabDirectory();
        string seriesInput = temporary.WriteInput(
            "numeric-series-v1.json",
            FixtureBytes("numeric-series-v1.json"));
        string seriesPackage = temporary.PackagePath("series-package");
        LabPackageResult package = LabPackage.Create(seriesInput, seriesPackage);

        JsonObject outsideWindow = ExperimentManifestFixture(package.ManifestSha256);
        outsideWindow["markers"]![0]!["monotonic_ns"] = 1;
        string outsideDraft = temporary.WriteInput(
            "outside-window-draft.json",
            Encoding.UTF8.GetBytes(outsideWindow.ToJsonString()));
        byte[] outsideManifestBytes = Encoding.UTF8.GetBytes(
            ExperimentManifestProducer.FinalizeFile(outsideDraft, temporary.RootPath));
        string outsideManifest = temporary.WriteInput(
            "outside-window-manifest.json",
            outsideManifestBytes);
        Check(
            Throws<ExperimentSeriesAnalysisException>(() =>
                ExperimentSeriesAnalyzer.Analyze(
                    outsideManifest,
                    Sha256Hex(outsideManifestBytes),
                    temporary.RootPath,
                    "series-package",
                    2,
                    Guid.NewGuid(),
                    DateTimeOffset.UtcNow)),
            "every numeric sample must remain inside its bound scenario markers");

        JsonObject largeSeries = JsonNode.Parse(FixtureBytes("numeric-series-v1.json"))!
            .AsObject();
        var samples = new JsonArray();
        var references = new JsonArray();
        for (int index = 0; index <= 6000; index++)
        {
            samples.Add(new JsonObject
            {
                ["monotonic_ns"] = index,
                ["value"] = index % 17,
            });
            references.Add(new JsonObject
            {
                ["monotonic_ns"] = index,
                ["value"] = (index + 1) % 17,
            });
        }

        largeSeries["samples"] = samples;
        largeSeries["reference"]!["samples"] = references;
        string largeInput = temporary.WriteInput(
            "large-numeric-series-v1.json",
            Encoding.UTF8.GetBytes(largeSeries.ToJsonString()));
        string largePackagePath = temporary.PackagePath("large-series-package");
        LabPackageResult largePackage = LabPackage.Create(largeInput, largePackagePath);
        JsonObject largeManifestRoot = ExperimentManifestFixture(largePackage.ManifestSha256);
        largeManifestRoot["artifact_packages"]![0]!["relative_path"] =
            "large-series-package";
        string largeDraft = temporary.WriteInput(
            "large-series-draft.json",
            Encoding.UTF8.GetBytes(largeManifestRoot.ToJsonString()));
        byte[] largeManifestBytes = Encoding.UTF8.GetBytes(
            ExperimentManifestProducer.FinalizeFile(largeDraft, temporary.RootPath));
        string largeManifest = temporary.WriteInput(
            "large-series-manifest.json",
            largeManifestBytes);
        Check(
            Throws<ExperimentSeriesAnalysisException>(() =>
                ExperimentSeriesAnalyzer.Analyze(
                    largeManifest,
                    Sha256Hex(largeManifestBytes),
                    temporary.RootPath,
                    "large-series-package",
                    1000,
                    Guid.NewGuid(),
                    DateTimeOffset.UtcNow)),
            "correlation must reject workloads above the defensive pair-evaluation limit");
    }

    private static void TestExperimentAnalysisRejectsNonFiniteDerivedValues()
    {
        using var temporary = new TemporaryLabDirectory();
        JsonObject extremeSeries = JsonNode.Parse(FixtureBytes("numeric-series-v1.json"))!
            .AsObject();
        foreach (JsonNode? sample in extremeSeries["samples"]!.AsArray())
        {
            sample!["value"] = 1e308;
        }

        string seriesInput = temporary.WriteInput(
            "extreme-numeric-series-v1.json",
            Encoding.UTF8.GetBytes(extremeSeries.ToJsonString()));
        string seriesPackagePath = temporary.PackagePath("extreme-series-package");
        LabPackageResult seriesPackage = LabPackage.Create(seriesInput, seriesPackagePath);
        JsonObject manifestRoot = ExperimentManifestFixture(seriesPackage.ManifestSha256);
        manifestRoot["artifact_packages"]![0]!["relative_path"] = "extreme-series-package";
        string draft = temporary.WriteInput(
            "extreme-series-draft.json",
            Encoding.UTF8.GetBytes(manifestRoot.ToJsonString()));
        byte[] manifestBytes = Encoding.UTF8.GetBytes(
            ExperimentManifestProducer.FinalizeFile(draft, temporary.RootPath));
        string manifest = temporary.WriteInput("extreme-series-manifest.json", manifestBytes);

        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var error = new StringWriter(CultureInfo.InvariantCulture);
        int exitCode = LabCli.Run(
            [
                "analyze-experiment-series",
                "--manifest",
                manifest,
                "--expected-manifest-sha256",
                Sha256Hex(manifestBytes),
                "--package-root",
                temporary.RootPath,
                "--series-package",
                "extreme-series-package",
            ],
            output,
            error);

        Check(exitCode == 1, "non-finite derived values must return the analysis failure code");
        Check(output.ToString().Length == 0,
            "non-finite derived values must not emit a partial report");
        using JsonDocument errorJson = JsonDocument.Parse(error.ToString());
        Check(
            errorJson.RootElement.GetProperty("error_code").GetString() == "analysis_error" &&
            errorJson.RootElement.GetProperty("message").GetString()!
                .Contains("non-finite", StringComparison.Ordinal),
            "non-finite derived values must use the structured analysis_error contract");
    }

    private static JsonObject ExperimentManifestFixture(string packageManifestSha256)
    {
        JsonObject root = JsonNode.Parse(
                FixtureBytes("experiment-manifest-draft-v1.json"))!
            .AsObject();
        root["artifact_packages"]![0]!["manifest_sha256"] = packageManifestSha256;
        return root;
    }

    private static void TestGpuzCorrelation()
    {
        byte[] content = Encoding.UTF8.GetBytes(
            "Date,Hot Spot [°C],GPU Temperature [°C],Board Power Draw [W],CPU Temperature [°C],PerfCap Reason [],\n" +
            "2026-08-25 18:40:27,40,30,10,20,16,\n" +
            "2026-08-25 18:40:28,42,31,20,19,16,\n" +
            "2026-08-25 18:40:29,44,32,30,18,16,\n" +
            "2026-08-25 18:40:30,46,33,40,17,16,\n");
        GpuzLogAnalysis analysis = GpuzSensorLog.Analyze(content, "correlation.csv");
        GpuzCorrelationReport report = GpuzCorrelation.Analyze(analysis, "Hot Spot");

        Check(report.SchemaVersion == 1, "correlation must declare schema v1");
        Check(report.SessionCount == 1, "single-session correlation must report one session");
        Check(report.Method == "pearson_zero_lag", "correlation method must be explicit");
        Check(report.Pairs.Count == 3, "raw-code channels must not be treated as measurements");
        Check(report.Pairs[0].Coefficient is 1 or -1,
            "perfectly linear candidates must have unit absolute correlation");
        GpuzCorrelationPair host = report.Pairs.Single(pair => pair.Channel == "CPU Temperature");
        Check(host.SourceScope == "host_system" && host.Coefficient == -1,
            "host correlation must remain visible and scoped to the host");
    }

    private static void TestGpuzCorrelationLimits()
    {
        byte[] content = Encoding.UTF8.GetBytes(
            "Date,Hot Spot [°C],GPU Temperature [°C],Board Power Draw [W],\n" +
            "2026-08-25 18:40:27,40,30,10,\n" +
            "2026-08-25 18:40:28,40,30,20,\n" +
            "2026-08-25 18:40:29,40,30,30,\n");
        GpuzLogAnalysis analysis = GpuzSensorLog.Analyze(content, "constant.csv");
        GpuzCorrelationReport report = GpuzCorrelation.Analyze(analysis, "Hot Spot");
        Check(report.Pairs.All(pair => pair.Status == "constant_reference"),
            "a constant reference must not produce correlation coefficients");
        Check(report.Pairs.All(pair => pair.Coefficient is null),
            "non-computable correlations must serialize as null");
        Check(
            Throws<GpuzCorrelationException>(
                () => GpuzCorrelation.Analyze(analysis, "Missing channel")),
            "an unknown reference channel must fail explicitly");
    }

    private static void TestGpuzSessionCorrelation()
    {
        byte[] content = Encoding.UTF8.GetBytes(
            "Date,Hot Spot [°C],GPU Temperature [°C],\n" +
            "2026-08-25 18:40:27,40,30,\n" +
            "2026-08-25 18:40:28,42,31,\n" +
            "2026-08-25 18:40:29,44,32,\n" +
            "Date,Hot Spot [°C],GPU Temperature [°C],\n" +
            "2026-08-25 18:58:24,50,35,\n" +
            "2026-08-25 18:58:25,51,34,\n" +
            "2026-08-25 18:58:26,52,33,\n");
        GpuzLogAnalysis analysis = GpuzSensorLog.Analyze(content, "sessions.csv");
        GpuzCorrelationReport report = GpuzCorrelation.Analyze(analysis, "Hot Spot", 1);

        Check(report.SessionCount == 2 && report.SelectedSessionIndex == 1,
            "session correlation must preserve total and selected session indexes");
        Check(report.SampleCount == 3, "session correlation must use only selected samples");
        Check(report.Pairs.Single().Coefficient == -1,
            "session correlation must not mix observations from another session");
        Check(
            Throws<GpuzCorrelationException>(
                () => GpuzCorrelation.Analyze(analysis, "Hot Spot", 2)),
            "an out-of-range session index must fail explicitly");
    }

    private static void TestThermChannelCorrelation()
    {
        using var temporary = new TemporaryLabDirectory();
        byte[] logBytes = Encoding.Latin1.GetBytes(
            "Date,GPU Temperature [°C],Hot Spot [°C],\r\n" +
            "2026-08-25 18:40:27,-,-,\r\n" +
            "Date,GPU Temperature [°C],Hot Spot [°C],\r\n" +
            "2026-08-25 22:37:40,33.1,43.1,\r\n" +
            "2026-08-25 22:37:41,33.3,43.3,\r\n" +
            "2026-08-25 22:37:42,33.4,43.4,\r\n" +
            "2026-08-25 22:37:43,33.5,43.5,\r\n");
        string log = temporary.WriteInput("gpuz-therm.txt", logBytes);
        string observation = temporary.WriteInput(
            "therm-observation.json",
            Encoding.UTF8.GetBytes(
                ThermChannelObservationJson(logBytes.LongLength, invalidLayout: false)));

        ThermChannelCorrelationReport report =
            ThermChannelCorrelation.AnalyzeFiles(observation, log);
        Check(
            report.SchemaVersion == 1 &&
            report.SourceKind == "nvapi_therm_channel_reference_correlation",
            "thermal correlation must declare its stable v1 source");
        Check(
            report.SelectedSessionIndex == 1 &&
            report.MappingStatus == "matched_external_reference",
            "thermal correlation must select the bounded appended session");
        Check(
            report.Mappings[0].SemanticChannel == "gpu_die_temperature" &&
            report.Mappings[0].ReferenceChannel == "GPU Temperature" &&
            report.Mappings[0].MaximumAbsoluteErrorCelsius <= 0.051,
            "channel 0 must match the GPU die reference within display rounding");
        Check(
            report.Mappings[1].SemanticChannel == "gpu_hotspot_temperature" &&
            report.Mappings[1].ReferenceChannel == "Hot Spot" &&
            report.Mappings[1].MaximumAbsoluteErrorCelsius <= 0.051,
            "channel 1 must match the hotspot reference within display rounding");
        Check(
            report.AlternativeCombinedMeanAbsoluteErrorCelsius >=
                report.CombinedMeanAbsoluteErrorCelsius + 1,
            "the swapped thermal mapping must be rejected quantitatively");

        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var error = new StringWriter(CultureInfo.InvariantCulture);
        int exitCode = LabCli.Run(
            [
                "correlate-nvapi-therm-channel",
                "--observation",
                observation,
                "--gpuz-log",
                log,
            ],
            output,
            error);
        Check(exitCode == 0 && error.ToString().Length == 0,
            "thermal correlation CLI must succeed offline");
        using JsonDocument document = JsonDocument.Parse(output.ToString());
        Check(
            document.RootElement.GetProperty("mapping_status").GetString() ==
                "matched_external_reference" &&
            document.RootElement.GetProperty("mappings").GetArrayLength() == 2,
            "thermal correlation CLI must serialize both identified channels");

        string invalidObservation = temporary.WriteInput(
            "invalid-therm-observation.json",
            Encoding.UTF8.GetBytes(
                ThermChannelObservationJson(logBytes.LongLength, invalidLayout: true)));
        Check(
            Throws<ThermChannelCorrelationException>(
                () => ThermChannelCorrelation.AnalyzeFiles(invalidObservation, log)),
            "thermal correlation must reject a changed channel layout");
    }

    private static void TestThermChannelCorrelationV2()
    {
        using var temporary = new TemporaryLabDirectory();
        string gpuz = temporary.WriteInput(
            "gpuz-therm-channel-reference-v2.csv",
            FixtureBytes("gpuz-therm-channel-reference-v2.csv"));
        string observation = temporary.WriteInput(
            "nvapi-therm-channel-v2-observation-v2.json",
            FixtureBytes("nvapi-therm-channel-v2-observation-v2.json"));

        ThermChannelCorrelationReportV2 report =
            ThermChannelCorrelationV2.AnalyzeFiles(observation, gpuz);
        Check(
            report.SchemaVersion == 2 &&
            report.MappingStatus == "matched_external_reference" &&
            report.Selection.SelectedSessionIndex == 2 &&
            report.Selection.IgnoredSessionIndicesWithoutExactChannels.SequenceEqual([0]) &&
            report.Selection.RejectedSessionIndicesWithInvalidExactChannelData.SequenceEqual([1]) &&
            report.DirectComparison.Mappings[0].ReferenceChannel == "GPU Temperature" &&
            report.DirectComparison.Mappings[1].ReferenceChannel == "Hot Spot" &&
            report.InvertedComparison.CombinedMeanAbsoluteErrorCelsius >= 10,
            "thermal v2 must isolate the incompatible session and preserve direct/inverted evidence");

        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var error = new StringWriter(CultureInfo.InvariantCulture);
        int exitCode = LabCli.Run(
            [
                "correlate-nvapi-therm-channel-v2",
                "--observation",
                observation,
                "--gpuz-log",
                gpuz,
            ],
            output,
            error);
        using JsonDocument cli = JsonDocument.Parse(output.ToString());
        Check(
            exitCode == 0 && error.ToString().Length == 0 &&
            cli.RootElement.GetProperty("schema_version").GetInt32() == 2 &&
            cli.RootElement.GetProperty("selection")
                .GetProperty("selected_session_index").GetInt32() == 2,
            "thermal v2 CLI must serialize the hardened correlation contract");

        JsonObject wrongProfile = JsonNode.Parse(
            FixtureBytes("nvapi-therm-channel-v2-observation-v2.json"))!.AsObject();
        wrongProfile["profile"]!["gpu"]!["uuid"] =
            "GPU-00000000-0000-0000-0000-000000000000";
        string wrongObservation = temporary.WriteInput(
            "wrong-thermal-profile-v2.json",
            Encoding.UTF8.GetBytes(wrongProfile.ToJsonString()));
        Check(
            Throws<ThermChannelCorrelationV2Exception>(() =>
                ThermChannelCorrelationV2.AnalyzeFiles(wrongObservation, gpuz)),
            "thermal v2 must reject a different physical GPU UUID before correlation");
    }

    private static void TestVoltageStatusCorrelationV2()
    {
        using var temporary = new TemporaryLabDirectory();
        string gpuz = temporary.WriteInput(
            "gpuz-voltage-reference.csv",
            FixtureBytes("gpuz-voltage-reference.csv"));
        string hwinfo = temporary.WriteInput(
            "hwinfo-voltage-reference.csv",
            FixtureBytes("hwinfo-voltage-reference.csv"));
        string observation = temporary.WriteInput(
            "voltage-observation-v2.json",
            FixtureBytes("nvapi-voltage-status-v1-observation-v2.json"));

        VoltageStatusCorrelationReportV2 report =
            VoltageStatusCorrelationV2.AnalyzeFiles(observation, gpuz, hwinfo);
        Check(
            report.SchemaVersion == 2 &&
            report.SourceKind == "nvapi_voltage_status_reference_correlation" &&
            report.MappingStatus == "matched_external_reference",
            "voltage v2 correlation must declare and match the fixed profile");
        Check(
            report.Mapping.WordIndex == 10 &&
            report.Mapping.OffsetBytes == 40 &&
            report.Mapping.DistinctRawValueCount == 3,
            "voltage v2 must retain the bounded word-10 mapping and independent values");
        Check(
            report.GpuzReference.Source == "GPU-Z" &&
            report.GpuzReference.MaximumAbsoluteErrorVolts <= 0.001 &&
            report.GpuzReference.AlignmentMethod ==
                "bounded_session_order_linear_resampling",
            "GPU-Z comparison must use time-bounded ordered alignment, not error search");
        Check(
            report.HwinfoReference is not null &&
            report.HwinfoReference.Source == "HWiNFO" &&
            report.HwinfoReference.MaximumAbsoluteErrorVolts <= 0.002 &&
            report.HwinfoReference.MaximumAlignmentDeltaMs == 0,
            "a growing HWiNFO prefix must be retained as explicit corroboration");

        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var error = new StringWriter(CultureInfo.InvariantCulture);
        int exitCode = LabCli.Run(
            [
                "correlate-nvapi-voltage-status-v2",
                "--observation",
                observation,
                "--gpuz-log",
                gpuz,
                "--hwinfo-log",
                hwinfo,
            ],
            output,
            error);
        Check(
            exitCode == 0 && error.ToString().Length == 0,
            "voltage v2 CLI must succeed with both anchored references");
        using (JsonDocument document = JsonDocument.Parse(output.ToString()))
        {
            Check(
                document.RootElement.GetProperty("mapping_status").GetString() ==
                    "matched_external_reference" &&
                document.RootElement.GetProperty("hwinfo_reference").ValueKind ==
                    JsonValueKind.Object,
                "voltage v2 CLI must explicitly serialize GPU-Z plus HWiNFO mode");
        }

        JsonObject gpuzOnlyRoot = VoltageObservationFixture();
        gpuzOnlyRoot["references"]!.AsObject()["hwinfo"] = null;
        string gpuzOnlyObservation = temporary.WriteInput(
            "voltage-observation-gpuz-only.json",
            Encoding.UTF8.GetBytes(gpuzOnlyRoot.ToJsonString()));
        VoltageStatusCorrelationReportV2 gpuzOnly =
            VoltageStatusCorrelationV2.AnalyzeFiles(gpuzOnlyObservation, gpuz);
        Check(
            gpuzOnly.MappingStatus == "matched_external_reference" &&
            gpuzOnly.HwinfoReference is null &&
            gpuzOnly.Warnings.Any(warning => warning.Contains(
                "sole external reference",
                StringComparison.Ordinal)),
            "GPU-Z-only mode must remain valid and be explicit in the report");
    }

    private static void TestVoltageStatusCorrelationV2RejectsProfileDrift()
    {
        using var temporary = new TemporaryLabDirectory();
        string gpuz = temporary.WriteInput(
            "gpuz-voltage-reference.csv",
            FixtureBytes("gpuz-voltage-reference.csv"));
        string hwinfo = temporary.WriteInput(
            "hwinfo-voltage-reference.csv",
            FixtureBytes("hwinfo-voltage-reference.csv"));

        JsonObject driverRoot = VoltageObservationFixture();
        driverRoot["profile"]!["gpu"]!["driver_version"] = "611.00";
        string wrongDriver = temporary.WriteInput(
            "wrong-driver.json",
            Encoding.UTF8.GetBytes(driverRoot.ToJsonString()));
        Check(
            Throws<VoltageStatusCorrelationV2Exception>(() =>
                VoltageStatusCorrelationV2.AnalyzeFiles(wrongDriver, gpuz, hwinfo)),
            "a different driver must fail closed even with matching PCI IDs");

        JsonObject vbiosRoot = VoltageObservationFixture();
        vbiosRoot["profile"]!["gpu"]!["vbios_version"] = "94.06.25.00.fd";
        string wrongVbios = temporary.WriteInput(
            "wrong-vbios.json",
            Encoding.UTF8.GetBytes(vbiosRoot.ToJsonString()));
        Check(
            Throws<VoltageStatusCorrelationV2Exception>(() =>
                VoltageStatusCorrelationV2.AnalyzeFiles(wrongVbios, gpuz, hwinfo)),
            "a different VBIOS must fail closed");

        JsonObject loadedModuleRoot = VoltageObservationFixture();
        loadedModuleRoot["profile"]!["loaded_nvapi_module"]!["file_sha256"] =
            new string('0', 64);
        string wrongLoadedModule = temporary.WriteInput(
            "wrong-loaded-module.json",
            Encoding.UTF8.GetBytes(loadedModuleRoot.ToJsonString()));
        Check(
            Throws<VoltageStatusCorrelationV2Exception>(() =>
                VoltageStatusCorrelationV2.AnalyzeFiles(
                    wrongLoadedModule,
                    gpuz,
                    hwinfo)),
            "the debugger-proven loaded NVAPI image must match the allowlisted digest");

        JsonObject loadedRangeRoot = VoltageObservationFixture();
        loadedRangeRoot["profile"]!["loaded_nvapi_module"]!["end_address"] =
            "0x68198010";
        string wrongLoadedRange = temporary.WriteInput(
            "wrong-loaded-module-range.json",
            Encoding.UTF8.GetBytes(loadedRangeRoot.ToJsonString()));
        Check(
            Throws<VoltageStatusCorrelationV2Exception>(() =>
                VoltageStatusCorrelationV2.AnalyzeFiles(
                    wrongLoadedRange,
                    gpuz,
                    hwinfo)),
            "the debugger-proven module range must contain the allowlisted function RVA");

        JsonObject rawRoot = VoltageObservationFixture();
        rawRoot["samples"]!.AsArray()[1]!["raw_words"]!.AsArray()[10] =
            "0x00000001";
        string inconsistentWord = temporary.WriteInput(
            "inconsistent-word.json",
            Encoding.UTF8.GetBytes(rawRoot.ToJsonString()));
        Check(
            Throws<VoltageStatusCorrelationV2Exception>(() =>
                VoltageStatusCorrelationV2.AnalyzeFiles(
                    inconsistentWord,
                    gpuz,
                    hwinfo)),
            "selected microvolts must equal raw_words[10]");
    }

    private static void TestVoltageStatusCorrelationV2ChangedEarlierLayout()
    {
        using var temporary = new TemporaryLabDirectory();
        byte[] currentSession = FixtureBytes("gpuz-voltage-reference.csv");
        byte[] previousSession = Encoding.UTF8.GetBytes(
            "Date,Crossbar Clock [MHz],Board Power Draw [W],\n" +
            "2026-08-26 17:00:00,900.0,62.0,\n" +
            "2026-08-26 17:00:01,901.0,62.1,\n" +
            "2026-08-26 17:00:02,902.0,62.2,\n");
        byte[] combined = previousSession.Concat(currentSession).ToArray();
        string gpuz = temporary.WriteInput("gpuz-voltage-reference.csv", combined);

        JsonObject root = VoltageObservationFixture();
        root["references"]!.AsObject()["hwinfo"] = null;
        JsonObject reference = root["references"]!["gpuz"]!.AsObject();
        reference["prefix_sha256"] = Sha256Hex(combined);
        reference["size_bytes_before"] = combined.LongLength - 2;
        reference["size_bytes_midpoint"] = combined.LongLength - 1;
        reference["size_bytes_after"] = combined.LongLength;
        string observation = temporary.WriteInput(
            "changed-earlier-layout.json",
            Encoding.UTF8.GetBytes(root.ToJsonString()));

        VoltageStatusCorrelationReportV2 report =
            VoltageStatusCorrelationV2.AnalyzeFiles(observation, gpuz);
        Check(
            report.MappingStatus == "matched_external_reference" &&
            report.GpuzReference.SelectedSessionIndex == 1,
            "v2 must preserve the whole-prefix hash while selecting only the bounded current layout");
        Check(
            Throws<GpuzLogException>(() =>
                GpuzSensorLog.Analyze(combined, "changed-layout.csv")),
            "the global GPU-Z parser must continue rejecting changed appended layouts");
    }

    private static void TestVoltageStatusCorrelationV2RejectsReferenceDrift()
    {
        using var temporary = new TemporaryLabDirectory();
        string gpuz = temporary.WriteInput(
            "gpuz-voltage-reference.csv",
            FixtureBytes("gpuz-voltage-reference.csv"));
        string hwinfo = temporary.WriteInput(
            "hwinfo-voltage-reference.csv",
            FixtureBytes("hwinfo-voltage-reference.csv"));
        string observation = temporary.WriteInput(
            "voltage-observation-v2.json",
            FixtureBytes("nvapi-voltage-status-v1-observation-v2.json"));

        Check(
            Throws<VoltageStatusCorrelationV2Exception>(() =>
                VoltageStatusCorrelationV2.AnalyzeFiles(observation, gpuz)),
            "an observation that records HWiNFO must require its exact prefix path");

        JsonObject gpuzOnlyRoot = VoltageObservationFixture();
        gpuzOnlyRoot["references"]!.AsObject()["hwinfo"] = null;
        string gpuzOnly = temporary.WriteInput(
            "gpuz-only.json",
            Encoding.UTF8.GetBytes(gpuzOnlyRoot.ToJsonString()));
        Check(
            Throws<VoltageStatusCorrelationV2Exception>(() =>
                VoltageStatusCorrelationV2.AnalyzeFiles(gpuzOnly, gpuz, hwinfo)),
            "GPU-Z-only observations must reject an unrecorded HWiNFO input");

        JsonObject incompleteWindowRoot = VoltageObservationFixture();
        incompleteWindowRoot["references"]!.AsObject()["hwinfo"] = null;
        JsonObject incompleteReference =
            incompleteWindowRoot["references"]!["gpuz"]!.AsObject();
        incompleteReference["last_sample_local_before"] = "2026-08-26 18:18:20";
        incompleteReference["last_sample_local_midpoint"] = "2026-08-26 18:18:32";
        incompleteReference["last_sample_local_after"] = "2026-08-26 18:18:40";
        string incompleteWindow = temporary.WriteInput(
            "incomplete-gpuz-window.json",
            Encoding.UTF8.GetBytes(incompleteWindowRoot.ToJsonString()));
        Check(
            Throws<VoltageStatusCorrelationV2Exception>(() =>
                VoltageStatusCorrelationV2.AnalyzeFiles(incompleteWindow, gpuz)),
            "GPU-Z points only inside the interval must not substitute for before/midpoint/after boundary coverage");

        JsonObject staleRoot = VoltageObservationFixture();
        staleRoot["references"]!["hwinfo"]!["grew_during_capture"] = false;
        string stale = temporary.WriteInput(
            "stale-hwinfo.json",
            Encoding.UTF8.GetBytes(staleRoot.ToJsonString()));
        Check(
            Throws<VoltageStatusCorrelationV2Exception>(() =>
                VoltageStatusCorrelationV2.AnalyzeFiles(stale, gpuz, hwinfo)),
            "HWiNFO evidence without strict recorded growth must fail closed");

        JsonObject hashRoot = VoltageObservationFixture();
        hashRoot["references"]!["gpuz"]!["prefix_sha256"] = new string('0', 64);
        string wrongHash = temporary.WriteInput(
            "wrong-prefix-hash.json",
            Encoding.UTF8.GetBytes(hashRoot.ToJsonString()));
        Check(
            Throws<VoltageStatusCorrelationV2Exception>(() =>
                VoltageStatusCorrelationV2.AnalyzeFiles(wrongHash, gpuz, hwinfo)),
            "a changed reference prefix hash must fail before correlation");

        byte[] duplicateHeader = Encoding.UTF8.GetBytes(
            "Date,Time,\"GPU Core Voltage [V]\",\"GPU Core Voltage [V]\"\n" +
            "26.8.2026,18:18:31.000,1.081,1.081\n");
        Check(
            Throws<HwinfoVoltageLogException>(() =>
                HwinfoVoltageSensorLog.Analyze(duplicateHeader, "duplicate.csv")),
            "HWiNFO must contain exactly one core-voltage channel");
    }

    private static void TestVoltageStatusCorrelationV2RejectsOversizedObservation()
    {
        using var temporary = new TemporaryLabDirectory();
        string oversizedObservation = temporary.CreateSparseInput(
            "oversized-voltage-observation-v2.json",
            16L * 1024 * 1024 + 1);
        string gpuz = temporary.WriteInput(
            "gpuz-voltage-reference.csv",
            FixtureBytes("gpuz-voltage-reference.csv"));

        Check(
            Throws<VoltageStatusCorrelationV2Exception>(() =>
                VoltageStatusCorrelationV2.AnalyzeFiles(oversizedObservation, gpuz)),
            "voltage v2 must reject an observation above 16 MiB before JSON parsing");

        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var error = new StringWriter(CultureInfo.InvariantCulture);
        int exitCode = LabCli.Run(
            [
                "correlate-nvapi-voltage-status-v2",
                "--observation",
                oversizedObservation,
                "--gpuz-log",
                gpuz,
            ],
            output,
            error);

        Check(exitCode == 1, "an oversized voltage observation must fail the CLI");
        Check(
            output.ToString().Length == 0,
            "an oversized voltage observation must not emit a partial report");
        using JsonDocument errorJson = JsonDocument.Parse(error.ToString());
        Check(
            errorJson.RootElement.GetProperty("error_code").GetString() ==
                "analysis_error" &&
            errorJson.RootElement.GetProperty("message").GetString()!
                .Contains("outside the analysis limit", StringComparison.Ordinal),
            "an oversized voltage observation must use the structured analysis_error contract");
    }

    private static void TestGpuzVoltagePrefixIsolatesInvalidValues()
    {
        using var temporary = new TemporaryLabDirectory();
        foreach (string invalid in new[] { "garbage", "NaN", "0", "0.099", "2.001", "1e308" })
        {
            byte[] content = Encoding.UTF8.GetBytes(
                "Date,GPU Voltage [V],Board Power Draw [W],\n" +
                "2026-08-26 18:18:31,1.0810,110.9,\n" +
                $"2026-08-26 18:18:32,{invalid},46.9,\n" +
                "2026-08-26 18:18:33,0.9370,72.0,\n");
            string path = temporary.WriteInput(
                $"invalid-gpuz-voltage-{invalid}.csv",
                content);
            GpuzVoltagePrefixAnalysis analysis =
                GpuzVoltageSessionLog.AnalyzeFilePrefix(path, content.LongLength);
            Check(
                analysis.Sessions.Single().Samples.Count == 2 &&
                analysis.Sessions.Single().InvalidSampleTimestamps.SequenceEqual(
                    [new DateTime(2026, 8, 26, 18, 18, 32)]),
                $"GPU-Z voltage token '{invalid}' must be retained as a timestamped invalid sample");
        }

        byte[] missingContent = Encoding.UTF8.GetBytes(
            "Date,GPU Voltage [V],Board Power Draw [W],\n" +
            "2026-08-26 18:18:31,1.0810,110.9,\n" +
            "2026-08-26 18:18:32,-,46.9,\n" +
            "2026-08-26 18:18:33,,72.0,\n" +
            "2026-08-26 18:18:34,0.9370,72.0,\n");
        string missingPath = temporary.WriteInput(
            "missing-gpuz-voltage.csv",
            missingContent);
        GpuzVoltagePrefixAnalysis missing = GpuzVoltageSessionLog.AnalyzeFilePrefix(
            missingPath,
            missingContent.LongLength);
        Check(
            missing.Sessions.Single().Samples.Count == 2 &&
            missing.Sessions.Single().InvalidSampleTimestamps.Count == 0,
            "only '-' and an empty exact-channel token may be treated as missing");

        byte[] gpuzFixture = FixtureBytes("gpuz-voltage-reference.csv");
        int headerEnd = Array.IndexOf(gpuzFixture, (byte)'\n') + 1;
        byte[] historicalInvalid = Encoding.UTF8.GetBytes(
            "2026-08-26 17:00:00,0.0000,32.0,\n");
        byte[] gpuzWithHistoricalInvalid = gpuzFixture[..headerEnd]
            .Concat(historicalInvalid)
            .Concat(gpuzFixture[headerEnd..])
            .ToArray();
        string historicalGpuzFileName = "gpuz-voltage-with-historical-invalid.csv";
        string historicalGpuz = temporary.WriteInput(
            historicalGpuzFileName,
            gpuzWithHistoricalInvalid);
        JsonObject historicalRoot = VoltageObservationFixture();
        historicalRoot["references"]!.AsObject()["hwinfo"] = null;
        JsonObject historicalReference =
            historicalRoot["references"]!["gpuz"]!.AsObject();
        historicalReference["file_name"] = historicalGpuzFileName;
        historicalReference["prefix_sha256"] = Sha256Hex(gpuzWithHistoricalInvalid);
        historicalReference["size_bytes_before"] =
            historicalReference["size_bytes_before"]!.GetValue<long>() +
            historicalInvalid.LongLength;
        historicalReference["size_bytes_midpoint"] =
            historicalReference["size_bytes_midpoint"]!.GetValue<long>() +
            historicalInvalid.LongLength;
        historicalReference["size_bytes_after"] = gpuzWithHistoricalInvalid.LongLength;
        string historicalObservation = temporary.WriteInput(
            "voltage-observation-with-historical-invalid.json",
            Encoding.UTF8.GetBytes(historicalRoot.ToJsonString()));
        VoltageStatusCorrelationReportV2 historicalReport =
            VoltageStatusCorrelationV2.AnalyzeFiles(
                historicalObservation,
                historicalGpuz);
        Check(
            historicalReport.MappingStatus == "matched_external_reference" &&
            historicalReport.GpuzReference.SelectedSessionIndex == 0,
            "an invalid historical GPU-Z sample outside the bounded capture window must be isolated");

        byte[] invalidHwinfo = Encoding.UTF8.GetBytes(
            "Date,Time,\"GPU Core Voltage [V]\",\"GPU Power [W]\"\n" +
            "26.8.2026,18:18:31.000,1e308,110.9\n");
        Check(
            Throws<HwinfoVoltageLogException>(() =>
                HwinfoVoltageSensorLog.Analyze(invalidHwinfo, "invalid-hwinfo.csv")),
            "HWiNFO must reject a finite value that can overflow derived metrics");

        string invalidGpuzText = Encoding.UTF8.GetString(gpuzFixture)
            .Replace("1.0810", "1e308 ", StringComparison.Ordinal);
        byte[] invalidGpuzFixture = Encoding.UTF8.GetBytes(invalidGpuzText);
        Check(
            invalidGpuzFixture.LongLength == gpuzFixture.LongLength,
            "the adversarial GPU-Z fixture must preserve the anchored prefix length");
        string gpuz = temporary.WriteInput(
            "gpuz-voltage-reference.csv",
            invalidGpuzFixture);
        string hwinfo = temporary.WriteInput(
            "hwinfo-voltage-reference.csv",
            FixtureBytes("hwinfo-voltage-reference.csv"));
        string observation = temporary.WriteInput(
            "voltage-observation-v2.json",
            FixtureBytes("nvapi-voltage-status-v1-observation-v2.json"));
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var error = new StringWriter(CultureInfo.InvariantCulture);
        int exitCode = LabCli.Run(
            [
                "correlate-nvapi-voltage-status-v2",
                "--observation",
                observation,
                "--gpuz-log",
                gpuz,
                "--hwinfo-log",
                hwinfo,
            ],
            output,
            error);
        Check(
            exitCode == 1 && output.ToString().Length == 0,
            "an extreme finite GPU-Z voltage must fail without a partial report");
        using JsonDocument errorJson = JsonDocument.Parse(error.ToString());
        Check(
            errorJson.RootElement.GetProperty("error_code").GetString() == "analysis_error",
            "an extreme finite GPU-Z voltage must use the structured analysis_error contract");
    }

    private static void TestOrderedResamplingAtContractLimits()
    {
        const int sourceCount = 250_000;
        const int selectedCount = 100_000;
        int[] voltage = VoltageStatusCorrelationV2.EvenlySpacedIndices(
            sourceCount,
            selectedCount);
        int[] thermal = ThermChannelCorrelationV2.EvenlySpacedIndices(
            sourceCount,
            selectedCount);

        Check(
            voltage.Length == selectedCount && voltage[0] == 0 &&
            voltage[^1] == sourceCount - 1 &&
            voltage.Zip(voltage.Skip(1)).All(pair => pair.First < pair.Second),
            "voltage resampling must not overflow or leave the bounded source range");
        Check(
            thermal.Length == selectedCount && thermal[0] == 0 &&
            thermal[^1] == sourceCount - 1 &&
            thermal.Zip(thermal.Skip(1)).All(pair => pair.First < pair.Second),
            "thermal resampling must not overflow or leave the bounded source range");
    }

    private static JsonObject VoltageObservationFixture() =>
        JsonNode.Parse(FixtureBytes("nvapi-voltage-status-v1-observation-v2.json"))!
            .AsObject();

    private static void TestNvapiClassification()
    {
        using var temporary = new TemporaryLabDirectory();
        string observation = temporary.WriteInput(
            "nvapi-observation.json",
            Encoding.UTF8.GetBytes(NvapiObservationJson(observationCount: 3)));
        string interfaceTable = temporary.WriteInput(
            "nvapi_interface.h",
            Encoding.UTF8.GetBytes(
                "struct NVAPI_INTERFACE_TABLE nvapi_interface_table[] = {\n" +
                "  { \"NvAPI_Initialize\", 0x0150e828 },\n" +
                "};\n"));

        NvapiInterfaceClassificationReport report =
            NvapiInterfaceClassification.AnalyzeFiles(observation, interfaceTable);
        Check(report.SchemaVersion == 1, "NVAPI classification must declare schema v1");
        Check(report.ObservationCount == 3 && report.ObservedUniqueInterfaceCount == 2,
            "NVAPI classification must preserve observed call and unique counts");
        Check(report.PublicCatalogMatchCount == 1 && report.NotInPublicCatalogCount == 1,
            "NVAPI classification must separate matched and unmatched IDs");
        NvapiInterfaceClassificationEntry publicEntry = report.Interfaces.Single(
            entry => entry.InterfaceId == "0x0150e828");
        Check(
            publicEntry.Classification == "public_catalog_match" &&
            publicEntry.PublicFunction == "NvAPI_Initialize" &&
            publicEntry.CallCount == 2,
            "a matched ID must retain its public function and call count");
        NvapiInterfaceClassificationEntry unknownEntry = report.Interfaces.Single(
            entry => entry.InterfaceId == "0xdeadbeef");
        Check(
            unknownEntry.Classification == "not_in_public_catalog" &&
            unknownEntry.PublicFunction is null,
            "an unmatched ID must remain unidentified");

        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var error = new StringWriter(CultureInfo.InvariantCulture);
        int exitCode = LabCli.Run(
            [
                "classify-nvapi-ids",
                "--input",
                observation,
                "--interface-table",
                interfaceTable,
            ],
            output,
            error);
        Check(exitCode == 0, "NVAPI classification CLI must succeed offline");
        Check(error.ToString().Length == 0, "NVAPI classification success must not write stderr");
        using JsonDocument document = JsonDocument.Parse(output.ToString());
        Check(
            document.RootElement.GetProperty("not_in_public_catalog_count").GetInt32() == 1,
            "NVAPI classification CLI must serialize unmatched IDs");
    }

    private static void TestNvapiClassificationRejectsCounts()
    {
        using var temporary = new TemporaryLabDirectory();
        string observation = temporary.WriteInput(
            "bad-observation.json",
            Encoding.UTF8.GetBytes(NvapiObservationJson(observationCount: 4)));
        string interfaceTable = temporary.WriteInput(
            "nvapi_interface.h",
            Encoding.UTF8.GetBytes("{ \"NvAPI_Initialize\", 0x0150e828 },\n"));

        Check(
            Throws<NvapiInterfaceClassificationException>(
                () => NvapiInterfaceClassification.AnalyzeFiles(observation, interfaceTable)),
            "inconsistent NVAPI observation counts must fail closed");
    }

    private static void TestNvapiCandidateInventory()
    {
        using var temporary = new TemporaryLabDirectory();
        string observation = temporary.WriteInput(
            "nvapi-observation.json",
            Encoding.UTF8.GetBytes(NvapiObservationJson(observationCount: 3)));
        string interfaceTable = temporary.WriteInput(
            "nvapi_interface.h",
            Encoding.UTF8.GetBytes("{ \"NvAPI_Initialize\", 0x0150e828 },\n"));
        NvapiInterfaceClassificationReport classification =
            NvapiInterfaceClassification.AnalyzeFiles(observation, interfaceTable);
        string classificationPath = temporary.WriteInput(
            "nvapi-classification.json",
            Encoding.UTF8.GetBytes(
                LabJson.SerializeNvapiInterfaceClassification(classification)));
        string callPath = temporary.WriteInput(
            "nvapi-calls.json",
            Encoding.UTF8.GetBytes(NvapiCallJson()));

        NvapiCandidateInventoryReport report =
            NvapiCandidateInventory.AnalyzeFiles(classificationPath, callPath);
        Check(
            report.CandidateCount == 2 && report.ExecutedCandidateCount == 2,
            "candidate inventory must retain both executed binary targets");
        Check(
            report.ExecutedPublicCatalogCount == 1 &&
            report.ExecutedNotInPublicCatalogCount == 1 &&
            report.ResolvedNotObservedCount == 0,
            "candidate inventory must separate public and unidentified execution");
        NvapiCandidateInventoryEntry unknown = report.Candidates.Single(
            entry => entry.InterfaceId == "0xdeadbeef");
        Check(
            unknown.PublicFunction is null &&
            unknown.SemanticStatus == "unidentified_binary_candidate" &&
            unknown.ModuleName == "nvapi_impl.dll" &&
            unknown.Rva == "0x00123456" &&
            unknown.ObservedCallCount == 19,
            "an unknown candidate must retain its binary origin and call count");

        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var error = new StringWriter(CultureInfo.InvariantCulture);
        int exitCode = LabCli.Run(
            [
                "inventory-nvapi-candidates",
                "--classification",
                classificationPath,
                "--calls",
                callPath,
            ],
            output,
            error);
        Check(exitCode == 0 && error.ToString().Length == 0,
            "candidate inventory CLI must succeed offline");
        using JsonDocument document = JsonDocument.Parse(output.ToString());
        Check(
            document.RootElement.GetProperty("executed_not_in_public_catalog_count")
                .GetInt32() == 1,
            "candidate inventory CLI must serialize unidentified execution counts");
    }

    private static void TestWindowsHandleIdentity()
    {
        using var waitHandle = new EventWaitHandle(
            initialState: false,
            EventResetMode.AutoReset);
        long rawHandle = waitHandle.SafeWaitHandle.DangerousGetHandle().ToInt64();
        string handle = $"0x{rawHandle:x}";

        WindowsHandleIdentityReport report = WindowsHandleIdentity.Resolve(
            Environment.ProcessId,
            handle);
        Check(report.SchemaVersion == 1 && report.SourceKind == "windows_handle_identity",
            "handle identity must declare its stable v1 source");
        Check(report.ProcessId == Environment.ProcessId && report.ObjectType == "Event",
            "handle identity must resolve the duplicated test event");
        Check(report.ObjectName is null && report.DosDeviceAlias is null,
            "an unnamed event must remain unnamed without a fabricated DOS alias");
        Check(report.ProcessImageSha256.Length == 64,
            "handle identity must anchor the source process image");
        Check(
            Throws<WindowsHandleIdentityException>(
                () => WindowsHandleIdentity.ParseHandle("368")),
            "handle parsing must require an explicit hexadecimal prefix");

        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var error = new StringWriter(CultureInfo.InvariantCulture);
        int exitCode = LabCli.Run(
            [
                "resolve-windows-handle",
                "--process-id",
                Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
                "--handle",
                handle,
            ],
            output,
            error);
        Check(exitCode == 0 && error.ToString().Length == 0,
            "handle identity CLI must resolve a valid local handle");
        using JsonDocument document = JsonDocument.Parse(output.ToString());
        Check(
            document.RootElement.GetProperty("object_type").GetString() == "Event",
            "handle identity CLI must serialize the operating-system object type");
    }

    private static string NvapiObservationJson(int observationCount) =>
        "{\n" +
        "  \"schema_version\": 1,\n" +
        "  \"source_kind\": \"nvapi_query_interface_observation\",\n" +
        "  \"captured_utc\": \"2026-08-25T22:41:09Z\",\n" +
        "  \"duration_seconds\": 15,\n" +
        $"  \"gpuz_sha256\": \"{new string('0', 64)}\",\n" +
        "  \"forced_target_process_ids\": [123],\n" +
        $"  \"observation_count\": {observationCount},\n" +
        "  \"unique_interface_count\": 2,\n" +
        "  \"interfaces\": [\n" +
        "    { \"interface_id\": \"0x0150e828\", \"call_count\": 2 },\n" +
        "    { \"interface_id\": \"0xdeadbeef\", \"call_count\": 1 }\n" +
        "  ],\n" +
        "  \"warning\": \"observation only\"\n" +
        "}\n";

    private static string ThermChannelObservationJson(
        long logPrefixSizeBytes,
        bool invalidLayout)
    {
        int[] channel0 = [8480, 8512, 8544, 8576];
        int[] channel1 = [11040, 11072, 11104, 11136];
        var samples = new StringBuilder();
        int sequence = 0;
        for (int index = 0; index < channel0.Length; index++)
        {
            AppendSample(channel: 0, channel0[index]);
            AppendSample(channel: 1, channel1[index]);
        }

        return "{\n" +
            "  \"schema_version\": 1,\n" +
            "  \"source_kind\": \"nvapi_therm_channel_v2_observation\",\n" +
            "  \"gpuz_sha256\": " +
            "\"6cb0ef29682452de81a9576808881685161411a1fad00938ba04131159979c29\",\n" +
            "  \"nvapi_module_sha256\": " +
            "\"fbc9aed43bfa5bda19b7f83a809a081a0ce454b6d6003dcabc565ecb3e6afdaf\",\n" +
            "  \"interface_id\": \"0x65fe3aad\",\n" +
            "  \"function_rva\": \"0x001ad310\",\n" +
            "  \"structure_version\": \"0x000200a8\",\n" +
            "  \"structure_size_bytes\": 168,\n" +
            "  \"fixed_point_fractional_bits\": 8,\n" +
            "  \"reference_log\": {\n" +
            $"    \"size_bytes_after\": {logPrefixSizeBytes},\n" +
            "    \"last_sample_local_before\": \"2026-08-25 22:37:39\",\n" +
            "    \"last_sample_local_after\": \"2026-08-25 22:37:44\"\n" +
            "  },\n" +
            $"  \"call_count\": {sequence},\n" +
            "  \"samples\": [\n" +
            samples + "\n" +
            "  ]\n" +
            "}\n";

        void AppendSample(int channel, int raw)
        {
            sequence++;
            int selectedWordIndex = 10 + channel;
            if (invalidLayout && sequence == 1)
            {
                selectedWordIndex++;
            }

            if (samples.Length > 0)
            {
                samples.Append(",\n");
            }

            samples.Append("    { \"sequence\": ");
            samples.Append(sequence.ToString(CultureInfo.InvariantCulture));
            samples.Append(", \"return_status\": \"0x00000000\", ");
            samples.Append("\"structure_version\": \"0x000200a8\", ");
            samples.Append("\"channel_index\": ");
            samples.Append(channel.ToString(CultureInfo.InvariantCulture));
            samples.Append(", \"selected_word_index\": ");
            samples.Append(selectedWordIndex.ToString(CultureInfo.InvariantCulture));
            samples.Append(", \"selected_raw_fixed_8\": ");
            samples.Append(raw.ToString(CultureInfo.InvariantCulture));
            samples.Append(", \"selected_celsius\": ");
            samples.Append((raw / 256.0).ToString("R", CultureInfo.InvariantCulture));
            samples.Append(" }");
        }
    }

    private static string NvapiCallJson() =>
        "{\n" +
        "  \"schema_version\": 1,\n" +
        "  \"source_kind\": \"nvapi_function_call_observation\",\n" +
        "  \"captured_utc\": \"2026-08-25T22:41:09Z\",\n" +
        "  \"duration_seconds\": 15,\n" +
        $"  \"gpuz_sha256\": \"{new string('0', 64)}\",\n" +
        $"  \"resolution_report_sha256\": \"{new string('1', 64)}\",\n" +
        "  \"target_count\": 2,\n" +
        "  \"observed_target_count\": 2,\n" +
        "  \"call_count\": 20,\n" +
        "  \"targets\": [\n" +
        "    { \"module_name\": \"nvapi.dll\", " +
        $"\"module_sha256\": \"{new string('2', 64)}\", " +
        "\"rva\": \"0x00001234\", \"interface_ids\": [\"0x0150e828\"], " +
        "\"call_count\": 1 },\n" +
        "    { \"module_name\": \"nvapi_impl.dll\", " +
        $"\"module_sha256\": \"{new string('3', 64)}\", " +
        "\"rva\": \"0x00123456\", \"interface_ids\": [\"0xdeadbeef\"], " +
        "\"call_count\": 19 }\n" +
        "  ],\n" +
        "  \"warning\": \"synthetic observation\"\n" +
        "}\n";

    private static string ManifestPath(string package) =>
        Path.Combine(package, LabPackage.ManifestFileName);

    private static string PayloadPath(string package) =>
        Path.Combine(
            package,
            LabPackage.ArtifactDirectoryName,
            LabPackage.ArtifactFileName);

    private static string ReadManifest(string package) =>
        File.ReadAllText(ManifestPath(package));

    private static string WriteManifest(string package, string content)
    {
        byte[] bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(content);
        string path = ManifestPath(package);
        File.WriteAllBytes(path, bytes);
        return Sha256Hex(bytes);
    }

    private static string Sha256Hex(ReadOnlySpan<byte> content) =>
        Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    private static byte[] SyntheticArtifact() =>
        Enumerable.Range(0, 4097).Select(index => (byte)((index * 31) & 0xff)).ToArray();

    private static byte[] FixtureBytes(string fileName) =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName));

    private static void Run(string name, Action test)
    {
        try
        {
            test();
        }
        catch (Exception error)
        {
            failures++;
            Console.Error.WriteLine($"FAILED: {name}: {error}");
        }
    }

    private static bool Throws<TException>(Action action) where TException : Exception
    {
        try
        {
            action();
            return false;
        }
        catch (TException)
        {
            return true;
        }
    }

    private static TException? CaptureException<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
            return null;
        }
        catch (TException error)
        {
            return error;
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void CreateHardLink(string newFileName, string existingFileName)
    {
        if (!CreateHardLinkW(newFileName, existingFileName, IntPtr.Zero))
        {
            throw new InvalidOperationException(
                $"Could not create test hardlink (Win32 {Marshal.GetLastPInvokeError()}).");
        }
    }

    private sealed class TemporaryLabDirectory : IDisposable
    {
        private readonly string directory;
        private readonly HashSet<string> files = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> packages = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> directories = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> links = new(StringComparer.OrdinalIgnoreCase);

        internal TemporaryLabDirectory()
        {
            directory = Path.Combine(
                Path.GetTempPath(),
                $"rtx-monitor-lab-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
        }

        internal string RootPath => directory;

        internal string WriteInput(string fileName, byte[] content) =>
            WriteFile(Path.Combine(directory, fileName), content);

        internal string WriteFile(string path, byte[] content)
        {
            File.WriteAllBytes(path, content);
            return TrackFile(path);
        }

        internal string TrackFile(string path)
        {
            files.Add(Path.GetFullPath(path));
            return path;
        }

        internal string CreateSparseInput(string fileName, long length)
        {
            string path = Path.Combine(directory, fileName);
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
            {
                stream.SetLength(length);
            }

            return TrackFile(path);
        }

        internal string PackagePath(string name) =>
            TrackPackage(Path.Combine(directory, name));

        internal string TrackPackage(string path)
        {
            packages.Add(Path.GetFullPath(path));
            return path;
        }

        internal string CreateDirectory(string name)
        {
            string path = Path.Combine(directory, name);
            Directory.CreateDirectory(path);
            directories.Add(Path.GetFullPath(path));
            return path;
        }

        internal string CreateDirectoryLink(string name, string target)
        {
            string path = Path.Combine(directory, name);
            var startInfo = new ProcessStartInfo("cmd.exe")
            {
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            startInfo.ArgumentList.Add("/d");
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add("mklink");
            startInfo.ArgumentList.Add("/J");
            startInfo.ArgumentList.Add(path);
            startInfo.ArgumentList.Add(target);
            using Process process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Could not start mklink for the test junction.");
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"Could not create test junction: {process.StandardError.ReadToEnd()}");
            }

            links.Add(Path.GetFullPath(path));
            return path;
        }

        public void Dispose()
        {
            string resolvedDirectory = Path.GetFullPath(directory);
            string resolvedTemp = EnsureTrailingSeparator(Path.GetFullPath(Path.GetTempPath()));
            if (!resolvedDirectory.StartsWith(resolvedTemp, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(resolvedDirectory).StartsWith(
                    "rtx-monitor-lab-tests-",
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Refusing to delete unexpected test directory: {resolvedDirectory}");
            }

            foreach (string link in links.OrderByDescending(path => path.Length))
            {
                DeleteExactLink(link);
            }

            foreach (string file in files.OrderByDescending(path => path.Length))
            {
                DeleteExactFile(file);
            }

            foreach (string package in packages.OrderByDescending(path => path.Length))
            {
                DeleteExactPackage(package);
            }

            foreach (string childDirectory in directories.OrderByDescending(path => path.Length))
            {
                DeleteEmptyDirectory(childDirectory);
            }

            DeleteEmptyDirectory(resolvedDirectory);
        }

        private static void DeleteExactPackage(string package)
        {
            if (!Directory.Exists(package))
            {
                return;
            }

            string artifactDirectory = Path.Combine(
                package,
                LabPackage.ArtifactDirectoryName);
            DeleteExactFile(Path.Combine(artifactDirectory, LabPackage.ArtifactFileName));
            DeleteEmptyDirectory(artifactDirectory);
            DeleteExactFile(Path.Combine(package, LabPackage.ManifestFileName));
            DeleteEmptyDirectory(package);
        }

        private static void DeleteExactFile(string path)
        {
            if (!File.Exists(path))
            {
                return;
            }

            FileAttributes attributes = File.GetAttributes(path);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            {
                throw new InvalidOperationException($"Unexpected test file entry: {path}");
            }

            File.Delete(path);
        }

        private static void DeleteExactLink(string path)
        {
            FileAttributes attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) == 0)
            {
                throw new InvalidOperationException($"Expected a test reparse point: {path}");
            }

            if ((attributes & FileAttributes.Directory) != 0)
            {
                Directory.Delete(path);
            }
            else
            {
                File.Delete(path);
            }
        }

        private static void DeleteEmptyDirectory(string path)
        {
            if (!Directory.Exists(path))
            {
                return;
            }

            if (Directory.EnumerateFileSystemEntries(path).Any())
            {
                throw new InvalidOperationException(
                    $"Refusing to delete non-empty test directory: {path}");
            }

            Directory.Delete(path);
        }

        private static string EnsureTrailingSeparator(string path) =>
            path.EndsWith(Path.DirectorySeparatorChar)
                ? path
                : path + Path.DirectorySeparatorChar;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(
        string fileName,
        string existingFileName,
        IntPtr securityAttributes);
}
