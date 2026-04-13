# Setup Checklist & Data Ingestion Guide

## Pre-Setup Checklist

Before you start, make sure you have:

- [ ] **.NET 8 SDK** installed
  ```bash
  dotnet --version  # Should show 8.0.x
  ```

- [ ] **Azure CLI** installed and authenticated
  ```bash
  az version
  az account show  # Should show your subscription
  ```

- [ ] **Existing Azure Resources**:
  - [ ] Azure AI Search service created
  - [ ] Azure OpenAI service with GPT-4 deployed
  - [ ] Embedding model deployed (text-embedding-ada-002)
  - [ ] Doctor data as JSON in blob storage (or local file)

---

## Step-by-Step Setup

### Step 1: Verify Azure Connection

```bash
# Check you're logged in
az account show

# List search services
az search service list --query "[].name"

# List OpenAI services
az cognitiveservices account list --query "[].{name:name, kind:kind}"
```

If either is empty, create the resources in Azure Portal first.

### Step 2: Get Resource Information

Run these commands to collect your Azure resource details:

```bash
# Set variables
$RG = "your-resource-group"
$SEARCH_NAME = "your-search-service"
$OPENAI_NAME = "your-openai-service"

# Get Search endpoint
$SEARCH_ENDPOINT = az search service show --resource-group $RG --name $SEARCH_NAME --query "endpoint" -o tsv
echo "Search Endpoint: $SEARCH_ENDPOINT"

# Get OpenAI endpoint
$OPENAI_ENDPOINT = az cognitiveservices account show --resource-group $RG --name $OPENAI_NAME --query "endpoint" -o tsv
echo "OpenAI Endpoint: $OPENAI_ENDPOINT"
```

Copy these values - you'll need them in the next step.

### Step 3: Update Configuration

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
      "BlobName": "YOUR_JSON_FILE_NAME.json"
    }
  }
}
```

**Important**: The application reads doctor data from **Azure Blob Storage** (not local files). 
→ See [BLOB_STORAGE_GUIDE.md](BLOB_STORAGE_GUIDE.md) for detailed setup.

**Important**: ModelDeploymentName should match your GPT-4 deployment name in Azure OpenAI.

### Step 4: Prepare Doctor Data

Your doctor data should be in JSON format. The app will read it from your Azure Blob Storage container.

#### Data Format

Create a `.json` file with this structure:

```json
{
  "doctors": [
    {
      "doctorId": "DOC001",
      "firstName": "John",
      "lastName": "Smith",
      "specialty": "Cardiology",
      "specialties": ["Cardiology", "Interventional Cardiology"],
      "clinicalTerms": "heart disease, chest pain, arrhythmia",
      "clinicalAliases": "heart doctor, cardiologist",
      "providerType": "MD",
      "gender": "Male",
      "languages": ["English", "Spanish"],
      "officeLocationName": "Heart Center",
      "city": "Cleveland",
      "state": "OH",
      "zip": "44106",
      "phone": "(216) 111-2222",
      "offersOnlineScheduling": true,
      "latitude": 41.5031,
      "longitude": -81.6956
    }
  ]
}
```

**Required fields**:
- `doctorId`, `firstName`, `lastName`, `specialty`
- `city`, `state`, `zip`, `phone`
- `latitude`, `longitude`

**Optional fields**:
- `specialties` - Array of all specialties
- `clinicalTerms` - Comma-separated medical conditions  
- `clinicalAliases` - Alternative names (e.g., "skeleton doctor" = Orthopedist)
- `gender` - "Male", "Female", or omit
- `languages` - Array of spoken languages
- `offersOnlineScheduling` - Boolean for online booking

#### Upload to Blob Storage

Once your JSON is ready:

1. Create an Azure Storage account (if not already done)
2. Create a blob container (e.g., "doctors")
3. Upload your JSON file to the container
4. **→ See [BLOB_STORAGE_GUIDE.md](BLOB_STORAGE_GUIDE.md) for step-by-step upload instructions**

Or use the sample data: repo includes `data/sample-doctors.json` with 10 sample doctors (ready to upload to blob storage).

### Step 5: Create Azure AI Search Index

#### Method A: Azure Portal (Easy)

1. Go to Azure Portal → Search Service
2. Click **Indexes** → **Create Index**
3. Name: `doctors`
4. Add fields from the schema below
5. Click **Create**

#### Method B: PowerShell Script

```powershell
# Set variables
$RG = "your-resource-group"
$SEARCH_NAME = "your-search-service"
$ENDPOINT = "https://$SEARCH_NAME.search.windows.net"
$INDEX_NAME = "doctors"
$API_KEY = (az search admin-key show --resource-group $RG --service-name $SEARCH_NAME --query "primaryKey" -o tsv)

