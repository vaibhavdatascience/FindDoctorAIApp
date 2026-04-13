# FindDoctor AI Assistant - Customer Implementation Guide

## 1. Executive Summary

This application is an AI-assisted doctor discovery web solution built on Azure services. It enables end users to search healthcare providers using natural language and supports three major query types:

- Specialty-based search (for example: dermatologist, cardiologist)
- Condition-based search (for example: acne, brain tumor)
- Doctor name-based search (for example: Aziza Wahby)

The solution combines:

- Azure AI Search for retrieval and ranking
- Azure Blob Storage for source data ingestion
- Azure OpenAI (deployed via Azure AI Foundry) for intent extraction
- ASP.NET Core (Razor Pages + API) as the application host and orchestration layer

The implementation prioritizes reliability, explainability, and practical production behavior over rigid rule-only matching.

---

## 2. Business Goals and Functional Scope

### 2.1 Goals

- Enable patients to discover relevant providers through conversational search.
- Reduce dependency on static keyword-only search.
- Support continuous updates from provider source data in Blob Storage.
- Keep architecture simple enough for enterprise support and auditability.

### 2.2 Supported User Intents

- "Find a dermatologist near me"
- "Doctor for acne treatment"
- "Aziza Wahby"
- "Female cardiologist with online scheduling"

### 2.3 Non-Goals

- Diagnosis or clinical decision support.
- Medical advice generation.
- Full referral workflow management.

---

## 3. High-Level Architecture

1. User interacts with web chat UI.
2. UI calls backend endpoint `/api/chat`.
3. Orchestrator extracts structured filters from natural language.
4. Search service builds Azure AI Search query and retrieves candidates.
5. Results are post-processed, ranked, and returned to UI.
6. On startup (or via API), source doctor data is ingested from Blob Storage into the search index.

Core application startup, dependency wiring, and endpoints are implemented in:

- `src/FindDoctor.Web/Program.cs`

---

## 4. Azure Services Used

### 4.1 Azure AI Search

Purpose:

- Primary retrieval layer for provider discovery.
- Supports searchable fields, filtering, semantic configuration, and ranking.

### 4.2 Azure Blob Storage

Purpose:

- Source-of-truth location for provider data file (`doctors.json`).
- Ingestion service reads blob and upserts into index.

### 4.3 Azure OpenAI (Foundry Deployment)

Purpose:

- Converts free-form user text into structured intent filters.

Runtime model settings are configured in appsettings and include endpoint + deployment name.

### 4.4 ASP.NET Core Application Host

Purpose:

- Hosts UI + API.
- Coordinates indexing, ingestion, orchestration, and response formatting.

---

## 5. Data Ingestion and Indexing Design

Implementation:

- `src/FindDoctor.Web/Utilities/DataIngestService.cs`
- `src/FindDoctor.Web/Utilities/SearchIndexCreationService.cs`

### 5.1 Index Lifecycle Strategy

- On startup, the app checks whether the index exists.
- If missing, it creates the index with predefined schema and semantic configuration.
- If present, it reuses existing index.

This behavior makes environment startup deterministic and reduces manual provisioning steps.

### 5.2 Index Schema Choices

Key schema decisions:

- `DoctorId` is configured as key field.
- Name fields (`FirstName`, `LastName`) are searchable and sortable.
- Clinical fields (`ClinicalTerms`, `ClinicalAliases`) are searchable.
- Location and scheduling fields are filterable (`City`, `State`, `OffersOnlineScheduling`, `Latitude`, `Longitude`).
- `Languages` is indexed as a searchable collection.

Semantic configuration:

- Title field: specialty
- Content fields: specialties + clinical terms
- Keywords fields: aliases + office location

This supports both lexical and semantic relevance.

### 5.3 Source JSON Normalization

The ingestion pipeline handles real-world JSON variability:

- Supports direct JSON array source format.
- Includes fallback line-by-line parsing to recover records when individual records are malformed.
- Uses custom converter for list fields to handle:
  - null values
  - arrays
  - pipe-delimited strings

This was critical because production-like data contained mixed list representations.

### 5.4 Upsert Strategy

- Uses `MergeOrUpload` in batches of 100 documents.
- Logs per-batch success and failures.
- Enables incremental refresh behavior without full reindex requirement.

Why this matters:

- Reduces operational disruption for repeated syncs.
- Supports evolving provider data with idempotent behavior.

