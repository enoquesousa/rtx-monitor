using RtxMonitor.Storage;

namespace RtxMonitor.Service;

public sealed class TelemetryStoreProvider : IHistorySource
{
    private SqliteTelemetryStore? current;

    public void SetAvailable(SqliteTelemetryStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        Volatile.Write(ref current, store);
    }

    public void Clear(SqliteTelemetryStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        Interlocked.CompareExchange(ref current, null, store);
    }

    public void Clear() => Volatile.Write(ref current, null);

    public IReadOnlyList<StoredTelemetryEvidence> Query(TelemetryEventQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        SqliteTelemetryStore store = Volatile.Read(ref current) ??
            throw new ServiceDependencyUnavailableException(
                "O armazenamento de telemetria não está disponível.");
        try
        {
            return store.QueryEvents(query);
        }
        catch (TelemetryStoreException error)
        {
            throw new ServiceDependencyUnavailableException(
                "Não foi possível consultar o armazenamento de telemetria.",
                error);
        }
    }
}
