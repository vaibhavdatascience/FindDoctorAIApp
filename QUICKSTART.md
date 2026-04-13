# Quick Start Guide - 5 Minutes to Working Demo

## Step 0: Prerequisites Check
```bash
# Ensure you're logged into Azure
az login

# Verify you can access your Azure resources
az search service list --query "[].{name:name, resourceGroup:resourceGroup}" -o table
az cognitiveservices account list --query "[].{name:name, kind:kind}" -o table
```

## Step 1: Get Your Azure Resource Information
Run these commands and copy the outputs:

### Get Azure AI Search Endpoint
```bash
az search service show --resource-group <YOUR_RG> --name <YOUR_SEARCH_NAME> --query "endpoint"
```

### Get OpenAI Endpoint  
```bash
az cognitiveservices account show --resource-group <YOUR_RG> --name <YOUR_OPENAI_NAME> --query "endpoint"
```

## Step 2: Update appsettings.json
Edit `src/FindDoctor.Web/appsettings.json`:

```json
{
  "Azure": {
    "Search": {
      "Endpoint": "https://<YOUR_SEARCH_SERVICE>.search.windows.net",
      "IndexName": "doctors"
    },
    "OpenAI": {
      "Endpoint": "https://<YOUR_AOAI_OR_FOUNDRY_RESOURCE>.cognitiveservices.azure.com",
      "ModelDeploymentName": "gpt-4"
    },
    "Storage": {
      "AccountName": "YOUR_STORAGE_ACCOUNT_NAME",
      "ContainerName": "YOUR_CONTAINER_NAME",
      "BlobName": "doctors.json"
    }
  }
}
```

**→ For detailed blob storage setup, see [BLOB_STORAGE_GUIDE.md](BLOB_STORAGE_GUIDE.md)**

## Step 3: Upload Your Data to Blob Storage

1. Upload your doctor data JSON file to your Azure Blob Storage container
2. See [BLOB_STORAGE_GUIDE.md](BLOB_STORAGE_GUIDE.md) for step-by-step upload instructions
3. Make sure the blob file name matches your `appsettings.json` configuration (e.g., "doctors.json")

**Optional**: Use included sample data (`data/sample-doctors.json`) for quick testing

## Step 4: Start the Application

```bash
cd src/FindDoctor.Web
dotnet restore
dotnet run
```

Visit: **https://localhost:5001**

## Step 5: Load Data from Blob Storage

Once the app is running, trigger data ingestion with this curl command:

```bash
curl -X POST https://localhost:5001/api/ingest \
  -H "Content-Type: application/json" \
  -d '{"blobFileName": "doctors.json"}'
```

Or with PowerShell:
```powershell
Invoke-WebRequest -Uri "https://localhost:5001/api/ingest" `
    -Method POST `
    -Headers @{"Content-Type"="application/json"} `
    -Body '{"blobFileName": "doctors.json"}'
```

**Expected Response**:
```json
{
  "success": true,
  "message": "Successfully ingested 10 documents from blob storage to search index"
}
```

## Step 6: Test with Sample Queries

In the chat UI, try:
- "Find a dermatologist"
- "Female cardiologist near Cleveland"
- "Doctor for acne near me"

## Troubleshooting

### Port already in use
```bash
# Kill process on port 5001
netstat -ano | findstr :5001
taskkill /PID <PID> /F
```

### Managed Identity issues
```bash
# Check current Azure login
az account show

# Login with explicit subscription
az login --subscription <SUBSCRIPTION_ID>
```

### Search index not found
```bash
# List your indexes
az search index list --service-name <YOUR_SEARCH> --resource-group <YOUR_RG>
```

## What's Happening Behind the Scenes?

When you type "Female dermatologist near Cleveland":

1. **Browser sends**: `POST /api/chat` with query
2. **AgentOrchestrator**: Uses Azure OpenAI to understand: "specialty=Dermatology, gender=Female, location=Cleveland"
3. **AzureSearchService**: 
   - Executes hybrid search in Azure AI Search
   - Ranks by relevance + distance
   - Returns top 10 results
4. **Chat UI**: Displays doctor cards with:
   - Name, specialty, location
   - Distance if you allowed location access
   - Phone number

## Demo Notes for Customers

**Key talking points:**

✅ **Semantic Search** - "skin doctor" automatically matches "Dermatology" (no hardcoding)
✅ **Geo-Aware** - Results ranked by relevance AND distance
✅ **Secure** - No API keys in code; uses Managed Identity
✅ **Simple** - ~400 lines of C# code; everything clear
✅ **Scalable** - Backed by Azure AI Search (handles millions of records)

**Demo Script:**
1. Open app, show clean UI
2. Try: "dermatologist near me" (shows how "near me" works)
3. Try: "female skin doctor for acne" (shows semantic understanding)
4. Try: "cardiologist with online scheduling" (shows filtering)
5. Show code: point out clean separation (UI → API → Services → Azure)

---

**Need help?** Check the main README.md for full architecture details.
