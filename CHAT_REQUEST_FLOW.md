# Chat Request Flow Chart

This diagram shows the function-level call flow across files from user chat input to rendered response.

```mermaid
flowchart TD
    A["UI: sendMessage
    Index.cshtml"] --> B["HTTP POST /api/chat
    Program.cs MapPost"]
    B --> C["ChatEndpoint
    Program.cs"]
    C --> D["AgentOrchestrator.ProcessUserQueryAsync
    AgentOrchestrator.cs"]

    D --> E{Extract filters}
    E -->|Primary| F["ExtractSearchFiltersAsync"]
    E -->|OpenAI failure| G["BuildFallbackFilters"]

    F --> H["NormalizeFilters"]
    G --> H

    H --> I{SearchIsAmbiguous?}
    I -->|Yes| J["BuildClarifyingQuestion"]
    J --> K["Return ChatSearchResponse
    RequiresClarification = true"]

    I -->|No| L["ResolveEffectiveCoordinatesAsync
    AgentOrchestrator.cs"]

    L --> M["IsNearMeQuery"]
    L --> N["TryExtractExplicitLocation"]
    N --> O{Explicit location found?}
    O -->|Yes| P["ResolveCoordinatesFromLocationAsync
    AzureSearchService.cs"]
    O -->|No| Q["Use browser coords if available"]

    P --> R["HybridSearchAsync
    AzureSearchService.cs"]
    Q --> R

    R --> S["BuildSearchOptions
    + geo.distance filter when coords exist
    + default radius 50 miles"]
    R --> T["BuildSearchQuery - strict"]
    R --> U["SearchClient.SearchAsync"]
    U --> V["IsRelevantConditionMatch"]
    V --> W["MapToDoctorModel"]
    V --> X["CalculateDistance - Haversine"]
    V --> Y["CalculateRankingScore - 70/30 blend"]
    V --> Y1["GetMatchedClinicalPreferenceLevel"]
    Y1 --> Y2["CalculateClinicalPreferenceBoost"]
    Y2 --> Y3["Final RankingScore = base + boost"]

    R --> Z{No results?}
    Z -->|Yes| AA["BuildSearchQuery - tolerant"]
    AA --> AB["Retry SearchAsync"]
    AB --> V
    Z -->|No| AC["Skip tolerant retry"]

    W --> AD["Sort + Top 5
    with coords: clinical preference desc, distance asc, ranking desc
    without coords: UH first, clinical preference desc, ranking desc"]
    X --> AD
    Y --> AD
    Y3 --> AD
    AC --> AD

    AD --> AE["Return List of DoctorSearchResult"]
    AE --> AF["FormatResultsMessage
    AgentOrchestrator.cs"]
    AF --> AK{No results and location anchor?}
    AK -->|Yes| AL["Return: No provider available near you.
    Modify the search and search again."]
    AK -->|No| AG["Return ChatSearchResponse"]
    AG --> AH["HTTP JSON response /api/chat"]
    AH --> AI["UI: addAssistantMessage
    Index.cshtml"]
    AI --> AJ["Doctor cards rendered to user"]

    K --> AH
    AL --> AH
```

## Notes

- Entry point for backend chat processing is `ChatEndpoint` in [src/FindDoctor.Web/Program.cs](src/FindDoctor.Web/Program.cs).
- Core orchestration is in `ProcessUserQueryAsync` in [src/FindDoctor.Web/Services/AgentOrchestrator.cs](src/FindDoctor.Web/Services/AgentOrchestrator.cs).
- Core search execution/ranking is in `HybridSearchAsync` in [src/FindDoctor.Web/Services/AzureSearchService.cs](src/FindDoctor.Web/Services/AzureSearchService.cs).
- Geo pre-filtering is applied before ranking when a location anchor is present, with a default 50-mile radius.
- Clinical-term preference level is computed from `ClinicalPreferenceMap` and applied both as a sort key and ranking boost.
- The empty-result response for location-anchored searches is an explicit no-provider message.
- Browser request and rendering are in [src/FindDoctor.Web/Pages/Index.cshtml](src/FindDoctor.Web/Pages/Index.cshtml).
