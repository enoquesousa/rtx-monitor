using System.Globalization;
using System.Security.Cryptography;

namespace RtxMonitor.Lab;

public static class LabCli
{
    public static int Run(
        IReadOnlyList<string> args,
        TextWriter standardOutput,
        TextWriter standardError)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(standardOutput);
        ArgumentNullException.ThrowIfNull(standardError);

        string? operation = args.Count > 0 ? args[0] : null;
        try
        {
            if (args.Count == 0)
            {
                standardOutput.WriteLine(Usage);
                return 0;
            }

            if (operation is "--help" or "-h" or "help")
            {
                if (args.Count != 1)
                {
                    throw new LabCliException("The help operation does not accept arguments.");
                }

                standardOutput.WriteLine(Usage);
                return 0;
            }

            return operation switch
            {
                "create" => RunCreate(args, standardOutput),
                "verify" => RunVerify(args, standardOutput),
                "analyze-gpuz-log" => RunAnalyzeGpuzLog(args, standardOutput),
                "correlate-gpuz-log" => RunCorrelateGpuzLog(args, standardOutput),
                "correlate-nvapi-therm-channel" =>
                    RunCorrelateNvapiThermChannel(args, standardOutput),
                "correlate-nvapi-voltage-status" =>
                    RunCorrelateNvapiVoltageStatus(args, standardOutput),
                "classify-nvapi-ids" => RunClassifyNvapiIds(args, standardOutput),
                "inventory-nvapi-candidates" => RunInventoryNvapiCandidates(
                    args,
                    standardOutput),
                "resolve-windows-handle" => RunResolveWindowsHandle(
                    args,
                    standardOutput),
                "mark" => RunMark(args, standardOutput),
                _ => throw new LabCliException(
                    $"Unknown operation '{operation}'. Expected create, verify, " +
                    "analyze-gpuz-log, correlate-gpuz-log, " +
                    "correlate-nvapi-therm-channel, classify-nvapi-ids, " +
                    "inventory-nvapi-candidates, resolve-windows-handle, or mark."),
            };
        }
        catch (LabCliException error)
        {
            standardError.WriteLine(
                LabJson.SerializeError(operation, "invalid_arguments", error.Message));
            return 2;
        }
        catch (LabPackageException error)
        {
            standardError.WriteLine(
                LabJson.SerializeError(operation, "package_error", error.Message));
            return 1;
        }
        catch (GpuzLogException error)
        {
            standardError.WriteLine(
                LabJson.SerializeError(operation, "analysis_error", error.Message));
            return 1;
        }
        catch (GpuzCorrelationException error)
        {
            standardError.WriteLine(
                LabJson.SerializeError(operation, "analysis_error", error.Message));
            return 1;
        }
        catch (ThermChannelCorrelationException error)
        {
            standardError.WriteLine(
                LabJson.SerializeError(operation, "analysis_error", error.Message));
            return 1;
        }
        catch (VoltageStatusCorrelationException error)
        {
            standardError.WriteLine(
                LabJson.SerializeError(operation, "analysis_error", error.Message));
            return 1;
        }
        catch (NvapiInterfaceClassificationException error)
        {
            standardError.WriteLine(
                LabJson.SerializeError(operation, "analysis_error", error.Message));
            return 1;
        }
        catch (NvapiCandidateInventoryException error)
        {
            standardError.WriteLine(
                LabJson.SerializeError(operation, "analysis_error", error.Message));
            return 1;
        }
        catch (WindowsHandleIdentityException error)
        {
            standardError.WriteLine(
                LabJson.SerializeError(operation, "analysis_error", error.Message));
            return 1;
        }
        catch (PlatformNotSupportedException error)
        {
            standardError.WriteLine(
                LabJson.SerializeError(operation, "unsupported_platform", error.Message));
            return 1;
        }
        catch (Exception error) when (
            error is IOException or UnauthorizedAccessException or CryptographicException)
        {
            standardError.WriteLine(
                LabJson.SerializeError(operation, "io_error", error.Message));
            return 1;
        }
    }

    public const string Usage =
        "Usage:\n" +
        "  rtxmon-lab create --input FILE --output DIRECTORY " +
        "[--gpu VALUE] [--driver-version VALUE] [--vbios-version VALUE]\n" +
        "  rtxmon-lab verify --package DIRECTORY " +
        "--expected-manifest-sha256 HASH\n" +
        "  rtxmon-lab analyze-gpuz-log --input FILE\n" +
        "  rtxmon-lab correlate-gpuz-log --input FILE --reference CHANNEL [--session INDEX]\n" +
        "  rtxmon-lab correlate-nvapi-therm-channel --observation REPORT " +
        "--gpuz-log FILE\n" +
        "  rtxmon-lab correlate-nvapi-voltage-status --observation REPORT " +
        "--gpuz-log FILE\n" +
        "  rtxmon-lab classify-nvapi-ids --input REPORT --interface-table HEADER\n" +
        "  rtxmon-lab inventory-nvapi-candidates --classification REPORT --calls REPORT\n" +
        "  rtxmon-lab resolve-windows-handle --process-id PID --handle 0xVALUE\n" +
        "  rtxmon-lab mark --scenario ID --phase begin|end|note [--note VALUE]";

    private static int RunCreate(
        IReadOnlyList<string> args,
        TextWriter standardOutput)
    {
        Dictionary<string, string> options = ParseOptions(args, startIndex: 1);
        RequireOnly(
            options,
            "--input",
            "--output",
            "--gpu",
            "--driver-version",
            "--vbios-version");
        string input = RequireOption(options, "--input");
        string output = RequireOption(options, "--output");

        var device = new LabDeviceMetadata(
            OptionalOption(options, "--gpu"),
            OptionalOption(options, "--driver-version"),
            OptionalOption(options, "--vbios-version"));
        LabPackageResult result = LabPackage.Create(input, output, device);
        standardOutput.WriteLine(LabJson.SerializeResult("create", "created", result));
        return 0;
    }

    private static int RunVerify(
        IReadOnlyList<string> args,
        TextWriter standardOutput)
    {
        Dictionary<string, string> options = ParseOptions(args, startIndex: 1);
        RequireOnly(options, "--package", "--expected-manifest-sha256");
        string package = RequireOption(options, "--package");
        string expectedManifestSha256 = RequireOption(
            options,
            "--expected-manifest-sha256");
        ValidateSha256Argument(expectedManifestSha256);
        LabPackageResult result = LabPackage.Verify(package, expectedManifestSha256);
        standardOutput.WriteLine(LabJson.SerializeResult("verify", "verified", result));
        return 0;
    }

    private static int RunAnalyzeGpuzLog(
        IReadOnlyList<string> args,
        TextWriter standardOutput)
    {
        Dictionary<string, string> options = ParseOptions(args, startIndex: 1);
        RequireOnly(options, "--input");
        string input = RequireOption(options, "--input");
        GpuzLogAnalysis analysis = GpuzSensorLog.AnalyzeFile(input);
        standardOutput.WriteLine(LabJson.SerializeGpuzLogAnalysis(analysis));
        return 0;
    }

    private static int RunCorrelateGpuzLog(
        IReadOnlyList<string> args,
        TextWriter standardOutput)
    {
        Dictionary<string, string> options = ParseOptions(args, startIndex: 1);
        RequireOnly(options, "--input", "--reference", "--session");
        string input = RequireOption(options, "--input");
        string reference = RequireOption(options, "--reference");
        int? sessionIndex = null;
        if (OptionalOption(options, "--session") is string sessionRaw)
        {
            if (!int.TryParse(sessionRaw, out int parsedSession) || parsedSession < 0)
            {
                throw new LabCliException("Option '--session' must be a non-negative integer.");
            }

            sessionIndex = parsedSession;
        }

        GpuzCorrelationReport report = GpuzCorrelation.AnalyzeFile(
            input,
            reference,
            sessionIndex);
        standardOutput.WriteLine(LabJson.SerializeGpuzCorrelation(report));
        return 0;
    }

    private static int RunCorrelateNvapiThermChannel(
        IReadOnlyList<string> args,
        TextWriter standardOutput)
    {
        Dictionary<string, string> options = ParseOptions(args, startIndex: 1);
        RequireOnly(options, "--observation", "--gpuz-log");
        ThermChannelCorrelationReport report = ThermChannelCorrelation.AnalyzeFiles(
            RequireOption(options, "--observation"),
            RequireOption(options, "--gpuz-log"));
        standardOutput.WriteLine(LabJson.SerializeThermChannelCorrelation(report));
        return 0;
    }

    private static int RunCorrelateNvapiVoltageStatus(
        IReadOnlyList<string> args,
        TextWriter standardOutput)
    {
        Dictionary<string, string> options = ParseOptions(args, startIndex: 1);
        RequireOnly(options, "--observation", "--gpuz-log");
        VoltageStatusCorrelationReport report = VoltageStatusCorrelation.AnalyzeFiles(
            RequireOption(options, "--observation"), RequireOption(options, "--gpuz-log"));
        standardOutput.WriteLine(LabJson.SerializeVoltageStatusCorrelation(report));
        return 0;
    }

    private static int RunMark(
        IReadOnlyList<string> args,
        TextWriter standardOutput)
    {
        Dictionary<string, string> options = ParseOptions(args, startIndex: 1);
        RequireOnly(options, "--scenario", "--phase", "--note");
        string scenario = RequireOption(options, "--scenario");
        string phase = RequireOption(options, "--phase");
        string? note = OptionalOption(options, "--note");
        ValidateScenarioId(scenario);
        if (phase is not ("begin" or "end" or "note"))
        {
            throw new LabCliException("Option '--phase' must be begin, end, or note.");
        }

        if (note is not null)
        {
            ValidateTextOption(note, "--note", 4096);
        }

        ExperimentMarker marker = ExperimentMarkers.Create(scenario, phase, note);
        standardOutput.WriteLine(LabJson.SerializeExperimentMarker(marker));
        return 0;
    }

    private static int RunClassifyNvapiIds(
        IReadOnlyList<string> args,
        TextWriter standardOutput)
    {
        Dictionary<string, string> options = ParseOptions(args, startIndex: 1);
        RequireOnly(options, "--input", "--interface-table");
        string input = RequireOption(options, "--input");
        string interfaceTable = RequireOption(options, "--interface-table");
        NvapiInterfaceClassificationReport report =
            NvapiInterfaceClassification.AnalyzeFiles(input, interfaceTable);
        standardOutput.WriteLine(LabJson.SerializeNvapiInterfaceClassification(report));
        return 0;
    }

    private static int RunInventoryNvapiCandidates(
        IReadOnlyList<string> args,
        TextWriter standardOutput)
    {
        Dictionary<string, string> options = ParseOptions(args, startIndex: 1);
        RequireOnly(options, "--classification", "--calls");
        NvapiCandidateInventoryReport report = NvapiCandidateInventory.AnalyzeFiles(
            RequireOption(options, "--classification"),
            RequireOption(options, "--calls"));
        standardOutput.WriteLine(LabJson.SerializeNvapiCandidateInventory(report));
        return 0;
    }

    private static int RunResolveWindowsHandle(
        IReadOnlyList<string> args,
        TextWriter standardOutput)
    {
        Dictionary<string, string> options = ParseOptions(args, startIndex: 1);
        RequireOnly(options, "--process-id", "--handle");
        string processIdRaw = RequireOption(options, "--process-id");
        if (!int.TryParse(
                processIdRaw,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int processId) ||
            processId <= 0)
        {
            throw new LabCliException(
                "Option '--process-id' must be a positive decimal integer.");
        }

        WindowsHandleIdentityReport report = WindowsHandleIdentity.Resolve(
            processId,
            RequireOption(options, "--handle"));
        standardOutput.WriteLine(LabJson.SerializeWindowsHandleIdentity(report));
        return 0;
    }

    private static Dictionary<string, string> ParseOptions(
        IReadOnlyList<string> args,
        int startIndex)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int index = startIndex; index < args.Count; index += 2)
        {
            string name = args[index];
            if (!name.StartsWith("--", StringComparison.Ordinal))
            {
                throw new LabCliException($"Unexpected argument '{name}'.");
            }

            if (index + 1 >= args.Count)
            {
                throw new LabCliException($"Option '{name}' requires a value.");
            }

            string value = args[index + 1];
            if (value.StartsWith("--", StringComparison.Ordinal))
            {
                throw new LabCliException($"Option '{name}' requires a value.");
            }

            if (!options.TryAdd(name, value))
            {
                throw new LabCliException($"Option '{name}' was provided more than once.");
            }
        }

        return options;
    }

    private static void RequireOnly(
        IReadOnlyDictionary<string, string> options,
        params string[] acceptedNames)
    {
        var accepted = new HashSet<string>(acceptedNames, StringComparer.Ordinal);
        string? unsupported = options.Keys.FirstOrDefault(name => !accepted.Contains(name));
        if (unsupported is not null)
        {
            throw new LabCliException($"Unsupported option '{unsupported}'.");
        }
    }

    private static string RequireOption(
        IReadOnlyDictionary<string, string> options,
        string name)
    {
        if (!options.TryGetValue(name, out string? value) || string.IsNullOrWhiteSpace(value))
        {
            throw new LabCliException($"Option '{name}' is required.");
        }

        return value;
    }

    private static string? OptionalOption(
        IReadOnlyDictionary<string, string> options,
        string name) =>
        options.TryGetValue(name, out string? value) ? value : null;

    private static void ValidateSha256Argument(string value)
    {
        if (value.Length != 64 ||
            value.Any(character =>
                character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            throw new LabCliException(
                "Option '--expected-manifest-sha256' must contain exactly " +
                "64 lowercase hexadecimal characters.");
        }
    }

    private static void ValidateScenarioId(string value)
    {
        if (value.Length > 128 ||
            value[0] is not (>= 'a' and <= 'z') and not (>= '0' and <= '9') ||
            value.Any(character =>
                character is not (>= 'a' and <= 'z') and
                not (>= '0' and <= '9') and
                not '.' and not '_' and not '-'))
        {
            throw new LabCliException(
                "Option '--scenario' must match [a-z0-9][a-z0-9._-]{0,127}.");
        }
    }

    private static void ValidateTextOption(string value, string name, int maximumLength)
    {
        if (value.Length == 0 || value.Length > maximumLength ||
            value.Any(character => char.IsControl(character)))
        {
            throw new LabCliException(
                $"Option '{name}' must contain 1 to {maximumLength} non-control characters.");
        }
    }

    private sealed class LabCliException : Exception
    {
        internal LabCliException(string message)
            : base(message)
        {
        }
    }
}
