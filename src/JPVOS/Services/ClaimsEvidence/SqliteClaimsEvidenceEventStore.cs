using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;

namespace JPVOS.Services.ClaimsEvidence;

public sealed class SqliteClaimsEvidenceEventStore : IClaimsEvidenceEventStore
{
    private readonly string _connectionString;
    private readonly IDataProtector _protector;

    public SqliteClaimsEvidenceEventStore(string databasePath, IDataProtectionProvider dataProtectionProvider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentNullException.ThrowIfNull(dataProtectionProvider);
        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadWriteCreate }.ToString();
        _protector = dataProtectionProvider.CreateProtector("JPVOS.ClaimsEvidence.Idempotency.v1");
        Initialize();
    }

    private void Initialize()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA foreign_keys=ON;
            CREATE TABLE IF NOT EXISTS claims_case_events (
              event_id TEXT PRIMARY KEY,
              case_id TEXT NOT NULL,
              sequence INTEGER NOT NULL,
              occurred_at_utc TEXT NOT NULL,
              event_type TEXT NOT NULL,
              actor_class TEXT NOT NULL,
              idempotency_key TEXT NULL,
              sensitivity TEXT NOT NULL,
              payload_json TEXT NOT NULL,
              UNIQUE(case_id, sequence)
            );
            CREATE TABLE IF NOT EXISTS claims_case_access (
              case_id TEXT PRIMARY KEY,
              tracking_verifier TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS claims_idempotency (
              operation_scope TEXT NOT NULL,
              idempotency_key TEXT NOT NULL,
              request_hash TEXT NOT NULL,
              result_json TEXT NOT NULL,
              PRIMARY KEY(operation_scope, idempotency_key)
            );
            """;
        command.ExecuteNonQuery();
    }

    public async Task<IdempotentOperationResult> CreateCaseAtomicallyAsync(
        string operationScope, string idempotencyKey, string requestHash, string caseId, string trackingVerifier,
        IReadOnlyList<ClaimsEvidenceEvent> events, string resultJson, CancellationToken cancellationToken)
    {
        if (events.Count == 0) throw new ArgumentException("At least one event is required.", nameof(events));
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(SqliteTransactionMode.Immediate);
        try
        {
            var replay = await TryGetIdempotentResultAsync(connection, transaction, operationScope, idempotencyKey, requestHash, cancellationToken);
            if (replay is not null) { transaction.Commit(); return replay; }

            await using (var access = connection.CreateCommand())
            {
                access.Transaction = transaction;
                access.CommandText = "INSERT INTO claims_case_access(case_id,tracking_verifier) VALUES($case,$verifier)";
                access.Parameters.AddWithValue("$case", caseId);
                access.Parameters.AddWithValue("$verifier", trackingVerifier);
                await access.ExecuteNonQueryAsync(cancellationToken);
            }

            long sequence = 0;
            foreach (var item in events)
                await InsertEventAsync(connection, transaction, item with { CaseId = caseId, Sequence = ++sequence }, cancellationToken);

            await StoreIdempotentResultAsync(connection, transaction, operationScope, idempotencyKey, requestHash, resultJson, cancellationToken);
            transaction.Commit();
            return new IdempotentOperationResult(false, resultJson);
        }
        catch (ClaimsEvidenceIdempotencyConflictException) { transaction.Rollback(); throw; }
        catch (Exception ex)
        {
            transaction.Rollback();
            throw new ClaimsEvidencePersistenceException("Failed to persist initial case event stream.", ex);
        }
    }

    public async Task<IdempotentOperationResult> AppendAtomicallyAsync(
        string operationScope, string idempotencyKey, string requestHash, ClaimsEvidenceEvent @event,
        string resultJson, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(SqliteTransactionMode.Immediate);
        try
        {
            var replay = await TryGetIdempotentResultAsync(connection, transaction, operationScope, idempotencyKey, requestHash, cancellationToken);
            if (replay is not null) { transaction.Commit(); return replay; }

            await using var sequenceCommand = connection.CreateCommand();
            sequenceCommand.Transaction = transaction;
            sequenceCommand.CommandText = "SELECT COALESCE(MAX(sequence),0)+1 FROM claims_case_events WHERE case_id=$case";
            sequenceCommand.Parameters.AddWithValue("$case", @event.CaseId);
            var sequence = Convert.ToInt64(await sequenceCommand.ExecuteScalarAsync(cancellationToken));
            if (sequence == 1) throw new ClaimsEvidenceValidationException("Case does not exist.");

            await InsertEventAsync(connection, transaction, @event with { Sequence = sequence }, cancellationToken);
            await StoreIdempotentResultAsync(connection, transaction, operationScope, idempotencyKey, requestHash, resultJson, cancellationToken);
            transaction.Commit();
            return new IdempotentOperationResult(false, resultJson);
        }
        catch (ClaimsEvidenceIdempotencyConflictException) { transaction.Rollback(); throw; }
        catch (ClaimsEvidenceValidationException) { transaction.Rollback(); throw; }
        catch (Exception ex)
        {
            transaction.Rollback();
            throw new ClaimsEvidencePersistenceException("Failed to append case event.", ex);
        }
    }

    public async Task<IReadOnlyList<ClaimsEvidenceEvent>> ReadStreamAsync(string caseId, CancellationToken cancellationToken)
    {
        var result = new List<ClaimsEvidenceEvent>();
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT event_id,case_id,sequence,occurred_at_utc,event_type,actor_class,idempotency_key,sensitivity,payload_json FROM claims_case_events WHERE case_id=$case ORDER BY sequence";
        command.Parameters.AddWithValue("$case", caseId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new ClaimsEvidenceEvent(
                reader.GetString(0), reader.GetString(1), reader.GetInt64(2), DateTime.Parse(reader.GetString(3), null, System.Globalization.DateTimeStyles.RoundtripKind),
                Enum.Parse<ClaimsEvidenceEventType>(reader.GetString(4), true), reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6), reader.GetString(7), reader.GetString(8)));
        return result;
    }

    public async Task<string?> GetTrackingVerifierAsync(string caseId, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT tracking_verifier FROM claims_case_access WHERE case_id=$case";
        command.Parameters.AddWithValue("$case", caseId);
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    private async Task<IdempotentOperationResult?> TryGetIdempotentResultAsync(SqliteConnection connection, SqliteTransaction transaction, string operationScope, string idempotencyKey, string requestHash, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT request_hash,result_json FROM claims_idempotency WHERE operation_scope=$scope AND idempotency_key=$key";
        command.Parameters.AddWithValue("$scope", operationScope);
        command.Parameters.AddWithValue("$key", idempotencyKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        var storedHash = reader.GetString(0);
        if (!string.Equals(storedHash, requestHash, StringComparison.Ordinal))
            throw new ClaimsEvidenceIdempotencyConflictException("Idempotency key was already used with different request content.");
        return new IdempotentOperationResult(true, _protector.Unprotect(reader.GetString(1)));
    }

    private async Task StoreIdempotentResultAsync(SqliteConnection connection, SqliteTransaction transaction, string operationScope, string idempotencyKey, string requestHash, string resultJson, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO claims_idempotency(operation_scope,idempotency_key,request_hash,result_json) VALUES($scope,$key,$hash,$result)";
        command.Parameters.AddWithValue("$scope", operationScope);
        command.Parameters.AddWithValue("$key", idempotencyKey);
        command.Parameters.AddWithValue("$hash", requestHash);
        command.Parameters.AddWithValue("$result", _protector.Protect(resultJson));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertEventAsync(SqliteConnection connection, SqliteTransaction transaction, ClaimsEvidenceEvent @event, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO claims_case_events(event_id,case_id,sequence,occurred_at_utc,event_type,actor_class,idempotency_key,sensitivity,payload_json) VALUES($id,$case,$sequence,$occurred,$type,$actor,$key,$sensitivity,$payload)";
        command.Parameters.AddWithValue("$id", @event.EventId);
        command.Parameters.AddWithValue("$case", @event.CaseId);
        command.Parameters.AddWithValue("$sequence", @event.Sequence);
        command.Parameters.AddWithValue("$occurred", @event.OccurredAtUtc.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$type", @event.Type.ToString());
        command.Parameters.AddWithValue("$actor", @event.ActorClass);
        command.Parameters.AddWithValue("$key", (object?)@event.IdempotencyKey ?? DBNull.Value);
        command.Parameters.AddWithValue("$sensitivity", @event.Sensitivity);
        command.Parameters.AddWithValue("$payload", @event.PayloadJson);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
