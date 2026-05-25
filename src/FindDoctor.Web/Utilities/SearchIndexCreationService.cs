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
        // This method is designed to be safe on every startup. It first attempts
        // to fetch the index; if it exists, it performs lightweight compatibility
        // checks (for example, adding newly required fields like UHProvider).
        // If it does not exist, it creates a full index from code so deployments
        // do not depend on manual portal setup. This provides idempotent,
        // environment-independent index provisioning.
        SearchIndex? existingIndex = null;
        try
        {
            // Check if index already exists
            existingIndex = (await _indexClient.GetIndexAsync(indexName)).Value;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            _logger.LogInformation("Index '{IndexName}' not found - creating...", indexName);
        }

        if (existingIndex != null)
        {
            var hasUhProvider = existingIndex.Fields.Any(f => string.Equals(f.Name, "UHProvider", StringComparison.OrdinalIgnoreCase));
            var hasGeoLocation = existingIndex.Fields.Any(f => string.Equals(f.Name, "GeoLocation", StringComparison.OrdinalIgnoreCase));
            var hasClinicalPreferenceMap = existingIndex.Fields.Any(f => string.Equals(f.Name, "ClinicalPreferenceMap", StringComparison.OrdinalIgnoreCase));

            if (hasUhProvider && hasGeoLocation && hasClinicalPreferenceMap)
            {
                _logger.LogInformation("Search index '{IndexName}' already exists", indexName);
                return;
            }

            if (!hasUhProvider)
                existingIndex.Fields.Add(new SimpleField("UHProvider", SearchFieldDataType.String) { IsFilterable = true });

            if (!hasGeoLocation)
                existingIndex.Fields.Add(new SimpleField("GeoLocation", SearchFieldDataType.GeographyPoint) { IsFilterable = true });

            if (!hasClinicalPreferenceMap)
                existingIndex.Fields.Add(new SearchableField("ClinicalPreferenceMap"));

            await _indexClient.CreateOrUpdateIndexAsync(existingIndex);
            _logger.LogInformation("Search index '{IndexName}' updated with newly required fields", indexName);
            return;
        }

        var index = BuildDoctorIndex(indexName);
        await _indexClient.CreateIndexAsync(index);
        _logger.LogInformation("Search index '{IndexName}' created successfully", indexName);
    }

    private static SearchIndex BuildDoctorIndex(string indexName)
    {
        // Field design principles used in this index:
        // 1) SearchableField is used for free-text user intent matching where we
        //    want lexical + semantic relevance (names, specialties, conditions,
        //    aliases, and human-readable locations).
        // 2) SimpleField is used for exact filtering/sorting/faceting semantics
        //    where tokenization would be harmful (booleans, enums, IDs, numeric
        //    coordinates, and stable categorical attributes).
        // 3) IsFilterable is enabled on fields frequently used for deterministic
        //    constraints (Zip, Gender, UHProvider, OffersOnlineScheduling).
        // 4) IsSortable is enabled only where explicit ordering is meaningful and
        //    low-risk (FirstName, LastName).
        // 5) Latitude/Longitude are modeled as filterable numeric primitives so
        //    the application can compute geo distance at query time.
        return new SearchIndex(indexName)
        {
            Fields =
            {
                // Primary key used for merge-or-upload ingestion operations.
                new SimpleField("DoctorId", SearchFieldDataType.String) { IsKey = true, IsFilterable = true },

                // Provider name fields are searchable for natural language lookup,
                // filterable for exact constraints, and sortable for deterministic
                // output ordering in admin/reporting scenarios.
                new SearchableField("FirstName")   { IsFilterable = true, IsSortable = true },
                new SearchableField("LastName")    { IsFilterable = true, IsSortable = true },

                // Specialty and clinically-relevant text are searchable because
                // user intent often arrives as free text (e.g., symptoms,
                // colloquial terms, and specialty names).
                new SearchableField("Specialty")   { IsFilterable = true },
                new SearchableField("SpecialtiesCombined"),
                new SearchableField("ClinicalTerms"),
                new SearchableField("ClinicalAliases"),
                new SearchableField("ClinicalPreferenceMap"),

                // Provider metadata is modeled as simple fields for exact-match
                // logic and predictable filtering behavior.
                new SimpleField("ProviderType",    SearchFieldDataType.String),
                new SimpleField("UHProvider",      SearchFieldDataType.String) { IsFilterable = true },
                new SimpleField("Gender",          SearchFieldDataType.String) { IsFilterable = true },

                // Location text is searchable (human-entered city/clinic terms),
                // while state/zip are also filterable for exact geospatial anchors.
                new SearchableField("OfficeLocationName"),
                new SearchableField("City")        { IsFilterable = true },
                new SimpleField("State",           SearchFieldDataType.String) { IsFilterable = true },
                new SearchableField("Zip")         { IsFilterable = true },

                // Contact/scheduling primitives are exact values; scheduling must
                // support strict true/false filtering.
                new SimpleField("Phone",           SearchFieldDataType.String),
                new SimpleField("OffersOnlineScheduling", SearchFieldDataType.Boolean) { IsFilterable = true },

                // Coordinates are numeric primitives required for distance ranking.
                new SimpleField("Latitude",        SearchFieldDataType.Double)  { IsFilterable = true },
                new SimpleField("Longitude",       SearchFieldDataType.Double)  { IsFilterable = true },
                new SimpleField("GeoLocation",     SearchFieldDataType.GeographyPoint) { IsFilterable = true },

                // Languages supports multilingual preference matching. Collection
                // type preserves each language token independently.
                new SearchableField("Languages", collection: true)
            },
            SemanticSearch = new SemanticSearch
            {
                Configurations =
                {
                    // Semantic config biases ranking toward medical intent:
                    // - TitleField: specialty as the top-level concept.
                    // - ContentFields: rich medical context (terms/specialties).
                    // - KeywordsFields: aliases and location phrases to improve
                    //   colloquial and practical query matching.
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
