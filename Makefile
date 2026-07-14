# M365 Migration Platform — common Docker tasks.
# `make up` is the canonical way to run the whole stack.
.DEFAULT_GOAL := help
COMPOSE := docker compose

.PHONY: help up build rebuild dev down stop restart logs status ps db clean

help: ## Show this help
	@grep -E '^[a-zA-Z_-]+:.*?## .*$$' $(MAKEFILE_LIST) | sort | \
	  awk 'BEGIN {FS = ":.*?## "}; {printf "  \033[36m%-10s\033[0m %s\n", $$1, $$2}'

up: ## Start the full stack (postgres + api + web), building images if needed
	@./start.sh

build: ## Build the api + web images
	$(COMPOSE) build

rebuild: ## Rebuild images and start
	@./start.sh --build

dev: ## Dev mode: postgres in Docker, api + web run locally with hot reload
	@./dev.sh

stop: ## Stop containers (keep them + the database volume)
	$(COMPOSE) stop

down: ## Remove containers + network (keep the database volume)
	$(COMPOSE) down

restart: ## Restart the api + web containers
	$(COMPOSE) restart api web

logs: ## Tail all logs (use `make logs s=api` for one service)
	$(COMPOSE) logs -f $(s)

status: ## Show container + API health status
	@./status.sh

ps: ## List containers
	$(COMPOSE) ps

db: ## Open a psql shell in the postgres container
	$(COMPOSE) exec postgres psql -U migration_user -d migration_platform

clean: ## Remove containers + network + DELETE the database volume (fresh start)
	$(COMPOSE) down -v