---

## 6. Query and Retrieval Design

Implementation:

- `src/FindDoctor.Web/Services/AzureSearchService.cs`

### 6.1 Query Construction

Query generation combines available filters:

- Doctor name search against `FirstName` and `LastName`
- Specialty search against `Specialty` and `SpecialtiesCombined`
- Condition search against `ClinicalTerms` and `ClinicalAliases`
- Optional filter clauses (gender, online scheduling)

### 6.2 Typo and Variant Tolerance

Without requiring synonym maps, tolerant fallback is implemented using:

- Fuzzy operator (`~`)
- Prefix wildcard matching (`*`)
- Lightweight token stemming

If strict query returns no results, system retries with tolerant query.

### 6.3 Condition Relevance Guardrails

Condition-only queries apply additional post-filter checks to prevent clinically irrelevant matches:

- Phrase match preference
- Token-level whole-word constraints on clinical fields

This avoids broad false positives from noisy term overlap.

### 6.4 Name Search Support

Doctor name support is first-class:

- Exact or near name token matching for first/last names
- Works with full names and partial tokens

### 6.5 Ranking

Final ranking combines:

- Search relevance score
- Optional location proximity score when user coordinates are available

Result set is normalized and trimmed to top results for UI presentation.

---

## 7. Orchestration and Intent Extraction

Implementation:

- `src/FindDoctor.Web/Services/AgentOrchestrator.cs`

### 7.1 Role of Orchestrator

The orchestrator is the conversational control layer:

1. Extracts structured intent from user text.
2. Normalizes intent to avoid over-inference errors.
3. Executes retrieval through search service.
4. Formats user-friendly response text and cards.

### 7.2 Extracted Intent Schema

Current extraction schema includes:

- `doctorName`
- `specialty`
- `condition`
- `location`
- `gender`
- `onlineScheduling`
- `isAmbiguous`

### 7.3 Reliability Behavior

- If OpenAI extraction fails, fallback parsing path is used.
- Current fallback is deliberately minimal and generic (no hardcoded specialty map), per test preference.
- Clarifying questions are generated only when intent is truly ambiguous.

### 7.4 Anti-Hallucination/Over-Inference Controls

Normalization logic prevents unsupported specialty assumptions:

- Specialty is trusted only when query context supports it.
- Condition-driven queries are preserved as condition queries when specialty confidence is low.

This was introduced to prevent incorrect mapping (for example: unrelated conditions surfacing dermatology-only results).

---

## 8. User Interface and API Surface

### 8.1 Web UI

Implementation:

- `src/FindDoctor.Web/Pages/Index.cshtml`

Features:

- Chat-style interaction
- Async request/response rendering
- Doctor result cards
- Optional geolocation capture for proximity ranking

### 8.2 API Endpoints

Defined in:

- `src/FindDoctor.Web/Program.cs`

Endpoints:

- `POST /api/chat` - natural language query endpoint
- `POST /api/ingest` - explicit data ingestion trigger
- `GET /health` - health status

---

## 9. Security and Identity Considerations

### 9.1 Authentication Model

- Managed identity-style credential flow (`DefaultAzureCredential`) is used for runtime Azure service auth where applicable.
- Search indexing operations use admin-key client credential in current implementation.

### 9.2 Data and Secret Handling Recommendations

For production hardening:

- Move all secrets to Key Vault.
- Remove direct secrets from application settings.
- Apply least-privilege RBAC for search and storage operations.
- Restrict CORS policy from `AllowAll` to approved origins.

### 9.3 Customer Environment Configuration (Required)

Before running this solution in your own environment, update the placeholders in:

- `src/FindDoctor.Web/appsettings.json`

Required values and where they are used:

1. `Azure:Search:Endpoint`
- Example format: `https://<your-search-service>.search.windows.net`
- Used by: `SearchClient` and `SearchIndexClient` in `src/FindDoctor.Web/Program.cs`

2. `Azure:Search:AdminKey`
- Value: Azure AI Search admin key for your search service
- Used by: indexing and query clients in `src/FindDoctor.Web/Program.cs`

3. `Azure:Search:IndexName`
- Value: index name to use (default `doctors`)
- Used by: search client routing and startup index creation

4. `Azure:OpenAI:Endpoint`
- Example format: `https://<your-aoai-or-foundry-resource>.cognitiveservices.azure.com`
- Used by: `AzureOpenAIClient` initialization in `src/FindDoctor.Web/Program.cs`

