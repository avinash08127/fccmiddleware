# Repository, Branching & CI/CD Pipeline Design

## 1. Output Location
- **Target file:** `docs/specs/foundation/tier-3-2-repo-branching-and-cicd.md`
- **Location rationale:** Foundational engineering decisions that affect all three components. Maps to `docs/specs/foundation` per `STRUCTURE.md`.

## 2. Scope
- **TODO items addressed:** 3.2 Repository & Branching Strategy, 3.3 CI/CD Pipeline Design
- **In scope:** Repo topology, branching model, naming conventions, PR rules, commit conventions, release tagging, per-component pipeline stages, environment definitions, IaC tool and state management
- **Out of scope:** Actual Terraform modules, actual GitHub Actions YAML, actual Dockerfile contents, testing strategy details (covered by TODO 3.4), observability setup (TODO 3.5)

## 3. Source Traceability
- **Requirements:** NFR-1 (availability — pipeline must support zero-downtime deploys), NFR-7 (observability — pipeline must integrate structured logging checks), NFR-8 (recovery)
- **HLD sections:** Cloud §5.1 (single repo for cloud, separate repos for Edge/Portal), Cloud §5.2 (solution structure with `infra/` folder), Cloud §10.1 (GitHub Actions, Terraform/CDK), Edge §5.1 (separate repo), Edge §8.3 (Sure MDM distribution), Portal §5.1 (separate repo), Portal §8.2 (build/deploy pipeline), Portal §8.3 (environment strategy)
- **Assumptions:** All three HLDs independently recommend separate repositories. Cloud HLD recommends GitHub Actions and Terraform.

## 4. Key Decisions

| # | Decision | Why | Impact |
|---|----------|-----|--------|
| 1 | **Three separate repositories** | Each component has distinct build tooling (.NET, Gradle, Angular CLI), independent release cadence, and separate deployment targets. All three HLDs align on this. | Simpler CI configs. Independent versioning. Shared contracts managed via generated artifacts (NuGet package → TypeScript types). |
| 2 | **Trunk-based development** with short-lived feature branches | Small team, fast iteration for MVP. Avoids long-lived branch merge pain. Feature flags not needed at this scale. | All environments deploy from `main`. Release branches cut only for hotfixes to production. |
| 3 | **Conventional Commits** enforced via CI | Enables automated changelog generation and semantic versioning. Well-supported tooling across .NET, Kotlin, and Node ecosystems. | Commit lint check in PR pipeline. Changelog generated at release tag time. |
| 4 | **Terraform** for IaC with S3+DynamoDB state backend | Terraform is cloud-agnostic, widely adopted, and recommended in Cloud HLD. CDK adds .NET compilation step. | Single `infra/terraform/` folder in cloud-backend repo. State locked per environment. |
| 5 | **GitHub Actions** for all pipelines | Recommended in all three HLDs. Native GitHub integration. Free tier sufficient for MVP. | One workflow file per pipeline stage. Reusable workflows for shared steps. |

## 5. Detailed Specification

### 5.1 Repository Topology

| Repository | Contents | Build Tool | Deploy Target |
|-----------|----------|-----------|---------------|
| `fcc-cloud-backend` | .NET API + Worker, EF Core migrations, Terraform, Docker | `dotnet` CLI, Docker | ECS Fargate (ECR images) |
| `fcc-edge-agent` | Kotlin Android app, Gradle build, FCC simulator | Gradle (Kotlin DSL) | APK → Sure MDM |
| `fcc-angular-portal` | Angular SPA, environment configs | Angular CLI (`ng`) | S3 + CloudFront |
| `fcc-contracts` (optional, defer if premature) | OpenAPI specs, JSON schemas, shared docs | None (reference only) | NuGet package (cloud) → TypeScript types (portal) |

Shared contract flow: Cloud Backend publishes OpenAPI spec → `fcc-angular-portal` CI step generates TypeScript client types via `openapi-typescript-codegen`. Edge Agent implements the same canonical model from the shared spec documentation — no binary dependency.

### 5.2 Branching Model

```
main ──────────────────────────────────────────►
  ├── feature/TICKET-123-add-ingestion-api ──┐
  │                                          PR merge (squash)
  ├── bugfix/TICKET-456-fix-dedup-key ───────┐
  │                                          PR merge (squash)
  ├── release/1.2.0 (cut from main when ready)
  │     └── hotfix/TICKET-789-fix-prod-crash ─► cherry-pick to main
  └── ...
```

