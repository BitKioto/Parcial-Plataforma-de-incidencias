using Algolia.Search.Clients;
using Algolia.Search.Exceptions;
using Algolia.Search.Models.Search;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;
using PlataformaIncidencias.Areas.Operaciones.Models;
using PlataformaIncidencias.Data;
using PlataformaIncidencias.Models;
using System.Net.Http.Json;
using System.Text.Json;

namespace PlataformaIncidencias.Areas.Operaciones.Controllers;

/// <summary>
/// Listado y búsqueda de incidencias.
/// El listado general se sirve desde la caché de Redis (con escritura de 60 segundos);
/// la búsqueda de texto se delega en Algolia, pero el estado se resuelve siempre
/// contra la base de datos local: sólo se muestran incidencias con estado "Abierta".
/// El cierre de una incidencia publica además un evento en tiempo real en PieSocket.
/// </summary>
[Area("Operaciones")]
public class IncidenciasController : Controller
{
    /// <summary>Clave bajo la que se cachea el listado general de incidencias abiertas.</summary>
    public const string CacheKeyListadoGeneral = "IncidenciasAbiertasList";

    /// <summary>Evento publicado en el canal de PieSocket cuando cambia el estado de una incidencia.</summary>
    public const string EventoIncidenciaActualizada = "IncidenciaActualizada";

    /// <summary>Vigencia de la entrada de caché del listado general.</summary>
    private static readonly TimeSpan CacheExpiracionListado = TimeSpan.FromSeconds(60);

    /// <summary>Estado al que pasa una incidencia al cerrarse.</summary>
    private const string EstadoCerrada = "Cerrada";

    /// <summary>Número máximo de hits solicitados a Algolia por consulta.</summary>
    private const int HitsPorPagina = 50;

    /// <summary>
    /// Tamaño de lote para las consultas "IN (...)" contra SQLite, que limita el número
    /// de parámetros vinculados por sentencia.
    /// </summary>
    private const int TamanoLoteIds = 500;

    private static readonly JsonSerializerOptions OpcionesJsonCache = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Cliente HTTP reutilizado para la API de publicación de PieSocket. Estático y único por
    /// proceso a propósito: PieSocket expone la publicación por REST, y mantener una sola
    /// instancia evita agotar los sockets al encadenar publicaciones.
    /// </summary>
    private static readonly HttpClient HttpPieSocket = new() { Timeout = TimeSpan.FromSeconds(10) };

