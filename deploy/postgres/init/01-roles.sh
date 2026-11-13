#!/bin/sh
# Runs once, on first start of an empty data volume, as POSTGRES_USER (payflow_migrator).
set -eu

psql -v ON_ERROR_STOP=1 \
     --username "$POSTGRES_USER" \
     --dbname "$POSTGRES_DB" \
     -v app_password="$PAYFLOW_APP_PASSWORD" \
     -v db_name="$POSTGRES_DB" \
     -f /payflow-sql/roles.sql
