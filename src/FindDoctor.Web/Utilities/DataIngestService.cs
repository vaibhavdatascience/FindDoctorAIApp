using Azure.Search.Documents;
using Azure.Search.Documents.Models;
using Azure.Storage.Blobs;
using FindDoctor.Web.Services;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FindDoctor.Web.Utilities;

/// <summary>
/// Utility for ingesting doctor data from JSON into Azure AI Search.
/// Loads from Azure Blob Storage container using Managed Identity.
/// </summary>
public class DataIngestService
{
    private readonly SearchClient _searchClient;
    private readonly BlobContainerClient _blobContainerClient;
    private readonly ILogger<DataIngestService> _logger;

    public DataIngestService(
        SearchClient searchClient,
        BlobContainerClient blobContainerClient,
        ILogger<DataIngestService> logger)
    {
        _searchClient = searchClient;
        _blobContainerClient = blobContainerClient;
        _logger = logger;
    }

    /// <summary>
    /// Load doctors from Azure Blob Storage JSON file and upload to search index
    /// </summary>
    public async Task IngestFromBlobAsync(string blobFileName)
    {
        try
        {
            _logger.LogInformation($"Reading doctor data from blob: {blobFileName}");
            
            // Get reference to the blob
            var blobClient = _blobContainerClient.GetBlobClient(blobFileName);
            
            // Check if blob exists
            if (!await blobClient.ExistsAsync())
                throw new FileNotFoundException($"Blob not found: {blobFileName}");

            // Download blob content
            var download = await blobClient.DownloadAsync();
            
            // Read as string
            using var streamReader = new StreamReader(download.Value.Content);
            var json = await streamReader.ReadToEndAsync();
            
            // Parse JSON - handle both direct array and wrapped object formats
            List<DoctorDataModel> doctors;
            try
            {
                // Try parsing as direct array first (most common format)
                var options = new JsonSerializerOptions 
                { 
                    PropertyNameCaseInsensitive = true,
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                    UnknownTypeHandling = JsonUnknownTypeHandling.JsonElement,
                    WriteIndented = false
                };
                options.Converters.Add(new StringListConverter());
                doctors = JsonSerializer.Deserialize<List<DoctorDataModel>>(json, options) ?? new();
                _logger.LogInformation($"Successfully parsed {doctors.Count} doctors from direct JSON array");
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Array parse with strict converters failed: {ex.Message}. Attempting line-by-line parse...");
                // Fall back to line-by-line parsing to skip problematic records
                doctors = await ParseDoctorsLineByLine(json);
                _logger.LogInformation($"Line-by-line parsing recovered {doctors.Count} doctors");
            }
            
            if (doctors == null || doctors.Count == 0)
                throw new InvalidOperationException("No doctors found in JSON file");

            _logger.LogInformation($"Loaded {doctors.Count} doctors from blob storage");

            // Convert to search documents and upload in batches
            await UploadDocumentsAsync(doctors);
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error ingesting from blob: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Parse doctors JSON line-by-line, skipping records that fail to deserialize
    /// </summary>
    private Task<List<DoctorDataModel>> ParseDoctorsLineByLine(string json)
    {
        var doctors = new List<DoctorDataModel>();
        var options = new JsonSerializerOptions 
        { 
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
        options.Converters.Add(new StringListConverter());

        // Remove brackets and split by comma, then try to parse each record
        var trimmed = json.Trim();
        if (trimmed.StartsWith("["))
            trimmed = trimmed.Substring(1);
        if (trimmed.EndsWith("]"))
            trimmed = trimmed.Substring(0, trimmed.Length - 1);

        // Simple JSON array parser
        var depth = 0;
        var currentRecord = "";
        foreach (var c in trimmed)
        {
            if (c == '{') depth++;
            if (c == '}') depth--;
            
            if (c == ',' && depth == 0)
            {
                if (!string.IsNullOrWhiteSpace(currentRecord))
                {
                    try
                    {
                        var doctor = JsonSerializer.Deserialize<DoctorDataModel>(currentRecord, options);
                        if (doctor != null)
                            doctors.Add(doctor);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning($"Skipped problematic record: {ex.Message}");
                    }
                }
                currentRecord = "";
            }
            else
                currentRecord += c;
        }

        // Don't forget the last record
        if (!string.IsNullOrWhiteSpace(currentRecord))
        {
            try
            {
                var doctor = JsonSerializer.Deserialize<DoctorDataModel>(currentRecord, options);
                if (doctor != null)
                    doctors.Add(doctor);
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Skipped final problematic record: {ex.Message}");
            }
        }

        return Task.FromResult(doctors);
    }

    /// <summary>
    /// Upload doctor documents to Azure AI Search in batches (incremental updates)
    /// Uses MergeOrUpload to handle both new and updated records
    /// </summary>
    private async Task UploadDocumentsAsync(List<DoctorDataModel> doctors)
    {
        const int batchSize = 100;
        var totalUploaded = 0;

        for (int i = 0; i < doctors.Count; i += batchSize)
        {
            var batch = doctors.Skip(i).Take(batchSize).ToList();
            var documents = batch.Select(ConvertToSearchDocument).ToList();

            try
            {
                // Use MergeOrUpload for incremental updates:
                // - New documents: inserted
                // - Existing documents: updated
                var batch_action = IndexDocumentsBatch.MergeOrUpload(documents);
                var result = await _searchClient.IndexDocumentsAsync(batch_action);
                
                var succeeded = result.Value.Results.Count(r => r.Succeeded);
                var failed = result.Value.Results.Count(r => !r.Succeeded);
                
                _logger.LogInformation($"Batch {i/batchSize + 1}: {succeeded} succeeded, {failed} failed");
                totalUploaded += succeeded;

                // Log any failures for debugging
                foreach (var failedResult in result.Value.Results.Where(r => !r.Succeeded))
                {
                    _logger.LogWarning($"Failed: {failedResult.Key} - {failedResult.ErrorMessage}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error uploading batch {i/batchSize + 1}: {ex.Message}");
            }
        }

        _logger.LogInformation($"Total uploaded: {totalUploaded}/{doctors.Count}");
    }

    /// <summary>
    /// Convert data model to search document
    /// </summary>
    private DoctorDocument ConvertToSearchDocument(DoctorDataModel doctor)
    {
        return new DoctorDocument
        {
            DoctorId = doctor.DoctorId,
            FirstName = doctor.FirstName,
            LastName = doctor.LastName,
            Specialty = doctor.Specialty,
            SpecialtiesCombined = string.Join(", ", doctor.Specialties ?? new List<string> { doctor.Specialty }),
            ClinicalTerms = string.Join(", ", doctor.ClinicalTerms ?? new List<string>()),
            ClinicalAliases = string.Join(", ", doctor.ClinicalAliases ?? new List<string>()),
            ProviderType = doctor.ProviderType ?? "Provider",
            Gender = doctor.Gender,
            Languages = doctor.Languages ?? new List<string>(),
            OfficeLocationName = doctor.OfficeLocationName,
            City = doctor.City,
            State = doctor.State,
            Zip = doctor.Zip,
            Phone = doctor.Phone,
            OffersOnlineScheduling = doctor.OffersOnlineScheduling ?? false,
            Latitude = doctor.Latitude,
            Longitude = doctor.Longitude
        };
    }
}

/// <summary>
/// JSON file structure for doctor data import
/// </summary>
public class DoctorDataFile
{
    [JsonPropertyName("doctors")]
    public List<DoctorDataModel> Doctors { get; set; } = new();
}

/// <summary>
/// Data model for importing doctor records
/// </summary>
public class DoctorDataModel
{
    [JsonPropertyName("Id")]
    public string DoctorId { get; set; } = string.Empty;

    [JsonPropertyName("FirstName")]
    public string FirstName { get; set; } = string.Empty;

    [JsonPropertyName("LastName")]
    public string LastName { get; set; } = string.Empty;

    [JsonPropertyName("Specialty")]
    public string Specialty { get; set; } = string.Empty;

    [JsonPropertyName("Specialties")]
    public List<string>? Specialties { get; set; }

    [JsonPropertyName("ClinicalTerms")]
    public List<string>? ClinicalTerms { get; set; }

    [JsonPropertyName("ClinicalAliases")]
    public List<string>? ClinicalAliases { get; set; }

    [JsonPropertyName("ProviderType")]
    public string? ProviderType { get; set; }

    [JsonPropertyName("Gender")]
    public string Gender { get; set; } = string.Empty;

    [JsonPropertyName("Languages")]
    public List<string>? Languages { get; set; }

    [JsonPropertyName("OfficeLocationName")]
    public string OfficeLocationName { get; set; } = string.Empty;

    [JsonPropertyName("City")]
    public string City { get; set; } = string.Empty;

    [JsonPropertyName("State")]
    public string State { get; set; } = string.Empty;

    [JsonPropertyName("Zip")]
    public string Zip { get; set; } = string.Empty;

    [JsonPropertyName("Phone")]
    public string Phone { get; set; } = string.Empty;

    [JsonPropertyName("OffersOnlineScheduling")]
    public bool? OffersOnlineScheduling { get; set; }

    [JsonPropertyName("Latitude")]
    public double? Latitude { get; set; }

    [JsonPropertyName("Longitude")]
    public double? Longitude { get; set; }
}

/// <summary>
/// Custom JSON converter that handles null values in List<string> fields
/// Converts null to empty list for graceful deserialization
/// </summary>
public class StringListConverter : JsonConverter<List<string>>
{
    public override List<string> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
            return new List<string>();

        // Some fields are pipe-delimited strings instead of arrays
        if (reader.TokenType == JsonTokenType.String)
        {
            var value = reader.GetString();
            if (string.IsNullOrWhiteSpace(value)) return new List<string>();
            return value.Split(" | ", StringSplitOptions.RemoveEmptyEntries)
                        .Select(s => s.Trim())
                        .Where(s => s.Length > 0)
                        .ToList();
        }

        // Read array manually to avoid recursive call into this converter
        if (reader.TokenType == JsonTokenType.StartArray)
        {
            var list = new List<string>();
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                if (reader.TokenType == JsonTokenType.String)
                    list.Add(reader.GetString() ?? string.Empty);
                else if (reader.TokenType == JsonTokenType.Null)
                    { /* skip nulls inside array */ }
                else
                    reader.Skip();
            }
            return list;
        }

        return new List<string>();
    }

    public override void Write(Utf8JsonWriter writer, List<string> value, JsonSerializerOptions options)
    {
        if (value == null || value.Count == 0)
            writer.WriteNullValue();
        else
            JsonSerializer.Serialize(writer, value, options);
    }
}
