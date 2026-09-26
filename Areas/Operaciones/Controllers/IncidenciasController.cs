using Algolia.Search.Clients;
using Algolia.Search.Exceptions;
using Algolia.Search.Models.Search;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PlataformaIncidencias.Areas.Operaciones.Models;
using PlataformaIncidencias.Data;
using PlataformaIncidencias.Models;

namespace PlataformaIncidencias.Areas.Operaciones.Controllers;

/// <summary>
/// Listado y búsqueda de incidencias.
/// La búsqueda de texto se delega en Algolia, pero el estado se resuelve siempre
/// contra la base de datos local: sólo se muestran incidencias con estado "Abierta".
/// </summary>
[Area("Operaciones")]
public class IncidenciasController : Controller
{
    /// <summary>Número máximo de hits solicitados a Algolia por consulta.</summary>
    private const int HitsPorPagina = 50;

    /// <summary>
    /// Tamaño de lote para las consultas "IN (...)" contra SQLite, que limita el número
    /// de parámetros vinculados por sentencia.
    /// </summary>
    private const int TamanoLoteIds = 500;

    private readonly ApplicationDbContext _context;
    private readonly AlgoliaSettings _algolia;
    private readonly ILogger<IncidenciasController> _logger;

    /// <summary>
    /// Cliente de sólo lectura de Algolia. Es <see langword="null"/> cuando la aplicación
    /// no tiene credenciales configuradas; en ese caso la búsqueda devuelve un listado vacío.
    /// Se construye con la Search API Key, nunca con la Admin API Key.
    /// </summary>
    private readonly ISearchClient? _algoliaClient;

    public IncidenciasController(
        ApplicationDbContext context,
        IOptions<AlgoliaSettings> algoliaOptions,
        ILogger<IncidenciasController> logger,
        ISearchClient? algoliaClient = null)
    {
        _context = context;
        _algolia = algoliaOptions.Value;
        _logger = logger;
        _algoliaClient = algoliaClient;
    }

    /// <summary>
    /// Muestra las incidencias abiertas, opcionalmente filtradas por un término de búsqueda.
    /// </summary>
    /// <param name="searchQuery">Término a buscar por nombre de estación o descripción.</param>
    /// <param name="cancellationToken">Token de cancelación de la petición.</param>
    [HttpGet]
    public async Task<IActionResult> Index(string? searchQuery, CancellationToken cancellationToken)
    {
        var viewModel = new IncidenciasIndexViewModel
        {
            SearchQuery = searchQuery?.Trim() ?? string.Empty
        };

        viewModel.Incidencias = viewModel.BusquedaRealizada
            ? await BuscarAbiertasEnAlgoliaAsync(viewModel.SearchQuery, cancellationToken)
            : await ObtenerTodasLasAbiertasAsync(cancellationToken);

        return View(viewModel);
    }

    /// <summary>
    /// Listado habitual: todas las incidencias abiertas de la base de datos local.
    /// </summary>
    private Task<List<Incidencia>> ObtenerTodasLasAbiertasAsync(CancellationToken cancellationToken) =>
        _context.Incidencias
            .AsNoTracking()
            .Where(i => i.Estado == Incidencia.EstadoAbierta)
            .OrderByDescending(i => i.FechaReporte)
            .ThenBy(i => i.Id)
            .ToListAsync(cancellationToken);

    /// <summary>
    /// Consulta Algolia por nombre de estación o descripción y descarta los resultados
    /// que no estén registrados como "Abierta" en la base de datos local.
    /// </summary>
    private async Task<IReadOnlyList<Incidencia>> BuscarAbiertasEnAlgoliaAsync(string termino, CancellationToken cancellationToken)
    {
        if (_algoliaClient is null || !_algolia.IsConfigured)
        {
            _logger.LogWarning(
                "Búsqueda ignorada: falta la configuración de Algolia ({Variable}). Se define 'Algolia' en appsettings.json o mediante las variables de entorno ALGOLIA_APP_ID, ALGOLIA_SEARCH_KEY y ALGOLIA_INDEX_NAME.",
                _algolia.IsConfigured ? "IndexName" : "AppId/SearchApiKey/IndexName");
            return Array.Empty<Incidencia>();
        }

        var ids = await ObtenerIdsDesdeAlgoliaAsync(termino, cancellationToken);
        if (ids.Count == 0)
        {
            return Array.Empty<Incidencia>();
        }

        var abiertas = await ObtenerAbiertasPorIdsAsync(ids, cancellationToken);

        // Se preserva el orden de relevancia devuelto por Algolia.
        var porId = abiertas.ToDictionary(i => i.Id);
        return ids.Where(porId.ContainsKey).Select(id => porId[id]).ToList();
    }

    /// <summary>Devuelve los <c>objectID</c> de los hits de Algolia para el término indicado.</summary>
    private async Task<List<int>> ObtenerIdsDesdeAlgoliaAsync(string termino, CancellationToken cancellationToken)
    {
        try
        {
            var respuesta = await _algoliaClient!.SearchSingleIndexAsync<IncidenciaAlgoliaHit>(
                _algolia.IndexName,
                new SearchParams(new SearchParamsObject
                {
                    Query = termino,
                    HitsPerPage = HitsPorPagina,

                    // La búsqueda se resuelve sobre los atributos indexados de estación y descripción.
                    RestrictSearchableAttributes = new List<string>
                    {
                        Incidencia.AtributoBusquedaEstacion,
                        Incidencia.AtributoBusquedaDescripcion
                    },

                    // Sólo se pide la clave primaria: nombre, descripción y estado se leen de SQLite.
                    AttributesToRetrieve = new List<string> { nameof(IncidenciaAlgoliaHit.ObjectID) }
                }),
                cancellationToken: cancellationToken);

            return respuesta.Hits
                .Select(h => int.TryParse(h.ObjectID, out var id) ? id : (int?)null)
                .Where(id => id.HasValue)
                .Select(id => id!.Value)
                .Distinct()
                .ToList();
        }
        catch (Exception ex) when (ex is AlgoliaApiException or AlgoliaUnreachableHostException)
        {
            _logger.LogError(ex, "Error al consultar Algolia (índice '{Indice}') para el término '{Termino}'.", _algolia.IndexName, termino);
            return [];
        }
    }

    /// <summary>
    /// Resuelve los ids devueltos por Algolia contra SQLite, quedándose únicamente con
    /// las incidencias cuyo estado es "Abierta".
    /// </summary>
    private async Task<List<Incidencia>> ObtenerAbiertasPorIdsAsync(List<int> ids, CancellationToken cancellationToken)
    {
        var abiertas = new List<Incidencia>(ids.Count);

        for (var offset = 0; offset < ids.Count; offset += TamanoLoteIds)
        {
            var lote = ids.GetRange(offset, Math.Min(TamanoLoteIds, ids.Count - offset));

            abiertas.AddRange(await _context.Incidencias
                .AsNoTracking()
                .Where(i => lote.Contains(i.Id) && i.Estado == Incidencia.EstadoAbierta)
                .ToListAsync(cancellationToken));
        }

        return abiertas;
    }
}
