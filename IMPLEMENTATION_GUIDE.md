# FindDoctor Implementation Guide

This guide explains how the application works end-to-end, how data flows through the system, and where each responsibility lives in code. It is intended for engineers who want to understand, run, extend, or troubleshoot the project.

## 0. Run locally

Prerequisites:
- .NET SDK 8.x installed.
- Network access to configured Azure Search/OpenAI/Storage endpoints (or local fallback data for ingestion path).

From repository root:

```powershell
dotnet build .\src\FindDoctor.Web\FindDoctor.Web.csproj
dotnet run --project .\src\FindDoctor.Web\FindDoctor.Web.csproj
```

App URL:
- http://localhost:5000

If port 5000 is already in use:

```powershell
Get-Process dotnet -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet run --project .\src\FindDoctor.Web\FindDoctor.Web.csproj
```

## 1. Project overview

FindDoctor is an ASP.NET Core 8 Razor Pages application that supports natural-language provider search.

Core capabilities:
- Parse user intent from free-text queries.
- Search indexed provider data in Azure AI Search.
- Pre-filter by geolocation radius when location anchor is available.
- Rank results by relevance and distance.
- Support location-aware behavior:
- near me uses browser coordinates.
- near ZIP/city/address resolves and ranks near that explicit location.
- Refresh index data from blob storage with local fallback.

## 2. Runtime architecture

Logical components:
- UI layer: Razor page + browser JavaScript.
- API layer: minimal endpoints in Program.cs.
- Orchestration layer: intent extraction and query flow control.
- Search layer: query composition, tolerant fallback, filtering, ranking.
- Ingestion layer: JSON parsing and index upsert.
- Index management layer: schema creation/update.

External services:
- Azure AI Search.
- Azure OpenAI (intent extraction only).
- Azure Blob Storage.

## 3. Code map

### Application startup and endpoint wiring
- [src/FindDoctor.Web/Program.cs](src/FindDoctor.Web/Program.cs)
- Loads configuration from appsettings and environment variables.
- Registers Azure/Search/OpenAI/blob clients and app services in DI.
- Runs startup bootstrap:
- Ensure index exists.
- Ingest from blob.
- Fall back to local file if blob ingestion fails.
- Exposes endpoints:
- GET /health
- POST /api/chat
- POST /api/ingest

### API and domain models
- [src/FindDoctor.Web/Models/Doctor.cs](src/FindDoctor.Web/Models/Doctor.cs)
- Doctor: response model for UI/API.
- SearchFilters: structured intent extracted from query.
- DoctorSearchResult: doctor plus relevance/distance/ranking metadata.
- ChatSearchRequest/ChatSearchResponse: endpoint contracts.

### Intent orchestration
- [src/FindDoctor.Web/Services/AgentOrchestrator.cs](src/FindDoctor.Web/Services/AgentOrchestrator.cs)
- Main workflow entry: ProcessUserQueryAsync.
- Extracts filters using OpenAI JSON output.
- Uses fallback parser if extraction fails.
- Applies normalization/guardrails.
- Resolves effective coordinates based on near me vs explicit location.
- Invokes search service.
- Formats assistant summary text.

### Search execution and ranking
- [src/FindDoctor.Web/Services/AzureSearchService.cs](src/FindDoctor.Web/Services/AzureSearchService.cs)
- Resolves explicit location text to coordinates from indexed data.
- Builds strict Lucene query from filters.
- Builds OData geo filter when coordinates exist:
- geo.distance(GeoLocation, geography'POINT(lon lat)') le radiusKm
- Uses default radius 50 miles (80.4672 km) when no explicit radius is provided.
- Executes semantic-enabled search.
- Applies condition relevance guard.
- Retries with tolerant matching when strict query returns zero.
- Computes distance and ranking score.
- Sorts and returns top 5:
- with coordinates: nearest first.
- without coordinates: UH provider preference then ranking.

### Search index schema
- [src/FindDoctor.Web/Utilities/SearchIndexCreationService.cs](src/FindDoctor.Web/Utilities/SearchIndexCreationService.cs)
- Idempotent index provisioning at startup.
- Creates full schema when index is missing.
- Adds UHProvider and GeoLocation fields if existing index is older.
- Configures semantic search profile default.

### Data ingestion
- [src/FindDoctor.Web/Utilities/DataIngestService.cs](src/FindDoctor.Web/Utilities/DataIngestService.cs)
- Ingest from blob or local file.
- Supports two JSON shapes:
- wrapped object: { "doctors": [...] }
- direct array: [ ... ]
- Supports flattened indexing from nested provider-location JSON.
- Emits one index document per location (Option 1 flattening).
- Builds stable location-level keys so each provider location can be independently filtered and ranked.
- Populates GeoLocation for distance filtering.
- Includes recovery parser for malformed records.
- Converts source records to index documents.
- Uploads in batches using MergeOrUpload.

