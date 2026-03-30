#!/usr/bin/env bash
# scripts/init-db.sh — Digital Wallet System
# Runs Flyway migrations against the PostgreSQL database.
# Called by the 'migrate' init container in docker-compose.yml.
#
# Environment variables (set by docker-compose via .env):
#   POSTGRES_HOST, POSTGRES_PORT, POSTGRES_DB, POSTGRES_USER, POSTGRES_PASSWORD

set -euo pipefail

POSTGRES_HOST="${POSTGRES_HOST:-postgres}"
POSTGRES_PORT="${POSTGRES_PORT:-5432}"
POSTGRES_DB="${POSTGRES_DB:-digital_wallet}"
POSTGRES_USER="${POSTGRES_USER:-devuser}"
POSTGRES_PASSWORD="${POSTGRES_PASSWORD:-devpass}"

echo "==> Waiting for PostgreSQL at ${POSTGRES_HOST}:${POSTGRES_PORT}..."
until pg_isready -h "$POSTGRES_HOST" -p "$POSTGRES_PORT" -U "$POSTGRES_USER"; do
  sleep 1
done
echo "==> PostgreSQL is ready."

echo "==> Creating database '${POSTGRES_DB}' if it does not exist..."
PGPASSWORD="$POSTGRES_PASSWORD" psql \
  -h "$POSTGRES_HOST" \
  -p "$POSTGRES_PORT" \
  -U "$POSTGRES_USER" \
  -d postgres \
  -tc "SELECT 1 FROM pg_database WHERE datname = '${POSTGRES_DB}'" \
  | grep -q 1 || \
  PGPASSWORD="$POSTGRES_PASSWORD" psql \
    -h "$POSTGRES_HOST" \
    -p "$POSTGRES_PORT" \
    -U "$POSTGRES_USER" \
    -d postgres \
    -c "CREATE DATABASE ${POSTGRES_DB};"
echo "==> Database ready."

echo "==> Running Flyway migrations..."
flyway \
  -url="jdbc:postgresql://${POSTGRES_HOST}:${POSTGRES_PORT}/${POSTGRES_DB}" \
  -user="$POSTGRES_USER" \
  -password="$POSTGRES_PASSWORD" \
  -locations="filesystem:/migrations" \
  -baselineOnMigrate=true \
  -validateOnMigrate=true \
  migrate

echo "==> Migrations complete."
