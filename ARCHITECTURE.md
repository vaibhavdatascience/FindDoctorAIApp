# Architecture & Implementation Guide for Customers

## System Architecture

```
┌─────────────────────────────────────────────────────────────┐
│                    USER BROWSER                             │
│  ┌────────────────────────────────────────────────────────┐ │
│  │         Chat UI (Razor Pages)                          │ │
│  │  - Simple HTML + inline CSS                           │ │
│  │  - Vanilla JavaScript (no frameworks)                │ │
│  │  - Real-time chat messages                           │ │
│  └────────────────────────────────────────────────────────┘ │
└──────────────────────────┬─────────────────────────────────┘
                           │ REST API
                           ↓
┌─────────────────────────────────────────────────────────────┐
│          ASP.NET CORE 8 MINIMAL API BACKEND                 │
│  ┌────────────────────────────────────────────────────────┐ │
│  │  POST /api/chat                                        │ │
│  │  - Receives: user query + location                    │ │
│  │  - Returns: structured results                        │ │
│  └────────────────────────────────────────────────────────┘ │
│                           │                                  │
│  ┌────────────────────────▼────────────────────────────────┐ │
│  │  AGENT ORCHESTRATOR                                     │ │
│  │  ┌──────────────────────────────────────────────────┐  │ │
│  │  │ 1. Extract intent from natural language         │  │ │
│  │  │    using Azure OpenAI (GPT-4)                  │  │ │
│  │  │                                                  │  │ │
│  │  │ Input:  "female dermatologist for acne"        │  │ │
│  │  │ Output: {specialty: "Dermatology",              │  │ │
│  │  │         condition: "acne",                      │  │ │
│  │  │         gender: "female"}                       │  │ │
│  │  └──────────────────────────────────────────────────┘  │ │
│  │                           │                             │ │
│  │  ┌────────────────────────▼─────────────────────────┐  │ │
│  │  │ 2. Build search filters                         │  │ │
│  │  │    - Check for ambiguity                        │  │ │
│  │  │    - Ask clarifying questions if needed         │  │ │
│  │  └────────────────────────▬─────────────────────────┘  │ │
│  └────────────────────────────┼───────────────────────────┘ │
└─────────────────────────────────┼───────────────────────────┘
                                   ↓
┌─────────────────────────────────────────────────────────────┐
│         AZURE SEARCH SERVICE                                 │
│  ┌────────────────────────────────────────────────────────┐ │
│  │  HYBRID SEARCH WITH RANKING                            │ │
│  │                                                         │ │
│  │  Step 1: Keyword Search                               │ │
│  │  ────────────────────────────────                      │ │
│  │  Query: "Specialty:Dermatology AND Gender:Female"     │ │
│  │  Fields: Specialty, SpecialtiesCombined, City         │ │
│  │                                                         │ │
│  │  Step 2: Semantic Ranking                             │ │
│  │  ────────────────────────────────                      │ │
│  │  Uses Azure's semantic ranking algorithm               │ │
│  │  Understands: "skin doctor" ← matches → "Dermatology" │ │
│  │                                                         │ │
│  │  Step 3: Vector Search (Optional)                     │ │
│  │  ────────────────────────────────                      │ │
│  │  Embed condition: "acne treatment"                    │ │
│  │  Search ClinicalTerms field (semantic match)           │ │
│  │                                                         │ │
│  │  Step 4: Geo-Distance Ranking                         │ │
│  │  ────────────────────────────────                      │ │
│  │  User location: 41.5031, -81.6956 (Cleveland)         │ │
│  │  For each result:                                      │ │
│  │    - Calculate distance                                │ │
│  │    - Combine: 70% relevance + 30% distance            │ │
│  │    - Sort by combined score                            │ │
│  │                                                         │ │
│  │  Final Result: Top 10 ranked doctors                  │ │
│  └────────────────────────────────────────────────────────┘ │
└─────────────────────────────────────────────────────────────┘
                           ↑
                           │
              ┌────────────┴────────────┐
              │                         │
    ┌─────────▼──────────┐   ┌─────────▼──────────┐
    │  AZURE AI SEARCH   │   │  AZURE OPENAI      │
    │  (Index Storage)   │   │  (GPT-4, Embeddings)
    │                    │   │                    │
    │  - Doctor records  │   │  - Intent parsing  │
    │  - Indexed fields  │   │  - Embeddings      │
    │  - Ranking config  │   │  - Chat responses  │
    └────────────────────┘   └────────────────────┘
              ↑                         ↑
              │                         │
         Managed Identity         Managed Identity
         (DefaultAzureCredential - NO SECRETS IN CODE)
```

