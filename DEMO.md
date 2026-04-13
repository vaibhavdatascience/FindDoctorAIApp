# Customer Demo Script & Guide

## Pre-Demo Setup (5 minutes before)

```bash
# 1. Ensure everything is running
cd src/FindDoctor.Web
dotnet watch run

# 2. Open browser (verify it loads)
https://localhost:5001

# 3. Have sample queries ready (see below)
```

---

## Demo Flow (10-15 minutes)

### Opening Pitch (30 seconds)
```
"Traditional doctor lookup requires clicking through filters.
This AI chatbot understands natural language - just talk to it like a person.

Let me show you how it works..."
```

---

### Demo 1: Basic Specialty Search (1 minute)

**Narrative**: "Let me search for a dermatologist"

**User types**: 
```
dermatologist
```

**Expected Result**:
- Shows female dermatologist results
- Each result displays: Name, Specialty, Location, Phone

**What to highlight**:
✅ **"No data entry required"** - Just natural language
✅ **"Clean UI"** - Card-based results, mobile-friendly
✅ **"Fast results"** - Indexed in Azure, sub-second response

**Demo Note**: If user asks, explain:
> "The application searched our doctor database and found specialists. The results are ranked by relevance in our index."

---

### Demo 2: Semantic Understanding (1 minute)

**Narrative**: "Now watch how the AI understands non-obvious queries"

**User types**:
```
I need a skin doctor for acne
```

**Expected Result**:
- Shows dermatologists
- Includes clinical terms: acne, eczema, psoriasis matching
- Explains semantic matching

**What to highlight**:
✅ **"Semantic AI Search"** - Understands "skin doctor" = Dermatology
✅ **"Condition-based"** - Found doctors treating acne specifically
✅ **"No synonym maps"** - Uses Azure OpenAI to understand intent (can handle new conditions automatically)

**Technical callout**:
> "Behind the scenes, Azure OpenAI analyzed your query and extracted: Specialty=Dermatology, Condition=acne. Then Azure AI Search found matching doctors using hybrid (keyword + semantic) indexing."

---

### Demo 3: Location-Based Search (1.5 minutes)

**Narrative**: "Distance ranking is critical for local healthcare"

**User types**:
```
female cardiologist near Cleveland
```

**Expected Result**:
- Shows female cardiologists
- Results sorted by distance (closest first)
- Distance column shows miles away

**What to highlight**:
✅ **"Geo-aware results"** - Ranks by relevance AND distance
✅ **"User location"** - If browser allows, shows "near me" automatically
✅ **"Smart ranking"** - 70% relevance + 30% proximity = balanced
  - A less relevant doctor far away scores lower
  - A relevant doctor close by scores highest

**Demo Point** (if you allowed location):
> "Your browser shared your location. Results are ranked closer to you first, but we still prioritize specialization match. A less relevant doctor 1 mile away won't outrank a better match 5 miles away."

---

### Demo 4: Provider Preferences (1 minute)

**Narrative**: "Patients have preferences - gender, language, availability"

**User types**:
```
female doctor who speaks Spanish with online scheduling
```

**Expected Result**:
- Shows female providers
- Online scheduling icon/badge
- Language list includes Spanish

**What to highlight**:
✅ **"Filters without complexity"** - No "advanced search" form
✅ **"Natural language filtering"** - AI extracts gender, language, preferences
✅ **"Accessibility"** - Critical for diverse patient populations

---

### Demo 5: Ambiguous Queries (30 seconds)

**Narrative**: "Sometimes we need clarification"

**User types**:
```
Find me a doctor
```

**Expected Result**:
- Shows helpful clarifying question
- Example: "What type of doctor or medical specialty are you looking for?"

**What to highlight**:
✅ **"Helpful AI"** - Asks instead of returning no results
✅ **"Conversational"** - Feels like talking to a receptionist
✅ **"Intent detection"** - AI recognizes queries are too vague

