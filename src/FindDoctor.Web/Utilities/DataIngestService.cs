using Azure.Search.Documents;
using Azure.Search.Documents.Models;
using Azure.Storage.Blobs;
using Azure.Core.GeoJson;
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
    /// Load doctors from a local JSON file and upload to search index
    /// </summary>
    public async Task IngestFromLocalFileAsync(string filePath)
    {
        // Local file ingestion is the resilience path used when blob access is not
        // available (for example local developer machine without storage RBAC).
        // Keeping this path first-class ensures the app remains demo/test ready.
        _logger.LogInformation($"Reading doctor data from local file: {filePath}");
        var json = await File.ReadAllTextAsync(filePath);
        await IngestJsonAsync(json);
    }

    /// <summary>
    /// Load doctors from Azure Blob Storage JSON file and upload to search index
    /// </summary>
    public async Task IngestFromBlobAsync(string blobFileName)
    {
        // Blob ingestion is the primary data refresh mechanism in cloud setups.
        // This method validates blob existence, downloads JSON content, and then
        // delegates all parsing/indexing behavior to IngestJsonAsync so local and
        // cloud ingestion paths share identical transformation logic.
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

            await IngestJsonAsync(json);
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error ingesting from blob: {ex.Message}");
            throw;
        }
    }

    private async Task IngestJsonAsync(string json)
    {
        // JSON parsing supports two source shapes used across environments:
        // 1) wrapped object: { "doctors": [...] }
        // 2) direct array: [ ... ]
        // If standard deserialization fails, a recovery parser attempts partial
        // ingestion so one malformed record does not block all data updates.
        // Parse JSON - handle both wrapped { "doctors": [...] } and direct array formats
        List<DoctorDataModel> doctors;
        var options = new JsonSerializerOptions 
        { 
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
        options.Converters.Add(new StringListConverter());

        try
        {
            var trimmed = json.TrimStart();
            if (trimmed.StartsWith("{"))
            {
                // Wrapped format: { "doctors": [...] }
                var wrapper = JsonSerializer.Deserialize<DoctorListWrapper>(json, options);
                doctors = wrapper?.Doctors ?? new();
                _logger.LogInformation($"Successfully parsed {doctors.Count} doctors from wrapped JSON object");
            }
            else
            {
                // Direct array format
                doctors = JsonSerializer.Deserialize<List<DoctorDataModel>>(json, options) ?? new();
                _logger.LogInformation($"Successfully parsed {doctors.Count} doctors from direct JSON array");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning($"JSON parse failed: {ex.Message}. Attempting line-by-line parse...");
            doctors = await ParseDoctorsLineByLine(json);
            _logger.LogInformation($"Line-by-line parsing recovered {doctors.Count} doctors");
        }

        if (doctors == null || doctors.Count == 0)
            throw new InvalidOperationException("No doctors found in JSON file");

        _logger.LogInformation($"Loaded {doctors.Count} doctors");

        // Convert to search documents and upload in batches
        await UploadDocumentsAsync(doctors);
    }

    /// <summary>
    /// Parse doctors JSON line-by-line, skipping records that fail to deserialize
    /// </summary>
    private Task<List<DoctorDataModel>> ParseDoctorsLineByLine(string json)
    {
        // Recovery parser walks the JSON text by object depth and attempts record-
        // level deserialization. This allows ingestion to continue in the presence
        // of isolated malformed objects while logging exactly what was skipped.
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
        // Upload uses MergeOrUpload in batches for idempotent incremental indexing:
        // new doctor IDs are inserted and existing IDs are updated in-place. Batch
        // size is tuned to balance throughput and operational observability.
        const int batchSize = 100;
        var documents = doctors
            .Where(IsValidDoctorRecord)
            .SelectMany(ConvertToSearchDocuments)
            .ToList();

        if (documents.Count == 0)
            throw new InvalidOperationException("No searchable provider-location documents were produced from source data");

        var totalUploaded = 0;

        for (int i = 0; i < documents.Count; i += batchSize)
        {
            var batch = documents.Skip(i).Take(batchSize).ToList();

            try
            {
                // Use MergeOrUpload for incremental updates:
                // - New documents: inserted
                // - Existing documents: updated
                var batch_action = IndexDocumentsBatch.MergeOrUpload(batch);
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

        _logger.LogInformation($"Total uploaded: {totalUploaded}/{documents.Count}");
    }

    /// <summary>
    /// Flatten provider records into location-specific search documents.
    /// </summary>
    private IEnumerable<DoctorDocument> ConvertToSearchDocuments(DoctorDataModel doctor)
    {
        var baseDoctorId = ResolveDoctorId(doctor);
        var locations = doctor.Locations ?? new List<DoctorLocationDataModel>();

        if (locations.Count == 0)
        {
            yield return CreateSearchDocument(doctor, null, baseDoctorId);
            yield break;
        }

        var index = 0;
        foreach (var location in locations)
        {
            index++;
            var locationSuffix = !string.IsNullOrWhiteSpace(location.PractitionerLocationId)
                ? location.PractitionerLocationId
                : index.ToString();
            yield return CreateSearchDocument(doctor, location, $"{baseDoctorId}_{locationSuffix}");
        }
    }

    private DoctorDocument CreateSearchDocument(
        DoctorDataModel doctor,
        DoctorLocationDataModel? location,
        string documentId)
    {
        var normalizedClinicalTerms = doctor.ClinicalTerms ?? new List<ClinicalTermDataModel>();
        var normalizedClinicalAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var alias in doctor.ClinicalAliases ?? new List<string>())
        {
            if (!string.IsNullOrWhiteSpace(alias))
                normalizedClinicalAliases.Add(alias.Trim());
        }

        foreach (var term in normalizedClinicalTerms)
        {
            foreach (var alias in term.Aliases)
            {
                if (!string.IsNullOrWhiteSpace(alias))
                    normalizedClinicalAliases.Add(alias.Trim());
            }
        }

        var clinicalPreferenceMap = BuildClinicalPreferenceMap(normalizedClinicalTerms, normalizedClinicalAliases, doctor.MaxPreferenceLevel ?? 0);

        var latitude = location?.Latitude ?? doctor.Latitude;
        var longitude = location?.Longitude ?? doctor.Longitude;
        var geoPoint = latitude.HasValue && longitude.HasValue
            ? new GeoPoint(longitude.Value, latitude.Value)
            : null;

        return new DoctorDocument
        {
            DoctorId = documentId,
            FirstName = doctor.FirstName,
            LastName = doctor.LastName,
            Specialty = ResolveSpecialty(doctor),
            SpecialtiesCombined = ResolveSpecialtiesCombined(doctor),
            ClinicalTerms = string.Join(", ", normalizedClinicalTerms.Select(t => t.Term).Where(t => !string.IsNullOrWhiteSpace(t))),
            ClinicalAliases = string.Join(", ", normalizedClinicalAliases),
            ClinicalPreferenceMap = clinicalPreferenceMap,
            ProviderType = doctor.ProviderType ?? "Provider",
            UHProvider = NormalizeUhProvider(doctor.UHProvider, doctor.IsUHProviderType),
            Gender = doctor.Gender,
            Languages = doctor.Languages ?? new List<string>(),
            OfficeLocationName = location?.OfficeLocationName ?? doctor.OfficeLocationName,
            City = location?.City ?? doctor.City,
            State = location?.State ?? doctor.State,
            Zip = location?.Zip ?? doctor.Zip,
            Phone = location?.AppointmentPhone ?? location?.Phone ?? doctor.Phone,
            OffersOnlineScheduling = doctor.OffersOnlineScheduling ?? false,
            Latitude = latitude,
            Longitude = longitude,
            GeoLocation = geoPoint
        };
    }

    private static bool IsValidDoctorRecord(DoctorDataModel doctor)
    {
        if (!string.IsNullOrWhiteSpace(doctor.DoctorId) || !string.IsNullOrWhiteSpace(doctor.Id) || doctor.PractitionerId.HasValue)
            return true;

        return !string.IsNullOrWhiteSpace(doctor.FirstName)
            || !string.IsNullOrWhiteSpace(doctor.LastName)
            || !string.IsNullOrWhiteSpace(doctor.Specialty)
            || (doctor.Locations?.Count > 0);
    }

    private static string BuildClinicalPreferenceMap(
        List<ClinicalTermDataModel> terms,
        HashSet<string> fallbackAliases,
        int fallbackPreference)
    {
        var tokenToPreference = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var term in terms)
        {
            var normalizedTerm = (term.Term ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(normalizedTerm))
                tokenToPreference[normalizedTerm] = Math.Max(tokenToPreference.GetValueOrDefault(normalizedTerm), term.PreferenceLevel);

            foreach (var alias in term.Aliases)
            {
                var normalizedAlias = (alias ?? string.Empty).Trim();
                if (!string.IsNullOrWhiteSpace(normalizedAlias))
                    tokenToPreference[normalizedAlias] = Math.Max(tokenToPreference.GetValueOrDefault(normalizedAlias), term.PreferenceLevel);
            }
        }

        foreach (var alias in fallbackAliases)
        {
            var normalizedAlias = alias.Trim();
            if (!string.IsNullOrWhiteSpace(normalizedAlias))
                tokenToPreference[normalizedAlias] = Math.Max(tokenToPreference.GetValueOrDefault(normalizedAlias), fallbackPreference);
        }

        return string.Join(
            '|',
            tokenToPreference
                .OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase)
                .Select(kvp => $"{kvp.Key}:{kvp.Value}"));
    }

    private static string ResolveDoctorId(DoctorDataModel doctor)
    {
        if (!string.IsNullOrWhiteSpace(doctor.DoctorId))
            return doctor.DoctorId;
        if (!string.IsNullOrWhiteSpace(doctor.Id))
            return doctor.Id;
        if (doctor.PractitionerId.HasValue)
            return doctor.PractitionerId.Value.ToString();

        return Guid.NewGuid().ToString("N");
    }

    private static string ResolveSpecialty(DoctorDataModel doctor)
    {
        if (!string.IsNullOrWhiteSpace(doctor.Specialty))
            return doctor.Specialty;

        return doctor.Specialties?.FirstOrDefault() ?? string.Empty;
    }

    private static string ResolveSpecialtiesCombined(DoctorDataModel doctor)
    {
        if (!string.IsNullOrWhiteSpace(doctor.SpecialtiesCombined))
            return doctor.SpecialtiesCombined;

        var specialties = doctor.Specialties ?? new List<string>();
        if (specialties.Count == 0 && !string.IsNullOrWhiteSpace(doctor.Specialty))
            specialties = new List<string> { doctor.Specialty };

        return string.Join(", ", specialties);
    }

    private static string NormalizeUhProvider(string? value, int? providerType)
    {
        // Normalize upstream yes/no variants into a stable value set consumed by
        // ranking and UI logic.
        if (providerType.HasValue)
            return providerType.Value == 1 ? "yes" : "No";

        if (string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase))
            return "yes";

        return "No";
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
/// Wrapper for JSON files with { "doctors": [...] } format
/// </summary>
public class DoctorListWrapper
{
    [JsonPropertyName("doctors")]
    public List<DoctorDataModel> Doctors { get; set; } = new();
}

/// <summary>
/// Data model for importing doctor records
/// </summary>
public class DoctorDataModel
{
    [JsonPropertyName("doctorId")]
    public string DoctorId { get; set; } = string.Empty;

    [JsonPropertyName("Id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("PractitionerId")]
    public int? PractitionerId { get; set; }

    [JsonPropertyName("FirstName")]
    public string FirstName { get; set; } = string.Empty;

    [JsonPropertyName("LastName")]
    public string LastName { get; set; } = string.Empty;

    [JsonPropertyName("Specialty")]
    public string Specialty { get; set; } = string.Empty;

    [JsonPropertyName("Specialties")]
    public List<string>? Specialties { get; set; }

    [JsonPropertyName("SpecialtiesCombined")]
    public string? SpecialtiesCombined { get; set; }

    [JsonPropertyName("ClinicalTerms")]
    [JsonConverter(typeof(ClinicalTermsConverter))]
    public List<ClinicalTermDataModel>? ClinicalTerms { get; set; }

    [JsonPropertyName("ClinicalAliases")]
    [JsonConverter(typeof(StringListConverter))]
    public List<string>? ClinicalAliases { get; set; }

    [JsonPropertyName("MaxPreferenceLevel")]
    public int? MaxPreferenceLevel { get; set; }

    [JsonPropertyName("ProviderType")]
    public string? ProviderType { get; set; }

    [JsonPropertyName("UHProvider")]
    public string? UHProvider { get; set; }

    [JsonPropertyName("IsUHProviderType")]
    public int? IsUHProviderType { get; set; }

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

    [JsonPropertyName("Locations")]
    public List<DoctorLocationDataModel>? Locations { get; set; }
}

public class DoctorLocationDataModel
{
    [JsonPropertyName("PractitionerLocationId")]
    public string PractitionerLocationId { get; set; } = string.Empty;

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

    [JsonPropertyName("AppointmentPhone")]
    public string AppointmentPhone { get; set; } = string.Empty;

    [JsonPropertyName("Latitude")]
    public double? Latitude { get; set; }

    [JsonPropertyName("Longitude")]
    public double? Longitude { get; set; }
}

public class ClinicalTermDataModel
{
    public string Term { get; set; } = string.Empty;
    public List<string> Aliases { get; set; } = new();
    public int PreferenceLevel { get; set; }
}

public class ClinicalTermsConverter : JsonConverter<List<ClinicalTermDataModel>>
{
    public override List<ClinicalTermDataModel> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
            return new List<ClinicalTermDataModel>();

        if (reader.TokenType == JsonTokenType.String)
        {
            var value = reader.GetString();
            if (string.IsNullOrWhiteSpace(value))
                return new List<ClinicalTermDataModel>();

            return value
                .Split(new[] { "|", "," }, StringSplitOptions.RemoveEmptyEntries)
                .Select(v => new ClinicalTermDataModel { Term = v.Trim(), PreferenceLevel = 0 })
                .Where(v => !string.IsNullOrWhiteSpace(v.Term))
                .ToList();
        }

        if (reader.TokenType != JsonTokenType.StartArray)
            return new List<ClinicalTermDataModel>();

        using var document = JsonDocument.ParseValue(ref reader);
        var results = new List<ClinicalTermDataModel>();

        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var term = item.GetString();
                if (!string.IsNullOrWhiteSpace(term))
                {
                    results.Add(new ClinicalTermDataModel
                    {
                        Term = term.Trim(),
                        PreferenceLevel = 0
                    });
                }
                continue;
            }

            if (item.ValueKind != JsonValueKind.Object)
                continue;

            var termValue = GetStringProperty(item, "term", "clinicalTerm", "name", "value", "label");
            var aliases = GetStringListProperty(item, "aliases", "clinicalAliases", "alias");
            var preferenceLevel = GetIntProperty(item, "preferenceLevel", "PreferenceLevel", "level");

            if (!string.IsNullOrWhiteSpace(termValue))
            {
                results.Add(new ClinicalTermDataModel
                {
                    Term = termValue.Trim(),
                    Aliases = aliases,
                    PreferenceLevel = preferenceLevel
                });
            }
        }

        return results;
    }

    public override void Write(Utf8JsonWriter writer, List<ClinicalTermDataModel> value, JsonSerializerOptions options)
    {
        JsonSerializer.Serialize(writer, value, options);
    }

    private static string? GetStringProperty(JsonElement element, params string[] propertyNames)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (!propertyNames.Any(name => string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)))
                continue;

            if (property.Value.ValueKind == JsonValueKind.String)
                return property.Value.GetString();
        }

        return null;
    }

    private static int GetIntProperty(JsonElement element, params string[] propertyNames)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (!propertyNames.Any(name => string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)))
                continue;

            if (property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt32(out var parsed))
                return parsed;

            if (property.Value.ValueKind == JsonValueKind.String && int.TryParse(property.Value.GetString(), out parsed))
                return parsed;
        }

        return 0;
    }

    private static List<string> GetStringListProperty(JsonElement element, params string[] propertyNames)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (!propertyNames.Any(name => string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)))
                continue;

            if (property.Value.ValueKind == JsonValueKind.Array)
            {
                return property.Value
                    .EnumerateArray()
                    .Where(value => value.ValueKind == JsonValueKind.String)
                    .Select(value => value.GetString() ?? string.Empty)
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Select(value => value.Trim())
                    .ToList();
            }

            if (property.Value.ValueKind == JsonValueKind.String)
            {
                var value = property.Value.GetString();
                if (string.IsNullOrWhiteSpace(value))
                    return new List<string>();

                return value
                    .Split(new[] { "|", "," }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(v => v.Trim())
                    .Where(v => !string.IsNullOrWhiteSpace(v))
                    .ToList();
            }
        }

        return new List<string>();
    }
}

/// <summary>
/// Custom JSON converter that handles null values in List<string> fields
/// Converts null to empty list for graceful deserialization
/// </summary>
public class StringListConverter : JsonConverter<List<string>>
{
    public override List<string> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        // Source systems may emit list fields as null, string, or array. This
        // converter tolerates all three shapes and returns a safe list value,
        // preventing deserialization failures from schema inconsistencies.
        if (reader.TokenType == JsonTokenType.Null)
            return new List<string>();

        // Some fields are pipe-delimited strings instead of arrays
        if (reader.TokenType == JsonTokenType.String)
        {
            var value = reader.GetString();
            if (string.IsNullOrWhiteSpace(value)) return new List<string>();
            return value
                        .Split(new[] { "|", "," }, StringSplitOptions.RemoveEmptyEntries)
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
        // Writes null for empty lists to keep output compact and aligned with the
        // source schema style used in existing data files.
        if (value == null || value.Count == 0)
            writer.WriteNullValue();
        else
            JsonSerializer.Serialize(writer, value, options);
    }
}
