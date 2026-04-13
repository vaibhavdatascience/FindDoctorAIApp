using Azure;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;

namespace FindDoctor.Web.Utilities;

/// <summary>
/// Creates the doctors Azure AI Search index if it doesn't already exist.
/// Safe to call on every startup - skips creation if index is already present.
/// </summary>
public class SearchIndexCreationService
{
    private readonly SearchIndexClient _indexClient;
    private readonly ILogger<SearchIndexCreationService> _logger;

    public SearchIndexCreationService(
        SearchIndexClient indexClient,
        ILogger<SearchIndexCreationService> logger)
    {
        _indexClient = indexClient;
        _logger = logger;
    }

    public async Task EnsureIndexExistsAsync(string indexName)
    {
        try
        {
            // Check if index already exists
            await _indexClient.GetIndexAsync(indexName);
            _logger.LogInformation("Search index '{IndexName}' already exists", indexName);
            return;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            _logger.LogInformation("Index '{IndexName}' not found - creating...", indexName);
        }

        var index = BuildDoctorIndex(indexName);
        await _indexClient.CreateIndexAsync(index);
        _logger.LogInformation("Search index '{IndexName}' created successfully", indexName);
    }

    private static SearchIndex BuildDoctorIndex(string indexName)
    {
        return new SearchIndex(indexName)
        {
            Fields =
            {
                new SimpleField("DoctorId", SearchFieldDataType.String) { IsKey = true, IsFilterable = true },
                new SearchableField("FirstName")   { IsFilterable = true, IsSortable = true },
                new SearchableField("LastName")    { IsFilterable = true, IsSortable = true },
                new SearchableField("Specialty")   { IsFilterable = true },
                new SearchableField("SpecialtiesCombined"),
                new SearchableField("ClinicalTerms"),
                new SearchableField("ClinicalAliases"),
                new SimpleField("ProviderType",    SearchFieldDataType.String),
                new SimpleField("Gender",          SearchFieldDataType.String) { IsFilterable = true },
                new SearchableField("OfficeLocationName"),
                new SearchableField("City")        { IsFilterable = true },
                new SimpleField("State",           SearchFieldDataType.String) { IsFilterable = true },
                new SearchableField("Zip")         { IsFilterable = true },
                new SimpleField("Phone",           SearchFieldDataType.String),
                new SimpleField("OffersOnlineScheduling", SearchFieldDataType.Boolean) { IsFilterable = true },
                new SimpleField("Latitude",        SearchFieldDataType.Double)  { IsFilterable = true },
                new SimpleField("Longitude",       SearchFieldDataType.Double)  { IsFilterable = true },
                new SearchableField("Languages", collection: true)
            },
            SemanticSearch = new SemanticSearch
            {
                Configurations =
                {
                    new SemanticConfiguration("default", new SemanticPrioritizedFields
                    {
                        TitleField       = new SemanticField("Specialty"),
                        ContentFields    = { new SemanticField("SpecialtiesCombined"), new SemanticField("ClinicalTerms") },
                        KeywordsFields   = { new SemanticField("ClinicalAliases"), new SemanticField("OfficeLocationName") }
                    })
                }
            }
        };
    }
}
