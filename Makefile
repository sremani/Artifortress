CONFIGURATION := Debug
PROJECTS := \
	src/Artifortress.Domain/Artifortress.Domain.fsproj \
	src/Artifortress.Api/Artifortress.Api.fsproj \
	src/Artifortress.Worker/Artifortress.Worker.fsproj \
	tests/Artifortress.Domain.Tests/Artifortress.Domain.Tests.fsproj
TEST_PROJECTS := \
	tests/Artifortress.Domain.Tests/Artifortress.Domain.Tests.fsproj

.PHONY: help restore build test format dev-up dev-down dev-logs wait-db storage-bootstrap db-migrate db-smoke smoke phase1-demo

help:
	@echo "Targets:"
	@echo "  restore            Restore .NET dependencies"
	@echo "  build              Build all projects"
	@echo "  test               Run unit tests"
	@echo "  format             Verify formatting"
	@echo "  dev-up             Start local dependencies (Postgres, MinIO, Redis, OTel, Jaeger)"
	@echo "  dev-down           Stop local dependencies"
	@echo "  dev-logs           Tail dependency logs"
	@echo "  wait-db            Wait until Postgres is ready"
	@echo "  storage-bootstrap  Create MinIO bucket for development"
	@echo "  db-migrate         Apply SQL migrations"
	@echo "  db-smoke           Verify baseline schema exists"
	@echo "  smoke              End-to-end phase-0 smoke run"
	@echo "  phase1-demo        Run Phase 1 auth/repo demo script"

restore:
	@for project in $(PROJECTS); do \
		echo "Restoring $$project"; \
		dotnet restore "$$project" --ignore-failed-sources -p:NuGetAudit=false -v minimal; \
	done

build: restore
	@for project in $(PROJECTS); do \
		echo "Building $$project"; \
		dotnet build "$$project" --configuration $(CONFIGURATION) --no-restore -v minimal; \
	done

test: build
	@for project in $(TEST_PROJECTS); do \
		echo "Testing $$project"; \
		dotnet test "$$project" --configuration $(CONFIGURATION) --no-build -v minimal; \
	done

format:
	@echo "Checking for tabs in source and config files..."
	@if rg -n "\t" src tests scripts db docs .github --glob "*.fs" --glob "*.fsproj" --glob "*.md" --glob "*.sql" --glob "*.yml" --glob "*.yaml" --glob "*.sh"; then \
		echo "Tabs are not allowed in tracked text files."; \
		exit 1; \
	fi
	@echo "Checking for trailing whitespace..."
	@if rg -n "[[:blank:]]$$" src tests scripts db docs .github --glob "*.fs" --glob "*.fsproj" --glob "*.md" --glob "*.sql" --glob "*.yml" --glob "*.yaml" --glob "*.sh"; then \
		echo "Trailing whitespace detected."; \
		exit 1; \
	fi
	@echo "Formatting checks passed."

dev-up:
	docker compose up -d

dev-down:
	docker compose down --remove-orphans

dev-logs:
	docker compose logs -f --tail=100

wait-db:
	./scripts/wait-for-postgres.sh

storage-bootstrap:
	./scripts/bootstrap-storage.sh

db-migrate: wait-db
	./scripts/db-migrate.sh

db-smoke: db-migrate
	./scripts/db-smoke.sh

smoke: dev-up wait-db storage-bootstrap db-smoke build test
	./scripts/smoke-api.sh

phase1-demo: build
	./scripts/phase1-demo.sh