## Code Flow Walkthrough

### Example: "Find a female dermatologist near Cleveland treating acne"

**Frontend (Browser)**
```javascript
// User types and clicks "Send"
const response = await fetch('/api/chat', {
    method: 'POST',
    body: JSON.stringify({
        query: "Find a female dermatologist near Cleveland treating acne",
        userLatitude: 41.5031,
        userLongitude: -81.6956
    })
});
```

**Backend - Chat Endpoint** (Program.cs)
```csharp
app.MapPost("/api/chat", async (ChatSearchRequest request, AgentOrchestrator agent) =>
{
    var response = await agent.ProcessUserQueryAsync(
        request.Query,
        request.UserLatitude,
        request.UserLongitude
    );
    return response;
});
```

**Step 1: Intent Extraction** (AgentOrchestrator.cs)
```csharp
// Call Azure OpenAI to understand the query
var system = "Extract search filters from the user query";
var response = await openAiClient.GetChatCompletionsAsync(
    new ChatCompletionsOptions
    {
        Messages = { 
            new(ChatRole.System, system),
            new(ChatRole.User, userQuery)
        }
    }
);

// OpenAI returns JSON:
// {
//   "specialty": "Dermatology",
//   "condition": "acne",
//   "gender": "female",
//   "location": "Cleveland"
// }
```

**Step 2: Build Search Filters**
```csharp
var filters = new SearchFilters
{
    Specialty = "Dermatology",
    Condition = "acne",
    Gender = "female",
    Location = "Cleveland"
};
```

**Step 3: Execute Hybrid Search** (AzureSearchService.cs)
```csharp
// Build Azure AI Search query
var query = "(Specialty:Dermatology OR SpecialtiesCombined:Dermatology) " +
            "AND (ClinicalTerms:acne OR ClinicalAliases:acne) " +
            "AND Gender:female";

var searchOptions = new SearchOptions
{
    SemanticSearch = new SemanticSearch { ... },
    QueryType = SearchQueryType.Full,
    SearchMode = SearchMode.All  // AND logic
};

var results = await searchClient.SearchAsync<DoctorDocument>(
    query,
    searchOptions
);
```

**Step 4: Rank by Distance**
```csharp
foreach (var doc in results)
{
    var distance = CalculateDistance(
        userLat, userLon,      // 41.5031, -81.6956 (converted from "Cleveland")
        doc.Latitude,          // Doctor's latitude
        doc.Longitude          // Doctor's longitude
    );
    
    // Combine relevance + distance
    var rankingScore = (relevance * 0.7) + (inverseDistance * 0.3);
}

// Sort by ranking score and return top 10
```

**Step 5: Format Response**
```csharp
return new ChatSearchResponse
{
    UserQueryResponse = "Found 3 female dermatologists near Cleveland treating acne:",
    Results = new List<DoctorSearchResult>
    {
        new DoctorSearchResult
        {
            Doctor = new Doctor 
            { 
                FirstName = "Sarah",
                LastName = "Johnson",
                Specialty = "Dermatology",
                OfficeLocationName = "Cleveland Clinic - Main Campus",
                City = "Cleveland",
                State = "OH",
                Phone = "(216) 444-5555",
                Gender = "Female"
            },
            RelevanceScore = 3.8,
            DistanceMiles = 0.5,
            RankingScore = 0.92
        }
        // ... more results
    }
};
```

**Frontend: Display Results**
```javascript
// Response displayed as chat message:
"Found 3 female dermatologists near Cleveland treating acne:
 • Dr. Sarah Johnson - Dermatology (0.5 mi away)
 • Dr. Jennifer Martinez - Dermatology (2.3 mi away)
 • ..."
```

---

## Key Design Principles

### 1. **Semantic Understanding (No Synonym Maps)**
❌ Bad: Hard-code "skin doctor" → "Dermatology"
✅ Good: Let Azure OpenAI understand that "skin doctor" means "Dermatology"

