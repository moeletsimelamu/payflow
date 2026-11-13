-- Database roles (docs/DATABASE.md section 4). Executed by the schema owner (payflow_migrator)
-- before any migration runs. psql variables: app_password, db_name.
--  * payflow_migrator owns every object and runs migrations (DDL).
--  * payflow_app is what the API connects as: DML only. Migrations additionally revoke
--    UPDATE/DELETE/TRUNCATE on append-only tables (PayFlow.Infrastructure.Persistence.AppendOnlySql).
-- The integration tests apply this same file, so the privilege model is tested, not assumed.

CREATE ROLE payflow_app LOGIN PASSWORD :'app_password';
GRANT CONNECT ON DATABASE :"db_name" TO payflow_app;

-- Objects later created by the migrator are automatically usable by the app role.
ALTER DEFAULT PRIVILEGES GRANT USAGE ON SCHEMAS TO payflow_app;
ALTER DEFAULT PRIVILEGES GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO payflow_app;
ALTER DEFAULT PRIVILEGES GRANT USAGE, SELECT ON SEQUENCES TO payflow_app;
ALTER DEFAULT PRIVILEGES GRANT EXECUTE ON FUNCTIONS TO payflow_app;

REVOKE CREATE ON SCHEMA public FROM PUBLIC;
