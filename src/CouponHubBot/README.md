# CouponHubBot

Telegram bot for collaborative coupon management in a private Russian-speaking community.

## Commands

### DM Commands (in menu)

- `/add` (alias `/a`) — add a coupon via wizard
- `/list` (alias `/l`, legacy `/coupons`) — available coupons (expired hidden)
- `/my` (alias `/m`) — my coupons: photo album + description with buttons
- `/stats` (alias `/s`) — user statistics
- `/feedback` (alias `/f`) — send feedback to admins (next message is forwarded)
- `/help` — help

### Additional Commands

- `/take <id>` (or `/take` as alias for `/list`) — take a coupon (transactional)
- `/used <id>` — mark coupon as used
- `/return <id>` — return coupon to available pool

### Rules

- **Take limit**: max **4** coupons at a time (race-safe)
- **Expired coupons**: hidden from available list, cannot be taken

### Notifications

- **Group**: morning reminder about expiring coupons; Monday weekly stats (used + added)
- **DM**: morning reminder for users holding coupons in `taken` status > 1 day

## Deployment

- **GHCR image**: `ghcr.io/szer/coupon-bot`
- **Deploy**: push to `main` triggers CI/CD via GitHub Actions → GHCR → ArgoCD

## Database

- **Database**: `coupon_hub_bot`
- **Role**: `coupon_hub_bot_service` (always add `GRANT` in migrations)
- **Migrations**: `src/coupon-hub-bot/migrations/` (Flyway)

## Documentation

| Topic | File |
|-------|------|
| Architecture | [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) |
| Bot Logic | [docs/TELEGRAM-BOT-LOGIC.md](docs/TELEGRAM-BOT-LOGIC.md) |
| Testing | [docs/TESTING.md](docs/TESTING.md) |
| Database | [docs/DATABASE.md](docs/DATABASE.md) |
| OCR | [docs/OCR.md](docs/OCR.md) |
| Deployment | [docs/DEPLOYMENT.md](docs/DEPLOYMENT.md) |
| Observability | [docs/OBSERVABILITY.md](docs/OBSERVABILITY.md) |
| Product Vision | [docs/PRODUCT-VISION.md](docs/PRODUCT-VISION.md) |

## Local Development

See [README.dev.md](README.dev.md) for local development setup, OCR testing, and troubleshooting.

## License

MIT. See [root LICENSE](../../LICENSE).
