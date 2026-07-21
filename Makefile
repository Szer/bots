# AlitaBot real-Telegram test loop (plan §5). Credentials: ~/.alita-test/env.
# `make real-test` is THE agent loop: dev DB + Release build + tests/AlitaBot.RealTests.

# `docker` may be a shell alias for podman (not visible to make) — pick the real binary.
DOCKER   ?= $(shell command -v docker >/dev/null 2>&1 && echo docker || echo podman)
COMPOSE   = $(DOCKER) compose -f src/alita-bot/docker-compose.dev.yml
ALITA_ENV = $(HOME)/.alita-test/env
ARTIFACTS = test-artifacts/AlitaBot.RealTests

.PHONY: alita-db alita-build alita-test real-test selfcheck smoke alita-logs tg-login tg-chats alita-clean

alita-db:
	$(COMPOSE) up -d

alita-build:
	dotnet build src/AlitaBot -c Release

alita-test:
	dotnet test tests/AlitaBot.Tests -c Release

real-test: alita-db alita-build
	dotnet test tests/AlitaBot.RealTests -c Release

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

alita-clean:
	$(COMPOSE) down -v
	. $(ALITA_ENV) && curl -s "https://api.telegram.org/bot$$ALITA_TEST_BOT_TOKEN/deleteWebhook?drop_pending_updates=true" > /dev/null && echo "webhook deleted"
