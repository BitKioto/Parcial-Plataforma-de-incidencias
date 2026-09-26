namespace PlataformaIncidencias.Models;

/// <summary>
/// Configuración de la integración con Algolia, enlazada desde la sección
/// <c>Algolia</c> de <c>appsettings.json</c> o desde las variables de entorno
/// <c>ALGOLIA_APP_ID</c>, <c>ALGOLIA_SEARCH_KEY</c> y <c>ALGOLIA_INDEX_NAME</c>.
/// </summary>
public class AlgoliaSettings
{
    /// <summary>Nombre de la sección de configuración.</summary>
    public const string SectionName = "Algolia";

    /// <summary>Application ID de la aplicación de Algolia.</summary>
    public string AppId { get; set; } = string.Empty;

    /// <summary>
    /// Search API Key (clave de sólo consulta). Es la ÚNICA clave admitida por la aplicación:
    /// la Admin API Key nunca debe registrarse aquí, porque expondría escritura y borrado de índices.
    /// </summary>
    public string SearchApiKey { get; set; } = string.Empty;

    /// <summary>Nombre del índice contra el que se ejecutan las consultas de búsqueda.</summary>
    public string IndexName { get; set; } = string.Empty;

    /// <summary>Indica si se guardó la configuración mínima necesaria para consultar Algolia.</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(AppId)
        && !string.IsNullOrWhiteSpace(SearchApiKey)
        && !string.IsNullOrWhiteSpace(IndexName);
}
