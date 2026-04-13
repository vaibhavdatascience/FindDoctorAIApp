using Azure;
using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Azure.AI.OpenAI;
using Azure.Storage.Blobs;
using Azure.Identity;
using FindDoctor.Web.Models;
using FindDoctor.Web.Services;
using FindDoctor.Web.Utilities;

var builder = WebApplication.CreateBuilder(args);

// ===================================
// CONFIGURATION
// ===================================
builder.Configuration
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
    .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true)
    .AddEnvironmentVariables();

// ===================================
// SERVICES REGISTRATION
// ===================================

// Azure AI Search - using connection string with admin key for incremental updates
var searchEndpoint = builder.Configuration["Azure:Search:Endpoint"];
var searchIndexName = builder.Configuration["Azure:Search:IndexName"] ?? "doctors";
var searchAdminKey = builder.Configuration["Azure:Search:AdminKey"];

if (string.IsNullOrEmpty(searchEndpoint) || string.IsNullOrEmpty(searchAdminKey))
    throw new InvalidOperationException("Azure:Search:Endpoint and Azure:Search:AdminKey must be configured");

var searchCredential = new Azure.AzureKeyCredential(searchAdminKey);

var searchIndexClient = new SearchIndexClient(
    new Uri(searchEndpoint),
    searchCredential);

var searchClient = new SearchClient(
    new Uri(searchEndpoint),
    searchIndexName,
    searchCredential);

builder.Services.AddSingleton(searchIndexClient);
builder.Services.AddSingleton(searchClient);
builder.Services.AddScoped<AzureSearchService>();
builder.Services.AddScoped<SearchIndexCreationService>();

// Azure OpenAI - for intent extraction (using Managed Identity)
var openAiEndpoint = builder.Configuration["Azure:OpenAI:Endpoint"];
if (string.IsNullOrEmpty(openAiEndpoint))
    throw new InvalidOperationException("Azure:OpenAI:Endpoint not configured");

var credential = new DefaultAzureCredential();
var openAiClient = new AzureOpenAIClient(
    new Uri(openAiEndpoint),
    credential);

builder.Services.AddSingleton(openAiClient);
builder.Services.AddScoped<AgentOrchestrator>();

// Azure Blob Storage - using connection string
var storageConnectionString = builder.Configuration["Azure:Storage:ConnectionString"];
var containerName = builder.Configuration["Azure:Storage:ContainerName"];
var blobName = builder.Configuration["Azure:Storage:BlobName"];

if (string.IsNullOrEmpty(storageConnectionString) || string.IsNullOrEmpty(containerName) || string.IsNullOrEmpty(blobName))
    throw new InvalidOperationException("Azure:Storage:ConnectionString, ContainerName, and BlobName must be configured");

// Parse connection string to extract account name
string accountName = ExtractAccountName(storageConnectionString);

var blobContainerClient = new BlobContainerClient(
    new Uri($"https://{accountName}.blob.core.windows.net/{containerName}"),
    credential);

builder.Services.AddSingleton(blobContainerClient);
builder.Services.AddScoped<DataIngestService>();

// Helper functions to parse connection string
string ExtractAccountName(string connectionString)
{
    var parts = connectionString.Split(';');
    foreach (var part in parts)
    {
        if (part.StartsWith("AccountName="))
            return part.Substring("AccountName=".Length);
    }
    throw new InvalidOperationException("AccountName not found in connection string");
}

// Logging
builder.Services.AddLogging(config =>
{
    config.AddConsole();
    config.AddDebug();
});

// API controllers
builder.Services.AddControllers();
builder.Services.AddRazorPages();
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

var app = builder.Build();

// ===================================
// AUTOMATIC STARTUP: CREATE INDEX + SYNC DATA
// ===================================
using (var scope = app.Services.CreateScope())
{
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();

    // Step 1: Ensure the search index exists (creates it if missing)
    try
    {
        var indexCreator = scope.ServiceProvider.GetRequiredService<SearchIndexCreationService>();
        await indexCreator.EnsureIndexExistsAsync(searchIndexName);
    }
    catch (Exception ex)
    {
        logger.LogError($"Index creation failed: {ex.Message}");
    }

    // Step 2: Sync data from blob storage into the index
    try
    {
        var ingestService = scope.ServiceProvider.GetRequiredService<DataIngestService>();
        logger.LogInformation("Starting automatic data sync from blob storage...");
        await ingestService.IngestFromBlobAsync(blobName);
        logger.LogInformation("Data sync completed successfully");
    }
    catch (Exception ex)
    {
        logger.LogError($"Data sync failed: {ex.Message}");
        // Don't throw - app should still start even if sync fails
    }
}

// ===================================
// MIDDLEWARE
// ===================================
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseCors("AllowAll");
app.UseRouting();

// ===================================
// ENDPOINTS
// ===================================

// Health check endpoint
app.MapGet("/health", () => Results.Ok(new { status = "healthy" }))
    .WithName("Health")
    .WithOpenApi();

// Chat endpoint - Core API for doctor search
app.MapPost("/api/chat", ChatEndpoint)
    .WithName("Chat")
    .WithOpenApi()
    .Accepts<ChatSearchRequest>("application/json")
    .Produces<ChatSearchResponse>(StatusCodes.Status200OK);

// Data ingestion endpoint - Load doctors from blob storage
app.MapPost("/api/ingest", IngestDataEndpoint)
    .WithName("IngestData")
    .WithOpenApi()
    .Produces<IngestResponse>(StatusCodes.Status200OK);

app.MapRazorPages();
app.MapControllers();

app.Run();

// ===================================
// ENDPOINT IMPLEMENTATIONS
// ===================================

/// <summary>
/// Main chat endpoint.
/// Accepts natural language doctor search query, returns structured results.
/// </summary>
async Task<ChatSearchResponse> ChatEndpoint(
    ChatSearchRequest request,
    AgentOrchestrator agent,
    ILogger<Program> logger)
{
    logger.LogInformation($"Chat request: {request.Query}");
    
    var response = await agent.ProcessUserQueryAsync(
        request.Query,
        request.UserLatitude,
        request.UserLongitude);
    
    logger.LogInformation($"Sending {response.Results.Count} results");
    return response;
}

/// <summary>
/// Data ingestion endpoint.
/// Loads doctor data from Azure Blob Storage into the search index.
/// </summary>
async Task<IngestResponse> IngestDataEndpoint(
    IngestRequest request,
    DataIngestService ingestService,
    ILogger<Program> logger)
{
    try
    {
        logger.LogInformation($"Ingesting data from blob: {request.BlobFileName}");
        
        await ingestService.IngestFromBlobAsync(request.BlobFileName);
        
        return new IngestResponse 
        { 
            Success = true,
            Message = $"Successfully ingested data from {request.BlobFileName}"
        };
    }
    catch (Exception ex)
    {
        logger.LogError($"Ingestion failed: {ex.Message}");
        return new IngestResponse 
        { 
            Success = false,
            Message = $"Error: {ex.Message}"
        };
    }
}

// ===================================
// REQUEST/RESPONSE MODELS
// ===================================

public class IngestRequest
{
    public string BlobFileName { get; set; } = string.Empty;
}

public class IngestResponse
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
}
