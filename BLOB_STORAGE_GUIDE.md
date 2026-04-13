# Azure Blob Storage Data Ingestion Guide

## Overview

The application now reads doctor data directly from **Azure Blob Storage** instead of local JSON files. This allows you to:
- Keep your data in the cloud
- Update doctor records by simply updating the blob file
- Use existing data pipeline
- No local file synchronization needed

## Configuration

Edit `src/FindDoctor.Web/appsettings.json`:

```json
{
  "Azure": {
    "Storage": {
      "AccountName": "YOUR_STORAGE_ACCOUNT_NAME",
      "ContainerName": "YOUR_CONTAINER_NAME",
      "BlobName": "YOUR_JSON_FILE_NAME.json"
    }
  }
}
```

### Example
If your storage account is `mystorageaccount` and your blob file is at:
```
https://mystorageaccount.blob.core.windows.net/doctors/data.json
```

Then configure:
```json
{
  "Azure": {
    "Storage": {
      "AccountName": "mystorageaccount",
      "ContainerName": "doctors",
      "BlobName": "data.json"
    }
  }
}
```

## Expected JSON Format

Your blob file should be a JSON array of doctors:

```json
{
  "doctors": [
    {
      "doctorId": "DOC001",
      "firstName": "John",
      "lastName": "Smith",
      "specialty": "Dermatology",
      "specialties": ["Dermatology", "Cosmetic Dermatology"],
      "clinicalTerms": "acne, eczema, psoriasis, rashes",
      "clinicalAliases": "skin doctor, dermatologist",
      "providerType": "MD",
      "gender": "Male",
      "languages": ["English", "Spanish"],
      "officeLocationName": "Cleveland Clinic",
      "city": "Cleveland",
      "state": "OH",
      "zip": "44106",
      "phone": "(216) 555-0100",
      "offersOnlineScheduling": true,
      "latitude": 41.5031,
      "longitude": -81.6956
    }
  ]
}
```

### Required Fields
- `doctorId` - Unique identifier
- `firstName`, `lastName` - Provider name
- `specialty` - Primary specialty
- `officeLocationName` - Office/clinic name
- `city`, `state`, `zip` - Location
- `phone` - Contact number
- `latitude`, `longitude` - Geographic coordinates (for distance ranking)

### Optional Fields
- `specialties` - List of all specialties
- `clinicalTerms` - Medical conditions they treat
- `clinicalAliases` - Alternative names/descriptions
- `providerType` - MD, DO, PA, NP, etc.
- `gender` - Male, Female
- `languages` - Spoken languages
- `offersOnlineScheduling` - Boolean (true/false)

## Authentication

The application uses **Managed Identity** to authenticate with Azure Blob Storage. No connection strings or access keys needed in your config.

### Local Development
1. Ensure you're logged in: `az login`
2. Verify permissions on storage account
3. Run the app normally

### Azure Deployment
1. Create a System-Assigned Managed Identity for your app
2. Grant it these roles on the storage account:
   - `Storage Blob Data Reader` (to read files)

## Ingestion Methods

### Method 1: Using the API Endpoint (Recommended)

```bash
# Make a POST request to trigger ingestion
curl -X POST https://localhost:5001/api/ingest \
  -H "Content-Type: application/json" \
  -d '{"blobFileName": "doctors.json"}'
```

Response:
```json
{
  "success": true,
  "message": "Successfully ingested data from doctors.json"
}
```

### Method 2: Using DataIngestService Directly

In your code:
```csharp
[HttpPost("manual-ingest")]
public async Task<IActionResult> ManualIngest(
    DataIngestService ingestService,
    ILogger<YourController> logger)
{
    try
    {
        await ingestService.IngestFromBlobAsync("doctors.json");
        return Ok("Ingestion complete");
    }
    catch (Exception ex)
    {
        logger.LogError(ex.Message);
        return BadRequest(ex.Message);
    }
}
```

## Step-by-Step Setup

