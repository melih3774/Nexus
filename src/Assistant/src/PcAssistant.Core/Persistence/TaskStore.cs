using Microsoft.Data.Sqlite;
using PcAssistant.Core.Model;

namespace PcAssistant.Core.Persistence;

/// <summary>
/// Local SQLite store for tasks, their conversation, and their normalized events.
/// No Claude credentials are ever written here — only the session id Claude Code itself
/// uses for --resume.
/// </summary>
public sealed class TaskStore : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly object _gate = new();

    public TaskStore(string databaseFile)
    {
        var dir = Path.GetDirectoryName(databaseFile);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        _connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databaseFile,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
        }.ToString());

        _connection.Open();
        Execute("PRAGMA journal_mode=WAL;");
        Execute("PRAGMA foreign_keys=ON;");
        CreateSchema();
    }

    private void CreateSchema()
    {
        Execute("""
            CREATE TABLE IF NOT EXISTS tasks (
                id                    TEXT PRIMARY KEY,
                title                 TEXT NOT NULL,
                kind                  INTEGER NOT NULL,
                mode                  INTEGER NOT NULL,
                working_directory     TEXT NOT NULL,
                workspace_path        TEXT NULL,
                state                 INTEGER NOT NULL,
                created_at            TEXT NOT NULL,
                updated_at            TEXT NOT NULL,
                claude_session_id     TEXT NULL,
                has_claude_session    INTEGER NOT NULL DEFAULT 0,
                raw_log_path          TEXT NULL,
                continued_from_task_id TEXT NULL,
                last_error            TEXT NULL
            );
            """);

        Execute("""
            CREATE TABLE IF NOT EXISTS messages (
                id         TEXT PRIMARY KEY,
                task_id    TEXT NOT NULL REFERENCES tasks(id) ON DELETE CASCADE,
                role       INTEGER NOT NULL,
                text       TEXT NOT NULL,
                created_at TEXT NOT NULL
            );
            """);

        Execute("""
            CREATE TABLE IF NOT EXISTS events (
                id         TEXT PRIMARY KEY,
                task_id    TEXT NOT NULL REFERENCES tasks(id) ON DELETE CASCADE,
                kind       INTEGER NOT NULL,
                title      TEXT NOT NULL,
                detail     TEXT NULL,
                tool_name  TEXT NULL,
                created_at TEXT NOT NULL
            );
            """);

        // Privileged history deliberately has no foreign key to tasks: an administrator
        // operation is a fact about the machine and must survive the task being deleted.
        Execute("""
            CREATE TABLE IF NOT EXISTS privileged_operations (
                id         TEXT PRIMARY KEY,
                task_id    TEXT NULL,
                task_title TEXT NOT NULL DEFAULT '',
                created_at TEXT NOT NULL,
                operation  TEXT NOT NULL,
                target     TEXT NOT NULL,
                approval   TEXT NOT NULL,
                result     TEXT NOT NULL,
                exit_code  INTEGER NULL,
                detail     TEXT NULL
            );
            """);

        Execute("CREATE INDEX IF NOT EXISTS ix_privileged_created ON privileged_operations(created_at DESC);");
        Execute("CREATE INDEX IF NOT EXISTS ix_messages_task ON messages(task_id, created_at);");
        Execute("CREATE INDEX IF NOT EXISTS ix_events_task   ON events(task_id, created_at);");
        Execute("CREATE INDEX IF NOT EXISTS ix_tasks_updated ON tasks(updated_at DESC);");
    }

    private void Execute(string sql)
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }
    }

    public void UpsertTask(AssistantTask task)
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO tasks (id, title, kind, mode, working_directory, workspace_path, state,
                                   created_at, updated_at, claude_session_id, has_claude_session,
                                   raw_log_path, continued_from_task_id, last_error)
                VALUES ($id, $title, $kind, $mode, $wd, $ws, $state,
                        $created, $updated, $sid, $hassid, $log, $from, $err)
                ON CONFLICT(id) DO UPDATE SET
                    title=$title, mode=$mode, working_directory=$wd, workspace_path=$ws,
                    state=$state, updated_at=$updated, claude_session_id=$sid,
                    has_claude_session=$hassid, raw_log_path=$log,
                    continued_from_task_id=$from, last_error=$err;
                """;
            cmd.Parameters.AddWithValue("$id", task.Id.ToString());
            cmd.Parameters.AddWithValue("$title", task.Title);
            cmd.Parameters.AddWithValue("$kind", (int)task.Kind);
            cmd.Parameters.AddWithValue("$mode", (int)task.Mode);
            cmd.Parameters.AddWithValue("$wd", task.WorkingDirectory);
            cmd.Parameters.AddWithValue("$ws", (object?)task.WorkspacePath ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$state", (int)task.State);
            cmd.Parameters.AddWithValue("$created", task.CreatedAt.ToString("o"));
            cmd.Parameters.AddWithValue("$updated", task.UpdatedAt.ToString("o"));
            cmd.Parameters.AddWithValue("$sid", (object?)task.ClaudeSessionId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$hassid", task.HasClaudeSession ? 1 : 0);
            cmd.Parameters.AddWithValue("$log", (object?)task.RawLogPath ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$from", (object?)task.ContinuedFromTaskId?.ToString() ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$err", (object?)task.LastError ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<AssistantTask> LoadTasks()
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT * FROM tasks ORDER BY updated_at DESC;";
            using var reader = cmd.ExecuteReader();

            var list = new List<AssistantTask>();
            while (reader.Read()) list.Add(ReadTask(reader));
            return list;
        }
    }

    private static AssistantTask ReadTask(SqliteDataReader r) => new()
    {
        Id = Guid.Parse(r.GetString(r.GetOrdinal("id"))),
        Title = r.GetString(r.GetOrdinal("title")),
        Kind = (TaskKind)r.GetInt32(r.GetOrdinal("kind")),
        Mode = (AssistantMode)r.GetInt32(r.GetOrdinal("mode")),
        WorkingDirectory = r.GetString(r.GetOrdinal("working_directory")),
        WorkspacePath = GetNullableString(r, "workspace_path"),
        State = (TaskState)r.GetInt32(r.GetOrdinal("state")),
        CreatedAt = DateTimeOffset.Parse(r.GetString(r.GetOrdinal("created_at"))),
        UpdatedAt = DateTimeOffset.Parse(r.GetString(r.GetOrdinal("updated_at"))),
        ClaudeSessionId = GetNullableString(r, "claude_session_id"),
        HasClaudeSession = r.GetInt32(r.GetOrdinal("has_claude_session")) != 0,
        RawLogPath = GetNullableString(r, "raw_log_path"),
        ContinuedFromTaskId = GetNullableString(r, "continued_from_task_id") is { } s ? Guid.Parse(s) : null,
        LastError = GetNullableString(r, "last_error"),
    };

    private static string? GetNullableString(SqliteDataReader r, string column)
    {
        var i = r.GetOrdinal(column);
        return r.IsDBNull(i) ? null : r.GetString(i);
    }

    public void AddMessage(TaskMessage message)
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT OR REPLACE INTO messages (id, task_id, role, text, created_at)
                VALUES ($id, $task, $role, $text, $created);
                """;
            cmd.Parameters.AddWithValue("$id", message.Id.ToString());
            cmd.Parameters.AddWithValue("$task", message.TaskId.ToString());
            cmd.Parameters.AddWithValue("$role", (int)message.Role);
            cmd.Parameters.AddWithValue("$text", message.Text);
            cmd.Parameters.AddWithValue("$created", message.CreatedAt.ToString("o"));
            cmd.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<TaskMessage> LoadMessages(Guid taskId)
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT * FROM messages WHERE task_id=$t ORDER BY created_at;";
            cmd.Parameters.AddWithValue("$t", taskId.ToString());
            using var r = cmd.ExecuteReader();

            var list = new List<TaskMessage>();
            while (r.Read())
            {
                list.Add(new TaskMessage
                {
                    Id = Guid.Parse(r.GetString(r.GetOrdinal("id"))),
                    TaskId = taskId,
                    Role = (MessageRole)r.GetInt32(r.GetOrdinal("role")),
                    Text = r.GetString(r.GetOrdinal("text")),
                    CreatedAt = DateTimeOffset.Parse(r.GetString(r.GetOrdinal("created_at"))),
                });
            }
            return list;
        }
    }

    public void AddEvent(AssistantEvent evt)
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT OR REPLACE INTO events (id, task_id, kind, title, detail, tool_name, created_at)
                VALUES ($id, $task, $kind, $title, $detail, $tool, $created);
                """;
            cmd.Parameters.AddWithValue("$id", evt.Id.ToString());
            cmd.Parameters.AddWithValue("$task", evt.TaskId.ToString());
            cmd.Parameters.AddWithValue("$kind", (int)evt.Kind);
            cmd.Parameters.AddWithValue("$title", evt.Title);
            cmd.Parameters.AddWithValue("$detail", (object?)evt.Detail ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$tool", (object?)evt.ToolName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$created", evt.CreatedAt.ToString("o"));
            cmd.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<AssistantEvent> LoadEvents(Guid taskId)
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT * FROM events WHERE task_id=$t ORDER BY created_at;";
            cmd.Parameters.AddWithValue("$t", taskId.ToString());
            using var r = cmd.ExecuteReader();

            var list = new List<AssistantEvent>();
            while (r.Read())
            {
                list.Add(new AssistantEvent
                {
                    Id = Guid.Parse(r.GetString(r.GetOrdinal("id"))),
                    TaskId = taskId,
                    Kind = (AssistantEventKind)r.GetInt32(r.GetOrdinal("kind")),
                    Title = r.GetString(r.GetOrdinal("title")),
                    Detail = GetNullableString(r, "detail"),
                    ToolName = GetNullableString(r, "tool_name"),
                    CreatedAt = DateTimeOffset.Parse(r.GetString(r.GetOrdinal("created_at"))),
                });
            }
            return list;
        }
    }

    public void AddPrivilegedOperation(PrivilegedOperationRecord record)
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT OR REPLACE INTO privileged_operations
                    (id, task_id, task_title, created_at, operation, target, approval, result, exit_code, detail)
                VALUES ($id, $task, $title, $created, $op, $target, $approval, $result, $exit, $detail);
                """;
            cmd.Parameters.AddWithValue("$id", record.Id.ToString());
            cmd.Parameters.AddWithValue("$task", (object?)record.TaskId?.ToString() ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$title", record.TaskTitle);
            cmd.Parameters.AddWithValue("$created", record.CreatedAt.ToString("o"));
            cmd.Parameters.AddWithValue("$op", record.Operation);
            cmd.Parameters.AddWithValue("$target", record.Target);
            cmd.Parameters.AddWithValue("$approval", record.Approval);
            cmd.Parameters.AddWithValue("$result", record.Result);
            cmd.Parameters.AddWithValue("$exit", (object?)record.ExitCode ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$detail", (object?)record.Detail ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Newest first. Used by the Capabilities panel and by task history.</summary>
    public IReadOnlyList<PrivilegedOperationRecord> LoadPrivilegedOperations(int limit = 50, Guid? taskId = null)
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = taskId is null
                ? "SELECT * FROM privileged_operations ORDER BY created_at DESC LIMIT $limit;"
                : "SELECT * FROM privileged_operations WHERE task_id=$t ORDER BY created_at DESC LIMIT $limit;";
            cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500));
            if (taskId is not null) cmd.Parameters.AddWithValue("$t", taskId.Value.ToString());

            using var r = cmd.ExecuteReader();
            var list = new List<PrivilegedOperationRecord>();
            while (r.Read())
            {
                var exit = r.GetOrdinal("exit_code");
                list.Add(new PrivilegedOperationRecord
                {
                    Id = Guid.Parse(r.GetString(r.GetOrdinal("id"))),
                    TaskId = GetNullableString(r, "task_id") is { } t ? Guid.Parse(t) : null,
                    TaskTitle = r.GetString(r.GetOrdinal("task_title")),
                    CreatedAt = DateTimeOffset.Parse(r.GetString(r.GetOrdinal("created_at"))),
                    Operation = r.GetString(r.GetOrdinal("operation")),
                    Target = r.GetString(r.GetOrdinal("target")),
                    Approval = r.GetString(r.GetOrdinal("approval")),
                    Result = r.GetString(r.GetOrdinal("result")),
                    ExitCode = r.IsDBNull(exit) ? null : r.GetInt32(exit),
                    Detail = GetNullableString(r, "detail"),
                });
            }
            return list;
        }
    }

    /// <summary>
    /// Called once at startup. Any task still marked as running belonged to a previous
    /// process that is gone, so it becomes INTERRUPTED rather than falsely showing RUNNING.
    /// </summary>
    public int MarkRunningTasksInterrupted()
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                UPDATE tasks SET state=$interrupted, updated_at=$now,
                    last_error='PC Assistant exited while this task was running.'
                WHERE state IN ($running, $waiting);
                """;
            cmd.Parameters.AddWithValue("$interrupted", (int)TaskState.Interrupted);
            cmd.Parameters.AddWithValue("$running", (int)TaskState.Running);
            cmd.Parameters.AddWithValue("$waiting", (int)TaskState.WaitingForApproval);
            cmd.Parameters.AddWithValue("$now", DateTimeOffset.Now.ToString("o"));
            return cmd.ExecuteNonQuery();
        }
    }

    public void DeleteTask(Guid taskId)
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM tasks WHERE id=$t;";
            cmd.Parameters.AddWithValue("$t", taskId.ToString());
            cmd.ExecuteNonQuery();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _connection.Close();
            _connection.Dispose();
        }
    }
}
