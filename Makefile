CONFIGURATION := Debug
PROJECTS := \
	src/Artifortress.Domain/Artifortress.Domain.fsproj \
	src/Artifortress.Api/Artifortress.Api.fsproj \
	src/Artifortress.Worker/Artifortress.Worker.fsproj \
	tests/Artifortress.Domain.Tests/Artifortress.Domain.Tests.fsproj
TEST_PROJECTS := \
	tests/Artifortress.Domain.Tests/Artifortress.Domain.Tests.fsproj

.PHONY: help restore build test test-integration format dev-up dev-down dev-logs wait-db storage-bootstrap db-migrate db-smoke db-backup db-restore phase6-drill mutation-spike mutation-track mutation-fsharp-native mutation-fsharp-native-score mutation-fsharp-native-trend mutation-fsharp-native-burnin mutation-trackb-bootstrap mutation-trackb-build mutation-trackb-spike mutation-trackb-assert mutation-trackb-compile-validate smoke phase1-demo phase2-demo phase2-load phase3-demo phase4-demo phase5-demo phase6-demo phase7-demo

help:
	@echo "Targets:"
	@echo "  restore            Restore .NET dependencies"
	@echo "  build              Build all projects"
	@echo "  test               Run non-integration tests"
	@echo "  test-integration   Run integration tests (requires local deps)"
	@echo "  format             Verify formatting"
	@echo "  dev-up             Start local dependencies (Postgres, MinIO, Redis, OTel, Jaeger)"
	@echo "  dev-down           Stop local dependencies"
	@echo "  dev-logs           Tail dependency logs"
	@echo "  wait-db            Wait until Postgres is ready"
	@echo "  storage-bootstrap  Create MinIO bucket for development"
	@echo "  db-migrate         Apply SQL migrations"
	@echo "  db-smoke           Verify baseline schema exists"
	@echo "  db-backup          Create Postgres backup file (set BACKUP_PATH to override)"
	@echo "  db-restore         Restore Postgres backup file (requires RESTORE_PATH)"
	@echo "  phase6-drill       Run Phase 6 RPO/RTO backup-restore drill"
	@echo "  mutation-spike     Run F# mutation feasibility spike (wrapper CLI) and generate report"
	@echo "  mutation-track     Run mutation wrapper default flow and generate report"
	@echo "  mutation-fsharp-native     Run native F# mutation runtime lane and generate report"
	@echo "  mutation-fsharp-native-score  Compute native mutation score and threshold report"
	@echo "  mutation-fsharp-native-trend  Append native score history and generate trend report"
	@echo "  mutation-fsharp-native-burnin  Evaluate burn-in readiness from score history"
	@echo "  mutation-trackb-bootstrap  Prepare patched Stryker.NET workspace for MUT-06"
	@echo "  mutation-trackb-build      Build patched Stryker.NET CLI in local workspace"
	@echo "  mutation-trackb-spike      Run wrapper flow against patched Stryker.NET CLI"
	@echo "  mutation-trackb-assert     Assert Track B emitted-mutant invariants from latest artifacts"
	@echo "  mutation-trackb-compile-validate  Compile-validate sampled F# mutants for MUT-07c"
	@echo "  smoke              End-to-end phase-0 smoke run"
	@echo "  phase1-demo        Run Phase 1 auth/repo demo script"
	@echo "  phase2-demo        Run Phase 2 upload/download demo script"
	@echo "  phase2-load        Run Phase 2 throughput baseline script"
	@echo "  phase3-demo        Run Phase 3 draft/manifest/publish demo script"
	@echo "  phase4-demo        Run Phase 4 policy/quarantine/search demo script"
	@echo "  phase5-demo        Run Phase 5 tombstone/gc/reconcile demo script"
	@echo "  phase6-demo        Run Phase 6 GA readiness demo script"
	@echo "  phase7-demo        Run Phase 7 identity integration demo script"

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
		dotnet test "$$project" --configuration $(CONFIGURATION) --no-build -v minimal --filter "Category!=Integration"; \
	done

test-integration: build
	@for project in $(TEST_PROJECTS); do \
		echo "Testing integration suite $$project"; \
		dotnet test "$$project" --configuration $(CONFIGURATION) --no-build -v minimal --filter "Category=Integration"; \
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

db-backup:
	./scripts/db-backup.sh

db-restore:
	./scripts/db-restore.sh

phase6-drill:
	./scripts/phase6-drill.sh

mutation-spike:
	dotnet run --project tools/Artifortress.MutationTrack/Artifortress.MutationTrack.fsproj -- spike

mutation-track:
	dotnet run --project tools/Artifortress.MutationTrack/Artifortress.MutationTrack.fsproj -- run

mutation-fsharp-native:
	./scripts/mutation-fsharp-native-run.sh

mutation-fsharp-native-score:
	./scripts/mutation-fsharp-native-score.sh

mutation-fsharp-native-trend:
	./scripts/mutation-fsharp-native-trend.sh

mutation-fsharp-native-burnin:
	./scripts/mutation-fsharp-native-burnin.sh

mutation-trackb-bootstrap:
	./scripts/mutation-trackb-bootstrap.sh

mutation-trackb-build:
	./scripts/mutation-trackb-build.sh

mutation-trackb-spike:
	./scripts/mutation-trackb-spike.sh

mutation-trackb-assert:
	./scripts/mutation-trackb-assert.sh

mutation-trackb-compile-validate:
	./scripts/mutation-trackb-compile-validate.sh

smoke: dev-up wait-db storage-bootstrap db-smoke build test test-integration
	./scripts/smoke-api.sh

phase1-demo: build
	./scripts/phase1-demo.sh

phase2-demo: build
	./scripts/phase2-demo.sh

phase2-load: build
	./scripts/phase2-load.sh

phase3-demo: build
	./scripts/phase3-demo.sh

phase4-demo: build
	./scripts/phase4-demo.sh

phase5-demo: build
	./scripts/phase5-demo.sh

phase6-demo: build
	./scripts/phase6-demo.sh

phase7-demo: build
	./scripts/phase7-demo.sh