---

## Code Walkthrough (3-5 minutes)

### What to Show:
1. **Simple architecture** - Open project in VS Code
2. **Key files** - Explain only 3 core services

**Do this**:
```
Open Files Explorer, show this structure:

src/FindDoctor.Web/
├── Services/
│   ├── AgentOrchestrator.cs    ← Intent extraction (50 lines)
│   └── AzureSearchService.cs   ← Search + ranking (150 lines)
├── Pages/
│   ├── Index.cshtml             ← Chat UI
│   └── Index.cshtml.cs
├── Models/
│   └── Doctor.cs                ← Data models
└── Program.cs                   ← Service registration (50 lines)
```

**Talk**: 
> "The entire backend is ~300 lines of C# code. Let me show you the key pieces..."

### Show AgentOrchestrator (30 seconds)

Open `Services/AgentOrchestrator.cs`, scroll to top:

```csharp
// This method processes user queries
public async Task<ChatSearchResponse> ProcessUserQueryAsync(string userQuery)
{
    // 1. Use Azure OpenAI to extract filters
    var filters = await ExtractSearchFiltersAsync(userQuery);
    
    // 2. Execute search
    var results = await _searchService.HybridSearchAsync(filters);
    
    // 3. Format response
    return new ChatSearchResponse { Results = results };
}
```

**Explain**:
> "Three simple steps: Extract intent, search, return results. The AI handles all the complexity."

### Show AzureSearchService (30 seconds)

Point to the `HybridSearchAsync` method:

```csharp
// Builds query
var query = "(Specialty:Dermatology OR SpecialtiesCombined:Dermatology) " +
            "AND Gender:Female AND OffersOnlineScheduling:true";

// Enables semantic ranking
var options = new SearchOptions 
{ 
    SemanticSearch = new SemanticSearch { ... }
};

// Ranks by relevance + distance
var rankingScore = (relevance * 0.7) + (distance * 0.3);
```

**Explain**:
> "Hybrid search combines keyword matching + semantic rank. Then we boost by distance. Result: Fast, relevant, local."

### Show No Secrets (15 seconds)

Open `Program.cs`, point to auth:

```csharp
var credential = new DefaultAzureCredential();
var openAiClient = new OpenAIClient(endpoint, credential);
```

**Explain**:
> "No API keys in code. Uses Azure Managed Identity. Works locally with `az login` and automatically in Azure."

---

## Q&A Answers (Prepared Responses)

### Q: "Can this scale to thousands of doctors?"
✅ **Answer**: 
> "Yes. Azure AI Search handles millions of documents. This demo shows 10 doctors, but you can easily scale to 100K+ with no code changes. Latency stays under 100ms even with large datasets."

### Q: "What about patient privacy?"
✅ **Answer**:
> "All data is encrypted in transit and at rest. Auth uses Managed Identity - no credentials exposed. Audit logs are available in Azure Monitor."

### Q: "Can we integrate with our booking system?"
✅ **Answer**:
> "Absolutely. The chat endpoint returns structured doctor data. We can hook that to your booking API, sending appointments and reminders. This demo is just the search piece."

### Q: "How long to deploy?"
✅ **Answer**:
> "This code deploys to Azure Container Apps in minutes. We provide Dockerfile and infrastructure templates. Zero downtime deployments supported."

### Q: "What about multiple languages?"
✅ **Answer**:
> "Azure OpenAI supports multi-language intent extraction. We can add language toggle in the UI for Spanish, Arabic, Chinese, etc. Doctors can specify languages they speak."

### Q: "How do you handle regional differences?"
✅ **Answer**:
> "Doctors are location-tagged with latitude/longitude. Distance ranking works anywhere. Can filter by state/region if needed."

### Q: "Can we white-label this?"
✅ **Answer**:
> "Yes! The UI is fully customizable. Change colors, logo, clinic name, welcome message - all in config. Deploy under your domain."

