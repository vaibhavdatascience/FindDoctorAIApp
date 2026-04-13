# 🏗️ Automated System Architecture

## Complete Data Flow

```
┌─────────────────────────────────────────────────────────────┐
│               AZURE RESOURCES (Cloud)                       │
├─────────────────────────────────────────────────────────────┤
│                                                             │
│  ┌─────────────────────────────────────────────────────┐  │
│  │   Azure Blob Storage                                │  │
│  │   Container: "uhhs"                                 │  │
│  │   File: "doctors.json" (doctor records)             │  │
│  └────────────────^────────────────────────────────────┘  │
│                   │                                        │
│                   │ (1) Download via connection string     │
│                   │                                        │
│  ┌────────────────▼───────────────────────────────────┐  │
│  │   Azure AI Search                                   │  │
│  │   Index: "doctors"                                  │  │
│  │   Operations: MergeOrUpload (incremental)          │  │
│  │   Auth: Admin Key                                   │  │
│  └────────────────┬───────────────────────────────────┘  │
│                   │                                        │
│                   │ (3) Indexed data ready for search      │
│                   │                                        │
│  ┌────────────────▼───────────────────────────────────┐  │
│  │   Azure OpenAI (GPT-4)                              │  │
│  │   Used for: Intent extraction + embeddings         │  │
│  │   Auth: Managed Identity                            │  │
│  └────────────────┬───────────────────────────────────┘  │
└────────────────────┼────────────────────────────────────────┘
                     │
                     │ (2) Chat queries + results
                     │
┌────────────────────▼────────────────────────────────────────┐
│       LOCAL APPLICATION (Your Machine)                    │
├─────────────────────────────────────────────────────────────┤
│                                                            │
│  ┌──────────────────────────────────────────────────────┐ │
│  │  ASP.NET Core 8 Application                         │ │
│  │  localhost:5001                                      │ │
│  └──────────────────────────────────────────────────────┘ │
│                     │                                     │
│      ┌──────────────┼──────────────┐                     │
│      │              │              │                     │
│      ▼              ▼              ▼                     │
│  ┌────────────┐ ┌────────────┐ ┌────────────┐          │
│  │ Program.cs │ │DataIngest  │ │ Razor UI   │          │
│  │            │ │ Service    │ │            │          │
│  │• Startup   │ │            │ │• Chat      │          │
│  │  Data Sync │ │• Blob DL   │ │  Interface │          │
│  │• Config    │ │• JSON      │ │• Results   │          │
│  │• DI Setup  │ │  Parsing   │ │  Display   │          │
│  │            │ │• Indexing  │ │            │          │
│  │            │ │• Logging   │ │            │          │
│  └────────────┘ └────────────┘ └────────────┘          │
│      │              │              │                     │
│      └──────────────┼──────────────┘                     │
│                     │                                     │
└─────────────────────┼────────────────────────────────────┘
                      │
                      ▼
                  ┌─────────┐
                  │ Browser │
                  │ (Chat)  │
                  └─────────┘
```

---

## Startup Sequence (Step-by-Step)

```
1. User runs: dotnet run
   ↓
2. Program.cs Main Entry
   - Load configuration from appsettings.json
   - Parse connection strings
   ↓
3. Service Registration
   - Create SearchClient (with admin key)
   - Create BlobContainerClient (with connection string)
   - Register DataIngestService in DI container
   - Register AgentOrchestrator for chat
   - Register AzureSearchService for search
   ↓
4. Automatic Data Sync (NEW!)
   - var ingestService = scope.GetRequiredService<DataIngestService>()
   - await ingestService.IngestFromBlobAsync("doctors.json")
     ↓
     4a. Download from Blob Storage
         - BlobClient.DownloadAsync()
         - StreamReader.ReadToEndAsync()
         - Parse JSON
     ↓
     4b. Batch Upload to Search Index
         - Group doctors into batches of 100
         - For each batch:
             - IndexDocumentsBatch.MergeOrUpload(documents)
             - Await SearchClient.IndexDocumentsAsync()
             - Log results
     ↓
     4c. Completion Logging
         - Total uploaded: X/Y
         - ✅ Data sync completed successfully
   ↓
5. HTTP Server Starts
   - app.UseHttpsRedirection()
   - app.UseStaticFiles()
   - app.UseCors()
   - app.MapPost("/api/chat", ChatEndpoint)
   - app.MapPost("/api/ingest", IngestDataEndpoint)
   - app.MapRazorPages()
   ↓
6. Ready for Requests
   - Server listening on https://localhost:5001
   - UI accessible
   - Chat queries ready
   - Search index populated
```

