namespace PayFlow.Infrastructure.Persistence;

/// <summary>
/// SQL used by migrations to make tables append-only. Two layers of protection:
/// a trigger that rejects UPDATE, DELETE and TRUNCATE for every role, and revoked privileges
/// for the application role <c>payflow_app</c> when it exists (docs/DATABASE.md §4).
/// </summary>
public static class AppendOnlySql
{
    public const string AppRole = "payflow_app";

    public const string CreateGuardFunction = """
        CREATE OR REPLACE FUNCTION platform.reject_mutation() RETURNS trigger
        LANGUAGE plpgsql AS $$
        BEGIN
            RAISE EXCEPTION 'append_only_violation: %.% does not permit %', TG_TABLE_SCHEMA, TG_TABLE_NAME, TG_OP
                USING ERRCODE = 'P0001';
        END;
        $$;
        """;

    public const string DropGuardFunction = "DROP FUNCTION IF EXISTS platform.reject_mutation();";

    public static string Protect(string schema, string table)
    {
        ValidateIdentifier(schema);
        ValidateIdentifier(table);
        return $"""
            CREATE TRIGGER trg_{table}_append_only
                BEFORE UPDATE OR DELETE ON {schema}.{table}
                FOR EACH ROW EXECUTE FUNCTION platform.reject_mutation();
            CREATE TRIGGER trg_{table}_no_truncate
                BEFORE TRUNCATE ON {schema}.{table}
                FOR EACH STATEMENT EXECUTE FUNCTION platform.reject_mutation();
            DO $$
            BEGIN
                IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '{AppRole}') THEN
                    REVOKE UPDATE, DELETE, TRUNCATE ON {schema}.{table} FROM {AppRole};
                END IF;
            END $$;
            """;
    }

    /// <summary>
    /// For financial records whose status may change but which must never disappear (payments,
    /// refunds, reversals): rejects DELETE and TRUNCATE, and revokes them from the app role.
    /// </summary>
    public static string PreventDeletion(string schema, string table)
    {
        ValidateIdentifier(schema);
        ValidateIdentifier(table);
        return $"""
            CREATE TRIGGER trg_{table}_no_delete
                BEFORE DELETE ON {schema}.{table}
                FOR EACH ROW EXECUTE FUNCTION platform.reject_mutation();
            CREATE TRIGGER trg_{table}_no_truncate
                BEFORE TRUNCATE ON {schema}.{table}
                FOR EACH STATEMENT EXECUTE FUNCTION platform.reject_mutation();
            DO $$
            BEGIN
                IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '{AppRole}') THEN
                    REVOKE DELETE, TRUNCATE ON {schema}.{table} FROM {AppRole};
                END IF;
            END $$;
            """;
    }

    public static string AllowDeletion(string schema, string table)
    {
        ValidateIdentifier(schema);
        ValidateIdentifier(table);
        return $"""
            DROP TRIGGER IF EXISTS trg_{table}_no_delete ON {schema}.{table};
            DROP TRIGGER IF EXISTS trg_{table}_no_truncate ON {schema}.{table};
            """;
    }

    public static string Unprotect(string schema, string table)
    {
        ValidateIdentifier(schema);
        ValidateIdentifier(table);
        return $"""
            DROP TRIGGER IF EXISTS trg_{table}_append_only ON {schema}.{table};
            DROP TRIGGER IF EXISTS trg_{table}_no_truncate ON {schema}.{table};
            """;
    }

    private static void ValidateIdentifier(string value)
    {
        if (string.IsNullOrEmpty(value) || !value.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_'))
        {
            throw new ArgumentException($"'{value}' is not a safe SQL identifier.", nameof(value));
        }
    }
}