# Create index JSON (see below)
# POST to: $ENDPOINT/indexes?api-version=2024-07-01

$headers = @{
    "api-key" = $API_KEY
    "Content-Type" = "application/json"
}

$indexDefinition = @{
    name = $INDEX_NAME
    fields = @(
        @{name="DoctorId"; type="Edm.String"; key=$true; retrievable=$true},
        @{name="FirstName"; type="Edm.String"; searchable=$true; retrievable=$true},
        @{name="LastName"; type="Edm.String"; searchable=$true; retrievable=$true},
        @{name="Specialty"; type="Edm.String"; searchable=$true; retrievable=$true; filterable=$true},
        @{name="Gender"; type="Edm.String"; filterable=$true; retrievable=$true},
        @{name="City"; type="Edm.String"; searchable=$true; retrievable=$true; filterable=$true},
        @{name="State"; type="Edm.String"; retrievable=$true; filterable=$true},
        @{name="Zip"; type="Edm.String"; searchable=$true; retrievable=$true; filterable=$true},
        @{name="Phone"; type="Edm.String"; retrievable=$true},
        @{name="OffersOnlineScheduling"; type="Edm.Boolean"; filterable=$true; retrievable=$true},
        @{name="OfficeLocationName"; type="Edm.String"; searchable=$true; retrievable=$true},
        @{name="Latitude"; type="Edm.Double"; filterable=$true; retrievable=$true},
        @{name="Longitude"; type="Edm.Double"; filterable=$true; retrievable=$true},
        @{name="Languages"; type="Collection(Edm.String)"; retrievable=$true},
        @{name="SpecialtiesCombined"; type="Edm.String"; searchable=$true},
        @{name="ClinicalTerms"; type="Edm.String"; searchable=$true},
        @{name="ClinicalAliases"; type="Edm.String"; searchable=$true},
        @{name="ProviderType"; type="Edm.String"; retrievable=$true}
    )
    semantic = @{
        configurations = @(
            @{
                name = "default"
                prioritizedFields = @{
                    titleField = @{fieldName="Specialty"}
                    contentFields = @(
                        @{fieldName="SpecialtiesCombined"},
                        @{fieldName="ClinicalTerms"},
                        @{fieldName="OfficeLocationName"}
                    )
                }
            }
        )
    }
} | ConvertTo-Json

# Create index
Invoke-WebRequest -Uri "$ENDPOINT/indexes?api-version=2024-07-01" `
    -Method POST `
    -Headers $headers `
    -Body $indexDefinition
```

### Step 6: Upload Doctor Data from Blob Storage to Index

The application automatically reads doctor data from your Azure Blob Storage container and uploads it to the search index.

#### Prerequisites
1. **Upload your JSON file to Azure Blob Storage**:
   - Create a container in your storage account (e.g., "doctors")
   - Upload your JSON file (e.g., "doctors.json")
   - See [BLOB_STORAGE_GUIDE.md](BLOB_STORAGE_GUIDE.md) for detailed instructions

2. **Configure blob storage in appsettings.json** (Step 3 above):
   ```json
   "Storage": {
     "AccountName": "mystorageaccount",
     "ContainerName": "doctors",
     "BlobName": "doctors.json"
   }
   ```

#### Trigger Data Ingestion

Once the app is running, call the ingest endpoint to load data from blob storage:

```powershell
# PowerShell
$URI = "https://localhost:5001/api/ingest"
$body = @{ "blobFileName" = "doctors.json" } | ConvertTo-Json

Invoke-WebRequest -Uri $URI `
    -Method POST `
    -Headers @{"Content-Type"="application/json"} `
    -Body $body
```

Or with curl:
```bash
curl -X POST https://localhost:5001/api/ingest \
  -H "Content-Type: application/json" \
  -d '{"blobFileName": "doctors.json"}'
```

**Expected Response**:
```json
{
  "success": true,
  "message": "Successfully ingested 10 documents from blob storage to search index"
}
```

**Note**: The app uses **Managed Identity** to authenticate with blob storage (no connection strings or keys needed in code). See [BLOB_STORAGE_GUIDE.md](BLOB_STORAGE_GUIDE.md) for local development setup.

### Step 7: Verify Index Has Data

```powershell
# Count documents in index
Invoke-WebRequest -Uri "$ENDPOINT/indexes/$INDEX_NAME/docs?`$count=true&api-version=2024-07-01" `
    -Headers $headers | ConvertFrom-Json

# Should show: "@odata.count": 10
```