5. `Azure:OpenAI:ModelDeploymentName`
- Value: deployment name in your Azure OpenAI / Foundry resource (for example `gpt-4.1`)
- Used by: intent extraction in `src/FindDoctor.Web/Services/AgentOrchestrator.cs`

6. `Azure:Storage:ConnectionString`
- Value: storage account connection string for the account holding provider data
- Used by: blob client creation in `src/FindDoctor.Web/Program.cs`

7. `Azure:Storage:ContainerName`
- Value: blob container containing doctor source JSON
- Used by: ingestion service startup flow and `/api/ingest`

8. `Azure:Storage:BlobName`
- Value: doctor source filename (for example `doctors.json`)
- Used by: startup sync and manual ingestion endpoint

Recommended secure alternatives:

- Store `AdminKey` and `ConnectionString` in Azure Key Vault.
- Use environment variables for deployment environments and keep `appsettings.json` placeholder-only.

Suggested environment variable mappings:

- `Azure__Search__Endpoint`
- `Azure__Search__AdminKey`
- `Azure__Search__IndexName`
- `Azure__OpenAI__Endpoint`
- `Azure__OpenAI__ModelDeploymentName`
- `Azure__Storage__ConnectionString`
- `Azure__Storage__ContainerName`
- `Azure__Storage__BlobName`

Validation checklist before sharing to production users:

- App starts and `/health` returns `healthy`
- Startup logs show successful index check/creation
- Startup logs show data sync completion
- `/api/chat` returns results for at least one specialty query and one name query

---

## 10. Operational Runbook

### 10.1 Startup Behavior

On startup:

1. Ensure index exists.
2. Sync data from blob to index.
3. Start UI/API host.

### 10.2 Manual Re-Ingestion

Use `POST /api/ingest` with blob filename to force refresh.

### 10.3 Monitoring Signals

Track:

- Ingestion batch success/failure counts
- Search query text and result counts
- OpenAI extraction responses/errors
- API latency and 5xx rates

### 10.4 Known Data/Schema Caveats Encountered

- Source JSON used PascalCase properties.
- Some list fields were pipe-delimited strings, not arrays.
- Some boolean fields were nullable.

The current ingestion logic explicitly handles all of the above.

---

## 11. Validation Scenarios Executed

Representative tested scenarios:

- Specialty query: `dermatologist` -> returns dermatology providers.
- Condition query: `brain tumor doctor` -> no irrelevant dermatology false positives.
- Name query: `Aziza Wahby` -> returns direct provider match.

These tests validated intent extraction, query generation, relevance guardrails, and response formatting.

---

## 12. Design Decisions and Rationale

### Decision: Keep retrieval deterministic and auditable

Rationale:

- Search query construction remains explicit and inspectable.
- AI is used for intent extraction, not direct answer generation over uncontrolled context.

### Decision: Use incremental upsert ingestion

Rationale:

- Supports repeatable sync operations.
- Avoids expensive and risky full reindex cycles for small updates.

### Decision: Add tolerant lexical fallback instead of large synonym maps

Rationale:

- Avoids unbounded synonym maintenance burden.
- Handles spelling variants and minor query noise with lower operational overhead.

### Decision: Add post-retrieval condition relevance filtering

Rationale:

- Prevents clinical false positives when condition terms are broad/noisy.

---

## 13. Future Enhancement Recommendations

1. Introduce specialty taxonomy service (externalized, versioned) if business requires strict specialty governance.
2. Add location-aware filtering/radius constraints at query level (not only ranking).
3. Add conversation memory for follow-up queries (for example: "same doctor but near Cleveland").
4. Add confidence scores and "why this result" explanations for trust.
5. Integrate Application Insights dashboards for ingestion/query quality KPIs.
6. Add automated regression tests for intent extraction and retrieval scenarios.

---

## 14. Hand-off Summary for Customer Teams

The application is production-capable as a reference implementation and demonstrates:

- Robust Azure AI Search indexing and retrieval pipeline
- Real-world JSON ingestion resilience
- AI-assisted intent extraction with deterministic retrieval controls
- Flexible support for specialty, condition, and doctor-name search modes

This design provides a strong foundation for enterprise-scale provider search solutions and can be adapted to additional specialties, geographies, and clinical taxonomies.