---

## Authentication Mechanism per Service

| Service | Auth Method | Location | When Configured |
|---------|-------------|----------|-----------------|
| **Azure AI Search** | Admin Key | `appsettings.json` | Startup |
| **Azure Blob Storage** | Connection String | `appsettings.json` | Startup |
| **Azure OpenAI** | Managed Identity | Runtime | Startup |

### Why Different Auth Methods?

- **Search Admin Key**: Needed to modify index (merge/upload documents)
- **Blob Connection String**: Simplest for app to download files
- **OpenAI Managed Identity**: Would work locally if configured; fallback to DefaultAzureCredential

---

## Data Flow During Sync

```
appsettings.json
    │
    ├─ Search.AdminKey ──────────────┐
    │                                │
    ├─ Storage.ConnectionString ─┐   │
    │                             │   │
    └─ Storage.BlobName          │   │
         ("doctors.json")         │   │
                 │                │   │
                 ▼                │   │
         Parse Connection ────────┤   │
         Extract Account Name     │   │
         Extract Account Key      │   │
                 │                │   │
                 ▼                │   │
    BlobContainerClient           │   │
    (https://ACCOUNT.blob.core... │   │
     ConnectionString Auth)       │   │
                 │                │   │
                 ▼                │   │
    GetBlobClient("doctors.json") │   │
         │                         │   │
         ├─ ExistsAsync()          │   │
         │ └─ Validate blob exists │   │
         │                         │   │
         └─ DownloadAsync()        │   │
            └─ Get blob stream     │   │
                 │                 │   │
                 ▼                 │   │
        JSON Content              │   │
        (raw text stream)         │   │
                 │                 │   │
                 ▼                 │   │
        StreamReader              │   │
        .ReadToEndAsync()         │   │
                 │                 │   │
                 ▼                 │   │
        Parse JSON                │   │
        DoctorDataFile            │   │
        └─ List<Doctor>           │   │
                 │                 │   │
                 ▼                 │   │
    Convert to IndexDocuments     │   │
    (DoctorDocument)              │   │
                 │                 │   │
                 ├─ Batch 1: [DocA, DocB, ...]
                 ├─ Batch 2: [DocC, DocD, ...]
                 └─ Batch N: [...]
                 │                 │   │
                 ▼                 │   │
    IndexDocumentsBatch           │   │
    .MergeOrUpload(batch)         │   │
                 │                 │   │
                 │                 │   │
                 └────────────────►───┴──►  SearchClient
                                              (Admin Key Auth)
                                              .IndexDocumentsAsync()
                                              
                                              ▼
                                              
                                              Azure AI Search Index
                                              "doctors"
                                              
                                              Indexed & Ready
                                              for queries
```

---

## Configuration Resolution Order

1. **Default values** in code
2. **appsettings.json** (primary config)
3. **appsettings.{Environment}.json** (overrides)
4. **Environment variables** (override all)

Current configuration file: `src/FindDoctor.Web/appsettings.json`