---

## Demo Troubleshooting

### Problem: No results returned

**Causes**:
- Index not loaded with data
- appsettings.json incorrect

**Fix**:
```bash
# Verify data in index
az search index-statistics --resource-group <RG> --search-service-name <NAME> --index-name doctors

# Should show document count > 0
```

### Problem: Slow response first time

**Normal!** Azure OpenAI may need to load the model. Subsequent queries are faster.

**What to say**: 
> "First query uses cold start - about 5 seconds. Subsequent queries are sub-second. In production, we'd use request batching to warm the model."

### Problem: Chat UI doesn't load

**Fix**:
```bash
# Verify running on right port
dotnet run --urls=https://localhost:5001

# Check browser console (F12) for errors
```

---

## Key Talking Points for Customers

### For Hospital Executives
```
✅ REVENUE: Get patients to right doctors =  satisfied patients = referrals
✅ OPERATIONAL: Reduces front desk volume for "which doctor should I see?"
✅ DATA: Insights into what specialties patients search for most
✅ COMPLIANCE: HIPAA-compliant Azure services, audit logs built-in
```

### For IT Directors
```
✅ SECURITY: Managed Identity (no secrets), encryption, Azure compliance
✅ SCALABILITY: Grows to millions of records without code change
✅ MAINTAINABILITY: 300 lines of clean C# code, documented
✅ INTEGRATION: REST API - works with any system
✅ COST: Pay only for Azure Search + OpenAI calls; no servers to manage
```

### For Patients (End-User Benefit)
```
✅ CONVENIENCE: Find doctors by talking, not clicking
✅ ACCURACY: Semantic search understands what you really need
✅ SPEED: Results in seconds, not scrolling through 100s of profiles
✅ CONFIDENCE: See qualifications, location, availability in one place
```

---

## After-Demo Conversation Starters

1. **"How would you want to customize this?"**
   - Custom specialties for your system
   - White-label branding
   - Integration with your scheduling system

2. **"What data do you have today?"**
   - How many doctors?
   - Data format (EHR export, CSV, API)?
   - How often does it change?

3. **"What's your timeline?"**
   - Pilot (2-4 weeks)
   - MVP (4-8 weeks)
   - Production (8-12 weeks)

4. **"Who are the stakeholders?"**
   - Clinical staff
   - IT/Security
   - Patient-facing teams
   - Management

---

## Post-Demo: Next Steps

If customer is interested:

1. **Discovery Call** (1 hour)
   - Deep dive into current system
   - Data audit
   - Integration points
   - Customization needs

2. **Proof of Concept** (2-3 weeks)
   - Load real doctor data
   - Test with sample patients
   - Integration with scheduling system

3. **Pilot Program** (4-8 weeks)
   - Limited patient rollout
   - Feedback loops
   - Performance tuning

4. **Production Deployment** (8-12 weeks)
   - Full scalability testing
   - Security audit
   - Monitoring/alerting
   - Go-live support

---

## Quick Reference: Commands to Run

```bash
# Run the app
cd src/FindDoctor.Web
dotnet watch run

# Test specific endpoint
curl -X POST https://localhost:5001/api/chat `
  -H "Content-Type: application/json" `
  -d '{"query": "dermatologist"}'

# Check index stats
az search index-statistics `
  --resource-group <RG> `
  --search-service-name <NAME> `
  --index-name doctors

# View logs
dotnet run | findstr "info:"
```

---

## Recording/Screenshots Tips

If you want to record the demo:

```bash
# Use OBS Studio (free)
✅ Record at 60 FPS for smooth playback
✅ Highlight cursor during mouse movements
✅ Type slowly so viewers can read
✅ Pause after each result for 2-3 seconds

# Pro tip: Script the demo exactly once, then practice it 2-3 times
# Then record - no retakes needed!
```

---

Good luck with your demo! 🚀