### Step 8: Run the Application

```bash
# Navigate to project
cd src/FindDoctor.Web

# Restore dependencies
dotnet restore

# Run with hot reload (watch mode)
dotnet watch run

# Or regular run:
dotnet run
```

**Output should show**:
```
Building...
info: Microsoft.Hosting.Lifetime[14]
      Now listening on: https://localhost:5001
      Now listening on: http://localhost:5000
info: Microsoft.Hosting.Lifetime[0]
      Application started. Press Ctrl+C to exit.
```

### Step 9: Test the Chat

1. Open browser: **https://localhost:5001**
2. Try these queries:
   - "Find a dermatologist"
   - "Female cardiologist near Cleveland"
   - "Doctor for acne near me"

---

## Troubleshooting

### Error: "Azure:Search:Endpoint not configured"

**Solution**: Check `appsettings.json` is filled correctly

```bash
# Verify endpoint format
# Should be: https://name.search.windows.net
# NOT: https://name.search.windows.net/
# NOT: https://name-search.search.windows.net
```

### Error: "InvalidOperationException: Exception when enumerating the result set"

**This means**: Index doesn't have data yet

**Solution**:
1. Verify data was uploaded: `Invoke-WebRequest ... ?$count=true`
2. Check document formats match schema
3. Re-upload with correct field names

### Error: "Unauthorized - Invalid API Key"

**Solution**:
1. Get new key: `az search admin-key show --resource-group $RG --service-name $SEARCH`
2. Update PowerShell: `$API_KEY = ...`

### Error: "401 Unauthorized" from the chat

**Solution**: Managed Identity permissions missing

```bash
# Get app's principal ID (if in Azure)
$principalId = az identity show --name myIdentity --resource-group $RG --query principalId -o tsv

# Grant Search permissions
az role assignment create \
    --role "Search Service Contributor" \
    --assignee $principalId \
    --scope /subscriptions/$SUBSCRIPTION/resourceGroups/$RG/providers/Microsoft.Search/searchServices/$SEARCH_NAME

# Grant OpenAI permissions
az role assignment create \
    --role "Cognitive Services User" \
    --assignee $principalId \
    --scope /subscriptions/$SUBSCRIPTION/resourceGroups/$RG/providers/Microsoft.CognitiveServices/accounts/$OPENAI_NAME
```

### App runs but returns no results

**Checklist**:
- [ ] Index name matches `appsettings.json`
- [ ] Documents uploaded with correct field format
- [ ] Special characters (commas, quotes) properly escaped
- [ ] Latitude/Longitude are valid numbers
- [ ] Try simple query: "Cardiology"

---

## Data Format Validation

### Valid JSON
```json
{
  "doctors": [
    {
      "doctorId": "DOC001",
      "firstName": "John",
      "specialty": "Cardiology",
      ...
    }
  ]
}
```

### Invalid JSON (will fail)
```json
// ❌ Single quotes instead of double quotes
{doctors: [{"specialty": "Cardiology"}]}

// ❌ Trailing commas
{"doctors": [{"specialty": "Cardiology",}]}

// ❌ Unescaped quotes
{"description": "smith"s clinic"}
```

Use https://jsonlint.com to validate your JSON before uploading.

---

## Next Steps After Setup

1. **Add more doctors** - Expand JSON data
2. **Customize specialties** - Add your hospital's specialty list
3. **Test edge cases**:
   - Ambiguous queries: "Find me a doctor"
   - Location queries: "Near me", "within 25 miles"
   - Language filtering: "Doctor who speaks Spanish"
4. **Deploy to Azure** - Container Apps / App Service
5. **Add features**:
   - Chat history (store in Cosmos DB)
   - User authentication (Entra ID)
   - Analytics (Application Insights)
   - Appointment booking integration

---

## Support & Learning

- **Azure AI Search docs**: https://learn.microsoft.com/en-us/azure/search/
- **Azure OpenAI docs**: https://learn.microsoft.com/en-us/azure/cognitive-services/openai/
- **Semantic ranking**: https://learn.microsoft.com/en-us/azure/search/semantic-search-overview
- **Managed Identity**: https://learn.microsoft.com/en-us/azure/active-directory/managed-identities-azure-resources/

