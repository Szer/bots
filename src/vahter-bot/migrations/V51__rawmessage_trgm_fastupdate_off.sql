-- fastupdate off: with it on, the inserting backend flushes the GIN pending list every
-- ~500 MessageReceived rows, which blocks the webhook INSERT for 10-30s on this server tier.
DROP INDEX CONCURRENTLY IF EXISTS idx_event_rawmessage_trgm;
CREATE INDEX CONCURRENTLY idx_event_rawmessage_trgm
  ON event USING gin ((data->>'rawMessage') gin_trgm_ops)
  WITH (fastupdate = off)
  WHERE event_type = 'MessageReceived';
