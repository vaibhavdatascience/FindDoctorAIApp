# ⚡ TL;DR - Quick Reference

## The Absolute Minimum to Get Running

### 1. Edit Config (2 minutes)
```bash
code src/FindDoctor.Web/appsettings.json
```

Add your Azure endpoints:
```json
{
  "Azure": {
    "Search": {
      "Endpoint": "https://<YOUR_SEARCH_SERVICE>.search.windows.net"
    },
    "OpenAI": {
      "Endpoint": "https://<YOUR_AOAI_OR_FOUNDRY_RESOURCE>.cognitiveservices.azure.com"
    }
  }
}
```

### 2. Create Index (Azure Portal)
1. Portal → Search Service → Indexes → Create Index
2. Name: `doctors`
3. Fields: See SETUP.md for schema
4. Create

OR use PowerShell (see SETUP.md)

### 3. Load Sample Doctors (PowerShell)
```powershell
# Set your details
$ENDPOINT = "https://YOUR_SEARCH.search.windows.net"
$INDEX_NAME = "doctors"
$API_KEY = "your-api-key"

# Read sample data
$data = Get-Content "data/sample-doctors.json" | ConvertFrom-Json

# Upload to index
# (Full script in SETUP.md)
```

### 4. Run App (1 minute)
```bash
cd src/FindDoctor.Web
dotnet watch run
```

### 5. Open Browser
```
https://localhost:5001
```

### 6. Try These Queries
- `dermatologist`
- `female cardiologist near Cleveland`
- `doctor who speaks Spanish with online scheduling`

---

## File You'll Need to Modify
- `src/FindDoctor.Web/appsettings.json` - Add Azure endpoints
- `data/sample-doctors.json` - Replace with your doctors

## Files You Can Read to Understand
- `DEMO.md` - Before customer demo
- `ARCHITECTURE.md` - How it works
- `README.md` - Feature overview

---

## If Something Goes Wrong

| Error | Fix |
|-------|-----|
| "Endpoint not configured" | Check appsettings.json has `https://...` URLs |
| "401 Unauthorized" | Run `az login` |
| "No results" | Check index has documents: `$count=true` |
| "Port 5001 in use" | Kill process: `netstat -ano \| findstr :5001` |
| "Slow first query" | Normal! Cold start. <1s after first. |

---

## Architecture in 30 Seconds

```
Browser Chat UI
    ↓ (query)
ASP.NET Core API
    ↓ 
AgentOrchestrator (extract intent with OpenAI)
    ↓
AzureSearchService (hybrid search + geo-ranking)
    ↓
Azure AI Search (storage + semantic ranking)
Results back up the chain → displayed in chat
```

---

## Demo Script (2 Minutes)

```
User: "dermatologist near Cleveland"
Result: Shows female & male dermatologists, sorted by distance

User: "female skin doctor for acne"
Result: Shows dermatologists specializing in acne, females first

User: "doctor who speaks Spanish"
Result: Filters by language preference
```

---

## What Code to Look At

Only 3 files matter:

1. **`Services/AgentOrchestrator.cs`** (100 lines)
   - How it understands "skin doctor" = Dermatology
   
2. **`Services/AzureSearchService.cs`** (200 lines)
   - How it ranks by relevance + distance
   
3. **`Pages/Index.cshtml`** (Chat UI)
   - The user interface

Rest is boilerplate.

---

## Production Checklist

- [ ] Load real doctor data (not sample)
- [ ] Test with 100+ doctors
- [ ] Verify geo-distance ranking works
- [ ] Check Azure Search query costs
- [ ] Set up Application Insights logging
- [ ] Add authentication (Entra ID)
- [ ] Deploy to Container Apps / App Service

(See SETUP.md for details)

---

## Helpful Commands

```bash
# Run with hot reload
dotnet watch run

# Run in background
dotnet run --no-restore

# List Azure resources
az search service list --query "[].name"

# Check index has data
az search index-statistics --resource-group $RG --search-service-name $NAME --index-name doctors

# Test API directly
curl -X POST https://localhost:5001/api/chat \
  -H "Content-Type: application/json" \
  -d '{"query":"dermatologist"}'
```

---

## Total Setup Time
- **Optimistic**: 10 minutes (if all resources ready)
- **Realistic**: 30 minutes (first time, with docs)
- **Thorough**: 60 minutes (understand architecture)

---

**Need more details?** Read the guides in order:
1. GETTING_STARTED.md
2. QUICKSTART.md
3. DEMO.md
4. ARCHITECTURE.md

**Just want it running?** Follow the 5 steps above (10 min total).