| Rule | Value |
|------|-------|
| Default branch | `main` |
| Feature branch lifetime | ≤ 3 days (target), ≤ 5 days (hard limit before review) |
| Merge strategy | Squash merge to `main` |
| Release branch | Cut from `main` at release time; only hotfixes land here |
| Direct push to `main` | Blocked |
| Force push to `main` | Blocked |

### 5.3 Branch Naming Convention

| Prefix | Usage | Example |
|--------|-------|---------|
| `feature/` | New functionality | `feature/TICKET-123-edge-upload-api` |
| `bugfix/` | Non-production bug fixes | `bugfix/TICKET-456-fix-null-pump` |
| `hotfix/` | Production emergency fixes (from release branch) | `hotfix/TICKET-789-fix-auth-crash` |
| `release/` | Release candidates | `release/1.2.0` |
| `chore/` | CI, docs, refactoring with no behavior change | `chore/TICKET-100-update-terraform` |

Format: `<prefix>/<ticket-id>-<short-description>` — lowercase, hyphen-separated.

### 5.4 PR Review Requirements

| Rule | Value |
|------|-------|
| Minimum approvals | 1 (2 for `release/` → `main` back-merges) |
| CI checks must pass | All: build, lint, unit tests, integration tests |
| Branch must be up-to-date | Require rebase on `main` before merge |
| Commit message lint | Conventional Commits format validated |
| Code review tool | GitHub pull request reviews |
| Auto-merge | Disabled for MVP |

### 5.5 Commit Message Convention