    private readonly ApplicationDbContext _context;
    private readonly IDistributedCache _cache;
    private readonly IConfiguration _configuration;
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
        IDistributedCache cache,
        IConfiguration configuration,
        IOptions<AlgoliaSettings> algoliaOptions,
        ILogger<IncidenciasController> logger,
        ISearchClient? algoliaClient = null)
    {
        _context = context;
        _cache = cache;
        _configuration = configuration;
        _algolia = algoliaOptions.Value;
        _logger = logger;
        _algoliaClient = algoliaClient;
    }

    /// <summary>
    /// Canal, credenciales y endpoints de PieSocket ya resueltos.
    /// </summary>
    /// <param name="UrlWebSocket">URL <c>wss://</c> que consume el navegador para suscribirse.</param>
    /// <param name="ApiKey">API Key de PieSocket.</param>
    /// <param name="Canal">Identificador del canal de pub/sub.</param>
    /// <param name="UrlPublicacion">Endpoint REST que el backend usa para publicar.</param>
    public sealed record ConfiguracionPieHost(string UrlWebSocket, string ApiKey, string Canal, string UrlPublicacion)
    {
        /// <summary>Indica si hay credenciales suficientes para publicar.</summary>
        public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey) && !string.IsNullOrWhiteSpace(Canal);
    }

    /// <summary>
    /// Resuelve la configuración de PieSocket. Las variables de entorno
    /// <c>PIEHOST_WEBSOCKET_URL</c> y <c>PIEHOST_API_KEY</c> tienen precedencia sobre la
    /// sección <c>PieHost</c> de appsettings.json.
    /// </summary>
    /// <remarks>
    /// Es <see langword="public"/> y <see langword="static"/> para que la vista pueda obtener
    /// la URL de suscripción sin duplicar esta lógica ni ampliar el modelo de vista.
    /// </remarks>
    public static ConfiguracionPieHost ResolverConfiguracionPieHost(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var cluster = configuration["PieHost:ClusterId"]?.Trim();
        var canal = configuration["PieHost:ChannelId"]?.Trim();
        var apiKey = configuration["PIEHOST_API_KEY"] ?? configuration["PieHost:ApiKey"];

        cluster = string.IsNullOrWhiteSpace(cluster) ? "demo" : cluster;
        canal = string.IsNullOrWhiteSpace(canal) ? "incidencias" : canal;
        apiKey = apiKey?.Trim() ?? string.Empty;

        // URL de suscripción del navegador. "notify_self=1" hace que también reciba el evento
        // el supervisor que publicó el cierre desde su propia sesión.
        var urlWebSocket = configuration["PIEHOST_WEBSOCKET_URL"] ?? configuration["PieHost:WebSocketUrl"];
        if (string.IsNullOrWhiteSpace(urlWebSocket))
        {
            urlWebSocket = string.IsNullOrWhiteSpace(apiKey)
                ? string.Empty
                : $"wss://{cluster}.piesocket.com/v3/{Uri.EscapeDataString(canal)}" +
                  $"?api_key={Uri.EscapeDataString(apiKey)}&notify_self=1&source=aspnetcore&presence=0";
        }

        // Endpoint REST de publicación. Se permite sobreescribirlo completo porque PieSocket
        // admite dominios de cluster personalizados, no sólo *.piesocket.com.
        var urlPublicacion = configuration["PIEHOST_PUBLISH_URL"] ?? configuration["PieHost:PublishUrl"];
        if (string.IsNullOrWhiteSpace(urlPublicacion))
        {
            urlPublicacion = $"https://{cluster}.piesocket.com/api/publish?src=aspnetcore-plataforma&v=3";
        }

        return new ConfiguracionPieHost(urlWebSocket.Trim(), apiKey, canal, urlPublicacion.Trim());
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
            ? await BuscarAbiertasEnAlgoliaAsync(viewModel.SearchQuery, cancellationToken)   // Las búsquedas con término no usan caché.
            : await ObtenerListadoGeneralConCacheAsync(cancellationToken);

        return View(viewModel);
    }

    /// <summary>
    /// Cierra una incidencia e invalida la caché del listado general, que ya no reflejaría
    /// el estado real antes de que expirasen sus 60 segundos de vigencia.
    /// </summary>
    /// <param name="id">Identificador de la incidencia a cerrar.</param>
    /// <param name="cancellationToken">Token de cancelación de la petición.</param>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CerrarIncidencia(int id, CancellationToken cancellationToken)
    {
        var incidencia = await _context.Incidencias
            .FirstOrDefaultAsync(i => i.Id == id, cancellationToken);

        if (incidencia is null)
        {
            _logger.LogWarning("No se pudo cerrar la incidencia {Id}: no existe.", id);
            return NotFound($"La incidencia {id} no existe.");
        }

        incidencia.Estado = EstadoCerrada;

        // 1) Se persiste primero: la base de datos es la fuente de verdad del estado.
        await _context.SaveChangesAsync(cancellationToken);

        // 2) La caché ya no refleja el estado real.
        await InvalidarCacheListadoGeneralAsync(cancellationToken);

        // 3) Sólo después de persistir se avisa a los navegadores conectados al canal.
        await PublicarIncidenciaActualizadaAsync(incidencia, cancellationToken);

        return RedirectToAction(nameof(Index));
    }

    /// <summary>
    /// Publica en el canal de PieSocket el evento <c>IncidenciaActualizada</c> con el
    /// identificador y el nuevo estado. Un fallo de publicación no debe impedir el cierre,
    /// que ya está confirmado en la base de datos.
    /// </summary>
    private async Task PublicarIncidenciaActualizadaAsync(Incidencia incidencia, CancellationToken cancellationToken)
    {
        var pieHost = ResolverConfiguracionPieHost(_configuration);

        if (!pieHost.IsConfigured)
        {
            _logger.LogWarning(
                "No se publicó el evento '{Evento}' de la incidencia {Id}: falta la configuración de PieSocket. " +
                "Defina 'PieHost' en appsettings.json o las variables de entorno PIEHOST_WEBSOCKET_URL y PIEHOST_API_KEY.",
                EventoIncidenciaActualizada, incidencia.Id);
            return;
        }

        // Sobre de PieSocket (protocolo v3): { key, channelId, message: { event, data } }.
        var sobre = new
        {
            key = pieHost.ApiKey,
            channelId = pieHost.Canal,
            message = new
            {
                @event = EventoIncidenciaActualizada,
                data = new
                {
                    id = incidencia.Id,
                    estado = incidencia.Estado,
                    fecha = DateTime.UtcNow
                }
            }
        };

        try
        {
            using var respuesta = await HttpPieSocket.PostAsJsonAsync(pieHost.UrlPublicacion, sobre, cancellationToken);

            if (!respuesta.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "No se publicó el evento '{Evento}' de la incidencia {Id}: PieSocket respondió {Estado}.",
                    EventoIncidenciaActualizada, incidencia.Id, (int)respuesta.StatusCode);
                return;
            }

            _logger.LogInformation(
                "Evento '{Evento}' publicado en el canal '{Canal}' de PieSocket para la incidencia {Id}.",
                EventoIncidenciaActualizada, pieHost.Canal, incidencia.Id);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "No se pudo publicar el evento '{Evento}' de la incidencia {Id} en PieSocket.",
                EventoIncidenciaActualizada, incidencia.Id);
        }
    }

    /// <summary>
    /// Listado general servido desde Redis: en HIT devuelve el contenido cacheado y en MISS
    /// consulta la base de datos local y almacena el resultado durante 60 segundos.
    /// </summary>
    private async Task<IReadOnlyList<Incidencia>> ObtenerListadoGeneralConCacheAsync(CancellationToken cancellationToken)
    {
        var desdeCache = await LeerListadoGeneralDesdeCacheAsync(cancellationToken);
        if (desdeCache is not null)
        {
            _logger.LogInformation("Lectura de listado general desde: REDIS CACHE");
            return desdeCache;
        }

        var abiertas = await ObtenerTodasLasAbiertasAsync(cancellationToken);
        _logger.LogInformation("Lectura de listado general desde: BASE DE DATOS");

        await GuardarListadoGeneralEnCacheAsync(abiertas, cancellationToken);

        return abiertas;
    }

    /// <summary>
    /// Devuelve el listado cacheado, o <see langword="null"/> en caso de fallo (MISS).
    /// Si Redis no está disponible se registra una advertencia y se sigue con la base de datos,
    /// de modo que la caché degradada no provoque un error en la petición.
    /// </summary>
    private async Task<List<Incidencia>?> LeerListadoGeneralDesdeCacheAsync(CancellationToken cancellationToken)
    {
        try
        {
            var json = await _cache.GetStringAsync(CacheKeyListadoGeneral, cancellationToken);
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            return JsonSerializer.Deserialize<List<Incidencia>>(json, OpcionesJsonCache);
        }
        catch (JsonException ex)
        {
            // Contenido corrupto o de una versión anterior: se descarta para forzar un MISS.
            _logger.LogWarning(ex, "La entrada de caché '{Clave}' no se pudo deserializar; se descarta.", CacheKeyListadoGeneral);
            await InvalidarCacheListadoGeneralAsync(CancellationToken.None);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "No se pudo leer la caché '{Clave}'; se continúa con la base de datos.", CacheKeyListadoGeneral);
            return null;
        }
    }

    /// <summary>Guarda el listado general en Redis con una vigencia de 60 segundos.</summary>
    private async Task GuardarListadoGeneralEnCacheAsync(List<Incidencia> abiertas, CancellationToken cancellationToken)
    {
        try
        {
            var json = JsonSerializer.Serialize(abiertas, OpcionesJsonCache);

            await _cache.SetStringAsync(CacheKeyListadoGeneral, json, new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = CacheExpiracionListado
            }, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "No se pudo escribir la caché '{Clave}'.", CacheKeyListadoGeneral);
        }
    }

    /// <summary>
    /// Invalida la caché del listado general para que la siguiente lectura se resuelva
    /// contra la base de datos.
    /// </summary>
    private async Task InvalidarCacheListadoGeneralAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _cache.RemoveAsync(CacheKeyListadoGeneral, cancellationToken);
            _logger.LogInformation("Caché invalidada: {Clave}", CacheKeyListadoGeneral);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "No se pudo invalidar la caché '{Clave}'.", CacheKeyListadoGeneral);
        }
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