### Step 1: Prepare Your Doctor Data
Ensure your JSON file in blob storage matches the format above.

```bash
# Example: Upload to blob storage
az storage blob upload \
  --account-name YOUR_ACCOUNT \
  --container-name YOUR_CONTAINER \
  --name doctors.json \
  --file ./data.json
```

### Step 2: Update Configuration
Edit `appsettings.json`:
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
      "AccountName": "YOUR_STORAGE_ACCOUNT",
      "ContainerName": "YOUR_CONTAINER",
      "BlobName": "doctors.json"
    }
  }
}
```

### Step 3: Create Search Index
If you haven't already:
```bash
# Use Azure Portal or PowerShell script from SETUP.md
```

### Step 4: Run the App
```bash
cd src/FindDoctor.Web
dotnet run
```

### Step 5: Trigger Ingestion
```bash
# Option A: Using curl
curl -X POST https://localhost:5001/api/ingest \
  -H "Content-Type: application/json" \
  -d '{"blobFileName": "doctors.json"}'

# Option B: Check logs for auto-ingestion status
# The app logs ingestion progress:
# "Reading doctor data from blob: doctors.json"
# "Loaded X doctors from blob storage"
# "Batch 1: 100 succeeded, 0 failed"
# "Total uploaded: X/Y"
```

## Troubleshooting

### Error: "Azure:Storage:AccountName not configured"
→ Update `appsettings.json` with storage account settings

### Error: "Blob not found"
→ Check that blob file exists and name matches config

### Error: "401 Unauthorized"
→ Run `az login` to ensure authentication is set up

### Slow ingestion
→ Normal for large datasets. Check logs:
- "Reading doctor data from blob: ..." (blob download)
- "Batch X: Y succeeded" (upload progress)
- "Total uploaded: Z/N" (final count)

### No doctors in search results after ingestion
1. Verify ingestion succeeded: Check app logs
2. Verify doctor data format: Run ingestion endpoint and check response
3. Check index has documents: 
   ```bash
   az search index-statistics \
     --service-name YOUR_SEARCH \
     --index-name doctors \
     --resource-group YOUR_RG
   ```

## Updating Doctor Data

When you update your blob file:

1. **Update the JSON file** in your storage account
2. **Trigger re-ingestion** via the API:
   ```bash
   curl -X POST https://localhost:5001/api/ingest \
     -H "Content-Type: application/json" \
     -d '{"blobFileName": "doctors.json"}'
   ```
3. **Verify in search** - query should return updated results

## Performance Notes

- **Small files** (<10 MB): < 1 second
- **Medium files** (10-100 MB): 1-5 seconds
- **Large files** (>100 MB): 5-30 seconds (batched upload)

For very large datasets, consider:
- Splitting into multiple blob files and ingesting each
- Scheduling ingestion during off-peak hours
- Using Azure Data Factory for scheduled ingestion

## Security Best Practices

✅ **Use Managed Identity** - No secrets in config
✅ **RBAC Permissions** - Grant only needed roles
✅ **Data Encryption** - Azure Storage encrypts by default
✅ **Private Endpoints** - Optional, for network isolation
✅ **Audit Logging** - Monitor who accesses doctor data

## Quick Reference

```bash
# Check if blob exists
az storage blob exists \
  --account-name YOUR_ACCOUNT \
  --container-name YOUR_CONTAINER \
  --name doctors.json

# Upload new data
az storage blob upload \
  --account-name YOUR_ACCOUNT \
  --container-name YOUR_CONTAINER \
  --name doctors.json \
  --file ./new-data.json

# Download current data
az storage blob download \
  --account-name YOUR_ACCOUNT \
  --container-name YOUR_CONTAINER \
  --name doctors.json \
  --file ./doctors.json

# Trigger ingestion (from app running locally)
curl -X POST http://localhost:5001/api/ingest \
  -H "Content-Type: application/json" \
  -d '{"blobFileName": "doctors.json"}'
```

That's it! Your application will now seamlessly ingest doctor data from Azure Blob Storage.