Format: [Conventional Commits v1.0.0](https://www.conventionalcommits.org/)

```
<type>(<scope>): <description>

[optional body]

[optional footer(s)]
```

| Type | When |
|------|------|
| `feat` | New feature |
| `fix` | Bug fix |
| `refactor` | Code change that neither fixes a bug nor adds a feature |
| `test` | Adding or updating tests |
| `ci` | CI/CD pipeline changes |
| `docs` | Documentation only |
| `chore` | Build tooling, dependency updates |

Scopes per repo:
- **Cloud:** `ingestion`, `adapter`, `preauth`, `reconciliation`, `sync`, `auth`, `infra`, `migration`
- **Edge:** `adapter`, `buffer`, `sync`, `api`, `connectivity`, `config`
- **Portal:** `dashboard`, `transactions`, `reconciliation`, `agents`, `config`, `auth`

Breaking changes: append `!` after type (e.g., `feat(ingestion)!: change batch upload response format`).

### 5.6 Release Tagging & Changelog

| Item | Value |
|------|-------|
| Tag format | `v<semver>` (e.g., `v1.0.0`, `v1.2.1`) |
| Tag placement | On `main` (or on `release/` branch for hotfixes) |
| Versioning | Semantic versioning. `feat` → minor, `fix` → patch, `!` → major |
| Changelog generation | Automated from Conventional Commits at tag time via GitHub Actions |
| Edge Agent versioning | Same semver in `versionName`; `versionCode` is monotonically increasing integer |
| Portal versioning | Same semver; embedded in `environment.ts` at build time |

### 5.7 Environments

| Environment | Purpose | Deployed From | Approval Gate | Infra |
|-------------|---------|---------------|---------------|-------|
| `local-dev` | Developer workstation | N/A (local run) | None | Docker Compose (Postgres, Redis, FCC Simulator) |
| `dev` | Shared development | `main` (auto on merge) | None | AWS dev account, smaller instances |
| `staging` | Integration & QA | `main` (auto, after dev succeeds) | None | AWS staging account, production-like config |
| `uat` | User acceptance testing | `release/*` tag | Manual approval | AWS UAT account, production-like data |
| `production` | Live operations | `release/*` tag (after UAT sign-off) | Manual approval (2 approvers) | AWS production account, full HA |

### 5.8 Cloud Backend Pipeline (`fcc-cloud-backend`)

**Trigger:** Push to `main` or `release/*` tag.

| Stage | Steps | Failure Action |
|-------|-------|----------------|
| **Build** | `dotnet restore` → `dotnet build` → commit lint check | Block PR |
| **Unit Tests** | `dotnet test` (xUnit, domain + adapter tests) | Block PR |
| **Integration Tests** | Testcontainers (Postgres, Redis) → `dotnet test --filter Integration` | Block PR |
| **Docker Build** | Build `Dockerfile.api` + `Dockerfile.worker` → push to ECR | Block deploy |
| **DB Migration** | EF Core migration dry-run against target env DB (on deploy) | Block deploy, alert |
| **Deploy Dev** | Terraform apply (dev) → ECS service update → health check wait | Alert, auto-rollback |
| **Deploy Staging** | Terraform apply (staging) → ECS service update → smoke tests | Alert, manual rollback |
| **Deploy UAT** | Manual trigger → Terraform apply (uat) → ECS update | Alert |
| **Deploy Prod** | Manual approval (2) → Terraform apply (prod) → ECS rolling update → smoke tests | Alert, rollback procedure |

DB migrations run as a separate ECS task before the application deployment in each environment. Rollback procedure: revert ECS task definition to previous revision; migration rollback scripts maintained alongside forward migrations.

Terraform state: S3 bucket `fcc-terraform-state` with DynamoDB lock table, one state file per environment (`env/<env>/terraform.tfstate`).

### 5.9 Edge Agent Pipeline (`fcc-edge-agent`)

**Trigger:** Push to `main` or `release/*` tag.

| Stage | Steps | Failure Action |
|-------|-------|----------------|
| **Build** | `./gradlew assembleDebug` → commit lint check | Block PR |
| **Unit Tests** | `./gradlew testDebugUnitTest` (JUnit 5 + MockK) | Block PR |
| **Instrumented Tests** | GitHub Actions Android emulator → `./gradlew connectedDebugAndroidTest` | Block PR |
| **Sign APK** | `./gradlew assembleRelease` (keystore from GitHub Secrets) | Block release |
| **Publish Internal** | Upload signed APK to GitHub Releases (internal testing) | Alert |
| **Distribute UAT** | Manual trigger → upload APK to Sure MDM UAT group | Alert |
| **Distribute Prod** | Manual approval → push APK to Sure MDM production device group | Alert |

APK signing key management:
- Keystore file stored in **GitHub Actions encrypted secrets** (not in repo)
- Keystore password and key alias password in separate secrets
- Key rotation: generate new key, sign with both old and new during transition (Android allows multiple signers via APK Signature Scheme v2)
- Backup: keystore file backed up to AWS Secrets Manager (encrypted)

### 5.10 Angular Portal Pipeline (`fcc-angular-portal`)

**Trigger:** Push to `main` or `release/*` tag.

| Stage | Steps | Failure Action |
|-------|-------|----------------|
| **Build** | `npm ci` → `ng lint` → commit lint check | Block PR |
| **Unit Tests** | `ng test --watch=false --browsers=ChromeHeadless` | Block PR |
| **E2E Tests** | Playwright against local dev server | Block PR |
| **Build Prod** | `ng build --configuration=<env>` (injects API URL, Entra config) | Block deploy |
| **Deploy Dev** | `aws s3 sync dist/ s3://<dev-bucket>` → CloudFront invalidation | Alert |
| **Deploy Staging** | Auto after dev succeeds → S3 sync → CloudFront invalidation | Alert |
| **Deploy UAT** | Manual trigger → S3 sync → CloudFront invalidation | Alert |
| **Deploy Prod** | Manual approval → S3 sync → CloudFront invalidation | Alert |

Environment-specific builds: `environment.ts` files contain `apiBaseUrl`, `entraClientId`, `entraTenantId`, `entaRedirectUri`. Angular CLI `--configuration` flag selects the correct file at build time.

### 5.11 Infrastructure as Code

| Item | Value |
|------|-------|
| Tool | Terraform ≥ 1.6 |
| State backend | S3 + DynamoDB (lock) in a dedicated `fcc-infra` AWS account |
| Workspace strategy | One workspace per environment (`dev`, `staging`, `uat`, `prod`) |
| Module structure | `modules/ecs`, `modules/aurora`, `modules/s3-cloudfront`, `modules/networking`, `modules/secrets` |
| Plan review | `terraform plan` output posted as PR comment on infra changes |
| Apply | Auto-apply for `dev`; manual approval for `staging`/`uat`/`prod` |
| Managed resources | VPC, ALB, ECS cluster + services, Aurora PostgreSQL, ElastiCache Redis, S3 buckets, CloudFront, ECR, IAM roles, Secrets Manager, CloudWatch alarms |

## 6. Validation and Edge Cases

- **Concurrent PRs modifying Terraform:** DynamoDB state lock prevents concurrent applies. Plan output in PR comments ensures visibility.
- **APK signing key loss:** Keystore backup in AWS Secrets Manager. If lost, new key requires fresh install on all devices (cannot update existing APK signature). Document recovery procedure.
- **DB migration failure mid-deploy:** Migrations run in a dedicated ECS task with rollback SQL. Deployment blocked if migration task exits non-zero.
- **CloudFront cache staleness after portal deploy:** Explicit `/*` invalidation after S3 sync. Assets use content-hash filenames for long-term caching.
- **Edge Agent version skew with cloud:** Cloud Backend's `/agent/version-check` endpoint enforces minimum compatible agent version. Pipeline does not gate on this — it is a runtime check.

## 7. Cross-Component Impact

| Component | Impact |
|-----------|--------|
| **All** | Must adopt Conventional Commits and branch naming convention immediately |
| **Cloud Backend** | Owns Terraform modules for shared AWS infrastructure (VPC, ALB, CloudFront). Portal and Edge Agent pipelines reference outputs. |
| **Edge Agent** | APK signing secrets must be provisioned in GitHub before first release build |
| **Portal** | TypeScript client generation step depends on Cloud Backend OpenAPI spec being published |

## 8. Dependencies

- **Prerequisites:** TODO 3.1 (Project Scaffolding) — repo structure must exist before pipelines are configured
- **Downstream TODOs affected:** TODO 3.4 (Testing Strategy) — test stages defined here must align with testing tooling decisions; TODO 3.5 (Observability) — pipeline must integrate log/metric checks
- **Recommended next step:** Provision the three GitHub repositories, configure branch protection rules, and create initial GitHub Actions workflow files

## 9. Open Questions

| # | Question | Recommendation | Risk if Deferred |
|---|----------|----------------|------------------|
| 1 | Is a dedicated `fcc-contracts` repo needed now, or can shared OpenAPI specs live in the cloud backend repo and be consumed from there? | Start with specs in `fcc-cloud-backend/contracts/openapi/`. Extract to a separate repo only if multi-repo consumption becomes painful. | Low — easily migrated later. |
| 2 | Sure MDM API access for automated APK distribution — is API available, or is upload manual? | Investigate Sure MDM API. If unavailable, manual upload to Sure MDM console is acceptable for MVP. | Medium — manual upload is slower but workable. Pipeline can still produce the signed APK. |

## 10. Acceptance Checklist

- [ ] Three GitHub repositories created with `main` as default branch
- [ ] Branch protection rules configured per §5.4
- [ ] Conventional Commits lint check runs in PR pipeline for all three repos
- [ ] Cloud Backend pipeline builds, tests, Dockerizes, and deploys to at least `dev`
- [ ] Edge Agent pipeline builds, tests, signs APK, and publishes to GitHub Releases
- [ ] Angular Portal pipeline builds, tests, and deploys to at least `dev` (S3 + CloudFront)
- [ ] Terraform state backend (S3 + DynamoDB) provisioned
- [ ] Terraform applies successfully for `dev` environment
- [ ] APK signing keystore stored in GitHub Secrets and backed up to AWS Secrets Manager
- [ ] Environment list (`local-dev`, `dev`, `staging`, `uat`, `production`) operational or stubbed
- [ ] Release tagging produces a changelog from Conventional Commits

## 11. Output Files to Create

| File | Purpose |
|------|---------|
| `docs/specs/foundation/tier-3-2-repo-branching-and-cicd.md` | This artefact |

Pipeline YAML files, Terraform modules, Dockerfiles, and `docker-compose.yml` are implementation artefacts created during TODO 3.1 scaffolding and initial pipeline setup — not spec documents.

## 12. Recommended Next TODO

**TODO 3.4 — Testing Strategy.** The pipeline stages defined here reference unit, integration, instrumented, and e2e test stages. Testing strategy must specify tooling, coverage targets, and test data management to complete the pipeline configuration.
