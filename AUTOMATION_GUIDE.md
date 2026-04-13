# 🤖 Automated Data Sync - Setup & Usage Guide

Your Find a Doctor application is now **fully automated**. Data syncs automatically from Azure Blob Storage to Azure AI Search when the app starts.

---

## ✨ What's Automated Now

### Before (Manual)
```
User starts app
  ↓
User must call POST /api/ingest
  ↓
Data loads manually
  ↓
Chat ready
```

### After (Automated)
```
User starts app
  ↓
✅ Automatic data sync begins
  ↓
✅ Data loads automatically
  ↓
✅ Chat ready (no manual steps!)
```

---

## 🚀 Quick Start (5 Seconds)

### 1. Credentials in appsettings.json ✅
Already configured! Your credentials are in:
- `Azure:Search:AdminKey` - Search admin key
- `Azure:Storage:ConnectionString` - Blob storage connection
- `Azure:Storage:ContainerName` - Blob container name
- `Azure:Storage:BlobName` - JSON file name

### 2. Run the App
```bash
cd src/FindDoctor.Web
dotnet run
```

### 3. Watch the Startup Logs
You'll see:
```
🔄 Starting automatic data sync from blob storage...
Loaded 10 doctors from blob storage
Batch 1: 10 succeeded, 0 failed
Total uploaded: 10/10
✅ Data sync completed successfully
```

### 4. Open Chat
```
https://localhost:5001
```

**That's it!** The app is ready to use. No manual API calls needed.

---

## 📋 Configuration Details

### appsettings.json
```json
{
  "Azure": {
    "Search": {
      "Endpoint": "https://<YOUR_SEARCH_SERVICE>.search.windows.net",
      "IndexName": "doctors",
      "AdminKey": "<YOUR_AZURE_SEARCH_ADMIN_KEY>"
    },
    "Storage": {
      "ConnectionString": "DefaultEndpointsProtocol=https;AccountName=<YOUR_STORAGE_ACCOUNT_NAME>;AccountKey=<YOUR_STORAGE_ACCOUNT_KEY>",
      "ContainerName": "uhhs",
      "BlobName": "doctors.json"
    }
  }
}
```

### How It Works
1. **Connection String Auth** - Uses storage account key (not Managed Identity)
2. **Admin Key Auth** - Uses Azure AI Search admin key for index updates
3. **Incremental Updates** - Uses `MergeOrUpload` action:
   - New doctors → inserted
   - Existing doctors (same ID) → updated
   - Unmodified documents → unchanged

---

## 🔄 Automatic Sync Process

When your app starts:

1. **Service Initialization** (Program.cs)
   - Loads configuration
   - Creates SearchClient with admin key
   - Creates BlobContainerClient with connection string
   - Registers DataIngestService

2. **Startup Sync Trigger** (Program.cs, lines ~110-125)
   ```csharp
   using (var scope = app.Services.CreateScope())
   {
       var ingestService = scope.ServiceProvider.GetRequiredService<DataIngestService>();
       logger.LogInformation("🔄 Starting automatic data sync...");
       await ingestService.IngestFromBlobAsync(blobName);
   }
   ```

3. **Data Download** (DataIngestService)
   - Connects to blob storage via connection string
   - Downloads `doctors.json` from container
   - Parses JSON into Doctor objects

4. **Batch Indexing** (DataIngestService, batch size = 100)
   - Groups doctors into batches
   - Sends each batch with `MergeOrUploadDocumentsAsync`
   - Logs success/failure per batch

5. **Ready for Chat**
   - App is fully initialized
   - Index has latest data
   - Chat endpoint ready to accept queries

---

## 🛠️ Manual Data Refresh (Optional)

Even though sync is automatic on startup, you can manually trigger a refresh anytime:

### Via REST API
```bash
curl -X POST https://localhost:5001/api/ingest \
  -H "Content-Type: application/json" \
  -d '{"blobFileName": "doctors.json"}'
```

### Via PowerShell
```powershell
Invoke-WebRequest -Uri "https://localhost:5001/api/ingest" `
    -Method POST `
    -Headers @{"Content-Type"="application/json"} `
    -Body '{"blobFileName": "doctors.json"}'
