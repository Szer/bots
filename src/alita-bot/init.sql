CREATE ROLE admin WITH LOGIN PASSWORD 'admin'; -- no need for prod DB
CREATE ROLE alita_bot_service WITH LOGIN PASSWORD 'alita_bot_service'; -- change password for prod
GRANT alita_bot_service TO postgres; -- no need for prod DB
CREATE DATABASE alita_bot OWNER admin ENCODING 'UTF8';
GRANT CONNECT ON DATABASE alita_bot TO alita_bot_service;
\connect alita_bot
CREATE EXTENSION IF NOT EXISTS vector;