```json
{
  "Azure": {
    "Search": {
      "Endpoint": "https://<YOUR_SEARCH_SERVICE>.search.windows.net",
      "IndexName": "doctors",
      "AdminKey": "<YOUR_AZURE_SEARCH_ADMIN_KEY>"  ← From your input
    },
    "Storage": {
      "ConnectionString": "**CONFIGURED**",  ← From your input
      "ContainerName": "uhhs",  ← From your input
      "BlobName": "doctors.json"  ← Fixed
    }
  }
}
```

---

## Key Classes & Their Roles

### Program.cs
- **Lines 1-30**: Service registration
- **Lines 30-50**: Helper functions (connection string parsing)
- **Lines 50-70**: Build app
- **Lines 70-85**: **AUTOMATIC SYNC TRIGGER** (new!)
- **Lines 85+**: Middleware & endpoints

### DataIngestService.cs
- **IngestFromBlobAsync()**: Main entry point
  - Downloads blob
  - Parses JSON
  - Calls UploadDocumentsAsync()
  
- **UploadDocumentsAsync()**: Index documents
  - Batch splitting (100 per batch)
  - **MergeOrUpload action** ← Incremental (new!)
  - Logging & error handling

### AzureSearchService.cs
- **HybridSearchAsync()**: Search documents
  - Builds query
  - Distance ranking
  - Result mapping
  - (unchanged from before)

### AgentOrchestrator.cs
- **ProcessUserQueryAsync()**: Intent extraction
  - Calls OpenAI for semantic understanding
  - Passes results to search service
  - (unchanged from before)

---

## Performance Characteristics

| Metric | Value |
|--------|-------|
| **Batch Size** | 100 documents |
| **Sync Time (100 docs)** | ~2-3 seconds |
| **Sync Time (1000 docs)** | ~10-15 seconds |
| **Sync Time (10K docs)** | ~2-3 minutes |
| **Connection Pooling** | Automatic |
| **Timeout (blob download)** | Default (5 min) |
| **Retry Policy** | None (single attempt) |

---

## What Changed from Manual to Automated

### Before
```
❌ Manual trigger needed
❌ /api/ingest endpoint call required
❌ User controls when data loads
❌ Startup doesn't block on sync
⚠️  Unclear when index is ready
```

### Now
```
✅ Automatic on app startup
✅ No manual endpoint calls
✅ System controls sync timing
✅ Sync completes (timeout OK if fails)
✅ Console logs show exact status
```

---

## Disaster Recovery

### If Sync Fails
```
App Start → Sync Fails → Log Warning → App Continues
                           (Don't Block)
                           
User can:
1. Check blob exists
2. Verify connection string
3. Retry /api/ingest endpoint
4. Restart app (automatic retry)
```

### If Blob Storage Down
```
Connection timeout after ~30 sec
→ Error logged
→ App starts anyway
→ User can retry /api/ingest when blob is back up
```

### If Search Index Down
```
Index operation fails
→ Error logged per batch
→ Some docs succeed, some fail
→ User can retry /api/ingest
```

---

## Security Layers

```
┌─────────────────────────────────────┐
│  appsettings.json                   │
│  (Contains secrets)                 │
│  ⚠️  Don't commit to git!            │
└──────────────▲──────────────────────┘
               │
        (Add to .gitignore)
               │
        ┌──────▼──────────────┐
        │ Environment Variable│
        │ (Production)        │
        └─────────────────────┘
```

---

## Next Steps for Production

1. **Move credentials to Azure Key Vault**
   - Store AdminKey & ConnectionString in Key Vault
   - Use Managed Identity to retrieve secrets
   
2. **Add retry logic**
   - Exponential backoff on blob download failures
   - Max retries: 3
   
3. **Add scheduled sync**
   - Background job every 24 hours
   - Keeps data fresh even if blob updated
   
4. **Add monitoring**
   - Application Insights integration
   - Alert on sync failures
   - Track sync duration metrics

5. **Containerize**
   - Docker image for deployment
   - Azure Container Apps or AKS
   - Fully automated CI/CD

