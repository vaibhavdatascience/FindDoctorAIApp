# Find a Doctor - AI Chat Assistant

A simple, demo-friendly C# .NET 8 web application that implements doctor discovery through conversational AI.

## Architecture Overview

```
User Chat UI (Razor Pages)
        ↓
ASP.NET Core Minimal API
        ↓
AgentOrchestrator (uses Azure OpenAI to understand intent)
        ↓
AzureSearchService (hybrid/semantic/vector search)
        ↓
Azure AI Search + Azure OpenAI (managed identity auth)
```

## Key Features

✅ **Natural Language Understanding** - "dermatologist for acne near me" automatically understood
✅ **Hybrid Search** - Keyword + semantic ranking from Azure AI Search
✅ **Geo-Distance Ranking** - Results ranked by relevance AND proximity
✅ **Zero Secrets** - Uses Managed Identity for all Azure auth
✅ **Demo-Friendly** - Clean, simple code with clear separation of concerns
✅ **Responsive Chat UI** - Modern gradient design, real-time results

## Prerequisites

- .NET 8 SDK
- Azure Subscription with:
  - Azure AI Search service
  - Azure OpenAI service (GPT-4 deployed)
  - Embedding model deployed (text-embedding-ada-002 or similar)
  - **Azure Storage Account** with doctor data JSON in a blob container
- Logged in to Azure CLI: `az login`

## Setup Instructions

### Step 1-2: Configure Your Azure Resources & Create Index

See [SETUP.md](SETUP.md) for detailed instructions on:
- Configuring `appsettings.json` with your Azure resource endpoints
- Creating the Azure AI Search index
- **Uploading doctor data to Azure Blob Storage**

### Step 3: Ingest Doctor Data from Blob Storage

Update `appsettings.json` with your storage account details:

```json
{
  "Azure": {
    "Storage": {
      "AccountName": "mystorageaccount",
      "ContainerName": "doctors",
      "BlobName": "doctors.json"
    }
  }
}
```

See [BLOB_STORAGE_GUIDE.md](BLOB_STORAGE_GUIDE.md) for step-by-step blob storage setup and authentication configuration.

### Step 4: Run the Application

```bash
# Navigate to project directory
cd src\FindDoctor.Web

# Restore packages
dotnet restore

# Run (will start on https://localhost:5001)
dotnet run

# Or with hot reload
dotnet watch run
```

Access the chat at: **https://localhost:5001**

### Step 5: Load Data from Blob

Once the app is running, call the ingest endpoint:

```bash
curl -X POST https://localhost:5001/api/ingest \
  -H "Content-Type: application/json" \
  -d '{"blobFileName": "doctors.json"}'
```

Expected response:
```json
{
  "success": true,
  "message": "Successfully ingested 10 documents from blob storage to search index"
}
```

## Code Structure

```
src/FindDoctor.Web/
├── Program.cs                  ← Service registration & endpoints (/api/chat, /api/ingest)
├── appsettings.json            ← Azure resource configuration
├── Pages/
│   ├── Index.cshtml            ← Chat UI (Razor Pages + JavaScript)
│   └── Index.cshtml.cs         ← Page model
├── Models/
│   └── Doctor.cs               ← Domain models (Doctor, SearchFilters, etc.)
├── Services/
│   ├── AgentOrchestrator.cs    ← Intent extraction (Azure OpenAI)
│   ├── AzureSearchService.cs   ← Hybrid/semantic/vector search
│   └── DataIngestService.cs    ← Load doctor data from blob storage to search index
└── FindDoctor.Web.csproj       ← NuGet packages
```

## How It Works

### Example Query: "Find a dermatologist near Cleveland"

1. **User sends message** via chat UI
2. **AgentOrchestrator.ProcessUserQueryAsync()**
   - Calls Azure OpenAI: "Extract search filters from this query"
   - Returns: `{specialty: "Dermatology", location: "Cleveland"}`
3. **AzureSearchService.HybridSearchAsync()**
   - Builds Azure AI Search query: `Specialty:Dermatology OR SpecialtiesCombined:Dermatology`
   - Executes hybrid search (keyword + semantic ranking)
   - Geocodes "Cleveland" → latitude/longitude
   - Calculates distance for each result
   - Ranks by: 70% relevance + 30% distance
4. **Results returned** as formatted chat message with doctor cards

### Key Design Decisions

| Decision | Why |
|---|---|
| Use `DefaultAzureCredential` | No secrets in code; works locally + Azure |
| `AzureSearchService` separate | Encapsulates all search logic; easy to test |
| `AgentOrchestrator` wrapper | Orchestrates the flow; can add multi-turn history later |
| Minimal APIs | Fast, lightweight; no MVC overhead |
| Razor Pages for UI | Simple server-rendered pages; no SPA complexity |
| Inline CSS/JavaScript | Works standalone; no build tools needed; easy to demo |

## Testing the Chat

### Sample Queries to Try

```
# Basic specialty search
"Find a cardiologist"
"I need a heart doctor"

# Specialty + condition
"Female dermatologist for acne"
"Skin doctor who treats rashes"

# Location-based
"Dermatologist near Cleveland"
"Doctor within 25 miles"
"Find me a doctor near me"

# With preferences
"Cardiologist with online scheduling"
"Female doctor who speaks Spanish"
"Women's health specialist with telehealth"

# Ambiguous (should ask for clarification)
"Find me a doctor"
"Looking for healthcare"
```

## Debugging

### Check Logs

```bash
# Run with debug logging
dotnet run --configuration Debug

# Look for:
# - "Executing search: ..." → Search query being built
# - "OpenAI extraction response: ..." → Filters extracted
# - "Search returned X results" → Results count
```

### Troubleshooting

**Error: "Azure:Search:Endpoint not configured"**
→ Check appsettings.json has correct Endpoint

**Error: "401 Unauthorized"**
→ Make sure you're logged in: `az login`
→ Check Managed Identity has Search/OpenAI permissions

**No results returned**
→ Check index has data
→ Check field names match DoctorDocument class
→ Try simpler query first

**Slow response**
→ Check Azure Search query analytics in Portal
→ Consider indexing more fields

## Next Steps for Production

- [ ] Add chat history/multi-turn conversations
- [ ] Implement logging to Application Insights
- [ ] Add authentication (Entra ID)
- [ ] Deploy to Azure Container Apps or App Service
- [ ] Add email/SMS contact integration
- [ ] Build admin dashboard for data management
- [ ] Add analytics (who searching for what specialties)
- [ ] Implement result bookmarking/favorites

## Tech Stack Summary

- **Frontend**: HTML5 + Vanilla JavaScript (no frameworks)
- **Backend**: ASP.NET Core 8 Minimal APIs
- **Search**: Azure AI Search (hybrid + semantic + vector)
- **AI**: Azure OpenAI (GPT-4 for intent, embeddings)
- **Auth**: Managed Identity (DefaultAzureCredential)
- **Deployment**: Local with `dotnet run`, later: Docker + Container Apps

## Questions?

For customer demo, emphasize:
1. **No API keys in code** - Uses Managed Identity
2. **Semantic understanding** - Understands "skin doctor" = Dermatology automatically
3. **Fast hybrid search** - Keyword + semantic ranking + geo-distance
4. **Clean separation** - Agent orchestration → Search service → Azure resources
5. **Demo-ready** - All UI inline; zero build tools; runs locally in seconds