### UI and client request flow
- [src/FindDoctor.Web/Pages/Index.cshtml](src/FindDoctor.Web/Pages/Index.cshtml)
- Renders chat UI.
- Captures optional browser geolocation.
- Sends POST /api/chat.
- Renders provider cards with distance when available.
- [src/FindDoctor.Web/Pages/Index.cshtml.cs](src/FindDoctor.Web/Pages/Index.cshtml.cs)
- Thin page model with request logging.

### Build and runtime config
- [src/FindDoctor.Web/FindDoctor.Web.csproj](src/FindDoctor.Web/FindDoctor.Web.csproj)
- Package references.
- Copies local sample data into output on build.
- [src/FindDoctor.Web/appsettings.json](src/FindDoctor.Web/appsettings.json)
- Search, OpenAI, and storage settings.

## 4. End-to-end execution flow

### 4.1 Startup flow
1. App loads configuration and registers DI services.
2. Startup scope calls EnsureIndexExistsAsync.
3. App attempts IngestFromBlobAsync.
4. If blob ingestion fails, app ingests local sample data.
5. Web server starts and serves UI/API endpoints.

### 4.2 Query flow for POST /api/chat
1. Browser sends query and optional coordinates.
2. Endpoint forwards request to AgentOrchestrator.
3. Orchestrator extracts and normalizes filters.
4. If ambiguous, returns clarification message.
5. Orchestrator resolves effective location source.
6. Search service executes strict query.
7. If needed, search service executes tolerant retry.
8. Results are ranked/sorted and top 5 returned.
9. Orchestrator formats summary text and response payload.
10. UI renders result cards.

## 5. Location behavior

Decision rules:
- Query contains near me/nearby/around me:
- use browser coordinates.
- Query contains explicit ZIP/city/address:
- resolve coordinates from indexed provider locations.
- Explicit location cannot be resolved:
- do not silently fall back to browser location.

Why this matters:
- It avoids misleading distance output when the user explicitly requested a different location anchor.

Radius behavior:
- When a location anchor exists (near me or explicit location), search is constrained to a 50-mile radius before ranking.
- If no providers are found within the radius, response message is:
- No provider available near you. Modify the search and search again.

## 6. Ranking and sorting behavior

If coordinates are available:
- Search results are already constrained to providers within 50 miles.
- Primary sort: matched clinical-term preference level descending.
- Secondary sort: distance ascending.
- Tertiary sort: ranking score descending.
- Result size: top 5.

If coordinates are unavailable:
- Primary sort: UH provider preference.
- Secondary sort: matched clinical-term preference level descending.
- Tertiary sort: ranking score descending.
- Result size: top 5.

Ranking score:
- normalizedRelevance = min(rawScore / 4.0, 1.0)
- distanceScore = max(0, 1 - distanceMiles / 50)
- baseCombinedRanking = 0.7 * normalizedRelevance + 0.3 * distanceScore
- matchedClinicalPreferenceLevel is derived from `ClinicalPreferenceMap` for the matched condition terms/aliases.
- preferenceBoost = min(matchedClinicalPreferenceLevel, 10) / 10.0
- combinedRanking = baseCombinedRanking + preferenceBoost

Clinical preference mapping behavior:
- Ingestion accepts both legacy `ClinicalTerms` string arrays and object-based terms with aliases and `PreferenceLevel`.
- Each provider document stores a normalized `ClinicalPreferenceMap` containing term/alias to highest preference level.
- Query-time matching computes the highest matching preference level and uses it in sorting and score boost.

## 7. Index schema design notes

SearchableField is used for free-text matching:
- FirstName, LastName, Specialty, SpecialtiesCombined, ClinicalTerms, ClinicalAliases, OfficeLocationName, City, Zip, Languages.

SimpleField is used for exact semantics:
- DoctorId, ProviderType, UHProvider, Gender, State, Phone, OffersOnlineScheduling, Latitude, Longitude.

Filterable fields support deterministic constraints:
- UHProvider, Gender, City, State, Zip, OffersOnlineScheduling, Latitude, Longitude, GeoLocation, and key fields.

Sortable fields are limited to stable string fields:
- FirstName and LastName.

Semantic search profile default:
- Title: Specialty.
- Content: SpecialtiesCombined, ClinicalTerms.
- Keywords: ClinicalAliases, OfficeLocationName.

## 8. API contracts

### POST /api/chat
Request:
- query: string
- userLatitude: number|null
- userLongitude: number|null

