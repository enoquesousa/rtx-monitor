namespace RtxMonitor.Lab;

public sealed record CoolerStatusAnalysisReport(
    int SchemaVersion,
    string SourceKind,
    string MappingStatus,
    string EvidenceStage,
    string TemporalAlignment,
    CoolerStatusAnalysisSource Source,
    CoolerStatusAnalysisSession Session,
    int SampleCount,
    IReadOnlyList<CoolerStatusCallSiteAnalysis> CallSites,
    IReadOnlyList<CoolerStatusFieldAnalysis> Fields,
    IReadOnlyList<string> Warnings,
    CoolerGpuzReferenceContext? GpuzReference = null);

public sealed record CoolerStatusAnalysisSource(
    string OriginalFileName,
    long SizeBytes,
    string Sha256,
    int ObservationSchemaVersion,
    string ObservationSourceKind,
    string ProfileName,
    int ProcessId,
    string GpuzSha256,
    string DebuggerSha256,
    string DebuggerFileVersion,
    string IdentityProbeSha256,
    string CandidateInventorySha256,
    string PriorObservationSha256,
    string NvapiModuleSha256,
    string InterfaceId,
    string FunctionRva,
    string CallerModuleName,
    string StructureVersion,
    CoolerStatusGpuProfile GpuProfile,
    CoolerStatusLoadedModule LoadedNvapiModule);

public sealed record CoolerStatusGpuProfile(
    int GpuIndex,
    string GpuName,
    string GpuUuid,
    string DriverVersion,
    string NvmlVersion,
    string PciBusId,
    string PciVendorId,
    string PciDeviceId,
    string PciSubsystemVendorId,
    string PciSubsystemDeviceId,
    string VbiosVersion);

public sealed record CoolerStatusLoadedModule(
    string FileName,
    string FileSha256,
    string StartAddress,
    string EndAddress,
    string ProofSource);

public sealed record CoolerStatusAnalysisSession(
    string CaptureStartedUtc,
    string CapturedUtc,
    int DurationSeconds,
    CoolerStatusReferenceLog ReferenceLog);

public sealed record CoolerStatusReferenceLog(
    long SizeBytesBefore,
    long SizeBytesMidpoint,
    long SizeBytesAfter,
    string LastWriteUtcBefore,
    string LastWriteUtcMidpoint,
    string LastWriteUtcAfter,
    string LastSampleLocalBefore,
    string LastSampleLocalMidpoint,
    string LastSampleLocalAfter,
    bool GrewDuringCapture);

public sealed record CoolerStatusCallSiteAnalysis(
    string CallerRva,
    int SampleCount,
    int MinimumObservedEntryCount,
    int MaximumObservedEntryCount,
    int FirstSequence,
    int LastSequence);

/// <summary>Unsigned buffer words grouped by position, without a sensor name or unit.</summary>
public sealed record CoolerStatusFieldAnalysis(
    string CallerRva,
    int EntryOrdinal,
    int BaseWordIndex,
    int RawFieldByteOffsetWithinEntry,
    int RawFieldAbsoluteByteOffset,
    int ValidSampleCount,
    int FirstSequence,
    int LastSequence,
    uint MinimumRawValue,
    uint MaximumRawValue,
    uint FirstRawValue,
    uint LastRawValue,
    int DistinctValueCount,
    int TransitionCount,
    IReadOnlyList<uint> RawIdentifierWords);

public sealed class CoolerStatusAnalysisException : Exception
{
    public CoolerStatusAnalysisException(string message) : base(message) { }

    public CoolerStatusAnalysisException(string message, Exception innerException)
        : base(message, innerException) { }
}
