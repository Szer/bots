-- SUPERUSER (dev/test only, same "no need for prod DB" posture as everything else in this
-- file): pgvector/pgvector's `vector` extension control file doesn't set `trusted = true`,
-- so `CREATE EXTENSION vector` (V3 migration, run by Flyway as `admin`) requires superuser
-- even though `admin` already owns the database. Confirmed empirically — without this,
-- Flyway fails with "permission denied to create extension \"vector\" ... Must be superuser".
CREATE ROLE admin WITH LOGIN PASSWORD 'admin' SUPERUSER; -- no need for prod DB
CREATE ROLE alita_bot_service WITH LOGIN PASSWORD 'alita_bot_service'; -- change password for prod
GRANT alita_bot_service TO postgres; -- no need for prod DB
CREATE DATABASE alita_bot OWNER admin ENCODING 'UTF8';
GRANT CONNECT ON DATABASE alita_bot TO alita_bot_service;