Response:
- userQueryResponse: string
- results: DoctorSearchResult[]
- clarifyingQuestion: string
- requiresClarification: boolean

### POST /api/ingest
Request:
- blobFileName: string

Response:
- success: boolean
- message: string

### GET /health
Response:
- status payload indicating service health.

## 9. Pseudocode

### 9.1 Orchestration

```text
function ProcessUserQuery(query, userLat, userLon):
    filters = ExtractWithOpenAI(query) or BuildFallbackFilters(query)
    filters = NormalizeFilters(query, filters)

    if filters.SearchIsAmbiguous:
        return ClarificationResponse(filters)

    (effectiveLat, effectiveLon) = ResolveEffectiveCoordinates(
        query, filters, userLat, userLon)

    results = HybridSearch(filters, query, effectiveLat, effectiveLon)

    return BuildChatResponse(results, filters)
```

### 9.2 Effective coordinates

```text
function ResolveEffectiveCoordinates(query, filters, browserLat, browserLon):
    nearMe = IsNearMeQuery(query, filters.Location)
    explicitLocation = TryExtractExplicitLocation(query, filters.Location)

    if nearMe and browser coordinates exist:
        return browser coordinates

    if explicitLocation exists:
        resolved = ResolveCoordinatesFromLocation(explicitLocation)
        if resolved exists:
            update filters with resolved label and coordinates
            return resolved coordinates
        else:
            return (null, null)

    return browser coordinates
```

### 9.3 Hybrid search

```text
function HybridSearch(filters, query, lat, lon):
    options = BuildSearchOptions(filters, lat, lon, radiusMiles=50)

    strictQuery = BuildSearchQuery(filters, tolerant=false)
    results = Execute(strictQuery, options)
    results = ApplyConditionGuard(results, filters)

    if results empty and strictQuery != "*":
        tolerantQuery = BuildSearchQuery(filters, tolerant=true)
        results = Execute(tolerantQuery, options)
        results = ApplyConditionGuard(results, filters)

    for each result:
        result.DistanceMiles = CalculateDistance(lat, lon, docLat, docLon)
        result.RankingScore = CalculateRankingScore(relevance, distance)

    if coordinates exist:
        sort by distance asc, ranking desc
    else:
        sort by UHProvider desc, ranking desc

    return top 5
```

### 9.4 Ingestion

```text
function IngestJson(json):
    doctors = ParseWrappedOrArray(json)
    if parse fails:
        doctors = RecoverByLineParsing(json)

    if doctors empty:
        throw

    // Flatten nested source into one searchable document per location.
    flattenedDocs = FlattenToLocationDocuments(doctors)

    for batch in flattenedDocs.chunk(100):
        docs = ConvertToSearchDocuments(batch)
        IndexDocuments.MergeOrUpload(docs)
```

## 10. Troubleshooting map

Intent extraction issues:
- [src/FindDoctor.Web/Services/AgentOrchestrator.cs](src/FindDoctor.Web/Services/AgentOrchestrator.cs)
- ExtractSearchFiltersAsync, NormalizeFilters.

Location resolution issues:
- [src/FindDoctor.Web/Services/AgentOrchestrator.cs](src/FindDoctor.Web/Services/AgentOrchestrator.cs)
- ResolveEffectiveCoordinatesAsync.
- [src/FindDoctor.Web/Services/AzureSearchService.cs](src/FindDoctor.Web/Services/AzureSearchService.cs)
- ResolveCoordinatesFromLocationAsync.

Ranking/sorting issues:
- [src/FindDoctor.Web/Services/AzureSearchService.cs](src/FindDoctor.Web/Services/AzureSearchService.cs)
- HybridSearchAsync, CalculateRankingScore, CalculateDistance.

Ingestion/index refresh issues:
- [src/FindDoctor.Web/Utilities/DataIngestService.cs](src/FindDoctor.Web/Utilities/DataIngestService.cs)
- [src/FindDoctor.Web/Program.cs](src/FindDoctor.Web/Program.cs)

Schema mismatch issues:
- [src/FindDoctor.Web/Utilities/SearchIndexCreationService.cs](src/FindDoctor.Web/Utilities/SearchIndexCreationService.cs)

UI request payload issues:
- [src/FindDoctor.Web/Pages/Index.cshtml](src/FindDoctor.Web/Pages/Index.cshtml)

## 11. Operational notes

- Move secrets out of appsettings for production deployments.
- Local blob authorization failures are expected without proper identity/RBAC; fallback ingestion keeps the app functional.
- Semantic configuration name in search options must match the index definition default.
- Keep local sample data copied to output for fallback ingestion reliability.
- If migrating from pre-flattening data to location-level flattened keys/GeoLocation, run a one-time index rebuild to avoid stale legacy documents.
