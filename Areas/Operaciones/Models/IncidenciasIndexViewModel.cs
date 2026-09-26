using System.Text.Json.Serialization;
using PlataformaIncidencias.Models;

namespace PlataformaIncidencias.Areas.Operaciones.Models;

/// <summary>
/// Modelo de la vista <c>/Operaciones/Incidencias</c>.
/// Transporta el listado de incidencias-abiertas ya filtradas y el término de búsqueda,
/// para poder repoblar el formulario tras la consulta.
/// </summary>
public class IncidenciasIndexViewModel
{
    /// <summary>Término introducido por el usuario. Puede estar vacío.</summary>
    public string SearchQuery { get; set; } = string.Empty;

    /// <summary>Incidencias a mostrar: todas las abiertas, o las abiertas que coincidieron con la búsqueda.</summary>
    public IReadOnlyList<Incidencia> Incidencias { get; set; } = Array.Empty<Incidencia>();

    /// <summary><see langword="true"/> cuando la vista se está mostrando como resultado de una búsqueda.</summary>
    public bool BusquedaRealizada => !string.IsNullOrWhiteSpace(SearchQuery);
}

/// <summary>
/// Proyección mínima de un hit del índice de Algolia.
/// Sólo se solicita el <c>objectID</c>: el resto de los datos se resuelven contra
/// la base de datos local, que es la fuente de verdad del estado.
/// </summary>
public sealed class IncidenciaAlgoliaHit
{
    [JsonPropertyName("objectID")]
    public string? ObjectID { get; set; }
}
