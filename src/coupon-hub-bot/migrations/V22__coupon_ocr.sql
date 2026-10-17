CREATE TABLE coupon_ocr (
    photo_file_id TEXT PRIMARY KEY,
    raw_response JSONB NOT NULL
);

GRANT SELECT, INSERT, UPDATE ON coupon_ocr TO coupon_hub_bot_service;