```csharp
// Uses AI, not rule-based
var filters = await ExtractSearchFiltersAsync(userQuery);
// Automatically handles: dermatologist, skin doctor, skin specialist, etc.
```

### 2. **Managed Identity (No Secrets)**
❌ Bad: Store API keys in appsettings.json
✅ Good: Use DefaultAzureCredential

```csharp
var credential = new DefaultAzureCredential();
var openAiClient = new OpenAIClient(endpoint, credential);
var searchClient = new SearchClient(endpoint, credential);
// Works locally (az login) AND in Azure (auto-assigns identity)
```

### 3. **Hybrid Ranking (Relevance + Distance)**
```csharp
// NOT: keyword only
// NOT: distance only
// BUT: combined scoring

var rankingScore = (semanticRelevance * 0.7) +      // 70% relevance
                   (proximityScore * 0.3);           // 30% distance

// Result: Top matches are BOTH relevant AND close
```

### 4. **Clean Separation of Concerns**
```
Web Layer (Pages/Controllers)  → Should NOT know search logic
├─ Chat endpoint
└─ Razor Pages UI

Business Logic Layer (Services) → Should NOT know Azure details
├─ AgentOrchestrator
└─ AzureSearchService

Presentation Layer → Should NOT have business logic
├─ Razor Pages + template
└─ JavaScript UI
```

---

## Deployment Scenarios

### Local Development (Current Setup)
- Run with `dotnet run`
- Uses Managed Identity + `az login`
- Everything on localhost

### Production (Example)
```bash
# Deploy to Azure Container Apps
az containerapp create \
    --resource-group myRg \
    --name finddoctor \
    --image myacr.azurecr.io/finddoctor:latest \
    --system-assigned-identity \
    --environment-variables \
        Azure__Search__Endpoint=$SEARCH_ENDPOINT \
        Azure__OpenAI__Endpoint=$OPENAI_ENDPOINT

# Assign Managed Identity permissions
az role assignment create \
    --role "Search Service Contributor" \
    --assignee <identity-principal-id>
```

---

## Performance & Scalability Notes

### Azure AI Search Capabilities
- Handles **millions of records**
- Fast keyword + semantic search
- Geo-distance queries optimized
- Scales to 4M+ docs per index
- 99% SLA

### Optimization Strategies
1. **Indexing**: Define proper analyzers for specialty/condition fields
2. **Filtering**: Use facets for gender, location, online-scheduling
3. **Scoring profiles**: Define custom scoring if needed
4. **Caching**: Cache embedding results to reduce OpenAI calls

---

## Demo Script for Customers

### Opening
"This is an AI-powered doctor search chatbot. Instead of traditional filters, users just **talk naturally** to find doctors."

### Query 1: Basic Search
- User types: "Find a dermatologist"
- **Point out**: Application understood "dermatologist" = Dermatology specialty
- **Explain**: "No hard-coded synonym maps; Azure OpenAI interprets intent"

### Query 2: Semantic + Distance
- User types: "Female skin doctor near Cleveland"
- **Point out**: 
  - "Skin doctor" automatically matched to "Dermatology"
  - Results ranked by relevance AND distance
  - Only female providers returned
- **Explain**: "70% relevance + 30% proximity = balanced ranking"

### Query 3: Condition-Based
- User types: "Doctor for acne treatment"
- **Point out**: Results include dermatologists with acne expertise
- **Explain**: "Searches clinical terms, not just specialty names"

### Technical Highlights
- **Code**: Show the small, clean codebase
  - `Program.cs` - 50 lines (wiring)
  - `AgentOrchestrator.cs` - 100 lines (intent + orchestration)
  - `AzureSearchService.cs` - 150 lines (search + ranking)
- **Security**: "No API keys in code; Managed Identity handles auth"
- **Scalability**: "Backed by Azure AI Search (handles millions)"

---

## Customization Ideas

- [ ] **Save favorite doctors** - Add bookmarking with Azure Cosmos DB
- [ ] **Insurance filtering** - Add accepted insurance networks
- [ ] **Language support** - Multi-language chat UI
- [ ] **Appointment integration** - Link to booking system
- [ ] **Reviews/ratings** - Show patient feedback
- [ ] **Availability** - Real-time appointment slots