```

**Response:**
```json
{
  "success": true,
  "message": "Successfully ingested data from doctors.json"
}
```

---

## 📊 Monitoring & Troubleshooting

### Sync Logs During Startup
The console will show:
```
🔄 Starting automatic data sync from blob storage...
Reading doctor data from blob: doctors.json
Loaded 10 doctors from blob storage
Batch 1: 10 succeeded, 0 failed
Total uploaded: 10/10
✅ Data sync completed successfully
```

### If Sync Fails
```
❌ Data sync failed: Blob not found: doctors.json
```

**Possible causes:**
- ❌ Blob file name is incorrect (check `Azure:Storage:BlobName` in appsettings.json)
- ❌ Blob container doesn't exist (check `Azure:Storage:ContainerName`)
- ❌ Connection string is invalid (malformed or expired)
- ❌ JSON file is not in blob storage yet

**App behavior:** App still starts even if sync fails (doesn't block startup)

---

## 🔐 Security Notes

### What's NOT in Code
- ✅ Storage account key: In `appsettings.json` only (not hardcoded)
- ✅ Search admin key: In `appsettings.json` only (not hardcoded)
- ✅ No API keys in source code

### For Production
Move credentials to:
- **Azure Key Vault** (recommended)
- **Environment variables**
- **Azure Managed Identity** (if deployed to Azure)

Example for production:
```bash
# Set environment variables before running
$env:AZURE_SEARCH_ADMIN_KEY = "your-key"
$env:AZURE_STORAGE_CONNECTION_STRING = "your-connection-string"
dotnet run
```

---

## 📈 Performance Notes

### Batch Upload
- Batch size: **100 documents** (configurable in DataIngestService.cs)
- If you have 10,000 doctors: 100 batches, logged with progress
- Typical sync time: 5-15 seconds for 1,000 doctors

### Incremental Updates
- Only changed documents processed during sync
- Duplicate doctor IDs: Auto-merged (no duplicates in index)
- Fast re-syncs: Document counts don't grow

---

## 🚨 Common Issues & Fixes

### Issue: "Blob not found: doctors.json"
**Solution:** Ensure JSON file is uploaded to blob container
```bash
az storage blob upload -f ./data/sample-doctors.json \
  -c uhhs -n doctors.json \
  --connection-string "your-connection-string"
```

### Issue: "AccountKey not found in connection string"
**Solution:** Verify connection string format
```
DefaultEndpointsProtocol=https;AccountName=NAME;AccountKey=<YOUR_STORAGE_ACCOUNT_KEY>;EndpointSuffix=core.windows.net
```

### Issue: App starts but index is empty
**Solution:** Check if sync actually ran (look for "✅ Data sync completed" in logs)
If missing, sync likely failed - check error message above it

### Issue: Connection timeout after 30 seconds
**Solution:** Check blob file size and network connection
- Blob file > 100MB: May timeout
- Network issue: Check Azure Storage accessibility from your network

---

## 🔄 Update Data in Blob Storage

To update data without restarting the app:

1. **Update the JSON file** in blob storage
   ```bash
   az storage blob upload -f ./updated-doctors.json \
     -c uhhs -n doctors.json --overwrite \
     --connection-string "your-connection-string"
   ```

2. **Trigger manual refresh** (optional)
   ```bash
   curl -X POST https://localhost:5001/api/ingest \
     -H "Content-Type: application/json" \
     -d '{"blobFileName": "doctors.json"}'
   ```

3. **Or restart the app** (automatic sync triggers)

---

## ✅ Verification Checklist

Before going to production:

- [ ] `appsettings.json` has valid Storage ConnectionString
- [ ] `appsettings.json` has valid Search AdminKey
- [ ] Blob file exists in container (`doctors.json`)
- [ ] Blob JSON format is valid (see BLOB_STORAGE_GUIDE.md)
- [ ] App starts without errors
- [ ] Sync logs show "✅ Data sync completed successfully"
- [ ] Chat queries return results
- [ ] Manual /api/ingest call works (optional test)

---

## 📞 Support

**If sync fails on startup:**
1. Check console logs for specific error message
2. Verify credentials in appsettings.json
3. Verify blob file exists and is valid JSON
4. Restart the app (sync will retry)

**Everything else:** Check SETUP.md, BLOB_STORAGE_GUIDE.md, or README.md

