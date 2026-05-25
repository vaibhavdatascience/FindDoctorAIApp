# FindDoctor

## What changed

- Flattened provider indexing for nested JSON (Option 1): one searchable document per provider location.
- Added `GeoLocation` to the Azure AI Search index schema and ingestion pipeline for geo-aware queries.
- Added clinical-term preference ranking support:
  - Ingestion now supports `ClinicalTerms` entries with aliases and `PreferenceLevel`.
  - Preference data is normalized into `ClinicalPreferenceMap` for query-time ranking.
  - Providers with higher matched clinical-term preference are ranked higher.
- Search now applies a geolocation pre-filter before ranking when a location anchor is available.
- Default location radius is now 50 miles (converted to km for Azure Search `geo.distance` filtering).
- Updated no-results behavior for location-based searches to return:
  - `No provider available near you. Modify the search and search again.`
- Added index migration guidance: perform a one-time index rebuild when moving from non-flattened to flattened location-level documents.
