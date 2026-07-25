# AlitaBot real-Telegram test loop (plan §5). Credentials: ~/.alita-test/env.
# `make real-test` is the dev quick-iteration loop: dev DB + Release build +
# tests/AlitaBot.RealTests. A full run invokes paid external APIs (Azure AI Foundry
# and/or Gemini — chat completions, TTS/STT, image and music generation), so prefer
# scoping with FILTER, e.g.:
#   make real-test FILTER="FullyQualifiedName~ImageGenRealTests"
# which is passed straight through as `dotnet test`'s --filter. The full end-to-end
# suite against a real AKS deployment (.github/workflows/alita-real-test.yml) is a
# separate, manual `gh workflow run alita-real-test.yml --ref <branch>` — not part of
# this loop and not run per-PR.

# `docker` may be a shell alias for podman (not visible to make) — pick the real binary.
DOCKER     ?= $(shell command -v docker >/dev/null 2>&1 && echo docker || echo podman)
COMPOSE     = $(DOCKER) compose -f src/alita-bot/docker-compose.dev.yml
ALITA_ENV   = $(HOME)/.alita-test/env
ARTIFACTS   = test-artifacts/AlitaBot.RealTests
REAL_FILTER = $(FILTER)

.PHONY: alita-db alita-build alita-test real-test selfcheck smoke alita-logs tg-login tg-chats probe-draft alita-clean

alita-db:
	$(COMPOSE) up -d

alita-build:
	dotnet build src/AlitaBot -c Release

alita-test:
	dotnet test tests/AlitaBot.Tests -c Release

real-test: alita-db alita-build
	dotnet test tests/AlitaBot.RealTests -c Release $(if $(REAL_FILTER),--filter "$(REAL_FILTER)",)

selfcheck: alita-db alita-build
	dotnet run --project tests/AlitaBot.RealTests -c Release -- selfcheck

smoke:
	$(COMPOSE) --profile smoke up -d --build

alita-logs:
	tail -n 100 $(ARTIFACTS)/bot.log $(ARTIFACTS)/ngrok.log

tg-login:
	dotnet run --project tests/AlitaBot.RealTests -c Release -- login

tg-chats:
	dotnet run --project tests/AlitaBot.RealTests -c Release -- list-dialogs

probe-draft:
	dotnet run --project tests/AlitaBot.RealTests -c Release -- probe-draft

alita-clean:
	# --profile smoke: `down` only tears down containers belonging to profiles
	# passed on this invocation, so the `smoke` target's `bot` container (and the
	# network it pins in use) survives a plain `down` if `make smoke` ran earlier.
	$(COMPOSE) --profile smoke down -v
	. $(ALITA_ENV) && curl -s "https://api.telegram.org/bot$$ALITA_TEST_BOT_TOKEN/deleteWebhook?drop_pending_updates=true" > /dev/null && echo "webhook deleted"

# CouponHubBot real-Telegram test loop — tests/CouponHubBot.RealTests. Credentials:
# ~/.coupon-test/env. Unlike the AlitaBot loop above, this harness never spawns the bot
# itself: point COUPON_BOT_BASE_URL at an already-running bot (either the CI AKS pod, or
# your own local `docker compose -f src/coupon-hub-bot/docker-compose.dev.yml --profile
# smoke up -d --build` + ngrok + manual setWebhook per src/CouponHubBot/README.dev.md)
# before running any of these. `coupon-real-test` runs everything EXCEPT the
# membership-gate test (see MembershipGateRealTests.fs — it self-skips by default and
# must be run alone, via `coupon-real-test-membership`, against a pod you're prepared
# to discard afterward). Scope with FILTER, e.g.:
#   make coupon-real-test FILTER="FullyQualifiedName~AddFlowRealTests"
COUPON_REAL_FILTER = $(FILTER)

.PHONY: coupon-real-test coupon-real-test-membership coupon-tg-login coupon-tg-chats

coupon-real-test:
	dotnet test tests/CouponHubBot.RealTests -c Release \
		--filter "FullyQualifiedName!~MembershipGateRealTests$(if $(COUPON_REAL_FILTER), & $(COUPON_REAL_FILTER),)"

# Isolated run of the one test that poisons TelegramMembershipService's in-memory cache
# for the rest of a pod's life — see MembershipGateRealTests.fs's doc comment. Do not
# run coupon-real-test against the same pod afterward without a restart/redeploy.
coupon-real-test-membership:
	COUPON_REAL_TEST_RUN_MEMBERSHIP_GATE=1 dotnet test tests/CouponHubBot.RealTests -c Release \
		--filter "FullyQualifiedName~MembershipGateRealTests"


# Local dev loop only — CI does NOT run this. The coupon real-test workflow reuses
# Alita's already-logged-in MTProto session (same real Telegram account; the two
# workflows share the aks-vpn concurrency group with cancel-in-progress: false, so a
# coupon run and an Alita run never execute concurrently and never fight over the
# session file) and writes it to COUPON_TG_SESSION_PATH itself — no login step needed
# in CI. Use this only to create your OWN local session for the dev loop above.
coupon-tg-login:
	dotnet run --project tests/CouponHubBot.RealTests -c Release -- login

coupon-tg-chats:
	dotnet run --project tests/CouponHubBot.RealTests -c Release -- list-dialogs
